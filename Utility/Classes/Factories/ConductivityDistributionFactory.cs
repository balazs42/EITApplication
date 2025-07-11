using System.Reflection;
using Utility.Classes.Meshing;

namespace Utility.Classes.Factories
{
    public static class ConductivityDistributionFactory
    {
        public static ConductivityDistribution CreateHomogeneous(IMesh mesh)
        {
            ConductivityDistribution homogeneousDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in homogeneousDistribution.Conductivities)
                homogeneousDistribution.Conductivities[kvp.Key] = 1.0;

            return homogeneousDistribution;
        }

        public static ConductivityDistribution CreateRandom(IMesh mesh, double max = 1.0)
        {
            Random r = new Random();

            ConductivityDistribution randomDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in randomDistribution.Conductivities)
                randomDistribution.Conductivities[kvp.Key] = r.NextDouble() * max;

            return randomDistribution;
        }

        public static ConductivityDistribution CreateSlightlyDiffering(IMesh mesh, int numDiffering, double scaling = 0.95)
        {
            if (scaling < 0.0)
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

        public static ConductivityDistribution FromFEMMesh(FEMMesh mesh)
        {
            var conductivityDistribution = new Dictionary<int, double>();

            foreach(var element in mesh.Elements)
                conductivityDistribution.Add(element.Id, element.Conductivity);

            return new ConductivityDistribution(conductivityDistribution);
        }
    }
}
