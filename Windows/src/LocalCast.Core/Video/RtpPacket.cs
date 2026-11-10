using System.Buffers.Binary;

namespace LocalCast.Core.Video;

public readonly record struct RtpPacket(byte[] Payload, uint Timestamp, bool Marker, int PayloadType, int WireLength);

public static class RtpPacketParser
{
    public static bool TryParse(ReadOnlySpan<byte> data, out RtpPacket packet)
    {
        packet = default;
        if (data.Length < 12 || (data[0] >> 6) != 2) return false;

        int csrcCount = data[0] & 0x0F;
        bool hasExtension = (data[0] & 0x10) != 0;
        bool hasPadding = (data[0] & 0x20) != 0;

        int offset = 12 + csrcCount * 4;
        if (data.Length < offset) return false;

        if (hasExtension)
        {
            if (data.Length < offset + 4) return false;
            int words = (data[offset + 2] << 8) | data[offset + 3];
            offset += 4 + words * 4;
        }

        int end = data.Length;
        if (hasPadding && end > offset) end -= data[end - 1];
        if (end <= offset) return false;

        packet = new RtpPacket(
            data.Slice(offset, end - offset).ToArray(),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            (data[1] & 0x80) != 0,
            data[1] & 0x7F,
            data.Length);
        return true;
    }
}
