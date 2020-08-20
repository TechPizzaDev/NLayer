using System;
using System.IO;
using NAudio.Wave;

namespace NLayer.NAudioSupport
{
    public class ManagedMpegStream : WaveStream, IDisposable
    {
        private MpegFile _fileDecoder;
        private WaveFormat _waveFormat;

        public override WaveFormat WaveFormat => _waveFormat;

        public override long Length => _fileDecoder.Length ?? throw new IOException("The stream is not seekable.");

        public override long Position
        {
            get => _fileDecoder.Position;
            set => _fileDecoder.Position = value;
        }

        public StereoMode StereoMode
        {
            get => _fileDecoder.StereoMode;
            set => _fileDecoder.StereoMode = value;
        }

        public ManagedMpegStream(string fileName)
            : this(File.OpenRead(fileName), false)
        {

        }

        public ManagedMpegStream(Stream source)
            : this(source, true)
        {
        }

        public ManagedMpegStream(Stream source, bool leaveOpen)
        {
            _fileDecoder = new MpegFile(source, leaveOpen);
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_fileDecoder.SampleRate, _fileDecoder.Channels);
        }

        public void SetEQ(float[] eq)
        {
            _fileDecoder.SetEQ(eq);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _fileDecoder.ReadSamples(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (_fileDecoder != null)
            {
                _fileDecoder.Dispose();
                _fileDecoder = null;
            }
            base.Dispose(disposing);
        }
    }
}
