using Utility.Classes.Meshing;
using MathNet.Numerics.LinearAlgebra;
using Utility.Classes.Measurement;

namespace Utility.Classes.Solvers
{
    /// <summary>
    /// The core "engine" for all LBM-based calculations.
    /// This class uses an element-centric, coordinate-free approach for its main algorithm,
    /// relying on the neighbor-linking established in the LBMMesh.
    /// It implements the D2Q9 model (2 dimensions, 9 velocities).
    /// </summary>
    public sealed class LatticeBoltzmannSolver
    {
        // The 9 discrete velocity vectors (dx, dy) for the D2Q9 model.
        // Index k corresponds to the k-th distribution function Fi[k].
        // e.g., c[1] is (1, 0), which is the direction "right".
        private readonly (int cx, int cy)[] _c = { (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1) };

        // Maps a direction index to its opposite. E.g., opp[1] (right) is 3 (left).
        // This is essential for the bounce-back boundary condition.
        private readonly int[] _opp = { 0, 3, 4, 1, 2, 7, 8, 5, 6 };

        // The lattice weights for each direction, used to calculate the equilibrium distribution.
        private readonly double[] _w = { 4.0 / 9, 1.0 / 9, 1.0 / 9, 1.0 / 9, 1.0 / 9, 1.0 / 36, 1.0 / 36, 1.0 / 36, 1.0 / 36 };

        public PotentialDistribution RunSimulation(LBMMesh mesh, ConductivityDistribution sigma, BoundaryConditions bc, int iterations, Vector<double> source = null)
        {
            // Prepare the mesh with initial values for this simulation run.
            Initialize(mesh, sigma);

            // Translate the high-level boundary conditions into flags on the LBM elements
            // when source is not null, e.g. we are doing a forward step.
            ApplyBoundaryConditions(mesh, bc);

            // Convert the source vector into a more easily accessible format.
            var sourceField = MapSourceToElements(mesh, source);

            // Run the main simulation loop for a fixed number of iterations.
            for (int t = 0; t < iterations; t++)
                Step(mesh, sourceField);

            // Extract the macroscopic potential from the final distribution functions.
            return PackResult(mesh);
        }

        /// <summary>
        /// Computes the misfit gradient component (∇φ · ∇μ) for the LBM grid.
        /// </summary>
        public ConductivityDistribution ComputeGradient(LBMMesh mesh, PotentialDistribution phi, PotentialDistribution mu)
        {
            // First, calculate the gradient fields for both the forward (phi) and adjoint (mu) solutions.
            var gradPhi = LatticeBoltzmannOperators.CalculateGradient(mesh, phi);
            var gradMu = LatticeBoltzmannOperators.CalculateGradient(mesh, mu);

            var gradientDict = new Dictionary<int, double>();

            // We must iterate over the ELEMENTS, not the vertices, because all LBM physics
            // are defined at the center of each element in our simulation grid.
            foreach (var element in mesh.Elements.Cast<LBMElement>())
            {
                // Get the gradient vector for both fields at this element's location.
                var gP = gradPhi.GetVector(element.Id);
                var gM = gradMu.GetVector(element.Id);

                // The gradient of the cost function is the dot product of the two field gradients.
                gradientDict[element.Id] = gP.X * gM.X + gP.Y * gM.Y;
            }

            return new ConductivityDistribution(gradientDict);
        }


        #region Private LBM Helpers

        /// <summary>
        /// Resets and prepares the mesh elements for a new simulation run.
        /// </summary>
        private void Initialize(LBMMesh mesh, ConductivityDistribution sigma)
        {
            foreach (var element in mesh.Elements.Cast<LBMElement>())
            {
                // Set the local conductivity for this cell.
                element.Conductivity = sigma.GetConductivity(element.Id);

                // Reset boundary condition flags.
                element.IsPinned = false;
                element.IsElectrode = false;

                // Initialize the distribution functions to their equilibrium state for a zero field (phi=0).
                // This represents a "cold start".
                for (int k = 0; k < 9; k++)
                    element.Fi[k] = _w[k] * 0.0;
            }
        }

        /// <summary>
        /// Configures the boundary conditions by setting flags on the LBM elements.
        /// </summary>
        private void ApplyBoundaryConditions(LBMMesh mesh, BoundaryConditions bc)
        {
            if (bc?.Electrodes == null) 
                return;

            // For efficiency, create a lookup from element ID to the element object.
            var elementLookup = mesh.Elements.Cast<LBMElement>().ToDictionary(e => e.Id);

            // TODO: Copy electrodes from the boundary codnition
            mesh.Electrodes = bc.Electrodes;

            foreach (var electrode in bc.Electrodes)
            {
                if (elementLookup.TryGetValue(electrode.MeshId, out LBMElement electrodeElement))
                {
                    electrodeElement.IsElectrode = true;

                    // Driving electrodes are "pinned" to a fixed potential to simulate a current source.
                    if (electrode.IsExcitation)
                    {
                        electrodeElement.IsPinned = true;
                    
                        // Ground/sink is set to -1V, source is set to +1V.
                        electrodeElement.PinValue = electrode.IsGround ? -1.0 : 1.0;
                    }
                    // TEST 
                    //if(electrode.IsMeasuring)
                    //{
                    //    Random r = new Random();
                    //    electrodeElement.IsPinned = true;
                    //    electrodeElement.PinValue = r.NextDouble() > 0.5 ? 1.0 : -1.0;
                    //}
                }
            }
        }

        /// <summary>
        /// Performs one full time step of the LBM algorithm, including collision, streaming, and update.
        /// </summary>
        private void Step(LBMMesh mesh, Dictionary<int, double> sourceField)
        {
            var elements = mesh.Elements.Cast<LBMElement>();

            // --- Collision Step ---
            // In this step, we calculate how particle populations in each cell relax towards a local equilibrium.
            foreach (var element in elements)
            {
                if (element.IsWall)
                    continue;

                // The macroscopic variable (potential) is the sum of the distribution functions.
                double localPhi = element.Fi.Sum();

                // The relaxation time tau is determined by the local conductivity (diffusion coefficient).
                double tau = (element.Conductivity / (1.0 / 3.0)) + 0.5; // D = c_s^2 * (tau - 0.5)

                if (tau < 0.5)
                    throw new InvalidDataException("The relaxation time can not be smaller then 0.5, check code!");

                // The relaxation frequency is the inverse of the relaxation time
                double omega = 1.0 / tau;

                // The source term is used for the adjoint problem. For the forward problem, it's zero.
                double source = sourceField?.GetValueOrDefault(element.Id, 0.0) ?? 0.0;

                // For each of the 9 directions
                for (int k = 0; k < 9; k++)
                {
                    // Calculate the equilibrium distribution for this direction.
                    double geq = _w[k] * localPhi;

                    // Apply the BGK collision rule: relax towards equilibrium and add the source term.
                    element.Fi[k] += -omega * (element.Fi[k] - geq) + _w[k] * source;
                }
            }

            // --- Streaming Step ---
            // This step simulates the movement of the particle populations to neighboring cells.
            // It is NOT done in-place; we stream from the main `Fi` array to the `Fi_next` buffer.
            foreach (var element in elements)
            {
                if (element.IsWall) 
                    continue;

                // For each of the 9 directions
                for (int k = 0; k < 9; k++)
                {
                    // Get the neighbor in this direction. The link was set up in the LBMMesh constructor.
                    var neighbor = element.Neighbors[k];

                    // If the neighbor is a valid, open cell
                    if (neighbor != null && !neighbor.IsWall)
                        neighbor.Fi_next[k] = element.Fi[k];    // The population Fi[k] from the current element streams to the neighbor's buffer.
                    else
                        element.Fi_next[_opp[k]] = element.Fi[k];  // Otherwise, it hits a wall or the domain edge. It "bounces back". The population is placed back into the CURRENT element's buffer, but in the OPPOSITE direction.
                }
            }

            // --- Update and Boundary Conditions ---
            // This step commits the results of the streaming step and enforces fixed potentials.
            foreach (var element in elements)
            {
                if (element.IsWall) continue;

                // Copy the streamed values from the temporary buffer back to the main array.
                Array.Copy(element.Fi_next, element.Fi, 9);

                // Clear the buffer for the next time step.
                Array.Clear(element.Fi_next, 0, 9);

                // If this element is an electrode pinned to a fixed value
                if (element.IsPinned)
                {
                    // Overwrite its distribution functions with the equilibrium state for that fixed potential.
                    // This forces the macroscopic potential (phi) in this cell to the desired PinValue.
                    for (int k = 0; k < 9; k++)
                        element.Fi[k] = _w[k] * element.PinValue;
                }
            }
        }

        private Dictionary<int, double> MapSourceToElements(LBMMesh mesh, Vector<double> source)
        {
            if (source == null)
                return null;

            return source.Select((val, i) => new { Id = i, Value = val }).ToDictionary(x => x.Id, x => x.Value);
        }

        private PotentialDistribution PackResult(LBMMesh mesh)
        {
            // After the simulation, convert the final state back to a PotentialDistribution object.
            // First, calculate the final potential (phi) for every element in the grid.
            var calculatedPotentials = mesh.Elements
                                         .Cast<LBMElement>()
                                         .ToDictionary(el => el.Id, el => el.Fi.Sum());

            // Iterate through the physical electrodes defined on the mesh
            // and update their Voltage property with the calculated values.
            if (mesh.Electrodes != null)
            {
                foreach (var electrode in mesh.Electrodes)
                {
                    // Find the calculated potential for the element corresponding to this electrode.
                    if (calculatedPotentials.TryGetValue(electrode.MeshId, out double voltage))
                    {
                        // Update the Voltage property on the Electrode object itself.
                        electrode.Voltage = voltage;
                    }
                }

                // Now each electrode stores the calculated potentials associated with the electrode,
                // but we need the differential voltage.
                for (int i = 0; i < mesh.Electrodes.Count; i++)
                {
                    // Store the electrode which differential voltage we will use
                    var currentElectrode = mesh.Electrodes[i];
                    var adjecentElectrode = mesh.Electrodes[(i + 1) % mesh.Electrodes.Count];

                    // Get the calculated voltages
                    double currentElectrodeVoltage = currentElectrode.Voltage;
                    double adjecentElectrodeVoltage = adjecentElectrode.Voltage;

                    // Store the differential voltage associated with this electrode
                    currentElectrode.Voltage = currentElectrodeVoltage - adjecentElectrodeVoltage;
                }
            }

            // Finally, return the complete potential distribution for other uses (like visualization).
            return new PotentialDistribution(calculatedPotentials);
        }
        #endregion
    }
}