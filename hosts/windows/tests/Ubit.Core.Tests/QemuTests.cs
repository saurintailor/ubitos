using System.Text;
using Ubit.Core.Qemu;

namespace Ubit.Core.Tests;

public class QemuCommandTests
{
    private static readonly QemuOptions Options = new(
        @"C:\Program Files\qemu\share\edk2-x86_64-code.fd",
        @"C:\Users\me\AppData\Local\ubit\vm\efi-vars.fd",
        @"C:\Users\me\AppData\Local\ubit\vm\ubit.qcow2",
        @"C:\Users\me\AppData\Local\ubit\vm\agent.sock",
        @"C:\Users\me\AppData\Local\ubit\vm\qmp.sock");

    private static string After(IReadOnlyList<string> args, string flag, int nth = 0) =>
        args.Select((a, i) => (a, i)).Where(p => p.a == flag).Skip(nth).Select(p => args[p.i + 1]).First();

    [Fact]
    public void Prefers_whpx_and_falls_back_to_tcg()
    {
        var args = QemuCommand.Arguments(Options);
        Assert.Equal("whpx,kernel-irqchip=off", After(args, "-accel"));
        Assert.Equal("tcg", After(args, "-accel", 1));
    }

    [Fact]
    public void Agent_port_is_a_unix_socket_on_the_named_virtio_serial_port()
    {
        var args = QemuCommand.Arguments(Options);
        Assert.Contains($"socket,id=ubitagent,path={Options.AgentSocket},server=on,wait=off", args);
        Assert.Contains("virtserialport,chardev=ubitagent,name=org.ubit.agent.0", args);
        Assert.DoesNotContain(args, a => a.Contains("hostfwd", StringComparison.Ordinal));  // no network path to the agent
        Assert.DoesNotContain(args, a => a.Contains("tcp:", StringComparison.Ordinal));
    }

    [Fact]
    public void Asks_the_guest_for_a_console_session_and_shares_the_clipboard()
    {
        var args = QemuCommand.Arguments(Options);
        Assert.Equal("type=11,value=io.systemd.stub.kernel-cmdline-extra=ubit.console=1", After(args, "-smbios"));
        Assert.Contains("virtserialport,chardev=vdagent,name=com.redhat.spice.0", args);
    }

    [Fact]
    public void Commas_in_paths_are_doubled()
    {
        var args = QemuCommand.Arguments(Options with { Disk = @"C:\Users\Doe, Jane\ubit.qcow2" });
        Assert.Contains(@"if=virtio,format=qcow2,file=C:\Users\Doe,, Jane\ubit.qcow2", args);
    }
}

public class QmpClientTests
{
    [Fact]
    public async Task Negotiates_then_sends_the_command_and_skips_events()
    {
        var script = """
            {"QMP": {"version": {}, "capabilities": []}}
            {"return": {}}
            {"event": "POWERDOWN", "data": {}}
            {"return": {}}

            """;
        await using var stream = new ScriptedDuplex(script);
        await QmpClient.ExecuteAsync(stream, "system_powerdown");
        Assert.Equal(
            "{\"execute\":\"qmp_capabilities\"}\n{\"execute\":\"system_powerdown\"}\n",
            Encoding.UTF8.GetString(stream.Written.ToArray()));
    }

    [Fact]
    public async Task An_error_reply_throws()
    {
        var script = "{\"QMP\":{}}\n{\"error\":{\"class\":\"GenericError\",\"desc\":\"nope\"}}\n";
        await using var stream = new ScriptedDuplex(script);
        await Assert.ThrowsAsync<IOException>(() => QmpClient.ExecuteAsync(stream, "system_powerdown"));
    }

    /// <summary>Reads come from a fixed script; writes are recorded.</summary>
    private sealed class ScriptedDuplex(string script) : Stream
    {
        private readonly MemoryStream input = new(Encoding.UTF8.GetBytes(script));
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                Written.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public class QemuClipboardOptionTests
{
    [Fact]
    public void Clipboard_device_can_be_left_out()
    {
        var o = new QemuOptions("code", "vars", "disk", "agent", "qmp", Clipboard: false);
        Assert.DoesNotContain(QemuCommand.Arguments(o), a => a.Contains("vdagent", StringComparison.Ordinal));
        Assert.Contains(QemuCommand.Arguments(o with { Clipboard = true }), a => a.Contains("vdagent", StringComparison.Ordinal));
    }
}
