using System;
using System.Collections.Generic;
using System.Linq;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Measurement;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;
using Xunit;
using Xunit.Sdk;

namespace Utility.Tests
{
    public class LatticeBoltzmannSolverTests
    {
        private static (LBMGrid Grid, LBMBoundaryCondition Boundary) CreateTwoElectrodeSetup()
        {
            var grid = new LBMGrid(8, 8);

            int nyMid = grid.Ny / 2;
            var left = grid.GetElementAt(1, nyMid);
            var right = grid.GetElementAt(grid.Nx - 2, nyMid);

            var gridElectrodes = new List<LBMElectrode>
            {
                new LBMElectrode(id: 0, gridId: left.Id, current: -1.0, potential: 0.0, contactImpedance: 0.0, isExcitation: false, isGround: true),
                new LBMElectrode(id: 1, gridId: right.Id, current: 1.0, potential: 0.0, contactImpedance: 0.0, isExcitation: true, isGround: false)
            };

            grid.SetElectrodes(gridElectrodes);

            var bcElectrodes = new List<LBMElectrode>
            {
                new LBMElectrode(id: 0, gridId: left.Id, current: -1.0, potential: 0.0, contactImpedance: 0.0, isExcitation: false, isGround: true),
                new LBMElectrode(id: 1, gridId: right.Id, current: 1.0, potential: 0.0, contactImpedance: 0.0, isExcitation: true, isGround: false)
            };

            var bc = new LBMBoundaryCondition(bcElectrodes, requireDrivePair: false);
            return (grid, bc);
        }

        [Fact]
        public void CpuAndGpuForwardSolveAgreeWithinTolerance()
        {
            var (cpuGrid, cpuBoundary) = CreateTwoElectrodeSetup();
            var solverCpu = new LatticeBoltzmannSolver(2000, 1e-8, 50, useCuda: false);
            var cpuResult = solverCpu.SolveForward(cpuGrid, cpuBoundary);

            try
            {
                var (gpuGrid, gpuBoundary) = CreateTwoElectrodeSetup();
                var solverGpu = new LatticeBoltzmannSolver(2000, 1e-8, 50, useCuda: true);
                var gpuResult = solverGpu.SolveForward(gpuGrid, gpuBoundary);

                var cpuPotentials = cpuResult.Potentials;
                var gpuPotentials = gpuResult.Potentials;

                foreach (var element in cpuGrid.GetElements().Cast<LBMElement>())
                {
                    if (element.IsWall || element.GhostElement)
                        continue;

                    double cpuPhi = cpuPotentials[element.Id];
                    double gpuPhi = gpuPotentials[element.Id];
                    Assert.True(Math.Abs(cpuPhi - gpuPhi) < 1e-6, $"Potential mismatch at element {element.Id}: CPU={cpuPhi}, GPU={gpuPhi}");
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("CUDA"))
            {
                throw new SkipException("CUDA accelerator unavailable for GPU comparison test.");
            }
        }

        [Fact]
        public void ElectrodeFluxMatchesAppliedCurrent()
        {
            var (grid, boundary) = CreateTwoElectrodeSetup();
            var solver = new LatticeBoltzmannSolver(2000, 1e-8, 50, useCuda: false);
            var solution = solver.SolveForward(grid, boundary);

            var potentials = solution.Potentials;
            var electrodes = boundary.GetElectrodes().Cast<LBMElectrode>().ToList();

            foreach (var electrode in electrodes)
            {
                var cell = grid.GetElements().Cast<LBMElement>().First(e => e.Id == electrode.GridId);
                double phiInterior = potentials[cell.Id];
                double sigmaInterior = cell.Conductivity;
                double fluxSum = 0.0;

                for (int dir = 1; dir < 9; dir++)
                {
                    var ghost = cell.Neighbors[dir];
                    if (ghost is null || !ghost.GhostElement)
                        continue;

                    double sigmaGhost = ghost.Conductivity > 0.0 ? ghost.Conductivity : sigmaInterior;
                    if (sigmaGhost <= 0.0)
                        continue;

                    double sigmaAvg = 0.5 * (sigmaInterior + sigmaGhost);
                    double phiGhost = potentials[ghost.Id];
                    fluxSum += sigmaAvg * (phiInterior - phiGhost);
                }

                Assert.True(Math.Abs(fluxSum - electrode.Current) < 1e-6, $"Flux conservation violated for electrode {electrode.Id}: expected {electrode.Current}, got {fluxSum}");
            }
        }
    }
}
