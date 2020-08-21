﻿using System;
using System.Buffers.Binary;

namespace NLayer.Decoder
{
    internal class MpegFrame : FrameBase, IMpegFrame
    {
        private static readonly int[][][] _bitRateTable =
        {
            new int[][]
            {
                new int[] { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
                new int[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
                new int[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }
            },
            new int[][]
            {
                new int[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
                new int[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
                new int[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }
            },
        };

        // base: 2xIntPtr, 1x8, 1x4
        // total: 3xIntPtr, 4x8, 4x4
        // Long Mode: 72 bytes per instance
        // Prot Mode: 60 bytes per instance

        // IntPtr
        public MpegFrame? Next;

        // 4
        public int Number;

        // 4
        private int _syncBits;

        // 8
        private int _readOffset, _bitsRead;

        // 8
        private ulong _bitBucket = 0UL;

        // 8
        private long _offset;

        // 4
        private bool _isMuted;

        private MpegFrame()
        {
        }

        protected override int ValidateFrameHeader()
        {
            // TrySync has already validated version, layer, bitrate, and samplerate

            // if layer2, we have to verify the channel mode is valid for the bitrate selected
            if (Layer == MpegLayer.LayerII)
            {
                switch (BitRate)
                {
                    case 32000:
                    case 48000:
                    case 56000:
                    case 80000:
                        // don't allow anything except mono
                        if (ChannelMode != MpegChannelMode.Mono)
                            return -1;
                        break;

                    case 224000:
                    case 256000:
                    case 320000:
                    case 384000:
                        // don't allow mono
                        if (ChannelMode == MpegChannelMode.Mono)
                            return -1;
                        break;
                }
            }

            // calculate the frame's length
            int frameSize;
            if (BitRateIndex > 0)
            {
                if (Layer == MpegLayer.LayerI)
                {
                    frameSize = (12 * BitRate / SampleRate + Padding) * 4;
                }
                else
                {
                    frameSize = 144 * BitRate / SampleRate + Padding;
                }
            }
            else
            {
                // "free" frame...  we have to calculate it later

                // we know the frame will be at least this big...
                frameSize = _readOffset + GetSideDataSize() + Padding;
            }

            // now check the crc if one is present
            if (HasCrc)
            {
                // prep for CRC reading
                _readOffset = 4 + (HasCrc ? 2 : 0);

                // check the CRC
                if (!ValidateCRC())
                {
                    // mute this frame
                    _isMuted = true;
                    return 6;   // header + crc...  force the reader to re-sync
                }
            }

            // prep for reading
            Reset();

            // finally, let our caller know how big this frame is (including the sync header)
            return frameSize;
        }

        public int GetSideDataSize()
        {
            switch (Layer)
            {
                case MpegLayer.LayerI:
                    if (ChannelMode == MpegChannelMode.Mono)
                    {
                        // mono
                        return 16;
                    }
                    else if (ChannelMode == MpegChannelMode.Stereo || ChannelMode == MpegChannelMode.DualChannel)
                    {
                        // full stereo / dual channel
                        return 32;
                    }
                    else
                    {
                        // joint stereo...  ugh...
                        switch (ChannelModeExtension)
                        {
                            case 0:
                                return 18;
                            case 1:
                                return 20;
                            case 2:
                                return 22;
                            case 3:
                                return 24;
                        }
                    }
                    break;

                case MpegLayer.LayerII:
                    return 0;

                case MpegLayer.LayerIII:
                    if (ChannelMode == MpegChannelMode.Mono && Version >= MpegVersion.Version2)
                    {
                        return 9;
                    }
                    else if (ChannelMode != MpegChannelMode.Mono && Version < MpegVersion.Version2)
                    {
                        return 32;
                    }
                    else
                    {
                        return 17;
                    }
            }

            return 0;
        }

        private bool ValidateCRC()
        {
            uint crc = 0xFFFFU;

            // process the common bits...
            UpdateCRC(_syncBits, 16, ref crc);

            var apply = false;
            switch (Layer)
            {
                case MpegLayer.LayerI:
                    apply = Layer1Decoder.GetCRC(this, ref crc);
                    break;
                case MpegLayer.LayerII:
                    apply = Layer2Decoder.GetCRC(this, ref crc);
                    break;
                case MpegLayer.LayerIII:
                    apply = Layer3Decoder.GetCRC(this, ref crc);
                    break;
            }

            if (apply)
            {
                var checkCrc = ReadByte(4) << 8 | ReadByte(5);
                return checkCrc == crc;
            }

            return true;
        }

        public static void UpdateCRC(int data, int length, ref uint crc)
        {
            var masking = 1U << length;
            while ((masking >>= 1) != 0)
            {
                var carry = crc & 0x8000;
                crc <<= 1;
                if ((carry == 0) ^ ((data & masking) == 0))
                    crc ^= 0x8005;
            }
            crc &= 0xFFFF;
        }

        public VBRInfo? ParseVBR()
        {
            Span<byte> buf = stackalloc byte[4];

            // Xing first
            int offset;
            if (Version == MpegVersion.Version1 && ChannelMode != MpegChannelMode.Mono)
                offset = 32 + 4;
            else if (Version > MpegVersion.Version1 && ChannelMode == MpegChannelMode.Mono)
                offset = 9 + 4;
            else
                offset = 17 + 4;

            if (Read(offset, buf) != 4)
                return null;

            if (buf[0] == 'X' && buf[1] == 'i' && buf[2] == 'n' && buf[3] == 'g' ||
                buf[0] == 'I' && buf[1] == 'n' && buf[2] == 'f' && buf[3] == 'o')
                return ParseXing(offset + 4);

            // then VBRI (kinda rare)
            if (Read(36, buf) != 4)
                return null;

            if (buf[0] == 'V' && buf[1] == 'B' && buf[2] == 'R' && buf[3] == 'I')
                return ParseVBRI();

            return null;
        }

        private VBRInfo? ParseXing(int offset)
        {
            var info = new VBRInfo();
            info.Channels = Channels;
            info.SampleRate = SampleRate;
            info.SampleCount = SampleCount;

            Span<byte> buf = stackalloc byte[100];
            if (Read(offset, buf.Slice(0, 4)) != 4)
                return null;
            offset += 4;

            int flags = BinaryPrimitives.ReadInt32BigEndian(buf);

            // frame count
            if ((flags & 0x1) != 0)
            {
                if (Read(offset, buf.Slice(0, 4)) != 4)
                    return null;
                offset += 4;
                info.VBRFrames = BinaryPrimitives.ReadInt32BigEndian(buf);
            }

            // byte count
            if ((flags & 0x2) != 0)
            {
                if (Read(offset, buf.Slice(0, 4)) != 4)
                    return null;
                offset += 4;
                info.VBRBytes = BinaryPrimitives.ReadInt32BigEndian(buf);
            }

            // TOC
            if ((flags & 0x4) != 0)
            {
                // we're not using the TOC, so just discard it
                if (Read(offset, buf) != 100)
                    return null;
                offset += 100;
            }

            // scale
            if ((flags & 0x8) != 0)
            {
                if (Read(offset, buf.Slice(0, 4)) != 4)
                    return null;
                offset += 4;
                info.VBRQuality = BinaryPrimitives.ReadInt32BigEndian(buf);
            }

            //// now look for a LAME header (note: if it isn't found, it doesn't fail the VBR parse)
            //do
            //{
            //    if (Read(offset, buf, 0, 20) != 20) break;
            //    offset += 20;

            //    // LAME tag revision: only 0 and 1 are valid
            //    if ((buf[9] & 0xF0) > 0x10) break;

            //    // VBR mode: 0-6, 8 & 9 are valid
            //    var mode = buf[9] & 0xF;
            //    if (mode == 7 || mode > 9) break;

            //    // Lowpass filter value
            //    var lowpass = buf[10] / 100.0;

            //    // Replay Gain
            //    var rgPeak = BitConverter.ToSingle(buf, 11);
            //    var rgGainRadio = buf[15] << 8 | buf[16];
            //    var rgGain = buf[17] << 8 | buf[18];
            //} while (false);

            return info;
        }

        private VBRInfo? ParseVBRI()
        {
            var info = new VBRInfo();
            info.Channels = Channels;
            info.SampleRate = SampleRate;
            info.SampleCount = SampleCount;

            // VBRI is "fixed" size...  Yay. :)
            var buf = new byte[26];
            if (Read(36, buf) != 26)
                return null;

            // Version
            int version = buf[4] << 8 | buf[5];

            // Delay
            info.VBRDelay = buf[6] << 8 | buf[7];

            // Quality
            info.VBRQuality = buf[8] << 8 | buf[9];

            // Bytes
            info.VBRBytes = buf[10] << 24 | buf[11] << 16 | buf[12] << 8 | buf[13];

            // Frames
            info.VBRFrames = buf[14] << 24 | buf[15] << 16 | buf[16] << 8 | buf[17];

            // TOC
            // entries
            int tocEntries = buf[18] << 8 | buf[19];
            int tocScale = buf[20] << 8 | buf[21];
            int tocEntrySize = buf[22] << 8 | buf[23];
            int tocFramesPerEntry = buf[24] << 8 | buf[25];
            int tocSize = tocEntries * tocEntrySize;

            var toc = new byte[tocSize];
            if (Read(62, toc) != tocSize)
                return null;

            return info;
        }

        public int FrameLength => Length;

        // the order is backwards, and "0" is invalid
        public MpegLayer Layer => (MpegLayer)((4 - ((_syncBits >> 17) & 3)) % 4);

        public bool HasCrc => (_syncBits & 0x10000) == 0;

        public int BitRateIndex => (_syncBits >> 12) & 0xF;

        public int SampleRateIndex => (_syncBits >> 10) & 0x3;

        private int Padding => (_syncBits >> 9) & 0x1;

        public MpegChannelMode ChannelMode => (MpegChannelMode)((_syncBits >> 6) & 0x3);

        public int ChannelModeExtension => (_syncBits >> 4) & 0x3;

        public int Channels => (ChannelMode == MpegChannelMode.Mono ? 1 : 2);

        public bool IsCopyrighted => (_syncBits & 0x8) == 0x8;

        public bool IsOriginal => (_syncBits & 0x4) == 0x4;

        public int EmphasisMode => (_syncBits & 0x3);

        public bool IsCorrupted => _isMuted;

        public long SampleOffset
        {
            get => _offset;
            set => _offset = value;
        }

        public int SampleCount
        {
            get
            {
                if (Layer == MpegLayer.LayerI)
                    return 384;
                if (Layer == MpegLayer.LayerIII && Version > MpegVersion.Version1)
                    return 576;
                return 1152;
            }
        }


        public MpegVersion Version
        {
            get
            {
                return ((_syncBits >> 19) & 3) switch
                {
                    0 => MpegVersion.Version25,
                    2 => MpegVersion.Version2,
                    3 => MpegVersion.Version1,
                    _ => MpegVersion.Unknown,
                };
            }
        }

        public int BitRate
        {
            get
            {
                if (BitRateIndex > 0)
                {
                    return _bitRateTable[(int)Version / 10 - 1][(int)Layer - 1][BitRateIndex] * 1000;
                }
                else
                {
                    // bitrate is always an even multiple of 1000, so round
                    return ((((FrameLength * 8) * SampleRate) / SampleCount + 499) + 500) / 1000 * 1000;
                }
            }
        }

        public int SampleRate
        {
            get
            {
                var sr = SampleRateIndex switch
                {
                    0 => 44100,
                    1 => 48000,
                    2 => 32000,
                    _ => 0,
                };

                if (Version > MpegVersion.Version1)
                {
                    if (Version == MpegVersion.Version25)
                        sr /= 4;
                    else
                        sr /= 2;
                }
                return sr;
            }
        }

        public void Reset()
        {
            _readOffset = 4 + (HasCrc ? 2 : 0);
            _bitBucket = 0UL;
            _bitsRead = 0;
        }

        public int ReadBits(int bitCount)
        {
            if (bitCount < 1 || bitCount > 32)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (_isMuted)
                return 0;

            while (_bitsRead < bitCount)
            {
                int b = ReadByte(_readOffset);
                if (b == -1)
                    throw new System.IO.EndOfStreamException();

                _readOffset++;

                _bitBucket <<= 8;
                _bitBucket |= (byte)(b & 0xFF);
                _bitsRead += 8;
            }

            int tmp = (int)((_bitBucket >> (_bitsRead - bitCount)) & ((1UL << bitCount) - 1));
            _bitsRead -= bitCount;
            return tmp;
        }

        public static MpegFrame? TrySync(uint syncMark)
        {
            if ((syncMark & 0xFFE00000) == 0xFFE00000 && // frame sync
                (syncMark & 0x00180000) != 0x00080000 && // MPEG version != reserved
                (syncMark & 0x00060000) != 0x00000000 && // layer version != reserved
                (syncMark & 0x0000F000) != 0x0000F000 && // bitrate != bad
                (syncMark & 0x00000C00) != 0x00000C00)   // sample rate != reserved
            {
                // now check the stereo modes
                switch ((syncMark >> 4) & 0xF)
                {
                    case 0x0:   // stereo
                    case 0x4:   // joint stereo
                    case 0x5:   // "
                    case 0x6:   // "
                    case 0x7:   // "
                    case 0x8:   // dual channel
                    case 0xC:   // mono
                        return new MpegFrame { _syncBits = (int)syncMark };
                }
            }

            return null;
        }

#if DEBUG
        public override string ToString()
        {
            // version
            var sb = new System.Text.StringBuilder("MPEG");
            switch (Version)
            {
                case MpegVersion.Version1:
                    sb.Append("1");
                    break;
                case MpegVersion.Version2:
                    sb.Append("2");
                    break;
                case MpegVersion.Version25:
                    sb.Append("2.5");
                    break;
            }

            // layer
            sb.Append(" Layer ");
            switch (Layer)
            {
                case MpegLayer.LayerI:
                    sb.Append("I");
                    break;
                case MpegLayer.LayerII:
                    sb.Append("II");
                    break;
                case MpegLayer.LayerIII:
                    sb.Append("III");
                    break;
            }

            // bitrate
            sb.AppendFormat(" {0} kbps ", BitRate / 1000);

            // channel mode
            switch (ChannelMode)
            {
                case MpegChannelMode.Stereo:
                    sb.Append("Stereo");
                    break;
                case MpegChannelMode.JointStereo:
                    sb.Append("Joint Stereo");
                    switch (ChannelModeExtension)
                    {
                        case 1:
                            sb.Append(" (I)");
                            break;
                        case 2:
                            sb.Append(" (M/S)");
                            break;
                        case 3:
                            sb.Append(" (M/S,I)");
                            break;
                    }
                    break;
                case MpegChannelMode.DualChannel:
                    sb.Append("Dual Channel");
                    break;
                case MpegChannelMode.Mono:
                    sb.Append("Mono");
                    break;
            }

            // sample rate
            sb.AppendFormat(" {0} KHz", (float)SampleRate / 1000);

            var flagList = new System.Collections.Generic.List<string>();
            // protection
            if (HasCrc)
                flagList.Add("CRC");

            // copyright
            if (IsCopyrighted)
                flagList.Add("Copyright");

            // original
            if (IsOriginal)
                flagList.Add("Original");

            // emphasis
            switch (EmphasisMode)
            {
                case 1:
                    flagList.Add("50/15 ms");
                    break;
                case 2:
                    flagList.Add("Invalid Emphasis");
                    break;
                case 3:
                    flagList.Add("CCIT J.17");
                    break;
            }

            if (flagList.Count > 0)
            {
                sb.AppendFormat(" ({0})", string.Join(",", flagList.ToArray()));
            }

            return sb.ToString();
        }
#endif
    }
}
