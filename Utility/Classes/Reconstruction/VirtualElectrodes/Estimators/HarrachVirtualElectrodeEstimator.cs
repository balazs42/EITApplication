using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Discretizer;

namespace Utility.Classes.Reconstruction.VirtualElectrodes.Estimators
{
    /// <summary>
    /// Model-based estimator inspired by Harrach-style approaches. Uses a Jacobian (sensitivity) matrix to
    /// project measured real electrode voltages back into model parameter space and then forward to virtual
    /// channels.
    ///
    /// Steps:
    /// 1) Split Jacobian rows into real block J_r and virtual block J_v.
    /// 2) Solve regularized least squares for parameters: (J_rᵀ J_r + λ I) x = J_rᵀ y.
    /// 3) Predict virtual voltages: y_v = J_v x (optionally add back ReferenceVoltages).
    /// Falls back to simple linear combination if Jacobian is missing or inconsistent.
    /// </summary>
    internal sealed class HarrachVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        /// <inheritdoc />
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            if (forwardContext?.Jacobian == null)
            {
                var fallback = new LinearCombinationVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }

            // Determine real channel count
            int realCount = forwardContext.RealElectrodeCount > 0
                ? forwardContext.RealElectrodeCount
                : electrodes.Count(e => !e.IsVirtual);

            var jacobian = forwardContext.Jacobian;
            var matrix = Matrix<double>.Build.DenseOfArray(jacobian);
            if (matrix.RowCount < realCount)
            {
                var fallback = new LinearCombinationVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }

            // Partition into real rows (top) and virtual rows (bottom)
            var realBlock = matrix.SubMatrix(0, realCount, 0, matrix.ColumnCount);
            var virtBlock = matrix.RowCount > realCount
                ? matrix.SubMatrix(realCount, matrix.RowCount - realCount, 0, matrix.ColumnCount)
                : Matrix<double>.Build.Dense(0, matrix.ColumnCount);

            // Regularized normal equations: (J_rᵀ J_r + λI) x = J_rᵀ y
            var rhs = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(measuredVoltages);
            var lhs = realBlock.TransposeThisAndMultiply(realBlock);
            lhs += Matrix<double>.Build.DenseIdentity(lhs.RowCount) * Math.Max(settings.HarrachLambda, 1e-8);
            var solution = lhs.Solve(realBlock.TransposeThisAndMultiply(rhs));

            // Predict virtual rows
            var predictedVirtual = virtBlock.RowCount > 0 ? virtBlock * solution : MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(0);
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);

            // Fill virtual electrode ids in their encounter order
            int index = 0;
            foreach (var electrode in electrodes.Where(e => e.IsVirtual))
            {
                double val = index < predictedVirtual.Count ? predictedVirtual[index] : 0.0;
                if (forwardContext?.ReferenceVoltages != null && index < forwardContext.ReferenceVoltages.Length)
                    val += forwardContext.ReferenceVoltages[index];
                values[electrode.Id] = val;
                index++;
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }

}
