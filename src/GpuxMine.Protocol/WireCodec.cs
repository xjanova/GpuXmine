using System.Buffers.Binary;
using System.Text.Json;

namespace GpuxMine.Protocol;

/// <summary>
/// Frame layout on the wire, one WebSocket binary message per frame:
/// <code>
///   [1]  version
///   [4]  header length, big-endian
///   [n]  UTF-8 JSON TunnelHeader
///   [..] body bytes (may be empty)
/// </code>
/// Binary rather than JSON-with-base64 because every rendered image and video
/// comes back through this path; base64 would add a third to the size of the
/// single largest thing the network moves.
/// </summary>
public static class WireCodec
{
    public const byte Version = 1;

    /// <summary>Refuse anything larger rather than letting one peer exhaust the other's memory.</summary>
    public const int MaxFrameBytes = 192 * 1024 * 1024;

    private const int Prefix = 1 + 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(TunnelHeader header, ReadOnlySpan<byte> body = default)
    {
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var frame = new byte[Prefix + headerBytes.Length + body.Length];

        frame[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), headerBytes.Length);
        headerBytes.CopyTo(frame.AsSpan(Prefix));
        body.CopyTo(frame.AsSpan(Prefix + headerBytes.Length));

        return frame;
    }

    public static (TunnelHeader Header, byte[] Body) Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Prefix)
            throw new InvalidDataException($"Frame too short ({frame.Length} bytes)");
        if (frame[0] != Version)
            throw new InvalidDataException($"Unsupported frame version {frame[0]}");

        int headerLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(1, 4));
        if (headerLength < 0 || Prefix + headerLength > frame.Length)
            throw new InvalidDataException($"Header length {headerLength} does not fit in a {frame.Length}-byte frame");

        TunnelHeader header = JsonSerializer.Deserialize<TunnelHeader>(frame.Slice(Prefix, headerLength), JsonOptions)
            ?? throw new InvalidDataException("Frame header deserialised to null");

        byte[] body = frame[(Prefix + headerLength)..].ToArray();
        return (header, body);
    }
}
