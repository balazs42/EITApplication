using System.Numerics;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;

namespace Utility.Classes.VirtualElectrodes
{
    public interface IVirtualElectrodeEstimator
    {
        double[] CompleteElectrodePotentials(
            IReadOnlyList<Electrode> electrodes,
            double[] measuredVoltages,
            VirtualElectrodeSettings settings,
            ForwardModelContext? forwardContext = null);
    }

    public sealed class ForwardModelContext
    {
        public IReadOnlyDictionary<int, double>? ElectrodeAngles { get; init; }
        public double[,]? Jacobian { get; init; }
        public int RealElectrodeCount { get; init; }
        public double[]? ReferenceVoltages { get; init; }
    }

    public static class VirtualElectrodeEstimatorFactory
    {
        public static IVirtualElectrodeEstimator Create(VirtualElectrodeSettings settings)
        {
            return settings.Method switch
            {
                VirtualElectrodeMethod.GeometricInterpolation => new GeometricVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.LinearCombination => new LinearCombinationVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.HarrachSensitivityInterpolation => new HarrachVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.NdMapSpectralInterpolation => new NdMapVirtualElectrodeEstimator(),
                _ => new PassthroughVirtualElectrodeEstimator(),
            };
        }
    }

    internal static class VirtualElectrodeHelpers
    {
        public static Dictionary<int, double> ResolveAngles(IReadOnlyList<Electrode> electrodes, ForwardModelContext? context)
        {
            if (context?.ElectrodeAngles != null && context.ElectrodeAngles.Count > 0)
                return new Dictionary<int, double>(context.ElectrodeAngles);

            var angles = new Dictionary<int, double>(electrodes.Count);
            double step = electrodes.Count > 0 ? (2.0 * Math.PI) / electrodes.Count : 0.0;
            for (int i = 0; i < electrodes.Count; i++)
            {
                angles[electrodes[i].Id] = NormalizeAngle(i * step);
            }
            return angles;
        }

        public static Dictionary<int, double> ResolveRealAngles(IReadOnlyList<Electrode> electrodes, ForwardModelContext? context)
        {
            var all = ResolveAngles(electrodes, context);
            return electrodes.Where(e => !e.IsVirtual).ToDictionary(e => e.Id, e => all[e.Id]);
        }

        public static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2.0;
            double result = angle % twoPi;
            if (result < 0)
                result += twoPi;
            return result;
        }

        public static double AngleDelta(double from, double to)
        {
            double delta = NormalizeAngle(to - from);
            if (delta <= 0)
                delta += Math.PI * 2.0;
            return delta;
        }

        public static Dictionary<int, double> BuildMeasuredLookup(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages)
        {
            var lookup = new Dictionary<int, double>();
            int idx = 0;
            foreach (var electrode in electrodes)
            {
                if (electrode.IsVirtual)
                    continue;
                if (idx >= measuredVoltages.Length)
                    break;
                lookup[electrode.Id] = measuredVoltages[idx++];
            }
            return lookup;
        }

        public static double[] MergeVoltages(IReadOnlyList<Electrode> electrodes, Dictionary<int, double> values)
        {
            var result = new double[electrodes.Count];
            for (int i = 0; i < electrodes.Count; i++)
            {
                var electrode = electrodes[i];
                result[i] = values.TryGetValue(electrode.Id, out var v) ? v : 0.0;
            }
            return result;
        }

        public static (List<(int Id, double Angle)> Real, Dictionary<int, double> AllAngles) PrepareOrdering(IReadOnlyList<Electrode> electrodes, ForwardModelContext? context)
        {
            var angleLookup = ResolveAngles(electrodes, context);
            var real = electrodes
                .Where(e => !e.IsVirtual)
                .Select(e => (e.Id, Angle: angleLookup[e.Id]))
                .OrderBy(t => t.Angle)
                .ToList();
            return (real, angleLookup);
        }
    }

    internal sealed class PassthroughVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }

    internal class GeometricVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public virtual double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
            if (realOrder.Count == 0)
                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);

            foreach (var electrode in electrodes.Where(e => e.IsVirtual))
            {
                double angle = angles[electrode.Id];
                var (left, right, t) = LocateNeighbors(realOrder, angle);
                double leftValue = values.TryGetValue(left.Id, out var lv) ? lv : 0.0;
                double rightValue = values.TryGetValue(right.Id, out var rv) ? rv : leftValue;
                double interpolated = (1.0 - t) * leftValue + t * rightValue;
                values[electrode.Id] = interpolated;
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }

        protected static ( (int Id, double Angle) Left, (int Id, double Angle) Right, double T) LocateNeighbors(List<(int Id, double Angle)> ordered, double targetAngle)
        {
            if (ordered.Count == 1)
                return (ordered[0], ordered[0], 0.0);

            for (int i = 0; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var next = ordered[(i + 1) % ordered.Count];
                double span = VirtualElectrodeHelpers.AngleDelta(current.Angle, next.Angle);
                double rel = VirtualElectrodeHelpers.AngleDelta(current.Angle, targetAngle);
                if (rel <= span)
                {
                    double t = span > 0.0 ? rel / span : 0.0;
                    return (current, next, Math.Clamp(t, 0.0, 1.0));
                }
            }

            return (ordered[0], ordered[0], 0.0);
        }
    }

    internal sealed class LinearCombinationVirtualElectrodeEstimator : GeometricVirtualElectrodeEstimator
    {
        public override double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
            if (realOrder.Count == 0)
                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);

            double alphaGlobal = Math.Clamp(settings.LinearCombinationAlpha, 0.0, 1.0);

            foreach (var electrode in electrodes.Where(e => e.IsVirtual))
            {
                double angle = angles[electrode.Id];
                var (left, right, tGeom) = LocateNeighbors(realOrder, angle);
                double alpha = settings.LinearCombinationAlpha < 0.0 ? tGeom : alphaGlobal;
                double leftValue = values.TryGetValue(left.Id, out var lv) ? lv : 0.0;
                double rightValue = values.TryGetValue(right.Id, out var rv) ? rv : leftValue;
                double interpolated = (1.0 - alpha) * leftValue + alpha * rightValue;
                values[electrode.Id] = interpolated;
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }

    internal sealed class HarrachVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            if (forwardContext?.Jacobian == null)
            {
                var fallback = new LinearCombinationVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }

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

            var realBlock = matrix.SubMatrix(0, realCount, 0, matrix.ColumnCount);
            var virtBlock = matrix.RowCount > realCount
                ? matrix.SubMatrix(realCount, matrix.RowCount - realCount, 0, matrix.ColumnCount)
                : Matrix<double>.Build.Dense(0, matrix.ColumnCount);

            var rhs = Vector<double>.Build.DenseOfArray(measuredVoltages);
            var lhs = realBlock.TransposeThisAndMultiply(realBlock);
            lhs += Matrix<double>.Build.DenseIdentity(lhs.RowCount) * Math.Max(settings.HarrachLambda, 1e-8);
            var solution = lhs.Solve(realBlock.TransposeThisAndMultiply(rhs));

            var predictedVirtual = virtBlock.RowCount > 0 ? virtBlock * solution : Vector<double>.Build.Dense(0);
            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);

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

    internal sealed class NdMapVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
            if (realOrder.Count == 0)
                return VirtualElectrodeHelpers.MergeVoltages(electrodes, VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages));

            var realAngles = realOrder.Select(t => t.Angle).ToArray();
            var realValues = realOrder.Select((entry, idx) => measuredVoltages[Math.Min(idx, measuredVoltages.Length - 1)]).ToArray();
            int realCount = realOrder.Count;
            int maxMode = Math.Min(settings.NdMaxMode, Math.Max(1, realCount / 2));

            var coeffs = new Dictionary<int, Complex>();
            for (int n = -maxMode; n <= maxMode; n++)
            {
                Complex sum = Complex.Zero;
                for (int j = 0; j < realCount; j++)
                {
                    sum += realValues[j] * Complex.Exp(-Complex.ImaginaryOne * n * realAngles[j]);
                }
                coeffs[n] = sum / realCount;
            }

            var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
            foreach (var electrode in electrodes)
            {
                double angle = angles[electrode.Id];
                Complex reconstruction = Complex.Zero;
                for (int n = -maxMode; n <= maxMode; n++)
                    reconstruction += coeffs[n] * Complex.Exp(Complex.ImaginaryOne * n * angle);
                if (!values.ContainsKey(electrode.Id) || electrode.IsVirtual)
                    values[electrode.Id] = reconstruction.Real;
            }

            return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
        }
    }
}
