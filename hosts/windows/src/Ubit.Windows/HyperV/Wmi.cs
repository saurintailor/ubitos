using System.Management;
using System.Runtime.Versioning;

namespace Ubit.Windows.HyperV;

/// <summary>Small helpers around the Hyper-V WMI provider (root\virtualization\v2).</summary>
[SupportedOSPlatform("windows")]
internal static class Wmi
{
    private const uint JobStarted = 4096;
    private const ushort JobStateCompleted = 7;

    public static ManagementScope Connect()
    {
        var scope = new ManagementScope(@"\\.\root\virtualization\v2");
        scope.Connect();
        return scope;
    }

    public static ManagementObject? First(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().FirstOrDefault();
    }

    public static ManagementObject Single(ManagementScope scope, string wql) =>
        First(scope, wql) ?? throw new HyperVException($"Hyper-V WMI query returned nothing: {wql}");

    public static ManagementObject ManagementService(ManagementScope scope) =>
        Single(scope, "SELECT * FROM Msvm_VirtualSystemManagementService");

    /// <summary>
    /// The default settings object for a resource type, taken from its primordial pool
    /// (the documented pattern from Microsoft's Hyper-V WMI samples).
    /// </summary>
    public static ManagementObject DefaultSettings(ManagementScope scope, string resourceSubType)
    {
        using var pool = Single(scope,
            $"SELECT * FROM CIM_ResourcePool WHERE ResourceSubType = '{resourceSubType}' AND Primordial = True");
        using var capabilitiesList = pool.GetRelated("Msvm_AllocationCapabilities", "Msvm_ElementCapabilities",
            null, null, null, null, false, null);
        using var capabilities = capabilitiesList.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new HyperVException($"no allocation capabilities for {resourceSubType}");
        foreach (ManagementObject link in capabilities.GetRelationships("Msvm_SettingsDefineCapabilities"))
        {
            using (link)
            {
                if (Convert.ToUInt16(link["ValueRole"], System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    var settings = new ManagementObject((string)link["PartComponent"]);
                    settings.Get();
                    return settings;
                }
            }
        }
        throw new HyperVException($"no default settings for {resourceSubType}");
    }

    /// <summary>Invokes a method and waits for its job, if it started one.</summary>
    public static ManagementBaseObject Invoke(ManagementObject target, string method, Action<ManagementBaseObject> setInputs)
    {
        using var inputs = target.GetMethodParameters(method);
        setInputs(inputs);
        var outputs = target.InvokeMethod(method, inputs, null);
        var rc = Convert.ToUInt32(outputs["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
        if (rc == JobStarted)
        {
            WaitForJob((string)outputs["Job"], method);
        }
        else if (rc != 0)
        {
            throw new HyperVException($"{method} failed with code {rc}");
        }
        return outputs;
    }

    private static void WaitForJob(string jobPath, string method)
    {
        using var job = new ManagementObject(jobPath);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            job.Get();
            var state = Convert.ToUInt16(job["JobState"], System.Globalization.CultureInfo.InvariantCulture);
            if (state == JobStateCompleted)
            {
                return;
            }
            if (state > JobStateCompleted)
            {
                throw new HyperVException($"{method} failed: {job["ErrorDescription"]} (job state {state})");
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new HyperVException($"{method} timed out");
            }
            Thread.Sleep(250);
        }
    }

    public static string Text(ManagementObject settings) => settings.GetText(TextFormat.WmiDtd20);
}

public sealed class HyperVException(string message) : Exception(message);
