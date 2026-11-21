using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

namespace Ubit.Windows.HyperV;

public enum VmState
{
    Missing,
    Off,
    Running,
    Other,
}

public sealed record VmInfo(Guid Id, string Name, VmState State);

/// <summary>
/// Creates and controls the ubit VM through the Hyper-V WMI provider: Generation 2,
/// Secure Boot off (v0), enhanced session over hv_sock, one VHDX, Default Switch.
/// The caller must be an administrator or in the "Hyper-V Administrators" group.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HyperVManager
{
    public const string DefaultVmName = "ubit";

    private const string Gen2 = "Microsoft:Hyper-V:SubType:2";
    private const ushort EnhancedSessionOverHvSocket = 1;
    private const ushort StateRunning = 2;
    private const ushort StateOff = 3;

    private readonly ManagementScope scope = Wmi.Connect();

    public string VmName { get; init; } = DefaultVmName;

    public VmInfo GetVm()
    {
        using var vm = FindVm();
        return vm is null ? new VmInfo(Guid.Empty, VmName, VmState.Missing) : Describe(vm);
    }

    /// <summary>Creates the VM around <paramref name="vhdxPath"/> unless it already exists.</summary>
    public VmInfo EnsureVm(string vhdxPath, int memoryMb = 4096, int processors = 2)
    {
        using (var existing = FindVm())
        {
            if (existing is not null)
            {
                return Describe(existing);
            }
        }

        using var service = Wmi.ManagementService(scope);

        // 1. The system itself.
        using var settingsClass = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemSettingData"), null);
        using var settings = settingsClass.CreateInstance();
        settings["ElementName"] = VmName;
        settings["VirtualSystemSubType"] = Gen2;
        settings["SecureBootEnabled"] = false;
        settings["EnhancedSessionTransportType"] = EnhancedSessionOverHvSocket;
        var defined = Wmi.Invoke(service, "DefineSystem", p =>
        {
            p["SystemSettings"] = settings.GetText(TextFormat.WmiDtd20);
            p["ResourceSettings"] = null;
            p["ReferenceConfiguration"] = null;
        });
        using var vm = new ManagementObject((string)defined["ResultingSystem"]);
        vm.Get();
        using var vssd = SettingsOf(vm);

        // 2. CPU and memory (static memory keeps the guest's view simple in v0).
        using (var memory = Related(vssd, "Msvm_MemorySettingData"))
        {
            memory["VirtualQuantity"] = (ulong)memoryMb;
            memory["Reservation"] = (ulong)memoryMb;
            memory["Limit"] = (ulong)memoryMb;
            memory["DynamicMemoryEnabled"] = false;
            Modify(service, memory);
        }
        using (var cpu = Related(vssd, "Msvm_ProcessorSettingData"))
        {
            cpu["VirtualQuantity"] = (ulong)processors;
            Modify(service, cpu);
        }

        // 3. SCSI controller -> disk drive -> the VHDX.
        var controller = Add(service, vssd, "Microsoft:Hyper-V:Synthetic SCSI Controller", _ => { });
        var drive = Add(service, vssd, "Microsoft:Hyper-V:Synthetic Disk Drive", d =>
        {
            d["Parent"] = controller;
            d["AddressOnParent"] = "0";
        });
        Add(service, vssd, "Microsoft:Hyper-V:Virtual Hard Disk", d =>
        {
            d["Parent"] = drive;
            d["HostResource"] = new[] { vhdxPath };
        });

        // 4. Network adapter on the built-in NAT switch.
        var port = Add(service, vssd, "Microsoft:Hyper-V:Synthetic Ethernet Port", p =>
        {
            p["ElementName"] = "Network Adapter";
            p["VirtualSystemIdentifiers"] = new[] { Guid.NewGuid().ToString("B", CultureInfo.InvariantCulture) };
        });
        using var defaultSwitch = Wmi.First(scope, "SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = 'Default Switch'");
        if (defaultSwitch is not null)
        {
            Add(service, vssd, "Microsoft:Hyper-V:Ethernet Connection", c =>
            {
                c["Parent"] = port;
                c["HostResource"] = new[] { defaultSwitch.Path.Path };
            });
        }

        return Describe(vm);
    }

    /// <summary>Grows a VHDX (the guest grows its root partition on next boot).</summary>
    public void ResizeDisk(string vhdxPath, ulong bytes)
    {
        using var images = Wmi.Single(scope, "SELECT * FROM Msvm_ImageManagementService");
        Wmi.Invoke(images, "ResizeVirtualHardDisk", p =>
        {
            p["Path"] = vhdxPath;
            p["MaxInternalSize"] = bytes;
        });
    }

    public void Start() => RequestState(StateRunning);

    /// <summary>Hard power-off. Prefer <see cref="Shutdown"/>.</summary>
    public void TurnOff() => RequestState(StateOff);

    /// <summary>Asks the guest to shut down through the Hyper-V shutdown integration service.</summary>
    public void Shutdown()
    {
        using var vm = FindVm() ?? throw new HyperVException("the ubit VM does not exist");
        using var components = vm.GetRelated("Msvm_ShutdownComponent");
        using var shutdown = components.Cast<ManagementObject>().FirstOrDefault();
        if (shutdown is null)
        {
            TurnOff();
            return;
        }
        Wmi.Invoke(shutdown, "InitiateShutdown", p =>
        {
            p["Force"] = false;
            p["Reason"] = "ubit: shut down from the ubit app";
        });
    }

    /// <summary>Deletes the VM definition. The VHDX file is left in place.</summary>
    public void Delete()
    {
        using var vm = FindVm();
        if (vm is null)
        {
            return;
        }
        using var service = Wmi.ManagementService(scope);
        Wmi.Invoke(service, "DestroySystem", p => p["AffectedSystem"] = vm.Path.Path);
    }

    private void RequestState(ushort state)
    {
        using var vm = FindVm() ?? throw new HyperVException("the ubit VM does not exist");
        Wmi.Invoke(vm, "RequestStateChange", p => p["RequestedState"] = state);
    }

    private ManagementObject? FindVm() => Wmi.First(scope,
        $"SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine' AND ElementName = '{VmName.Replace("'", "''", StringComparison.Ordinal)}'");

    private static VmInfo Describe(ManagementObject vm)
    {
        var state = Convert.ToUInt16(vm["EnabledState"], CultureInfo.InvariantCulture) switch
        {
            StateRunning => VmState.Running,
            StateOff => VmState.Off,
            _ => VmState.Other,
        };
        return new VmInfo(Guid.Parse((string)vm["Name"]), (string)vm["ElementName"], state);
    }

    private static ManagementObject SettingsOf(ManagementObject vm)
    {
        using var all = vm.GetRelated("Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState",
            null, null, "SettingData", "ManagedElement", false, null);
        return all.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new HyperVException("could not read the VM's settings");
    }

    private static ManagementObject Related(ManagementObject vssd, string className)
    {
        using var all = vssd.GetRelated(className);
        return all.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new HyperVException($"VM settings have no {className}");
    }

    private static void Modify(ManagementObject service, ManagementObject resource) =>
        Wmi.Invoke(service, "ModifyResourceSettings", p => p["ResourceSettings"] = new[] { Wmi.Text(resource) });

    /// <summary>Adds a resource from its default template; returns the new resource's WMI path.</summary>
    private string Add(ManagementObject service, ManagementObject vssd, string subType, Action<ManagementObject> configure)
    {
        using var template = Wmi.DefaultSettings(scope, subType);
        configure(template);
        var result = Wmi.Invoke(service, "AddResourceSettings", p =>
        {
            p["AffectedConfiguration"] = vssd.Path.Path;
            p["ResourceSettings"] = new[] { Wmi.Text(template) };
        });
        var paths = (string[])result["ResultingResourceSettings"];
        return paths[0];
    }
}
