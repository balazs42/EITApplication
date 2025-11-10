using System;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{
    internal static class LbmElectrodeCoordinateHelper
    {
        public const double LayerShiftX = 0.1;
        public const double LayerShiftY = 0.1;

        public static (double x, double y) ToPhysicalCoordinates(LBMGrid grid, int gridId)
        {
            if (grid is null)
                throw new ArgumentNullException(nameof(grid));

            var (ix, iy) = grid.ToLattice(gridId);

            double cx = (grid.Nx - 1) / 2.0;
            double cy = (grid.Ny - 1) / 2.0;

            double x = (ix - cx) * LayerShiftX;
            double y = (iy - cy) * LayerShiftY;

            return (x, y);
        }
    }
}
