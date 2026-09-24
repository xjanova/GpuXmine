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
/// <remarks>
/// A frame may arrive split across several WebSocket fragments; the message
/// boundary, not the fragment, is the frame boundary. That is what lets a
/// reader look at the header before the body has arrived and decide what the
/// body is for — or that it is for nothing, and should not be kept.
/// </remarks>
public static class WireCodec
{
    public const byte Version = 1;

    /// <summary>
    /// The largest single frame a v0.1 peer accepts. A v0.1 agent enforces it
    /// on what the relay sends; a v0.1 relay ends the whole session over a
    /// reply larger than this, so a current agent talking to one answers 413
    /// instead of sending it.
    /// </summary>
    public const int MaxFrameBytes = 192 * 1024 * 1024;

    /// <summary>
    /// Ceiling on a frame's JSON header. Real headers are a few hundred bytes;
    /// the ceiling exists so a reader can hold the header in a fixed buffer
    /// and never has to grow one for a peer that claims otherwise.
    /// </summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>The most body one <see cref="FrameKind.ResponseChunk"/> may carry.</summary>
    /// <remarks>
    /// Small enough that a heartbeat or another request's answer is never
    /// stuck behind one frame for long on a home uplink, large enough that the
    /// per-frame header is noise.
    /// </remarks>
    public const int MaxChunkBytes = 1024 * 1024;

    /// <summary>Bytes before the JSON header: version plus length.</summary>
    public const int PrefixBytes = 1 + 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(TunnelHeader header, ReadOnlySpan<byte> body = default)
    {
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var frame = new byte[PrefixBytes + headerBytes.Length + body.Length];

        frame[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), headerBytes.Length);
        headerBytes.CopyTo(frame.AsSpan(PrefixBytes));
        body.CopyTo(frame.AsSpan(PrefixBytes + headerBytes.Length));

        return frame;
    }

    /// <summary>
    /// The prefix and header of a frame without its body, for a writer that
    /// sends the body as a separate fragment of the same message rather than
    /// copying it into one array first.
    /// </summary>
    public static byte[] EncodeHead(TunnelHeader header) => Encode(header, ReadOnlySpan<byte>.Empty);

    public static (TunnelHeader Header, byte[] Body) Decode(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPrefix(frame, out int headerLength, out string? problem))
            throw new InvalidDataException(problem);
        if (PrefixBytes + headerLength > frame.Length)
            throw new InvalidDataException($"Header length {headerLength} does not fit in a {frame.Length}-byte frame");

        TunnelHeader header = DecodeHeader(frame.Slice(PrefixBytes, headerLength));
        byte[] body = frame[(PrefixBytes + headerLength)..].ToArray();
        return (header, body);
    }

    /// <summary>
    /// Reads the version and header length from the first
    /// <see cref="PrefixBytes"/> of a frame.
    /// </summary>
    /// <returns>False, with the reason, when the frame cannot be read any further.</returns>
    public static bool TryReadPrefix(ReadOnlySpan<byte> prefix, out int headerLength, out string? problem)
    {
        headerLength = 0;
        if (prefix.Length < PrefixBytes)
        {
            problem = $"Frame too short ({prefix.Length} bytes)";
            return false;
        }
        if (prefix[0] != Version)
        {
            problem = $"Unsupported frame version {prefix[0]}";
            return false;
        }

        headerLength = BinaryPrimitives.ReadInt32BigEndian(prefix.Slice(1, 4));
        if (headerLength <= 0 || headerLength > MaxHeaderBytes)
        {
            problem = $"Header length {headerLength} is outside 1..{MaxHeaderBytes}";
            return false;
        }

        problem = null;
        return true;
    }

    public static TunnelHeader DecodeHeader(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize<TunnelHeader>(json, JsonOptions)
           ?? throw new InvalidDataException("Frame header deserialised to null");
}
