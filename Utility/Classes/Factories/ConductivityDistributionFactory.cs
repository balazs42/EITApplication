using System.Linq;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;

using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Classes.Factories
{
    public enum InitialDistributionTypes
    {
        Homogeneous = 1,
        Random = 2,
        SlightlyDiffering = 3,
        RandomSlightlyDiffering = 4,
        CloseToTarget = 5
    }

    public static class ConductivityDistributionFactory
    {
        /// <summary>
        /// Creates a homogeneous distribution which has the same strucuture as the provided mesh's.
        /// </summary>
        /// <param name="discretization">The mesh that contians the conductivity distribution definition.</param>
        /// <param name="homogeneousValue">The value which we will set the conductivities to.</param>
        /// <returns>The homogeneous distribution.</returns>
        public static ConductivityDistribution CreateHomogeneous(IDiscretization discretization, double homogeneousValue = 1.0)
        {
            ConductivityDistribution homogeneousDistribution = new(discretization.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in homogeneousDistribution.Conductivities)
                homogeneousDistribution.Conductivities[kvp.Key] = homogeneousValue;

            Workspace.AddLogMessage("ConductivityDistributionFactory", "Created Homogeneous ConductivityDistribution object.");

            return homogeneousDistribution;
        }

        /// <summary>
        /// Creates a completely random conductivity distribution.
        /// </summary>
        /// <param name="discretization">The mesh that contians the conductivity distribution definition.</param>
        /// <param name="max">The max amount that conductivities can take.</param>
        /// <returns>A random conductivity distribution which resembles the same structure provided in the mesh.</returns>
        public static ConductivityDistribution CreateRandom(IDiscretization discretization, double max = 1.0)
        {
            Random r = new();

            ConductivityDistribution randomDistribution = new(discretization.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in randomDistribution.Conductivities)
                randomDistribution.Conductivities[kvp.Key] = r.NextDouble() * max;

            Workspace.AddLogMessage("ConductivityDistributionFactory", "Created Random ConductivityDistribution object.");
            
            return randomDistribution;
        }

        /// <summary>
        /// Creates  slightly differing conductivity distribution, by scaling the mesh conductivities.
        /// </summary>
        /// <param name="discretization">The mesh that contians the conductivity distribution definition.</param>
        /// <param name="scaling">The scaling parameter which scales the provided conductivites.</param>
        /// <returns>The scaled conductivity distribution.</returns>
        public static ConductivityDistribution CreateSlightlyDiffering(IDiscretization discretization, double scaling = 0.95)
        {
            if (scaling < 0.0 || scaling > 1.0)
                scaling = 0.5;

            int elementCount = discretization.GetElements().Count;

            ConductivityDistribution conductivityDistribution = new(discretization.GetConductivityDistribution().Conductivities);

            foreach (var kvp in conductivityDistribution.Conductivities)
                    conductivityDistribution.Conductivities[kvp.Key] = conductivityDistribution.Conductivities[kvp.Key] * scaling;

            Workspace.AddLogMessage("ConductivityDistributionFactory", "Created Slightly differing ConductivityDistribution object.");

            return conductivityDistribution;
        }

        /// <summary>
        /// Creates a slightly differning conductivity distribution by scaling some random element's conductivities.
        /// </summary>
        /// <param name="discretization">The mesh that contians the conductivity distribution definition</param>
        /// <param name="numDiffering">Number of elements that should be modified.</param>
        /// <param name="scaling">The scaling parameter that should be used on the random elements.</param>
        /// <returns>The randomly scaled conductivity distribution.</returns>
        public static ConductivityDistribution CreateRandomSlightlyDiffering(IDiscretization discretization, int numDiffering, double scaling = 0.95)
        {
            if (scaling < 0.0 || scaling > 1.0)
                scaling = 0.5;

            Random r = new();

            int elementCount = discretization.GetElements().Count;

            double ratio = (double)numDiffering / (double)elementCount;

            if (ratio > 1.0)
                ratio = 1.0;

            ConductivityDistribution conductivityDistribution = new(discretization.GetConductivityDistribution().Conductivities);

            // Set randomly elements conductivity to slightly differing values
            foreach (var kvp in conductivityDistribution.Conductivities)
                if (r.NextDouble() > ratio)
                    conductivityDistribution.Conductivities[kvp.Key] = conductivityDistribution.Conductivities[kvp.Key] * scaling;

            Workspace.AddLogMessage("ConductivityDistributionFactory", "Created Random Slighly differing ConductivityDistribution object.");

            return conductivityDistribution;
        }

        /// <summary>
        /// Creates a conductivity distribution that closely resembles the original (target)
        /// distribution while applying a gentle random distortion.
        /// </summary>
        /// <param name="discretization">The discretization that defines the element layout.</param>
        /// <param name="distortion">Relative distortion strength in the [0, 1] range.</param>
        /// <returns>A conductivity distribution similar to the target distribution.</returns>
        public static ConductivityDistribution CreateCloseToTarget(IDiscretization discretization, double distortion = 0.1)
        {
            if (distortion < 0.0)
                distortion = 0.0;

            if (distortion > 1.0)
                distortion = 1.0;

            var random = new Random();

            ConductivityDistribution? targetDistribution = Workspace.GetOriginalConductivityDistribution()
                ?? Workspace.GetOriginalDiscretization()?.GetConductivityDistribution()
                ?? discretization.GetConductivityDistribution();

            // Clone the target conductivities so that callers get an independent copy.
            var closeDistribution = new ConductivityDistribution(targetDistribution.Conductivities);

            foreach (var elementId in closeDistribution.Conductivities.Keys.ToArray())
            {
                double originalValue = closeDistribution.Conductivities[elementId];
                // Apply a symmetric random distortion around the original value.
                double delta = (random.NextDouble() * 2.0 - 1.0) * distortion;
                double distortedValue = originalValue * (1.0 + delta);
                closeDistribution.Conductivities[elementId] = distortedValue;
            }

            Workspace.AddLogMessage("ConductivityDistributionFactory", "Created CloseToTarget ConductivityDistribution object.");

            return closeDistribution;
        }

        /// <summary>
        /// Creates a conductivity distribution object, which is exactly the same as the provided mesh has
        /// </summary>
        /// <param name="mesh">The mesh that contians the conductivity distribution definition.</param>
        /// <returns>The conductivity distribution which is exactly the same as the mesh's distribution.</returns>
        public static ConductivityDistribution FromFEMMesh(FEMMesh mesh)
        {
            Dictionary<int, double> conductivityDistribution = [];

            var elements = mesh.GetElements();

            foreach(var element in elements)
                conductivityDistribution.Add(element.Id, element.Conductivity);

            Workspace.AddLogMessage("ConductivityDistributionFactory", "Created from FEMMesh ConductivityDistribution object.");

            return new ConductivityDistribution(conductivityDistribution);
        }

        public static ConductivityDistribution CreateInitialDistribution(IDiscretization discretization, InitialDistributionTypes type)
        {
            return type switch
            {
                InitialDistributionTypes.Homogeneous => CreateHomogeneous(discretization),
                InitialDistributionTypes.Random => CreateRandom(discretization, 1.0),
                InitialDistributionTypes.SlightlyDiffering => CreateSlightlyDiffering(discretization, 0.95),
                InitialDistributionTypes.RandomSlightlyDiffering =>
                    CreateRandomSlightlyDiffering(discretization, Math.Max(1, discretization.GetElements().Count / 10), 0.95),
                InitialDistributionTypes.CloseToTarget => CreateCloseToTarget(discretization, 0.1),
                _ => CreateHomogeneous(discretization)
            };
        }
    }
}