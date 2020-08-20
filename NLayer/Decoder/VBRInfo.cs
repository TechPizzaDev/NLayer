namespace NLayer.Decoder
{
    internal class VBRInfo
    {
        public int SampleCount { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int VBRFrames { get; set; }
        public int VBRBytes { get; set; }
        public int VBRQuality { get; set; }
        public int VBRDelay { get; set; }

        // we assume the entire stream is consistent wrt samples per frame
        public long VBRStreamSampleCount => VBRFrames * SampleCount;

        public int VBRAverageBitrate =>
            (int)(VBRBytes / (VBRStreamSampleCount / (double)SampleRate) * 8);

        public VBRInfo()
        {
        }
    }
}
