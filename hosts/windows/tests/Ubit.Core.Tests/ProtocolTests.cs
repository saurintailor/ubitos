using System.Text;
using Ubit.Core.Inventory;
using Ubit.Core.Protocol;
using Ubit.Core.Transport;

namespace Ubit.Core.Tests;

public class ProtocolTests
{
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "protocol", "fixtures"));

    public static TheoryData<string> Valid() => Files("valid");

    public static TheoryData<string> Invalid() => Files("invalid");

    private static TheoryData<string> Files(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Fixtures, kind), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(f));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_fixtures_decode_and_round_trip(string name)
    {
        var message = ProtocolCodec.Decode(File.ReadAllBytes(Path.Combine(Fixtures, "valid", name)));
        var again = ProtocolCodec.Decode(ProtocolCodec.Encode(message).AsSpan(4));
        Assert.Equal(Normalize(message), Normalize(again));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_fixtures_are_rejected(string name)
    {
        Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(File.ReadAllBytes(Path.Combine(Fixtures, "invalid", name))));
    }

    [Fact]
    public void Encoded_messages_use_schema_field_names()
    {
        var json = Encoding.UTF8.GetString(ProtocolCodec.Encode(new Hello(HostOs.Windows, "0.1.0")).AsSpan(4));
        Assert.Contains("\"type\":\"hello\"", json, StringComparison.Ordinal);
        Assert.Contains("\"v\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"host_os\":\"windows\"", json, StringComparison.Ordinal);
        Assert.Contains("\"shell_version\":\"0.1.0\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_fixture_fields_are_read()
    {
        var inv = Assert.IsType<HostInventory>(ProtocolCodec.Decode(
            File.ReadAllBytes(Path.Combine(Fixtures, "valid", "host_inventory-windows.json"))));
        Assert.Equal("Google Chrome", inv.Apps[0].Name);
        Assert.Equal("ChromeHTML", inv.DefaultBrowser);
    }

    [Fact]
    public async Task Frames_round_trip_over_a_stream()
    {
        using var stream = new MemoryStream();
        await ProtocolCodec.WriteAsync(stream, new Ping(5));
        await ProtocolCodec.WriteAsync(stream, new Pong(6));
        stream.Position = 0;
        Assert.Equal(new Ping(5), await ProtocolCodec.ReadAsync(stream));
        Assert.Equal(new Pong(6), await ProtocolCodec.ReadAsync(stream));
        Assert.Null(await ProtocolCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Oversized_frames_are_refused()
    {
        using var stream = new MemoryStream([0x00, 0x20, 0x00, 0x00]);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => ProtocolCodec.ReadAsync(stream));
        Assert.Equal("frame_too_large", e.Code);
    }

    // Records holding lists compare by reference; compare their JSON instead.
    private static string Normalize(Message m) => Encoding.UTF8.GetString(ProtocolCodec.Encode(m));
}

public class HvSocketEndPointTests
{
    [Fact]
    public void Vsock_port_7710_maps_to_the_documented_service_id()
    {
        Assert.Equal(new Guid("00001e1e-facb-11e6-bd58-64006a7986d3"), HvSocketEndPoint.ServiceIdForVsockPort(7710));
        Assert.Equal(new Guid("00000ac9-facb-11e6-bd58-64006a7986d3"), HvSocketEndPoint.ServiceIdForVsockPort(2761));
    }

    [Fact]
    public void Bytes_follow_the_sockaddr_hv_layout()
    {
        var vm = Guid.NewGuid();
        var bytes = HvSocketEndPoint.ForVsockPort(vm, 7710).ToBytes();

        Assert.Equal(HvSocketEndPoint.AddressSize, bytes.Length);
        Assert.Equal(34, bytes[0] | (bytes[1] << 8));  // AF_HYPERV, little-endian
        Assert.Equal(0, bytes[2] | bytes[3]);          // reserved
        Assert.Equal(vm.ToByteArray(), bytes[4..20]);
        Assert.Equal(new byte[] { 0x1E, 0x1E, 0x00, 0x00 }, bytes[20..24]);  // Data1 = 0x00001e1e
    }

    [Fact]
    public void Socket_address_round_trips_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;  // AF_HYPERV SocketAddress exists only on Windows; covered by the windows CI job.
        }
        var endpoint = HvSocketEndPoint.ForVsockPort(Guid.NewGuid(), 7710);
        var address = endpoint.Serialize();
        Assert.Equal(HvSocketEndPoint.AddressSize, address.Size);
        Assert.Equal(endpoint, endpoint.Create(address));
    }
}

public class InstalledAppFilterTests
{
    [Fact]
    public void Drops_system_components_updates_and_children_and_merges_duplicates()
    {
        UninstallEntry E(string name, int? system = null, string? parent = null, string? release = null, string hive = "uninstall-hklm") =>
            new(name, hive, name, system, parent, release);

        var apps = InstalledAppFilter.Filter(
        [
            E("Google Chrome"),
            E("Google Chrome", hive: "uninstall-hkcu"),
            E("Microsoft Visual C++ 2015-2022 Redistributable (x64)", system: 1),
            E("Security Update for Microsoft Office (KB5002700) 64-Bit Edition"),
            E("Office 16 Click-to-Run Licensing Component", parent: "Office16"),
            E("Hotfix thing", release: "Hotfix"),
            new UninstallEntry("{GUID}", "uninstall-hklm", null, null, null, null),
            E("slack"),
        ]);

        Assert.Equal(["Google Chrome", "slack"], apps.Select(a => a.Name));
        Assert.Equal("uninstall-hklm", apps[0].Source);
    }
}
