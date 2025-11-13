using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Utility.Classes;
using Utility.Classes.Application;
using Utility.Classes.Discretizer; // For Electrode base type
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Factories;
using Utility.Classes.Measurement;
using Utility.Classes.Reconstruction; // For PotentialClipper
using Utility.Classes.Reconstruction.DESolvers;
using Utility.Classes.Reconstruction.VirtualElectrodes;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    /// <summary>
    /// Default measurement persistence implementation that reuses the configured
    /// differential equation solver to generate simulated electrode data prior to
    /// reconstruction.
    /// </summary>
    public class MeasurementPersistence : IMeasurementPersistence
    {
        /// <inheritdoc />
        public MeasurementSimulationResult SimulateFemMeasurements(FEMMesh mesh,
                                                                    double excitationAmplitude,
                                                                    DrivePattern drivePattern,
                                                                    bool usePotentialDifferences,
                                                                    IDifferentialEquationSolver solver,
                                                                    ElectrodeMeasurementSetup measurementSetup,
                                                                    VirtualElectrodeSettings virtualSettings)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (solver == null)
                throw new ArgumentNullException(nameof(solver));

            FEMMesh deepCopy = (FEMMesh)mesh.DeepCopy();
            var electrodes = deepCopy.GetElectrodes().Cast<FEMElectrode>().ToList();
            bool applyVirtuals = virtualSettings.ShouldApplyVirtualElectrodes();
            var realElectrodes = electrodes.Where(e => !e.IsVirtual).ToList();
            int electrodeCount = realElectrodes.Count;

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern);
            int cycleLength = electrodeCount > 0
                ? Math.Max(1, strategy.GetCycleLength(electrodeCount))
                : 1;

            var frames = new List<double[]>(cycleLength);
            MeasurementPattern? referencePattern = null;

            for (int i = 0; i < cycleLength; i++)
            {
                foreach (var el in electrodes)
                {
                    el.Current = 0.0;
                    el.IsExcitation = false;
                    el.IsGround = false;
                    el.IsMeasuring = true;
                    el.Potential = 0.0;
                }

                if (electrodeCount > 0)
                {
                    var (excitationIndex, groundIndex) = strategy.GetElectrodePair(electrodeCount, i);
                    var excitation = realElectrodes[excitationIndex];
                    excitation.IsExcitation = true;
                    excitation.IsMeasuring = false;
                    excitation.Current = excitationAmplitude;

                    var ground = realElectrodes[groundIndex];
                    ground.IsGround = true;
                    ground.IsMeasuring = false;
                    ground.Current = -excitationAmplitude;
                }

                FEMMesh result = SolveFemForward(deepCopy, solver);

                double[] potentials = PotentialClipper.Clip(result.GetElectrodePotentials());
                var electrodeProjectionList = electrodes.Cast<Utility.Classes.Discretizer.Electrode>().ToList();
                var pattern = MeasurementPatternBuilder.Build(electrodeProjectionList,
                                                              measurementSetup,
                                                              usePotentialDifferences);
                referencePattern ??= pattern;
                var raw = pattern.ExtractRawMeasurement(potentials);
                frames.Add(applyVirtuals
                    ? FilterVirtualMeasurementChannels(pattern, electrodeProjectionList, raw)
                    : raw);
            }

            return new MeasurementSimulationResult(frames, null, referencePattern, measurementSetup);
        }

        /// <inheritdoc />
        public MeasurementSimulationResult SimulateLbmMeasurements(LBMGrid grid,
                                                                    double excitationAmplitude,
                                                                    DrivePattern drivePattern,
                                                                    bool usePotentialDifferences,
                                                                    IDifferentialEquationSolver solver,
                                                                    ElectrodeMeasurementSetup measurementSetup,
                                                                    VirtualElectrodeSettings virtualSettings)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (solver == null)
                throw new ArgumentNullException(nameof(solver));

            LBMGrid deepCopy = (LBMGrid)grid.DeepCopy();
            // Ensure the ghost layer mirrors interior conductivities before launching forward solves.
            deepCopy.UpdateGhostConductivityFromNeighbors();

            // Work directly with the mesh-owned electrode objects so that current assignments
            // immediately influence the forward solve.  Ordering the collection by the logical
            // electrode id guarantees that the drive-pattern strategy rotates through adjacent
            // physical contacts even when electrodes were created or imported in an arbitrary
            // sequence (for instance after loading legacy grids or augmenting the boundary with
            // virtual electrodes).
            var electrodes = deepCopy
                .GetElectrodes()
                .Cast<LBMElectrode>()
                .OrderBy(e => e.Id)
                .ToList();

            // Persist the canonical ordering for other components that rely on the workspace cache
            // (e.g. UI inspection tools or diagnostic exports).
            Workspace.SetCurrentGlobalLbmElectrodes([.. electrodes]);

            // Re-apply the deterministic ordering to the mesh so subsequent calls that query the
            // discretisation receive electrodes in the same sequence.
            deepCopy.SetElectrodes(electrodes);
            bool applyVirtuals = virtualSettings.ShouldApplyVirtualElectrodes();
            var realElectrodes = electrodes.Where(e => !e.IsVirtual).ToList();
            int electrodeCount = realElectrodes.Count;

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern);
            int cycleLength = electrodeCount > 0
                ? Math.Max(1, strategy.GetCycleLength(electrodeCount))
                : 1;

            var frames = new List<double[]>(cycleLength);
            MeasurementPattern? referencePattern = null;

            for (int i = 0; i < cycleLength; i++)
            {
                foreach (var el in electrodes)
                {
                    el.IsMeasuring = true;
                    el.IsGround = false;
                    el.IsExcitation = false;
                    el.Potential = 0.0;
                    el.Current = 0.0;
                }

                if (electrodeCount > 1)
                {
                    var (excitationIndex, groundIndex) = strategy.GetElectrodePair(electrodeCount, i);

                    var excitation = realElectrodes[excitationIndex];
                    excitation.IsExcitation = true;
                    excitation.IsMeasuring = false;
                    excitation.Current = excitationAmplitude;

                    var ground = realElectrodes[groundIndex];
                    ground.IsGround = true;
                    ground.IsMeasuring = false;
                    ground.Current = -excitationAmplitude;
                }
                else
                {
                    Workspace.AddWarningMessage("LBM measurement simulation requires at least two real electrodes. Returning an empty frame for this step.");
                    frames.Add(Array.Empty<double>());
                    continue;
                }

                var boundaryCondition = new LBMBoundaryCondition(electrodes);
                Workspace.SetCurrentGlobalLbmBoundaryCondition(boundaryCondition);

                // Update workspace for the current LBM configuration using the simulation grid copy
                Workspace.UpdateCurrentGlobalLbmElectrodes(deepCopy);
                Workspace.UpdateCurrentGlobalLbmElements(deepCopy);

                // Execute the forward solve using the same solver configuration as reconstruction so
                // the resulting potentials match the reconstruction forward pass exactly.
                var solvedDistribution = PotentialClipper.Clip(solver.Solve(deepCopy, boundaryCondition, null));
                deepCopy.SetPotentialDistribution(solvedDistribution);

                // Persist the freshly solved state so diagnostic tools and exports observe the
                // electrode ordering and potentials that were actually used for the frame.
                Workspace.UpdateCurrentGlobalLbmElectrodes(deepCopy);
                Workspace.UpdateCurrentGlobalLbmElements(deepCopy);

                double[] electrodePotentials = PotentialClipper.Clip(deepCopy.GetElectrodePotentials());
                var electrodeProjectionList = electrodes.Cast<Utility.Classes.Discretizer.Electrode>().ToList();
                var pattern = MeasurementPatternBuilder.Build(electrodeProjectionList,
                                                              measurementSetup,
                                                              usePotentialDifferences);
                referencePattern ??= pattern;
                var raw = pattern.ExtractRawMeasurement(electrodePotentials);
                frames.Add(applyVirtuals
                    ? FilterVirtualMeasurementChannels(pattern, electrodeProjectionList, raw)
                    : raw);
            }

            return new MeasurementSimulationResult(frames, null, referencePattern, measurementSetup);
        }

        private static FEMMesh SolveFemForward(FEMMesh mesh, IDifferentialEquationSolver solver)
        {
            var boundaryCondition = new FEMBoundaryCondition(mesh.GetElectrodes().Cast<FEMElectrode>().ToList());
            var potentialDistribution = solver.Solve(mesh, boundaryCondition, null);
            mesh.SetPotentialDistribution(PotentialClipper.Clip(potentialDistribution));
            return mesh;
        }


        private static double[] FilterVirtualMeasurementChannels(MeasurementPattern pattern,
                                                                  IList<Utility.Classes.Discretizer.Electrode> electrodes,
                                                                  double[] raw)
        {
            if (pattern == null)
                throw new ArgumentNullException(nameof(pattern));
            if (electrodes == null)
                throw new ArgumentNullException(nameof(electrodes));
            if (raw == null)
                throw new ArgumentNullException(nameof(raw));

            var filtered = new List<double>(raw.Length);
            var channels = pattern.Channels;
            for (int idx = 0; idx < channels.Count; idx++)
            {
                var channel = channels[idx];
                bool involvesVirtual = false;

                if (channel.FirstElectrodeIndex >= 0 && channel.FirstElectrodeIndex < electrodes.Count)
                    involvesVirtual |= electrodes[channel.FirstElectrodeIndex].IsVirtual;

                if (channel.SecondElectrodeIndex >= 0 && channel.SecondElectrodeIndex < electrodes.Count)
                    involvesVirtual |= electrodes[channel.SecondElectrodeIndex].IsVirtual;

                if (involvesVirtual)
                    continue;

                filtered.Add(raw[idx]);
            }

            return filtered.ToArray();
        }
    }
}
