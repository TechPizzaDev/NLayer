
using System;
using System.Runtime.InteropServices;

namespace NLayer.NAudioSupport
{
    public class Mp3FrameDecompressor : NAudio.Wave.IMp3FrameDecompressor
    {
        private MpegFrameDecoder _decoder;
        private Mp3FrameWrapper _frame;

        public Mp3FrameDecompressor(NAudio.Wave.WaveFormat waveFormat)
        {
            // we assume waveFormat was calculated from the first frame already
            OutputFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(
                waveFormat.SampleRate, waveFormat.Channels);

            _decoder = new MpegFrameDecoder();
            _frame = new Mp3FrameWrapper();
        }

        public int DecompressFrame(NAudio.Wave.Mp3Frame frame, byte[] buffer, int offset)
        {
            _frame.WrappedFrame = frame;

            var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset));
            return _decoder.DecodeFrame(_frame, dst) * sizeof(float);
        }

        public NAudio.Wave.WaveFormat OutputFormat { get; private set; }

        public void SetEQ(float[] eq)
        {
            _decoder.SetEQ(eq);
        }

        public StereoMode StereoMode
        {
            get => _decoder.StereoMode;
            set => _decoder.StereoMode = value;
        }

        public void Reset()
        {
            _decoder.Reset();
        }

        public void Dispose()
        {
            // no-op, since we don't have anything to do here...
        }
    }
}
