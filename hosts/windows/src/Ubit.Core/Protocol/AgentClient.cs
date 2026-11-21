using System.Net.Sockets;
using Ubit.Core.Transport;

namespace Ubit.Core.Protocol;

/// <summary>
/// The host side of one conversation with ubit-agent, over any stream:
/// a Hyper-V socket to the guest's vsock port 7710 (Windows Pro/Enterprise), or the
/// Unix socket QEMU serves for the agent's virtio-serial port (Windows Home).
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private readonly Stream stream;

    public AgentClient(Stream stream)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>Hyper-V: retries until the agent answers or <paramref name="timeout"/> passes.</summary>
    public static Task<AgentClient> ConnectHyperVAsync(Guid vmId, TimeSpan timeout, CancellationToken ct = default) =>
        ConnectWithRetryAsync(
            () => new Socket(HvSocketEndPoint.HyperV, SocketType.Stream, HvSocketEndPoint.RawProtocol),
            HvSocketEndPoint.ForVsockPort(vmId, ProtocolInfo.AgentVsockPort),
            timeout, ct);

    /// <summary>QEMU: connects to the agent port's Unix socket, retrying until <paramref name="timeout"/>.</summary>
    public static Task<AgentClient> ConnectUnixAsync(string socketPath, TimeSpan timeout, CancellationToken ct = default) =>
        ConnectWithRetryAsync(
            () => new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
            new UnixDomainSocketEndPoint(socketPath),
            timeout, ct);

    private static async Task<AgentClient> ConnectWithRetryAsync(
        Func<Socket> create, System.Net.EndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var socket = create();
            try
            {
                await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                return new AgentClient(new NetworkStream(socket, ownsSocket: true));
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                socket.Dispose();
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Says hello and waits for the reply. Over a serial port nothing tells the agent
    /// that an earlier host went away, so a hello always starts the session over.
    /// </summary>
    public async Task<HelloAck> HandshakeAsync(string shellVersion, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await ProtocolCodec.WriteAsync(stream, new Hello(HostOs.Windows, shellVersion), ct).ConfigureAwait(false);
        return await ExpectAsync<HelloAck>(timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Says hello until the agent answers: over virtio-serial the socket accepts before
    /// the guest has booted, so the first hello may wait. Each try waits <paramref name="perTry"/>.
    /// </summary>
    public async Task<HelloAck> HandshakeWithRetryAsync(string shellVersion, TimeSpan perTry, TimeSpan total, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + total;
        while (true)
        {
            try
            {
                return await HandshakeAsync(shellVersion, perTry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                // Not answered yet; say hello again.
            }
        }
    }

    public async Task<ScoreResult> SubmitAsync(HostInventory inventory, CancellationToken ct = default)
    {
        await ProtocolCodec.WriteAsync(stream, inventory, ct).ConfigureAwait(false);
        return await ExpectAsync<ScoreResult>(null, ct).ConfigureAwait(false);
    }

    private async Task<T> ExpectAsync<T>(TimeSpan? timeout, CancellationToken ct) where T : Message
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            limit.CancelAfter(t);
        }
        var message = await ProtocolCodec.ReadAsync(stream, limit.Token).ConfigureAwait(false);
        // A hello repeated while the guest was still booting earns a second hello_ack; skip it.
        while (message is HelloAck && typeof(T) != typeof(HelloAck))
        {
            message = await ProtocolCodec.ReadAsync(stream, limit.Token).ConfigureAwait(false);
        }
        return message switch
        {
            T expected => expected,
            ErrorMessage e => throw new ProtocolException(e.Code, $"agent error {e.Code}: {e.Message}"),
            null => throw new ProtocolException("internal", "the agent closed the connection"),
            _ => throw new ProtocolException("unexpected_message", $"expected {typeof(T).Name}, got {message.GetType().Name}"),
        };
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
