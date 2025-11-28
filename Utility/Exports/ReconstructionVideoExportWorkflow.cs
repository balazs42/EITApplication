using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Utility.Classes;
using Utility.Rendering;

namespace Utility.Exports
{
    /// <summary>
    /// Encapsulates the reconstruction video export workflow so the view model can delegate
    /// heavy generation logic while keeping UI state management local.
    /// </summary>
    public static class ReconstructionVideoExportWorkflow
    {
        /// <summary>
        /// Generates a reconstruction video by rendering per-frame images and encoding them into the
        /// requested container. Returns a rich result with success flag and file path or error details.
        /// </summary>
        public static async Task<VideoExportResult> ExportAsync(SKSize distributionCanvasSize,
                                                                SKSize colorbarCanvasSize,
                                                                SKSize residualCanvasSize,
                                                                PotentialDisplayMode mode,
                                                                VideoExportContainer container,
                                                                IProgress<VideoExportProgressReport>? progress = null,
                                                                CancellationToken cancellationToken = default)
        {
            var frames = Workspace.GetReconstructionFrames().ToList();
            if (frames.Count == 0)
                return VideoExportResult.CreateFailure("No Frames", "There are no reconstruction frames to export.");

            progress?.Report(new VideoExportProgressReport(0.0,
                                                            "Preparing reconstruction frames for video generation..."));

            var results = Workspace.GetReconstructionResults().ToList();
            var fallbackDiscretization = results.Select(r => r.Discretization)
                                                .FirstOrDefault(d => d != null)
                                        ?? Workspace.GetDiscretization();

            if (fallbackDiscretization == null)
                return VideoExportResult.CreateFailure("No Mesh", "Unable to determine the discretization for rendering.");

            var residualHistory = results
                .Select(r => ReconstructionStatistics.CalculateResidual(r, true))
                .ToList();

            var distributionSize = ReconstructionVideoRenderer.NormalizeSize(distributionCanvasSize, 250, 250);
            var colorbarSize = ReconstructionVideoRenderer.NormalizeSize(colorbarCanvasSize, 250, 20);
            var residualSize = ReconstructionVideoRenderer.NormalizeSize(residualCanvasSize, 600, 170);

            string directory = FileSystem.Current.AppDataDirectory;
            Directory.CreateDirectory(directory);
            string baseFileName = $"reconstruction_{DateTime.Now:yyyyMMdd_HHmmss}";
            string mp4FilePath = Path.Combine(directory, baseFileName + ".mp4");
            string aviFallbackFilePath = Path.Combine(directory, baseFileName + ".avi");
            string requestedFilePath = container switch
            {
                VideoExportContainer.Avi => aviFallbackFilePath,
                _ => mp4FilePath
            };
            string? finalFilePath = null;

            try
            {
                await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int videoWidth = 0;
                    int videoHeight = 0;
                    double totalSteps = frames.Count + 1.0;
                    string tempFrameDirectory = Path.Combine(FileSystem.Current.CacheDirectory,
                                                             "VideoExportFrames",
                                                             Guid.NewGuid().ToString("N"));

                    Directory.CreateDirectory(tempFrameDirectory);

                    var encodedFrames = new List<byte[]>(frames.Count);
                    var frameImagePaths = new List<string>(frames.Count);

                    try
                    {
                        for (int i = 0; i < frames.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            double progressValue = (i + 1) / totalSteps;
                            progress?.Report(new VideoExportProgressReport(progressValue,
                                                                            $"Rendering frame {i + 1} of {frames.Count}..."));

                            var context = ReconstructionVideoRenderer.FindResultForFrame(results, i, out int resultIndex);
                            int residualCount = resultIndex >= 0
                                ? Math.Min(resultIndex + 1, residualHistory.Count)
                                : residualHistory.Count;

                            using var image = ReconstructionVideoRenderer.RenderFrameSnapshot(frames[i],
                                                                                             context,
                                                                                             fallbackDiscretization,
                                                                                             residualHistory,
                                                                                             residualCount,
                                                                                             distributionSize,
                                                                                             colorbarSize,
                                                                                             residualSize,
                                                                                             mode);

                            if (videoWidth == 0 || videoHeight == 0)
                            {
                                videoWidth = image.Width;
                                videoHeight = image.Height;
                            }
                            else if (image.Width != videoWidth || image.Height != videoHeight)
                            {
                                throw new InvalidOperationException("All exported frames must share the same dimensions.");
                            }

                            using var encodedFrame = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                            if (encodedFrame == null)
                                throw new InvalidOperationException("Failed to encode video frame to JPEG.");

                            var frameBytes = encodedFrame.ToArray();
                            encodedFrames.Add(frameBytes);

                            string framePath = Path.Combine(tempFrameDirectory, $"frame_{i:D6}.jpg");
                            await File.WriteAllBytesAsync(framePath, frameBytes, cancellationToken).ConfigureAwait(false);
                            frameImagePaths.Add(framePath);
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        progress?.Report(new VideoExportProgressReport(
                            Math.Min(0.98, frames.Count / (frames.Count + 1.0)),
                            "Encoding video stream..."));

                        bool mp4Created = false;

                        if (container != VideoExportContainer.Avi)
                        {
                            mp4Created = await Mp4VideoExporter.TryExportAsync(frameImagePaths,
                                                                              videoWidth,
                                                                              videoHeight,
                                                                              Workspace.ReconstructionVideoFramesPerSecond,
                                                                              requestedFilePath,
                                                                              cancellationToken).ConfigureAwait(false);

                            if (mp4Created)
                            {
                                finalFilePath = requestedFilePath;
                            }
                        }

                        if (!mp4Created)
                        {
                            if (encodedFrames.Count == 0)
                                throw new InvalidOperationException("No frames were encoded for export.");

                            if (File.Exists(mp4FilePath) && container != VideoExportContainer.Avi)
                            {
                                try
                                {
                                    File.Delete(mp4FilePath);
                                }
                                catch
                                {
                                    // Ignore failures when cleaning up a partial MP4 export.
                                }
                            }

                            string aviTargetPath = container == VideoExportContainer.Avi
                                ? requestedFilePath
                                : aviFallbackFilePath;

                            if (File.Exists(aviTargetPath))
                            {
                                try
                                {
                                    File.Delete(aviTargetPath);
                                }
                                catch
                                {
                                    // Ignore cleanup errors.
                                }
                            }

                            using var stream = File.Create(aviTargetPath);
                            using var videoStream = AviVideoWriter.BeginWrite(stream,
                                                                             videoWidth,
                                                                             videoHeight,
                                                                             Workspace.ReconstructionVideoFramesPerSecond,
                                                                             encodedFrames[0].Length,
                                                                             AviVideoWriter.AviVideoCodec.MotionJpeg);

                            foreach (var frame in encodedFrames)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                videoStream.WriteFrame(frame);
                            }

                            videoStream.Complete();
                            finalFilePath = aviTargetPath;
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(tempFrameDirectory))
                            {
                                Directory.Delete(tempFrameDirectory, recursive: true);
                            }
                        }
                        catch
                        {
                            // Ignore cleanup errors.
                        }
                    }
                }, cancellationToken);

                progress?.Report(new VideoExportProgressReport(1.0, "Video generation completed."));
                string path = finalFilePath ?? requestedFilePath;
                return VideoExportResult.CreateSuccess(path);
            }
            catch (OperationCanceledException)
            {
                foreach (var path in new[] { mp4FilePath, aviFallbackFilePath })
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch
                        {
                            // Ignore any errors encountered during cleanup.
                        }
                    }
                }

                return VideoExportResult.CreateFailure("Export Aborted", "The video export was aborted.");
            }
            catch (Exception ex)
            {
                foreach (var path in new[] { mp4FilePath, aviFallbackFilePath })
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch
                        {
                            // Ignore cleanup errors when reporting the failure.
                        }
                    }
                }

                return VideoExportResult.CreateFailure("Export Failed", ex.Message);
            }
        }
    }
}
