using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ElectricalImpedanceTomography.Helpers;

internal static class AviVideoWriter
{
    internal enum AviVideoCodec
    {
        UncompressedBgra32,
        MotionJpeg
    }

    private const uint AviIfKeyFrame = 0x10;
    private const uint AviMainHeaderHasIndex = 0x10;
    private const uint BiRgbCompression = 0u;
    private static readonly uint VideoChunkUncompressedFourCc = ToFourCC("00db");
    private static readonly uint VideoChunkCompressedFourCc = ToFourCC("00dc");
    private static readonly uint MotionJpegFourCc = ToFourCC("MJPG");

    public static void Write(Stream stream,
                             IReadOnlyList<byte[]> frames,
                             int width,
                             int height,
                             int framesPerSecond,
                             AviVideoCodec codec = AviVideoCodec.UncompressedBgra32)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required to create a video.", nameof(frames));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));

        int frameSize = frames[0].Length;
        if (frameSize == 0)
            throw new ArgumentException("Frame data was empty.", nameof(frames));
        if (codec == AviVideoCodec.UncompressedBgra32 && frames.Any(f => f.Length != frameSize))
            throw new ArgumentException("All frames must have the same size.", nameof(frames));

        using var writer = BeginWrite(stream, width, height, framesPerSecond, frameSize, codec);
        foreach (var frame in frames)
            writer.WriteFrame(frame);

        writer.Complete();
    }

    public static AviVideoStream BeginWrite(Stream stream,
                                            int width,
                                            int height,
                                            int framesPerSecond,
                                            int frameSize,
                                            AviVideoCodec codec = AviVideoCodec.UncompressedBgra32)
    {
        return new AviVideoStream(stream, width, height, framesPerSecond, frameSize, codec);
    }

    private static void WriteFourCC(BinaryWriter writer, string fourCc)
    {
        var bytes = Encoding.ASCII.GetBytes(fourCc);
        if (bytes.Length != 4)
            throw new ArgumentException("FourCC codes must be exactly four ASCII characters.", nameof(fourCc));
        writer.Write(bytes);
    }

    private static void WriteFourCC(BinaryWriter writer, uint fourCc)
    {
        writer.Write(fourCc);
    }

    private static uint ToFourCC(string fourCc)
    {
        var bytes = Encoding.ASCII.GetBytes(fourCc);
        if (bytes.Length != 4)
            throw new ArgumentException("FourCC codes must be exactly four ASCII characters.", nameof(fourCc));
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }

    private static void UpdateChunkSize(BinaryWriter writer, long sizePosition, long endPosition)
    {
        long current = writer.BaseStream.Position;
        writer.BaseStream.Seek(sizePosition, SeekOrigin.Begin);
        writer.Write((int)(endPosition - sizePosition - 4));
        writer.BaseStream.Seek(current, SeekOrigin.Begin);
    }

    private readonly struct AviIndexEntry
    {
        public AviIndexEntry(uint chunkId, uint flags, uint offset, uint size)
        {
            ChunkId = chunkId;
            Flags = flags;
            Offset = offset;
            Size = size;
        }

        public uint ChunkId { get; }
        public uint Flags { get; }
        public uint Offset { get; }
        public uint Size { get; }
    }

    internal sealed class AviVideoStream : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly List<AviIndexEntry> _indexEntries = new();
        private readonly long _riffSizePos;
        private readonly long _hdrlSizePos;
        private readonly long _moviSizePos;
        private readonly long _moviListStart;
        private readonly long _avihTotalFramesPos;
        private readonly long _avihMaxBytesPerSecondPos;
        private readonly long _avihSuggestedBufferSizePos;
        private readonly long _strhTotalFramesPos;
        private readonly long _strhSuggestedBufferSizePos;
        private readonly long _strfSizeImagePos;
        private readonly uint _framesPerSecond;
        private readonly uint _width;
        private readonly uint _height;
        private readonly uint _compressionFourCc;
        private readonly uint _chunkFourCc;
        private readonly ushort _bitsPerPixel;
        private readonly bool _allowVariableFrameSize;
        private readonly uint _microSecPerFrame;
        private uint _maxFrameSize;
        private bool _completed;
        private uint _frameCount;

        public AviVideoStream(Stream stream,
                              int width,
                              int height,
                              int framesPerSecond,
                              int frameSize,
                              AviVideoCodec codec)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (framesPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
            if (frameSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameSize));

            _width = (uint)width;
            _height = (uint)height;
            _framesPerSecond = (uint)framesPerSecond;
            _microSecPerFrame = (uint)Math.Round(1_000_000d / framesPerSecond);
            _maxFrameSize = (uint)frameSize;

            (_bitsPerPixel, _compressionFourCc, _chunkFourCc, _allowVariableFrameSize) = codec switch
            {
                AviVideoCodec.MotionJpeg => ((ushort)24, MotionJpegFourCc, VideoChunkCompressedFourCc, true),
                _ => ((ushort)32, BiRgbCompression, VideoChunkUncompressedFourCc, false)
            };

            _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            WriteFourCC(_writer, "RIFF");
            _riffSizePos = _writer.BaseStream.Position;
            _writer.Write(0);
            WriteFourCC(_writer, "AVI ");

            WriteFourCC(_writer, "LIST");
            _hdrlSizePos = _writer.BaseStream.Position;
            _writer.Write(0);
            WriteFourCC(_writer, "hdrl");

            WriteFourCC(_writer, "avih");
            _writer.Write(56);
            _writer.Write(_microSecPerFrame);
            _avihMaxBytesPerSecondPos = _writer.BaseStream.Position;
            _writer.Write(0u);
            _writer.Write(0u);
            _writer.Write(AviMainHeaderHasIndex);
            _avihTotalFramesPos = _writer.BaseStream.Position;
            _writer.Write(0u);
            _writer.Write(0u);
            _writer.Write(1u);
            _avihSuggestedBufferSizePos = _writer.BaseStream.Position;
            _writer.Write(_maxFrameSize);
            _writer.Write(_width);
            _writer.Write(_height);
            _writer.Write(0u);
            _writer.Write(0u);
            _writer.Write(0u);
            _writer.Write(0u);

            WriteFourCC(_writer, "LIST");
            long strlSizePos = _writer.BaseStream.Position;
            _writer.Write(0);
            WriteFourCC(_writer, "strl");

            WriteFourCC(_writer, "strh");
            _writer.Write(56);
            _writer.Write(ToFourCC("vids"));
            _writer.Write(_compressionFourCc);
            _writer.Write(0u);
            _writer.Write((ushort)0);
            _writer.Write((ushort)0);
            _writer.Write(0u);
            _writer.Write(1u);
            _writer.Write(_framesPerSecond);
            _writer.Write(0u);
            _strhTotalFramesPos = _writer.BaseStream.Position;
            _writer.Write(0u);
            _strhSuggestedBufferSizePos = _writer.BaseStream.Position;
            _writer.Write(_maxFrameSize);
            _writer.Write(uint.MaxValue);
            _writer.Write(0u);
            _writer.Write((short)0);
            _writer.Write((short)0);
            _writer.Write((short)width);
            _writer.Write((short)height);

            WriteFourCC(_writer, "strf");
            _writer.Write(40);
            _writer.Write(40u);
            _writer.Write(width);
            _writer.Write(height);
            _writer.Write((ushort)1);
            _writer.Write(_bitsPerPixel);
            _writer.Write(_compressionFourCc);
            _strfSizeImagePos = _writer.BaseStream.Position;
            _writer.Write(_maxFrameSize);
            _writer.Write(0u);
            _writer.Write(0u);
            _writer.Write(0u);
            _writer.Write(0u);

            long strlListEnd = _writer.BaseStream.Position;
            UpdateChunkSize(_writer, strlSizePos, strlListEnd);

            long hdrlListEnd = _writer.BaseStream.Position;
            UpdateChunkSize(_writer, _hdrlSizePos, hdrlListEnd);

            WriteFourCC(_writer, "LIST");
            _moviSizePos = _writer.BaseStream.Position;
            _writer.Write(0);
            WriteFourCC(_writer, "movi");
            _moviListStart = _writer.BaseStream.Position;
        }

        public void WriteFrame(ReadOnlySpan<byte> frameData)
        {
            if (_completed)
                throw new InvalidOperationException("Cannot write frames after the stream has been completed.");
            if (!_allowVariableFrameSize && (uint)frameData.Length != _maxFrameSize)
                throw new ArgumentException("Frame size did not match the expected size.", nameof(frameData));
            if (frameData.Length <= 0)
                throw new ArgumentException("Frame size did not match the expected size.", nameof(frameData));

            if ((uint)frameData.Length > _maxFrameSize)
                _maxFrameSize = (uint)frameData.Length;

            long chunkStart = _writer.BaseStream.Position;
            WriteFourCC(_writer, _chunkFourCc);
            _writer.Write(frameData.Length);
            _writer.Write(frameData);

            if ((frameData.Length & 1) == 1)
                _writer.Write((byte)0);

            uint offset = (uint)(chunkStart - _moviListStart - 4);
            _indexEntries.Add(new AviIndexEntry(_chunkFourCc, AviIfKeyFrame, offset, (uint)frameData.Length));
            _frameCount++;
        }

        public void Complete()
        {
            if (_completed)
                return;
            if (_frameCount == 0)
                throw new InvalidOperationException("At least one frame must be written before completing the stream.");

            UpdateHeaderForFrameSizes();

            long moviListEnd = _writer.BaseStream.Position;
            UpdateChunkSize(_writer, _moviSizePos, moviListEnd);

            WriteFourCC(_writer, "idx1");
            _writer.Write(_indexEntries.Count * 16);
            foreach (var entry in _indexEntries)
            {
                _writer.Write(entry.ChunkId);
                _writer.Write(entry.Flags);
                _writer.Write(entry.Offset);
                _writer.Write(entry.Size);
            }

            long fileEnd = _writer.BaseStream.Position;
            UpdateChunkSize(_writer, _riffSizePos, fileEnd);

            long current = _writer.BaseStream.Position;
            _writer.BaseStream.Seek(_avihTotalFramesPos, SeekOrigin.Begin);
            _writer.Write(_frameCount);
            _writer.BaseStream.Seek(_strhTotalFramesPos, SeekOrigin.Begin);
            _writer.Write(_frameCount);
            _writer.BaseStream.Seek(current, SeekOrigin.Begin);

            _writer.Flush();
            _completed = true;
        }

        public void Dispose()
        {
            if (!_completed && _frameCount > 0)
                Complete();

            _writer.Dispose();
        }

        private void UpdateHeaderForFrameSizes()
        {
            uint suggestedBufferSize = Math.Max(_maxFrameSize, 1u);
            ulong bytesPerSecond = suggestedBufferSize * (ulong)_framesPerSecond;
            uint clampedBytesPerSecond = bytesPerSecond > uint.MaxValue ? uint.MaxValue : (uint)bytesPerSecond;

            long current = _writer.BaseStream.Position;

            _writer.BaseStream.Seek(_avihMaxBytesPerSecondPos, SeekOrigin.Begin);
            _writer.Write(clampedBytesPerSecond);

            _writer.BaseStream.Seek(_avihSuggestedBufferSizePos, SeekOrigin.Begin);
            _writer.Write(suggestedBufferSize);

            _writer.BaseStream.Seek(_strhSuggestedBufferSizePos, SeekOrigin.Begin);
            _writer.Write(suggestedBufferSize);

            _writer.BaseStream.Seek(_strfSizeImagePos, SeekOrigin.Begin);
            _writer.Write(suggestedBufferSize);

            _writer.BaseStream.Seek(current, SeekOrigin.Begin);
        }
    }
}
