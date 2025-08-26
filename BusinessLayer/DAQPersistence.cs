using System;
using System.Numerics;
using DataAccessLayer;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

namespace BusinessLayer
{
    public class DAQPersistence : IDAQPersistence
    {
        private readonly IDAQRepository _daqRepository;
        private readonly IMeshRepository _meshRepository;

        public DAQPersistence(IDAQRepository daqRepository, IMeshRepository meshRepository)
        {
            _daqRepository = daqRepository;
            _meshRepository = meshRepository;
        }

        public EITMeasurement GetEITMeasurement()
        {
            return _daqRepository.GetEITMeasurement();
        }

        public Complex[][] ComputeFourierTransform(EITMeasurement measurement)
        {
            return _daqRepository.ComputeFourierTransform(measurement);
        }

        public Complex[][] ComputeDFT(EITMeasurement measurement)
        {
            return _daqRepository.ComputeDFT(measurement);
        }

        public double[][] ComputeDCT(EITMeasurement measurement)
        {
            return _daqRepository.ComputeDCT(measurement);
        }

        public Complex[][] ComputeFFT(EITMeasurement measurement)
        {
            return _daqRepository.ComputeFFT(measurement);
        }

        public void SaveEITMeasurement(EITMeasurement measurement, string name)
        {
            _daqRepository.SaveEITMeasurement(measurement, name);
        }

        public EITMeasurement LoadEITMeasurement(string name, DateTime savedAt)
        {
            return _daqRepository.LoadEITMeasurement(name, savedAt);
        }

        public void DeleteEITMeasurement(string name, DateTime savedAt)
        {
            _daqRepository.DeleteEITMeasurement(name, savedAt);
        }

        public void SaveFEMMesh(FEMMesh mesh, string name)
        {
            _meshRepository.SaveFEMMesh(mesh, name);
        }

        public void SaveLBMMesh(LBMMesh mesh, string name)
        {
            _meshRepository.SaveLBMMesh(mesh, name);
        }

        public FEMMesh LoadFEMMesh(string filePath)
        {
            return _meshRepository.LoadFEMMesh(filePath);
        }

        public LBMMesh LoadLBMMesh(string filePath)
        {
            return _meshRepository.LoadLBMMesh(filePath);
        }
    }
}

