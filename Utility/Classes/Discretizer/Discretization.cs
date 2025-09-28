using Utility.Classes.Discretizer.GraphMesh;
using Utility.Classes.Measurement;

namespace Utility.Classes.Discretizer
{
    /// <summary>
    /// Basic interface for a Mesh type later on. This helps the meshes to be a generic type.
    /// </summary>
    public interface IDiscretization
    {
        void LogDiscretization();

        ConductivityDistribution GetConductivityDistribution();
        PotentialDistribution GetPotentialDistribution();
        void SetPotentialDistribution(PotentialDistribution pd);
        void SetConductivityDistribution(ConductivityDistribution cd);

        Discretization GetDiscretization();

        IReadOnlyList<Electrode> GetElectrodes();
        IReadOnlyList<DiscretizationElement> GetElements();
        double[] GetElectrodePotentials();

        Discretization DeepCopy();

        DiscretizationMetaData Metadata { get; set; }
    }

    /// <summary>
    /// This class provides a bridging abstraction between the interface and the actual abstract class
    /// from which mesh structures should inherit from
    /// </summary>
    public abstract class Discretization : IDiscretization
    {
        public abstract ConductivityDistribution ConductivityDistribution { get; protected set; }
        public abstract PotentialDistribution PotentialDistribution { get; protected set; }

        public DiscretizationMetaData Metadata { get; set; } = new();

        public ConductivityDistribution GetConductivityDistribution() => ConductivityDistribution;
        public PotentialDistribution GetPotentialDistribution() => PotentialDistribution;
        public Discretization GetDiscretization() => this;

        public abstract IReadOnlyList<DiscretizationElement> GetElements();
        public abstract IReadOnlyList<Electrode> GetElectrodes();
        public abstract double[] GetElectrodePotentials();

        public abstract void SetConductivityDistribution(ConductivityDistribution cd);
        public abstract void SetPotentialDistribution(PotentialDistribution pd);        

        public abstract void LogDiscretization();
        public abstract Discretization DeepCopy();

        protected static void ValidateSameKeys(IEnumerable<int> a, IEnumerable<int> b)
        {
            var A = a.OrderBy(x => x).ToArray();
            var B = b.OrderBy(x => x).ToArray();
            if (A.Length != B.Length || !A.SequenceEqual(B))
                throw new ArgumentOutOfRangeException("Key-set mismatch between provided data and mesh state.");
        }
    }

    /// <summary>
    /// All mesh types have to inherit from ths Mesh abstract class. 
    /// This implements basic mesh functionality, like holding electrodes and elements, etc. data.
    /// </summary>
    public abstract class Discretization<TElement, TElectrode> : Discretization
        where TElement : DiscretizationElement
        where TElectrode : Electrode
    {
        protected readonly List<TElement> _elements = [];
        protected readonly List<TElectrode> _electrodes = [];

        public IReadOnlyList<TElement> ElementsTyped => _elements;
        public IReadOnlyList<TElectrode> ElectrodesTyped => _electrodes;

        public sealed override IReadOnlyList<DiscretizationElement> GetElements()
            => [.. _elements.Cast<DiscretizationElement>()];
        public sealed override IReadOnlyList<Electrode> GetElectrodes()
            => [.. _electrodes.Cast<Electrode>()];

        public sealed override double[] GetElectrodePotentials()
            => [.. _electrodes.Select(ReadPotentialOf)];

        public sealed override void SetConductivityDistribution(ConductivityDistribution cd)
        {
            if (cd is null) throw new ArgumentNullException(nameof(cd));
            ValidateSameKeys(cd.Conductivities.Keys, _elements.Select(e => e.Id));

            ConductivityDistribution = cd;
            foreach (var e in _elements)
                if (cd.Conductivities.TryGetValue(e.Id, out var value))
                    e.Conductivity = value;
        }

        public sealed override void SetPotentialDistribution(PotentialDistribution pd)
        {
            if (pd is null) throw new ArgumentNullException(nameof(pd));
            ValidateSameKeys(pd.Potentials.Keys, StateKeys());

            PotentialDistribution = pd;
            foreach (var kv in pd.Potentials)
                ApplyPotentialToState(kv.Key, kv.Value);

            RefreshElectrodePotentialsFromState();
        }

        public void SetElectrodes(IList<TElectrode> electrodes)
        {
            _electrodes.Clear();

            foreach (var el in electrodes)
                _electrodes.Add(el);
        }

        public void SetElements(IList<TElement> elements)
        {
            if (_elements.Count != elements.Count)
                throw new ArgumentException("Cannot set elements, list count mismatch!");

            _elements.Clear();

            foreach (var el in elements)
                _elements.Add(el);
        }

        public void SetConductivity(int id, double value)
        {
            var el = _elements.Find(x => x.Id == id) ?? throw new ArgumentOutOfRangeException("Cannot set conductivity, id not found in elements. Check lists!"); ;

            el.Conductivity = value;
            ConductivityDistribution.Conductivities[id] = value;
        }

        protected virtual void RefreshElectrodePotentialsFromState()
        {
            for (int i = 0; i < _electrodes.Count; i++)
                _electrodes[i].Potential = ReadPotentialOf(_electrodes[i]);
        }

        private void ResetElectrodes()
        {
            foreach(var el in _electrodes)
            {
                el.IsExcitation = false;
                el.IsMeasuring = true;
                el.IsGround = false;

                el.Current = 0.0;
                el.Potential = 0.0;
                el.ZContact = 1.0;
            }
        }

        public void ShiftExcitationElectrodes(DrivePattern drivePattern)
        {
            var excitationElectrode = _electrodes.Find(x => x.IsExcitation) ?? throw new NullReferenceException("Could not find an electrode which is specified as excitation!");
            int excitationElectrodeId = excitationElectrode.Id;

            double excitationCurrent = excitationElectrode.Current;

            var groundElectrode = _electrodes.Find(x => x.IsGround) ?? throw new NullReferenceException("Could not find an electrode which is specified as ground!");
            int groundElectrodeId = groundElectrode.Id;

            double groundCurrent = groundElectrode.Current;

            int electrodeCount = _electrodes.Count;

            ResetElectrodes();

            var strategy = DrivePatternStrategyProvider.GetStrategy(drivePattern);
            int cycleLength = Math.Max(1, strategy.GetCycleLength(electrodeCount));

            int currentStep = 0;
            bool stepFound = false;
            for (int step = 0; step < cycleLength; step++)
            {
                var pair = strategy.GetElectrodePair(electrodeCount, step);
                if (pair.Excitation == excitationElectrodeId && pair.Ground == groundElectrodeId)
                {
                    currentStep = step;
                    stepFound = true;
                    break;
                }
            }

            if (!stepFound)
            {
                for (int step = 0; step < cycleLength; step++)
                {
                    var pair = strategy.GetElectrodePair(electrodeCount, step);
                    if (pair.Excitation == excitationElectrodeId)
                    {
                        currentStep = step;
                        stepFound = true;
                        break;
                    }
                }
            }

            if (!stepFound)
                currentStep = excitationElectrodeId % cycleLength;

            int nextStep = (currentStep + 1) % cycleLength;
            var (nextExcitationElectrodeId, nextGroundElectrodeId) = strategy.GetElectrodePair(electrodeCount, nextStep);

            var nextExcitation = _electrodes[nextExcitationElectrodeId];
            nextExcitation.Current = excitationCurrent;
            nextExcitation.IsExcitation = true;
            nextExcitation.IsMeasuring = false;

            var nextGround = _electrodes[nextGroundElectrodeId];
            nextGround.Current = groundCurrent;
            nextGround.IsGround = true;
            nextGround.IsMeasuring = false;
        }

        protected abstract IEnumerable<int> StateKeys();                       
        protected abstract void ApplyPotentialToState(int key, double phi); 
        protected abstract double ReadPotentialOf(TElectrode electrode);

        public override ConductivityDistribution ConductivityDistribution { get; protected set; } = new([]);
        public override PotentialDistribution PotentialDistribution { get; protected set; } = new([]);

        /// <summary>
        /// Refines the discretization object to a higher resolution one.
        /// </summary>
        /// <param name="n">Number of leves of refinement.</param>
        /// <returns>The refined discretization object.</returns>
        public abstract Discretization<TElement, TElectrode> RefineUniform(int n);
        
        /// <summary>
        /// Convert the spatial discretization of the domain to a graph representation.
        /// </summary>
        /// <returns>A graph object that resembles the original mesh.</returns>
        public abstract Graph ToGraph();

        /// <summary>
        /// Converst a graph object to a spatial discretization of the domain.
        /// </summary>
        /// <param name="graphToConvert">The graph to convert.</param>
        /// <returns>A Discretization object that resembles the original graph.</returns>
        public abstract Discretization<TElement, TElectrode> FromGraph(Graph graphToConvert);
    }
}