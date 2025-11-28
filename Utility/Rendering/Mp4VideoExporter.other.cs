#if !WINDOWS
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ElectricalImpedanceTomography.Helpers;

internal static partial class Mp4VideoExporter
{
    public static partial Task<bool> TryExportAsync(IReadOnlyList<string> frameImagePaths,
                                                    int width,
                                                    int height,
                                                    int framesPerSecond,
                                                    string outputFilePath,
                                                    CancellationToken cancellationToken)
        => Task.FromResult(false);
}
#endif
