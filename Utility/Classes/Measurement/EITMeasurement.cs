using System;
using System.Collections.Generic;
using System.Linq;

﻿namespace Utility.Classes.Measurement
{
    public class EITMeasurement
    {
        // Frames will store each frame of the measurement
        public List<double[]> Frames = [];

        // Frame size stores the number of elements in each frame, exactly the same number as the number of electrodes used
        public int FrameSize { get; set; } = 16;

        // The current amplitude applied at the moment of the measuremnt.
        public double? CurrentAmplitude { get; set; } = null;

        public static int currentFrameIndex = 0;

        public MeasurementPattern? Pattern { get; set; }

        /// <summary>Optional drive/measurement description that generated the frames.</summary>
        public DrivePatternDescription? PatternDescription { get; set; }

        /// <summary>
        /// Tracks which drive-pattern step each frame corresponds to so excitation/ground
        /// assignment can be reproduced downstream.
        /// </summary>
        public List<int> StepIndices { get; } = new();

        public EITMeasurement(List<double[]> frames, MeasurementPattern? pattern = null, DrivePatternDescription? patternDescription = null, List<int>? stepIndices = null)
        {
            Frames = frames;

            int sizeCheck = Frames[0].Length;
            for (int i = 0; i < Frames.Count; i++)
                if (frames[i].Length != sizeCheck)
                    throw new ArgumentOutOfRangeException("All measurement frames should be of the same size!");

            FrameSize = Frames[0].Length;
            Pattern = pattern;
            PatternDescription = patternDescription;
            StepIndices = stepIndices ?? Enumerable.Range(0, Frames.Count).ToList();
        }

        public EITMeasurement(double[,] measurementFrames)
        {
            Frames.Clear();
            for(int i = 0; i < measurementFrames.GetLength(0); i++)
            {
                double[] frame = new double[measurementFrames.GetLength(1)];
                for(int j = 0; j < measurementFrames.GetLength(1); j++)
                    frame[j] = measurementFrames[i, j];

                Frames.Add(frame);
                StepIndices.Add(i);
            }
        }


        public EITMeasurement(List<double[]> frames, double currentAmplitude, MeasurementPattern? pattern = null, DrivePatternDescription? patternDescription = null, List<int>? stepIndices = null)
        {
            Frames = frames;

            int sizeCheck = Frames[0].Length;
            for (int i = 0; i < Frames.Count; i++)
                if (frames[i].Length != sizeCheck)
                    throw new ArgumentOutOfRangeException("All measurement frames should be of the same size!");

            FrameSize = Frames[0].Length;
            CurrentAmplitude = currentAmplitude;
            Pattern = pattern;
            PatternDescription = patternDescription;
            StepIndices = stepIndices ?? Enumerable.Range(0, Frames.Count).ToList();
        }

        public double[] GetNextFrame()
        {
            if (Frames.Count == 0 || Frames == null)
                throw new NullReferenceException("Cannot get next frame, since Frames list is null or empty. Check code!");

            int frameCount = Frames.Count;

            double[] frame = Frames[currentFrameIndex++ % frameCount];

            return frame;
        }
    }
}
