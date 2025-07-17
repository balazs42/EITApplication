using Utility.Classes.Meshing.FiniteElementMesh;

namespace Utility.Classes.Factories
{
    public static class ConductivityDistributionFactory
    {
        /// <summary>
        /// Creates a homogeneous distribution which has the same strucuture as the provided mesh's.
        /// </summary>
        /// <param name="mesh">The mesh that contians the conductivity distribution definition.</param>
        /// <param name="homogeneousValue">The value which we will set the conductivities to.</param>
        /// <returns>The homogeneous distribution.</returns>
        public static ConductivityDistribution CreateHomogeneous(IMesh mesh, double homogeneousValue = 1.0)
        {
            ConductivityDistribution homogeneousDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in homogeneousDistribution.Conductivities)
                homogeneousDistribution.Conductivities[kvp.Key] = homogeneousValue;

            return homogeneousDistribution;
        }

        /// <summary>
        /// Creates a completely random conductivity distribution.
        /// </summary>
        /// <param name="mesh">The mesh that contians the conductivity distribution definition.</param>
        /// <param name="max">The max amount that conductivities can take.</param>
        /// <returns>A random conductivity distribution which resembles the same structure provided in the mesh.</returns>
        public static ConductivityDistribution CreateRandom(IMesh mesh, double max = 1.0)
        {
            Random r = new Random();

            ConductivityDistribution randomDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in randomDistribution.Conductivities)
                randomDistribution.Conductivities[kvp.Key] = r.NextDouble() * max;

            return randomDistribution;
        }

        /// <summary>
        /// Creates  slightly differing conductivity distribution, by scaling the mesh conductivities.
        /// </summary>
        /// <param name="mesh">The mesh that contians the conductivity distribution definition.</param>
        /// <param name="scaling">The scaling parameter which scales the provided conductivites.</param>
        /// <returns>The scaled conductivity distribution.</returns>
        public static ConductivityDistribution CreateSlightlyDiffering(IMesh mesh, double scaling = 0.95)
        {
            if (scaling < 0.0 || scaling > 1.0)
                scaling = 0.5;

            int elementCount = mesh.GetElements().Count;

            ConductivityDistribution conductivityDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            foreach (var kvp in conductivityDistribution.Conductivities)
                    conductivityDistribution.Conductivities[kvp.Key] = conductivityDistribution.Conductivities[kvp.Key] * scaling;

            return conductivityDistribution;
        }

        /// <summary>
        /// Creates a slightly differning conductivity distribution by scaling some random element's conductivities.
        /// </summary>
        /// <param name="mesh">The mesh that contians the conductivity distribution definition</param>
        /// <param name="numDiffering">Number of elements that should be modified.</param>
        /// <param name="scaling">The scaling parameter that should be used on the random elements.</param>
        /// <returns>The randomly scaled conductivity distribution.</returns>
        public static ConductivityDistribution CreateRandomSlightlyDiffering(IMesh mesh, int numDiffering, double scaling = 0.95)
        {
            if (scaling < 0.0 || scaling > 1.0)
                scaling = 0.5;

            Random r = new Random();

            int elementCount = mesh.GetElements().Count;

            double ratio = (double)numDiffering / (double)elementCount;

            if (ratio > 1.0)
                ratio = 1.0;

            ConductivityDistribution conductivityDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            // Set randomly elements conductivity to slightly differing values
            foreach (var kvp in conductivityDistribution.Conductivities)
                if (r.NextDouble() > ratio)
                    conductivityDistribution.Conductivities[kvp.Key] = conductivityDistribution.Conductivities[kvp.Key] * scaling;

            return conductivityDistribution;
        }

        /// <summary>
        /// Creates a conductivity distribution object, which is exactly the same as the provided mesh has
        /// </summary>
        /// <param name="mesh">The mesh that contians the conductivity distribution definition.</param>
        /// <returns>The conductivity distribution which is exactly the same as the mesh's distribution.</returns>
        public static ConductivityDistribution FromFEMMesh(FEMMesh mesh)
        {
            var conductivityDistribution = new Dictionary<int, double>();

            foreach(var element in mesh.Elements)
                conductivityDistribution.Add(element.Id, element.Conductivity);

            return new ConductivityDistribution(conductivityDistribution);
        }
    }
}
