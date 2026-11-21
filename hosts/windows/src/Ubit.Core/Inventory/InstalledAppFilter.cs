using Ubit.Core.Protocol;

namespace Ubit.Core.Inventory;

/// <summary>One subkey of an Uninstall registry key, as read from the registry.</summary>
/// <param name="KeyName">Subkey name, e.g. "Google Chrome" or a product GUID.</param>
/// <param name="Hive">Where it came from: uninstall-hklm, uninstall-hklm-wow64, uninstall-hkcu.</param>
public sealed record UninstallEntry(
    string KeyName,
    string Hive,
    string? DisplayName,
    int? SystemComponent,
    string? ParentKeyName,
    string? ReleaseType);

/// <summary>
/// Turns raw Uninstall entries into the apps a person would recognise: drops system
/// components, updates, hotfixes and child entries, and merges duplicates across hives.
/// </summary>
public static class InstalledAppFilter
{
    private static readonly HashSet<string> SkippedReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup",
    };

    public static IReadOnlyList<App> Filter(IEnumerable<UninstallEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<App>();
        foreach (var e in entries)
        {
            var name = e.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name)
                || e.SystemComponent == 1
                || !string.IsNullOrEmpty(e.ParentKeyName)
                || (e.ReleaseType is not null && SkippedReleaseTypes.Contains(e.ReleaseType))
                || IsKbUpdate(name))
            {
                continue;
            }
            if (seen.Add(name))
            {
                apps.Add(new App(e.KeyName, name, e.Hive));
            }
        }
        return apps
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ProtocolInfo.MaxApps)
            .ToList();
    }

    // e.g. "Security Update for Microsoft Office (KB5002700) 64-Bit Edition"
    private static bool IsKbUpdate(string name) =>
        name.Contains("(KB", StringComparison.OrdinalIgnoreCase) && name.Contains("Update", StringComparison.OrdinalIgnoreCase);
}
