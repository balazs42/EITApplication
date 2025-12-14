using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Reconstruction.Regulizers;
using Utility.Classes.ReconstructionParameters;

using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Classes.Factories
{
    /// <summary>
    /// The regularization factory should be used to create the regulizer applied during the inverse solve step of the reconstruciton.
    /// </summary>
    public static class RegularizationFactory
    {
        public static IRegularizer Create(RegularizationTechnique rt, IDiscretization discretization, double lambda = 1e-3) => rt switch
        {
            RegularizationTechnique.None => CreateNoRegularizer(),
            RegularizationTechnique.ZeroOrderTikhonov => CreateZeroOrderTikhonovRegulizer(((FEMMesh)discretization).DeepCopy().GetConductivityDistribution()),
            RegularizationTechnique.FirstOrderTikhonov => CreateFirstOrderTikhonovRegulizer(),
            RegularizationTechnique.Laplace => CreateLaplaceRegulizer(),
            RegularizationTechnique.TotalVariation => CreateTotalVariationRegulizer(),
            _ => throw new NotSupportedException()
        };

        private static NoRegularizer CreateNoRegularizer() 
        {
            var regulizer = new NoRegularizer();

            Workspace.AddLogMessage("RegularizationFactory","Created No Regulizer object.");

            return regulizer;
        }
        private static ZeroOrderTikhonov CreateZeroOrderTikhonovRegulizer(ConductivityDistribution conductivityDistribution)
        {
            var regulizer = new ZeroOrderTikhonov(conductivityDistribution);

            Workspace.AddLogMessage("RegularizationFactory","Created Zero Order Tikhonov Regulizer object.");

            return regulizer;
        }
        private static FirstOrderTikhonov CreateFirstOrderTikhonovRegulizer() 
        {
            var regulizer = new FirstOrderTikhonov();

            Workspace.AddLogMessage("RegularizationFactory","Created First Order Tikhonov Regulizer object.");

            return regulizer;
        }
        private static LaplaceRegularizer CreateLaplaceRegulizer() 
        {
            var regulizer = new LaplaceRegularizer();

            Workspace.AddLogMessage("RegularizationFactory","Created Laplace Regulizer object.");

            return regulizer;
        }
        private static TotalVariationRegularizer CreateTotalVariationRegulizer()
        {
            var regulizer = new TotalVariationRegularizer();

            Workspace.AddLogMessage("RegularizationFactory","Created Total Variation Regulizer object.");

            return regulizer;
        }
    }
}
