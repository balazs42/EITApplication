using ILGPU;
using ILGPU.Runtime;

namespace Utility.Classes.Solvers.LatticeBoltzmannSolver;

internal static class CudaAcceleratorProvider
{
    public static bool TryCreate(out Context? context, out Accelerator? accelerator)
    {
        context = null;
        accelerator = null;

        try
        {
            context = Context.CreateDefault();
            foreach (var device in context)
            {
                if (device.AcceleratorType == AcceleratorType.Cuda)
                {
                    accelerator = device.CreateAccelerator(context);
                    return true;
                }
            }
        }
        catch
        {
            accelerator?.Dispose();
            context?.Dispose();
            context = null;
            accelerator = null;
            return false;
        }

        context?.Dispose();
        context = null;
        accelerator = null;
        return false;
    }
}
