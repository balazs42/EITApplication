using System;
using System.Numerics;
using DataAccessLayer;
using Utility.Classes;
using Utility.Classes.Measurement;

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

        public void SaveMesh(IMesh mesh, string name)
        {
            _meshRepository.SaveMesh(mesh, name);
        }

        public IMesh LoadMesh(string filePath)
        {
            return _meshRepository.LoadMesh(filePath);
        }
    }
}

