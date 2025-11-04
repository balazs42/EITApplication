using System.Numerics;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

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
                VirtualElectrodeMethod.MaximumLikelihoodFourier => new MaximumLikelihoodVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.BayesianFourier => new BayesianFourierVirtualElectrodeEstimator(),
                VirtualElectrodeMethod.GaussianProcessRegression => new GaussianProcessVirtualElectrodeEstimator(),
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

    internal sealed class MaximumLikelihoodVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            try
            {
                var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
                var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
                if (realOrder.Count == 0)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int n = realOrder.Count;
                int kMax = Math.Max(1, settings.FourierOrder);
                while (1 + 2 * kMax > n && kMax > 1)
                    kMax--;

                if (1 + 2 * kMax > n)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int parameterCount = 1 + 2 * kMax;
                var phi = Matrix<double>.Build.Dense(n, parameterCount);
                var y = Vector.Build.Dense(n);

                for (int i = 0; i < n; i++)
                {
                    var (id, angle) = realOrder[i];
                    double measurement = values.TryGetValue(id, out var mv) ? mv : 0.0;
                    y[i] = measurement;
                    phi[i, 0] = 1.0;
                    for (int k = 1; k <= kMax; k++)
                    {
                        phi[i, k] = Math.Cos(k * angle);
                        phi[i, kMax + k] = Math.Sin(k * angle);
                    }
                }

                double lambda = Math.Max(settings.MlRegularization, 0.0);
                var lhs = phi.TransposeThisAndMultiply(phi);
                if (lambda > 0.0)
                    lhs += Matrix<double>.Build.DenseIdentity(parameterCount) * lambda;
                var rhs = phi.TransposeThisAndMultiply(y);
                var thetaHat = lhs.Solve(rhs);

                bool invalid = false;
                foreach (var electrode in electrodes)
                {
                    double angle = angles[electrode.Id];
                    double prediction = thetaHat[0];
                    for (int k = 1; k <= kMax; k++)
                    {
                        prediction += thetaHat[k] * Math.Cos(k * angle) + thetaHat[kMax + k] * Math.Sin(k * angle);
                    }

                    if (double.IsNaN(prediction) || double.IsInfinity(prediction))
                    {
                        invalid = true;
                        break;
                    }

                    if (electrode.IsVirtual || !values.ContainsKey(electrode.Id))
                        values[electrode.Id] = prediction;
                }

                if (invalid)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
            }
            catch
            {
                var fallback = new GeometricVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }
        }
    }

    internal sealed class BayesianFourierVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            try
            {
                var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
                var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
                if (realOrder.Count == 0)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int n = realOrder.Count;
                int kMax = Math.Max(1, settings.FourierOrder);
                while (1 + 2 * kMax > n && kMax > 1)
                    kMax--;

                if (1 + 2 * kMax > n)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int parameterCount = 1 + 2 * kMax;
                var phi = Matrix<double>.Build.Dense(n, parameterCount);
                var y = Vector.Build.Dense(n);

                for (int i = 0; i < n; i++)
                {
                    var (id, angle) = realOrder[i];
                    double measurement = values.TryGetValue(id, out var mv) ? mv : 0.0;
                    y[i] = measurement;
                    phi[i, 0] = 1.0;
                    for (int k = 1; k <= kMax; k++)
                    {
                        phi[i, k] = Math.Cos(k * angle);
                        phi[i, kMax + k] = Math.Sin(k * angle);
                    }
                }

                double sigma2 = Math.Max(settings.BayesNoiseVariance, 1e-12);
                double tau2 = Math.Max(settings.BayesPriorVariance, 1e-12);

                var phiTphi = phi.TransposeThisAndMultiply(phi);
                var identity = Matrix<double>.Build.DenseIdentity(parameterCount);
                var lhs = (1.0 / sigma2) * phiTphi + (1.0 / tau2) * identity;
                var rhs = (1.0 / sigma2) * phi.TransposeThisAndMultiply(y);
                var thetaHat = lhs.Solve(rhs);

                bool invalid = false;
                foreach (var electrode in electrodes)
                {
                    double angle = angles[electrode.Id];
                    double prediction = thetaHat[0];
                    for (int k = 1; k <= kMax; k++)
                    {
                        prediction += thetaHat[k] * Math.Cos(k * angle) + thetaHat[kMax + k] * Math.Sin(k * angle);
                    }

                    if (double.IsNaN(prediction) || double.IsInfinity(prediction))
                    {
                        invalid = true;
                        break;
                    }

                    if (electrode.IsVirtual || !values.ContainsKey(electrode.Id))
                        values[electrode.Id] = prediction;
                }

                if (invalid)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
            }
            catch
            {
                var fallback = new GeometricVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }
        }
    }

    internal sealed class GaussianProcessVirtualElectrodeEstimator : IVirtualElectrodeEstimator
    {
        public double[] CompleteElectrodePotentials(IReadOnlyList<Electrode> electrodes, double[] measuredVoltages, VirtualElectrodeSettings settings, ForwardModelContext? forwardContext = null)
        {
            try
            {
                var values = VirtualElectrodeHelpers.BuildMeasuredLookup(electrodes, measuredVoltages);
                var (realOrder, angles) = VirtualElectrodeHelpers.PrepareOrdering(electrodes, forwardContext);
                if (realOrder.Count == 0)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                int n = realOrder.Count;
                var y = Vector.Build.Dense(n);
                var realAngles = new double[n];

                for (int i = 0; i < n; i++)
                {
                    var (id, angle) = realOrder[i];
                    realAngles[i] = angle;
                    y[i] = values.TryGetValue(id, out var mv) ? mv : 0.0;
                }

                double sigmaF2 = Math.Max(settings.GpSignalVariance, 1e-12);
                double lengthScale = Math.Max(settings.GpLengthScale, 1e-6);
                double sigmaN2 = Math.Max(settings.GpNoiseVariance, 1e-12);

                var kernel = Matrix<double>.Build.Dense(n, n);
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        double diff = Math.Abs(realAngles[i] - realAngles[j]);
                        double distance = Math.Min(diff, 2.0 * Math.PI - diff);
                        double ratio = distance / lengthScale;
                        double value = sigmaF2 * Math.Exp(-0.5 * ratio * ratio);
                        if (i == j)
                            value += sigmaN2;
                        kernel[i, j] = value;
                    }
                }

                var alpha = kernel.Solve(y);

                bool invalid = false;
                foreach (var electrode in electrodes)
                {
                    if (!electrode.IsVirtual && values.ContainsKey(electrode.Id))
                        continue;

                    double angle = angles[electrode.Id];
                    var kStar = Vector.Build.Dense(n);
                    for (int i = 0; i < n; i++)
                    {
                        double diff = Math.Abs(angle - realAngles[i]);
                        double distance = Math.Min(diff, 2.0 * Math.PI - diff);
                        double ratio = distance / lengthScale;
                        kStar[i] = sigmaF2 * Math.Exp(-0.5 * ratio * ratio);
                    }

                    double prediction = kStar.DotProduct(alpha);
                    if (double.IsNaN(prediction) || double.IsInfinity(prediction))
                    {
                        invalid = true;
                        break;
                    }

                    values[electrode.Id] = prediction;
                }

                if (invalid)
                {
                    var fallback = new GeometricVirtualElectrodeEstimator();
                    return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
                }

                return VirtualElectrodeHelpers.MergeVoltages(electrodes, values);
            }
            catch
            {
                var fallback = new GeometricVirtualElectrodeEstimator();
                return fallback.CompleteElectrodePotentials(electrodes, measuredVoltages, settings, forwardContext);
            }
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

            var rhs = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(measuredVoltages);
            var lhs = realBlock.TransposeThisAndMultiply(realBlock);
            lhs += Matrix<double>.Build.DenseIdentity(lhs.RowCount) * Math.Max(settings.HarrachLambda, 1e-8);
            var solution = lhs.Solve(realBlock.TransposeThisAndMultiply(rhs));

            var predictedVirtual = virtBlock.RowCount > 0 ? virtBlock * solution : MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(0);
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
