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
                                                                    VirtualElectrodeSettings virtualSettings,
                                                                    int drivePatternSkip = 0)
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

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern, drivePatternSkip);
            var patternDescription = strategy.BuildDescription(electrodeCount,
                                                               usePotentialDifferences
                                                                   ? MeasurementRepresentation.PotentialDifference
                                                                   : MeasurementRepresentation.Amplitude,
                                                               measurementSetup);
            int cycleLength = electrodeCount > 0
                ? Math.Max(1, patternDescription.CycleLength)
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
                    var step = patternDescription.GetStep(i);
                    var excitation = realElectrodes[step.Excitation.First];
                    excitation.IsExcitation = true;
                    excitation.IsMeasuring = false;
                    excitation.Current = excitationAmplitude;

                    var ground = realElectrodes[step.Excitation.Second];
                    ground.IsGround = true;
                    ground.IsMeasuring = false;
                    ground.Current = -excitationAmplitude;
                }

                deepCopy.SetElectrodes(electrodes);

                FEMMesh result = SolveFemForward(deepCopy, solver);

                double[] potentials = PotentialClipper.Clip(result.GetElectrodePotentials());
                var electrodeProjectionList = electrodes.Cast<Utility.Classes.Discretizer.Electrode>().ToList();
                var pattern = MeasurementPatternBuilder.BuildFromStep(electrodeProjectionList,
                                                                      patternDescription.GetStep(i));
                referencePattern ??= pattern;
                var raw = pattern.ExtractRawMeasurement(potentials);
                frames.Add(applyVirtuals
                    ? FilterVirtualMeasurementChannels(pattern, electrodeProjectionList, raw)
                    : raw);
            }

            return new MeasurementSimulationResult(frames, null, referencePattern, measurementSetup, patternDescription);
        }

        /// <inheritdoc />
        public MeasurementSimulationResult SimulateLbmMeasurements(LBMGrid grid,
                                                                    double excitationAmplitude,
                                                                    DrivePattern drivePattern,
                                                                    bool usePotentialDifferences,
                                                                    IDifferentialEquationSolver solver,
                                                                    ElectrodeMeasurementSetup measurementSetup,
                                                                    VirtualElectrodeSettings virtualSettings,
                                                                    int drivePatternSkip = 0)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (solver == null)
                throw new ArgumentNullException(nameof(solver));

            // Use the original grid instance to ensure identical topology (wall/ghost/interior layout)
            // between measurement simulation and later reconstruction. DeepCopy previously rebuilt
            // the domain/ghost layer which led to inflated boundary link counts.
            grid.UpdateGhostConductivityFromNeighbors();

            var electrodes = grid
                .GetElectrodes()
                .Cast<LBMElectrode>()
                .ToList(); // Preserve original ordering

            Workspace.SetCurrentGlobalLbmElectrodes([.. electrodes]);

            bool applyVirtuals = virtualSettings.ShouldApplyVirtualElectrodes();
            var realElectrodes = electrodes.Where(e => !e.IsVirtual).ToList();
            int electrodeCount = realElectrodes.Count;

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern, drivePatternSkip);
            var patternDescription = strategy.BuildDescription(electrodeCount,
                                                               usePotentialDifferences
                                                                   ? MeasurementRepresentation.PotentialDifference
                                                                   : MeasurementRepresentation.Amplitude,
                                                               measurementSetup);
            int cycleLength = electrodeCount > 0
                ? Math.Max(1, patternDescription.CycleLength)
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
                    var step = patternDescription.GetStep(i);

                    var excitation = realElectrodes[step.Excitation.First];
                    excitation.IsExcitation = true;
                    excitation.IsMeasuring = false;
                    excitation.Current = excitationAmplitude;

                    var ground = realElectrodes[step.Excitation.Second];
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

                Workspace.UpdateCurrentGlobalLbmElectrodes(grid);
                Workspace.UpdateCurrentGlobalLbmElements(grid);

                // Keep ghost conductivities in sync every frame as reconstruction does.
                grid.UpdateGhostConductivityFromNeighbors();

                var solvedDistribution = PotentialClipper.Clip(solver.Solve(grid, boundaryCondition, null));
                grid.SetPotentialDistribution(solvedDistribution);

                Workspace.UpdateCurrentGlobalLbmElectrodes(grid);
                Workspace.UpdateCurrentGlobalLbmElements(grid);

                double[] electrodePotentials = PotentialClipper.Clip(grid.GetElectrodePotentials());
                var electrodeProjectionList = electrodes.Cast<Utility.Classes.Discretizer.Electrode>().ToList();
                var pattern = MeasurementPatternBuilder.BuildFromStep(electrodeProjectionList,
                                                                      patternDescription.GetStep(i));
                referencePattern ??= pattern;
                var raw = pattern.ExtractRawMeasurement(electrodePotentials);
                frames.Add(applyVirtuals
                    ? FilterVirtualMeasurementChannels(pattern, electrodeProjectionList, raw)
                    : raw);
            }

            return new MeasurementSimulationResult(frames, excitationAmplitude, referencePattern, measurementSetup, patternDescription);
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
