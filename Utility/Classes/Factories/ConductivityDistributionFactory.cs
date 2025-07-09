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

            ConductivityDistribution radnomDistribution = new ConductivityDistribution(mesh.GetConductivityDistribution().Conductivities);

            // Reassing conductivity values
            foreach (var kvp in radnomDistribution.Conductivities)
                radnomDistribution.Conductivities[kvp.Key] = r.NextDouble() * max;

            return radnomDistribution;
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
