using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Ubit.Core.Transport;

/// <summary>
/// A Hyper-V socket address (SOCKADDR_HV): family, reserved, VM id, service id. 36 bytes.
/// A Linux guest listening on AF_VSOCK port P is reached through the service id made
/// from Microsoft's vsock template GUID with P in its first field.
/// See https://learn.microsoft.com/virtualization/hyper-v-on-windows/user-guide/make-integration-service
/// </summary>
public sealed class HvSocketEndPoint : EndPoint
{
    /// <summary>AF_HYPERV.</summary>
    public const AddressFamily HyperV = (AddressFamily)34;
    /// <summary>HV_PROTOCOL_RAW.</summary>
    public const ProtocolType RawProtocol = (ProtocolType)1;
    public const int AddressSize = 36;

    /// <summary>00000000-facb-11e6-bd58-64006a7986d3: the Linux vsock service template.</summary>
    public static readonly Guid VsockTemplate = new("00000000-facb-11e6-bd58-64006a7986d3");

    public HvSocketEndPoint(Guid vmId, Guid serviceId)
    {
        VmId = vmId;
        ServiceId = serviceId;
    }

    public Guid VmId { get; }
    public Guid ServiceId { get; }

    public override AddressFamily AddressFamily => HyperV;

    /// <summary>The service id for vsock port <paramref name="port"/> in a Linux guest.</summary>
    public static Guid ServiceIdForVsockPort(uint port)
    {
        Span<byte> bytes = stackalloc byte[16];
        VsockTemplate.TryWriteBytes(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, port);  // Guid's first field (Data1) is little-endian
        return new Guid(bytes);
    }

    public static HvSocketEndPoint ForVsockPort(Guid vmId, uint port) => new(vmId, ServiceIdForVsockPort(port));

    /// <summary>The raw SOCKADDR_HV bytes (little-endian family, reserved, VM id, service id).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[AddressSize];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)HyperV);
        VmId.TryWriteBytes(bytes.AsSpan(4, 16));
        ServiceId.TryWriteBytes(bytes.AsSpan(20, 16));
        return bytes;
    }

    /// <remarks>Only Windows accepts AF_HYPERV in SocketAddress; elsewhere this throws.</remarks>
    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(HyperV, AddressSize);
        var bytes = ToBytes();
        for (var i = 2; i < AddressSize; i++)
        {
            address[i] = bytes[i];
        }
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        if (socketAddress.Family != HyperV || socketAddress.Size < AddressSize)
        {
            throw new ArgumentException("not a Hyper-V socket address", nameof(socketAddress));
        }
        var vm = new byte[16];
        var service = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            vm[i] = socketAddress[4 + i];
            service[i] = socketAddress[20 + i];
        }
        return new HvSocketEndPoint(new Guid(vm), new Guid(service));
    }

    public override string ToString() => $"hvsock://{VmId}/{ServiceId}";

    public override bool Equals(object? obj) => obj is HvSocketEndPoint o && o.VmId == VmId && o.ServiceId == ServiceId;

    public override int GetHashCode() => HashCode.Combine(VmId, ServiceId);
}
