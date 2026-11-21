using System.Runtime.Versioning;
using Microsoft.Win32;
using Ubit.Core.Inventory;
using Ubit.Core.Protocol;

namespace Ubit.Windows.Inventory;

/// <summary>
/// Reads installed apps from the Uninstall registry keys and the default browser and
/// mail handlers. Reads names and identifiers only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RegistryInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static HostInventory Scan()
    {
        var entries = new List<UninstallEntry>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "uninstall-hklm", entries);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "uninstall-hklm-wow64", entries);
        Read(RegistryHive.CurrentUser, RegistryView.Default, "uninstall-hkcu", entries);
        return new HostInventory(InstalledAppFilter.Filter(entries), UserChoice("https"), UserChoice("mailto"));
    }

    private static void Read(RegistryHive hive, RegistryView view, string source, List<UninstallEntry> into)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = root.OpenSubKey(UninstallPath);
        if (uninstall is null)
        {
            return;
        }
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key is null)
            {
                continue;
            }
            into.Add(new UninstallEntry(
                name,
                source,
                key.GetValue("DisplayName") as string,
                key.GetValue("SystemComponent") as int?,
                key.GetValue("ParentKeyName") as string,
                key.GetValue("ReleaseType") as string));
        }
    }

    /// <summary>The ProgId the user chose for a protocol, e.g. ChromeHTML for https.</summary>
    private static string? UserChoice(string protocol)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{protocol}\UserChoice");
        return key?.GetValue("ProgId") as string;
    }
}
