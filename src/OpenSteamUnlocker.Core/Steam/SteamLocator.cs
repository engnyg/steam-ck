using Microsoft.Win32;
using ValveKeyValue;

namespace OpenSteamUnlocker.Core.Steam;

/// <summary>
/// Default <see cref="ISteamLocator"/> implementation. Resolves the Steam path
/// from the Windows registry (falling back to common install locations) and
/// parses <c>libraryfolders.vdf</c> for additional library roots.
/// </summary>
public sealed class SteamLocator : ISteamLocator
{
    private string? _steamPath;

    public SteamLocator() => Refresh();

    public string? SteamPath => _steamPath;

    public bool IsInstalled => _steamPath is not null && Directory.Exists(_steamPath);

    public string? LuaConfigPath =>
        _steamPath is null ? null : Path.Combine(_steamPath, "config", "lua");

    public void Refresh() => _steamPath = ResolveSteamPath();

    private static string? ResolveSteamPath()
    {
        // Registry is the authoritative source on Windows.
        foreach (var (hive, view, sub, name) in RegistryCandidates())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(sub);
                if (key?.GetValue(name) is string p && Directory.Exists(p))
                    return Path.GetFullPath(p);
            }
            catch
            {
                // Ignore and try the next candidate.
            }
        }

        // Fall back to conventional install locations.
        foreach (var guess in CommonPaths())
        {
            if (Directory.Exists(guess))
                return guess;
        }

        return null;
    }

    private static IEnumerable<(RegistryHive hive, RegistryView view, string sub, string name)> RegistryCandidates()
    {
        yield return (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
    }

    private static IEnumerable<string> CommonPaths()
    {
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"C:\Program Files\Steam";
        yield return @"D:\Steam";
    }

    public IReadOnlyList<string> GetLibraryFolders()
    {
        var result = new List<string>();
        if (_steamPath is null)
            return result;

        result.Add(_steamPath);

        var vdf = Path.Combine(_steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            return result;

        try
        {
            using var stream = File.OpenRead(vdf);
            var kv = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var document = kv.Deserialize(stream);

            foreach (var entry in document.Root.Children)
            {
                var path = entry.Value["path"]?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && !result.Contains(path))
                    result.Add(path);
            }
        }
        catch
        {
            // A malformed VDF should not break library enumeration.
        }

        return result;
    }
}
