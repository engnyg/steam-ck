namespace OpenSteamUnlocker.Core.Steam;

/// <summary>
/// Locates the local Steam installation and its library folders.
/// </summary>
public interface ISteamLocator
{
    /// <summary>Absolute path to the Steam root directory, or null if not found.</summary>
    string? SteamPath { get; }

    /// <summary>True when a Steam installation was located.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// The directory OpenSteamTool reads Lua scripts from
    /// (<c>&lt;Steam&gt;/config/lua</c>).
    /// </summary>
    string? LuaConfigPath { get; }

    /// <summary>
    /// The directory SteamTools reads plugin scripts from
    /// (<c>&lt;Steam&gt;/config/stplug-in</c>).
    /// </summary>
    string? SteamToolsPluginPath { get; }

    /// <summary>Re-reads the registry / disk to refresh <see cref="SteamPath"/>.</summary>
    void Refresh();

    /// <summary>All Steam library folders parsed from libraryfolders.vdf.</summary>
    IReadOnlyList<string> GetLibraryFolders();
}
