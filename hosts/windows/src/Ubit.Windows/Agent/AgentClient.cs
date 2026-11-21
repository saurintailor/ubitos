using System.Net.Sockets;
using System.Runtime.Versioning;
using Ubit.Core.Protocol;
using Ubit.Core.Transport;

namespace Ubit.Windows.Agent;

/// <summary>
/// The host side of one conversation with ubit-agent, over a Hyper-V socket to the
/// guest's vsock port 7710. Needs the service GUID registered once (setup-host.ps1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentClient : IAsyncDisposable
{
    private readonly NetworkStream stream;

    private AgentClient(Socket socket)
    {
        stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Retries every 2 seconds until the agent answers or <paramref name="timeout"/> passes.</summary>
    public static async Task<AgentClient> ConnectAsync(Guid vmId, TimeSpan timeout, CancellationToken ct = default)
    {
        var endpoint = HvSocketEndPoint.ForVsockPort(vmId, ProtocolInfo.AgentVsockPort);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var socket = new Socket(HvSocketEndPoint.HyperV, SocketType.Stream, HvSocketEndPoint.RawProtocol);
            try
            {
                await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                return new AgentClient(socket);
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                socket.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    public async Task<HelloAck> HandshakeAsync(string shellVersion, CancellationToken ct = default)
    {
        await ProtocolCodec.WriteAsync(stream, new Hello(HostOs.Windows, shellVersion), ct).ConfigureAwait(false);
        return await ExpectAsync<HelloAck>(ct).ConfigureAwait(false);
    }

    public async Task<ScoreResult> SubmitAsync(HostInventory inventory, CancellationToken ct = default)
    {
        await ProtocolCodec.WriteAsync(stream, inventory, ct).ConfigureAwait(false);
        return await ExpectAsync<ScoreResult>(ct).ConfigureAwait(false);
    }

    private async Task<T> ExpectAsync<T>(CancellationToken ct) where T : Message
    {
        var message = await ProtocolCodec.ReadAsync(stream, ct).ConfigureAwait(false);
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
