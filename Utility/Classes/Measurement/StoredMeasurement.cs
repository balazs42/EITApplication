namespace Utility.Classes.Measurement
{
    public sealed class StoredMeasurement
    {
        public string Name { get; set; } = string.Empty;
        public DateTime SavedAt { get; set; }
        public double[][] Frames { get; set; } = Array.Empty<double[]>();
        public int FrameSize { get; set; }
        public double? CurrentAmplitude { get; set; }
    }
}
