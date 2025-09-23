using System.Text;

namespace ElectricalImpedanceTomography.Helpers;

internal static class MotionJpegAviWriter
{
    private const uint AviIfKeyFrame = 0x10;
    private const uint AviMainHeaderHasIndex = 0x10;
    private const uint AviMainHeaderIsInterleaved = 0x100;

    public static void Write(Stream stream,
                             IReadOnlyList<byte[]> frames,
                             int width,
                             int height,
                             int framesPerSecond)
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

        int maxFrameSize = frames.Max(f => f.Length);
        if (maxFrameSize == 0)
            throw new ArgumentException("Frame data was empty.", nameof(frames));

        uint microSecPerFrame = (uint)Math.Round(1_000_000d / framesPerSecond);
        uint bytesPerSecond = (uint)(maxFrameSize * framesPerSecond);
        uint suggestedBufferSize = (uint)maxFrameSize;
        uint totalFrames = (uint)frames.Count;

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        long riffStart = writer.BaseStream.Position;
        WriteFourCC(writer, "RIFF");
        long riffSizePos = writer.BaseStream.Position;
        writer.Write(0);
        WriteFourCC(writer, "AVI ");

        long hdrlListStart = writer.BaseStream.Position;
        WriteFourCC(writer, "LIST");
        long hdrlSizePos = writer.BaseStream.Position;
        writer.Write(0);
        WriteFourCC(writer, "hdrl");

        WriteAviHeader(writer,
                       microSecPerFrame,
                       bytesPerSecond,
                       suggestedBufferSize,
                       totalFrames,
                       (uint)width,
                       (uint)height,
                       (uint)framesPerSecond,
                       (uint)maxFrameSize);

        long hdrlListEnd = writer.BaseStream.Position;
        UpdateChunkSize(writer, hdrlSizePos, hdrlListEnd);

        long moviListStart = writer.BaseStream.Position;
        WriteFourCC(writer, "LIST");
        long moviSizePos = writer.BaseStream.Position;
        writer.Write(0);
        WriteFourCC(writer, "movi");
        long moviDataStart = writer.BaseStream.Position;

        var indexEntries = new List<AviIndexEntry>(frames.Count);

        foreach (var frame in frames)
        {
            long chunkStart = writer.BaseStream.Position;
            WriteFourCC(writer, "00dc");
            writer.Write(frame.Length);
            writer.Write(frame);
            if ((frame.Length & 1) == 1)
                writer.Write((byte)0);

            uint offset = (uint)(chunkStart - moviDataStart);
            indexEntries.Add(new AviIndexEntry(ToFourCC("00dc"), AviIfKeyFrame, offset, (uint)frame.Length));
        }

        long moviListEnd = writer.BaseStream.Position;
        UpdateChunkSize(writer, moviSizePos, moviListEnd);

        WriteFourCC(writer, "idx1");
        writer.Write(indexEntries.Count * 16);
        foreach (var entry in indexEntries)
        {
            writer.Write(entry.ChunkId);
            writer.Write(entry.Flags);
            writer.Write(entry.Offset);
            writer.Write(entry.Size);
        }

        long fileEnd = writer.BaseStream.Position;
        UpdateChunkSize(writer, riffSizePos, fileEnd);
        writer.BaseStream.Seek(fileEnd, SeekOrigin.Begin);
    }

    private static void WriteAviHeader(BinaryWriter writer,
                                       uint microSecPerFrame,
                                       uint bytesPerSecond,
                                       uint suggestedBufferSize,
                                       uint totalFrames,
                                       uint width,
                                       uint height,
                                       uint framesPerSecond,
                                       uint maxFrameSize)
    {
        WriteFourCC(writer, "avih");
        writer.Write(56);
        writer.Write(microSecPerFrame);
        writer.Write(bytesPerSecond);
        writer.Write(0u);
        writer.Write(AviMainHeaderHasIndex | AviMainHeaderIsInterleaved);
        writer.Write(totalFrames);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(suggestedBufferSize);
        writer.Write(width);
        writer.Write(height);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        long strlListStart = writer.BaseStream.Position;
        WriteFourCC(writer, "LIST");
        long strlSizePos = writer.BaseStream.Position;
        writer.Write(0);
        WriteFourCC(writer, "strl");

        WriteFourCC(writer, "strh");
        writer.Write(56);
        writer.Write(ToFourCC("vids"));
        writer.Write(ToFourCC("MJPG"));
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(framesPerSecond);
        writer.Write(0u);
        writer.Write(totalFrames);
        writer.Write(suggestedBufferSize);
        writer.Write(uint.MaxValue);
        writer.Write(0u);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)width);
        writer.Write((short)height);

        WriteFourCC(writer, "strf");
        writer.Write(40);
        writer.Write(40u);
        writer.Write((int)width);
        writer.Write((int)height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(ToFourCC("MJPG"));
        writer.Write(maxFrameSize);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        long strlListEnd = writer.BaseStream.Position;
        UpdateChunkSize(writer, strlSizePos, strlListEnd);
    }

    private static void WriteFourCC(BinaryWriter writer, string fourCc)
    {
        var bytes = Encoding.ASCII.GetBytes(fourCc);
        if (bytes.Length != 4)
            throw new ArgumentException("FourCC codes must be exactly four ASCII characters.", nameof(fourCc));
        writer.Write(bytes);
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
}
