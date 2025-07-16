
namespace Utility.Classes.Measurement
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

        public EITMeasurement(List<double[]> frames)
        {
            Frames = frames;

            int sizeCheck = Frames[0].Length;
            for (int i = 0; i < Frames.Count; i++)
                if (frames[i].Length != sizeCheck)
                    throw new ArgumentOutOfRangeException("All measurement frames should be of the same size!");

            FrameSize = Frames[0].Length;
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
            }
        }


        public EITMeasurement(List<double[]> frames, double currentAmplitude)
        {
            Frames = frames;

            int sizeCheck = Frames[0].Length;
            for (int i = 0; i < Frames.Count; i++)
                if (frames[i].Length != sizeCheck)
                    throw new ArgumentOutOfRangeException("All measurement frames should be of the same size!");

            FrameSize = Frames[0].Length;
            CurrentAmplitude = currentAmplitude;
        }

        public double[] GetNextFrame()
        {
            if (Frames.Count == 0 || Frames == null)
                throw new NullReferenceException("Cannot get next frame, since Frames list is null or empty. Check code!");

            double[] frame = Frames[currentFrameIndex++ % FrameSize];

            return frame;
        }


    }
}
