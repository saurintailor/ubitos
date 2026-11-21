using System.Buffers.Binary;
using System.Text.Json;

namespace Ubit.Core.Protocol;

public sealed class ProtocolException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of the schema's error codes, e.g. bad_message.</summary>
    public string Code { get; } = code;
}

/// <summary>Frames: 4-byte big-endian length, then a UTF-8 JSON body of at most 1 MiB.</summary>
public static class ProtocolCodec
{
    /// <summary>Serializer settings shared by every message (see <see cref="ProtocolJsonContext"/>).</summary>
    public static JsonSerializerOptions JsonOptions => ProtocolJsonContext.Default.Options;

    public static byte[] Encode(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.Message);
        if (body.Length > ProtocolInfo.MaxFrameLength)
        {
            throw new ProtocolException("frame_too_large", $"frame of {body.Length} bytes is over the limit");
        }
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Parses and validates one frame body.</summary>
    public static Message Decode(ReadOnlySpan<byte> body)
    {
        Message? message;
        try
        {
            message = JsonSerializer.Deserialize(body, ProtocolJsonContext.Default.Message);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new ProtocolException("bad_message", $"malformed message: {e.Message}", e);
        }
        if (message is null)
        {
            throw new ProtocolException("bad_message", "empty message");
        }
        Validate(message);
        return message;
    }

    /// <summary>Rules JSON parsing alone cannot express (same as the Rust and Swift sides).</summary>
    public static void Validate(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.V != ProtocolInfo.Version)
        {
            throw new ProtocolException("unsupported_version", $"protocol version {message.V} is not supported");
        }
        switch (message)
        {
            case HostInventory inv when inv.Apps.Count > ProtocolInfo.MaxApps:
                throw new ProtocolException("bad_message", $"more than {ProtocolInfo.MaxApps} apps");
            case ScoreResult r when r.Score is < 0 or > 100:
                throw new ProtocolException("bad_message", "score must be between 0 and 100");
            case ScoreResult r when r.UnknownApps.Count > ProtocolInfo.MaxUnknownApps:
                throw new ProtocolException("bad_message", "too many unknown apps");
        }
    }

    public static async Task WriteAsync(Stream stream, Message message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(Encode(message), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null on a clean end of stream between frames.</summary>
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var got = await FillAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }
        if (got < 4)
        {
            throw new ProtocolException("internal", "connection closed mid-frame");
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > ProtocolInfo.MaxFrameLength)
        {
            throw new ProtocolException("frame_too_large", $"frame of {length} bytes is over the limit");
        }
        var body = new byte[length];
        if (await FillAsync(stream, body, ct).ConfigureAwait(false) < body.Length)
        {
            throw new ProtocolException("internal", "connection closed mid-frame");
        }
        return Decode(body);
    }

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            filled += n;
        }
        return filled;
    }
}
