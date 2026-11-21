using System.Text;
using System.Text.Json;

namespace Ubit.Core.Qemu;

/// <summary>
/// Minimal QEMU Machine Protocol client: one JSON object per line. Used only to ask
/// the guest to power down, like pressing its power button.
/// </summary>
public static class QmpClient
{
    /// <summary>Negotiates capabilities, then sends <paramref name="command"/> and waits for its reply.</summary>
    public static async Task ExecuteAsync(Stream stream, string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await ExpectAsync(reader, "QMP", ct).ConfigureAwait(false);  // greeting
        await SendAsync(stream, "qmp_capabilities", ct).ConfigureAwait(false);
        await ExpectAsync(reader, "return", ct).ConfigureAwait(false);
        await SendAsync(stream, command, ct).ConfigureAwait(false);
        await ExpectAsync(reader, "return", ct).ConfigureAwait(false);
    }

    private static async Task SendAsync(Stream stream, string command, CancellationToken ct)
    {
        var line = Encoding.UTF8.GetBytes($"{{\"execute\":\"{command}\"}}\n");
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads lines until one has <paramref name="key"/>; skips events; throws on "error".</summary>
    private static async Task ExpectAsync(StreamReader reader, string key, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("QEMU closed the control connection");
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                throw new IOException($"QEMU refused the command: {error}");
            }
            if (doc.RootElement.TryGetProperty(key, out _))
            {
                return;
            }
        }
    }
}
