﻿using Dinah.Core;
using Dinah.Core.Diagnostics;
using Dinah.Core.IO;
using Dinah.Core.StepRunner;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AaxDecrypter
{
    public interface ISimpleAaxToM4bConverter
    {
        event EventHandler<int> DecryptProgressUpdate;
        bool Run();
        string AppName { get; set; }
        string outDir { get; }
        string outputFileName { get; }
        ChapterInfo chapters { get; }
        void SetOutputFilename(string outFileName);
        string Title { get; }
        string Author { get; }
        string Narrator { get; }
        byte[] CoverArt { get; }
    }
    public interface IAdvancedAaxcToM4bConverter : ISimpleAaxToM4bConverter
    {
        void Cancel();
        bool Step1_CreateDir();
        bool Step2_DownloadAndCombine();
        bool Step3_RestoreMetadata();
        bool Step4_CreateCue();
        bool Step5_CreateNfo();
    }
    public class AaxcDownloadConverter : IAdvancedAaxcToM4bConverter
    {
        public event EventHandler<int> DecryptProgressUpdate;
        public event EventHandler<TimeSpan> DecryptTimeRemaining;
        public string AppName { get; set; } = nameof(AaxcDownloadConverter);
        public string outDir { get; private set; }
        public string outputFileName { get; private set; }
        public ChapterInfo chapters { get; private set; }
        public string Title => aaxcTagLib.Tag.Title.Replace(" (Unabridged)", "");
        public string Author => aaxcTagLib.Tag.FirstPerformer ?? "[unknown]";
        public string Narrator => aaxcTagLib.GetTag(TagLib.TagTypes.Apple).Narrator;
        public byte[] CoverArt => aaxcTagLib.Tag.Pictures.Length > 0 ? aaxcTagLib.Tag.Pictures[0].Data.Data : default;

        private TagLib.Mpeg4.File aaxcTagLib { get; set; }
        private StepSequence steps { get; }
        private DownloadLicense downloadLicense { get; set; }
        private FFMpegAaxcProcesser aaxcProcesser;
        public static async Task<AaxcDownloadConverter> CreateAsync(string outDirectory, DownloadLicense dlLic, ChapterInfo chapters = null)
        {
            var converter = new AaxcDownloadConverter(outDirectory, dlLic, chapters);           
            await converter.prelimProcessing();
            return converter;
        }

        private AaxcDownloadConverter(string outDirectory, DownloadLicense dlLic, ChapterInfo chapters)
        {
            ArgumentValidator.EnsureNotNullOrWhiteSpace(outDirectory, nameof(outDirectory));
            ArgumentValidator.EnsureNotNull(dlLic, nameof(dlLic));

            if (!Directory.Exists(outDirectory))
                throw new ArgumentNullException(nameof(outDirectory), "Directory does not exist");
            outDir = outDirectory;

            steps = new StepSequence
            {
                Name = "Convert Aax To M4b",

                ["Step 1: Create Dir"] = Step1_CreateDir,
                ["Step 2: Download and Combine Audiobook"] = Step2_DownloadAndCombine,
                ["Step 3: Restore Aaxc Metadata"] = Step3_RestoreMetadata,
                ["Step 4: Create Cue"] = Step4_CreateCue,
                ["Step 5: Create Nfo"] = Step5_CreateNfo,
            };

            downloadLicense = dlLic;
            this.chapters = chapters;
        }

        private async Task prelimProcessing()
        {
            //Get metadata from the file over http
            var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", downloadLicense.UserAgent);

            var networkFile = await NetworkFileAbstraction.CreateAsync(client, new Uri(downloadLicense.DownloadUrl));

            aaxcTagLib = await Task.Run(() => TagLib.File.Create(networkFile, "audio/mp4", TagLib.ReadStyle.Average) as TagLib.Mpeg4.File);

            var defaultFilename = Path.Combine(
              outDir,
              PathLib.ToPathSafeString(aaxcTagLib.Tag.FirstPerformer??"[unknown]"),
              PathLib.ToPathSafeString(aaxcTagLib.Tag.Title.Replace(" (Unabridged)", "")) + ".m4b"
              );

            SetOutputFilename(defaultFilename);
        }

        public void SetOutputFilename(string outFileName)
        {
            outputFileName = PathLib.ReplaceExtension(outFileName, ".m4b");
            outDir = Path.GetDirectoryName(outputFileName);

            if (File.Exists(outputFileName))
                File.Delete(outputFileName);
        }

        public bool Run()
        {
            var (IsSuccess, Elapsed) = steps.Run();

            if (!IsSuccess)
            {
                Console.WriteLine("WARNING-Conversion failed");
                return false;
            }

            var speedup = (int)(aaxcTagLib.Properties.Duration.TotalSeconds / (long)Elapsed.TotalSeconds);
            Console.WriteLine("Speedup is " + speedup + "x realtime.");
            Console.WriteLine("Done");
            return true;
        }

        public bool Step1_CreateDir()
        {
            ProcessRunner.WorkingDir = outDir;
            Directory.CreateDirectory(outDir);

            return true;
        }

        public bool Step2_DownloadAndCombine()
        {
            aaxcProcesser = new FFMpegAaxcProcesser(downloadLicense);
            aaxcProcesser.ProgressUpdate += AaxcProcesser_ProgressUpdate;

            bool userSuppliedChapters = chapters != null;

            string metadataPath = null;

            if (userSuppliedChapters)
            {
                //Only write chaopters to the metadata file. All other aaxc metadata will be
                //wiped out but is restored in Step 3.
                metadataPath = Path.Combine(outDir, Path.GetFileName(outputFileName) + ".ffmeta");
                File.WriteAllText(metadataPath, chapters.ToFFMeta(true));
            }

            aaxcProcesser.ProcessBook(
                outputFileName,
                metadataPath)
                .GetAwaiter()
                .GetResult();

            if (!userSuppliedChapters && aaxcProcesser.Succeeded)
                chapters = new ChapterInfo(outputFileName);

            if (userSuppliedChapters)
                FileExt.SafeDelete(metadataPath);

            DecryptProgressUpdate?.Invoke(this, 0);

            return aaxcProcesser.Succeeded;
        }

        private void AaxcProcesser_ProgressUpdate(object sender, TimeSpan e)
        {
            double averageRate = getAverageProcessRate(e);

            double remainingSecsToProcess = (aaxcTagLib.Properties.Duration - e).TotalSeconds;

            double estTimeRemaining = remainingSecsToProcess / averageRate;

            if (double.IsNormal(estTimeRemaining))
                DecryptTimeRemaining?.Invoke(this, TimeSpan.FromSeconds(estTimeRemaining));


            double progressPercent = 100 * e.TotalSeconds / aaxcTagLib.Properties.Duration.TotalSeconds;

            DecryptProgressUpdate?.Invoke(this, (int)progressPercent);
        }

        /// <summary>
        /// Calculates the average processing rate based on the last <see cref="MAX_NUM_AVERAGE"/> samples.
        /// </summary>
        /// <param name="lastProcessedPosition">Position in the audio file last processed</param>
        /// <returns>The average processing rate, in book_duration_seconds / second.</returns>
        private double getAverageProcessRate(TimeSpan lastProcessedPosition)
        {
            streamPositions.Enqueue(new StreamPosition
            {
                ProcessPosition = lastProcessedPosition,
                EventTime = DateTime.Now,
            });

            if (streamPositions.Count < 2)
                return double.PositiveInfinity;

            //Calculate the harmonic mean of the last AVERAGE_NUM progress updates
            //Units are Book_Duration_Seconds / second

            var lastPos = streamPositions.Count > MAX_NUM_AVERAGE ?  streamPositions.Dequeue() : null;

            double harmonicDenominator = 0;
            int harmonicNumerator = 0;

            foreach (var pos in streamPositions)
            {
                if (lastPos is null)
                {
                    lastPos = pos;
                    continue;
                }
                double dP = (pos.ProcessPosition - lastPos.ProcessPosition).TotalSeconds;
                double dT = (pos.EventTime - lastPos.EventTime).TotalSeconds;

                harmonicDenominator += dT / dP;
                harmonicNumerator++;
                lastPos = pos;
            }

            double harmonicMean = harmonicNumerator / harmonicDenominator;
            return harmonicMean;
        }
        private const int MAX_NUM_AVERAGE = 15;
        private class StreamPosition
        {
            public TimeSpan ProcessPosition { get; set; }
            public DateTime EventTime { get; set; }
        }

        private Queue<StreamPosition> streamPositions = new Queue<StreamPosition>();

        /// <summary>
        /// Copy all aacx metadata to m4b file, including cover art.
        /// </summary>
        public bool Step3_RestoreMetadata()
        {
            var outFile = new TagLib.Mpeg4.File(outputFileName, TagLib.ReadStyle.Average);

            var destTags = outFile.GetTag(TagLib.TagTypes.Apple) as TagLib.Mpeg4.AppleTag;
            destTags.Clear();

            var sourceTag = aaxcTagLib.GetTag(TagLib.TagTypes.Apple) as TagLib.Mpeg4.AppleTag;

            //copy all metadata fields in the source file, even those that TagLib doesn't
            //recognize, to the output file.
            //NOTE: Chapters aren't stored in MPEG-4 metadata. They are encoded as a Timed
            //Text Stream (MPEG-4 Part 17), so taglib doesn't read or write them.
            foreach (var stag in sourceTag)
            {
                destTags.SetData(stag.BoxType, stag.Children.Cast<TagLib.Mpeg4.AppleDataBox>().ToArray());
            }
            outFile.Save();

            return true;
        }

        public bool Step4_CreateCue()
        {
            File.WriteAllText(PathLib.ReplaceExtension(outputFileName, ".cue"), Cue.CreateContents(Path.GetFileName(outputFileName), chapters));
            return true;
        }

        public bool Step5_CreateNfo()
        {
            File.WriteAllText(PathLib.ReplaceExtension(outputFileName, ".nfo"), NFO.CreateContents(AppName, aaxcTagLib, chapters));
            return true;
        }

        public void Cancel()
        {
            aaxcProcesser.Cancel();
        }
    }
}
