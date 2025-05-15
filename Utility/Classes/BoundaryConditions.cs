namespace Utility.Classes
{
    // Full set of electrodes that define the boundary conditions for one
    // complete forward simulation step (CEM compatible).
    public sealed class BoundaryConditions
    {
        public IReadOnlyList<Electrode> Electrodes { get; }

        public BoundaryConditions(IEnumerable<Electrode> electrodes)
            => Electrodes = electrodes.ToArray();

        /* ─────────────── pre-built drive patterns ──────────────── */

        // Adjacent driving: electrode k sources +I, k+1 sinks –I,
        // all others floating (I=0).  Rotates k from 0 … Ne-1.
        public static IEnumerable<BoundaryConditions> AdjacentRotation(IReadOnlyList<IReadOnlyList<int>> electrodeVertices, double driveCurrent = 0.5, double zContact = 0.0)
        {
            int Ne = electrodeVertices.Count;
            for (int k = 0; k < Ne; ++k)
            {
                var list = new List<Electrode>(Ne);
                for (int e = 0; e < Ne; ++e)
                {
                    double I = 0.0;
                    if (e == k) I = driveCurrent;
                    else if (e == (k + 1) % Ne) I = -driveCurrent;

                    list.Add(new Electrode(e, electrodeVertices[e], I, zContact));
                }
                yield return new BoundaryConditions(list);
            }
        }

        // Opposite-pair drive pattern
        public static IEnumerable<BoundaryConditions> OppositePairs(IReadOnlyList<IReadOnlyList<int>> electrodeVertices, double driveCurrent = 0.5, double zContact = 0.0)
        {
            int Ne = electrodeVertices.Count;
            int half = Ne / 2;
            for (int k = 0; k < half; ++k)
            {
                int src = k;
                int sink = (k + half) % Ne;

                var list = new List<Electrode>(Ne);
                for (int e = 0; e < Ne; ++e)
                {
                    double I = (e == src) ? driveCurrent :
                               (e == sink) ? -driveCurrent : 0.0;
                    list.Add(new Electrode(e, electrodeVertices[e], I, zContact));
                }
                yield return new BoundaryConditions(list);
            }
        }
    }
}
