#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ElectricalImpedanceTomography.Helpers;

internal static partial class Mp4VideoExporter
{
    public static async partial Task<bool> TryExportAsync(IReadOnlyList<string> frameImagePaths,
                                                          int width,
                                                          int height,
                                                          int framesPerSecond,
                                                          string outputFilePath,
                                                          CancellationToken cancellationToken)
    {
        try
        {
            if (frameImagePaths.Count == 0)
                return false;

            cancellationToken.ThrowIfCancellationRequested();

            var composition = new MediaComposition();
            var frameDuration = TimeSpan.FromSeconds(1.0 / Math.Max(framesPerSecond, 1));

            foreach (var framePath in frameImagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imageFile = await StorageFile.GetFileFromPathAsync(framePath);
                cancellationToken.ThrowIfCancellationRequested();
                var clip = await MediaClip.CreateFromImageFileAsync(imageFile, frameDuration);
                composition.Clips.Add(clip);
            }

            if (composition.Clips.Count == 0)
                return false;

            string resolvedDirectory = Path.GetDirectoryName(outputFilePath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(resolvedDirectory);

            var storageFolder = await StorageFolder.GetFolderFromPathAsync(resolvedDirectory);
            string fileName = Path.GetFileName(outputFilePath);
            var outputFile = await storageFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            cancellationToken.ThrowIfCancellationRequested();

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)Math.Max(width, 1);
            profile.Video.Height = (uint)Math.Max(height, 1);
            profile.Video.FrameRate.Numerator = (uint)Math.Max(framesPerSecond, 1);
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;
            profile.Video.Bitrate = (uint)Math.Clamp((long)profile.Video.Width * profile.Video.Height * profile.Video.FrameRate.Numerator * 12,
                                                     1_000_000,
                                                     100_000_000);
            profile.Audio = null;

            var transcodeOperation = composition.RenderToFileAsync(outputFile,
                                                                   MediaTrimmingPreference.Precise,
                                                                   profile);
            using (cancellationToken.Register(() => transcodeOperation.Cancel()))
            {
                var transcodeResult = await transcodeOperation;

                if (transcodeResult != TranscodeFailureReason.None)
                {
                    try
                    {
                        await outputFile.DeleteAsync();
                    }
                    catch
                    {
                        // Ignore cleanup issues when the transcode fails.
                    }

                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (File.Exists(outputFilePath))
            {
                try
                {
                    File.Delete(outputFilePath);
                }
                catch
                {
                    // Ignore cleanup errors.
                }
            }

            return false;
        }
    }
}
#endif
