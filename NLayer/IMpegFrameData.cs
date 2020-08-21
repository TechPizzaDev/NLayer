
namespace NLayer
{
    /// <summary>
    /// Defines a way of representing MPEG frame data to the decoder.
    /// </summary>
    public interface IMpegFrameData
    {
        /// <summary>
        /// Provides sequential access to the bitstream in the frame (after the header and optional CRC).
        /// </summary>
        /// <param name="bitCount">The number of bits to read.</param>
        /// <returns>-1 if the end of the frame has been encountered, otherwise the bits requested.</returns>
        int ReadBits(int bitCount);

        /// <summary>
        /// Resets the frame to read it from the beginning.
        /// </summary>
        void Reset();
    }
}
