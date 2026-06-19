namespace OpenSteamUnlocker.Core.Engine;

/// <summary>The DLLs OpenSteamTool deploys into the Steam root directory.</summary>
public static class EngineFiles
{
    public const string HookDll = "OpenSteamTool.dll";
    public const string DwmApiDll = "dwmapi.dll";
    public const string XInputDll = "xinput1_4.dll";

    /// <summary>All engine binaries, in deploy order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { HookDll, DwmApiDll, XInputDll };
}

/// <summary>Snapshot of the OpenSteamTool engine's installation state.</summary>
public sealed record EngineStatus
{
    /// <summary>True when a Steam installation was located.</summary>
    public required bool SteamFound { get; init; }

    /// <summary>Steam root directory, or null.</summary>
    public string? SteamPath { get; init; }

    /// <summary>True when all three engine DLLs are present in the Steam root.</summary>
    public required bool Installed { get; init; }

    /// <summary>Engine DLLs found present in the Steam root.</summary>
    public required IReadOnlyList<string> PresentFiles { get; init; }

    /// <summary>Engine DLLs missing from the Steam root.</summary>
    public required IReadOnlyList<string> MissingFiles { get; init; }

    /// <summary>True when bundled engine binaries are available to install from.</summary>
    public required bool BinariesBundled { get; init; }

    /// <summary>The <c>&lt;Steam&gt;/config/lua</c> directory, or null.</summary>
    public string? LuaDirectory { get; init; }

    /// <summary>AppIDs of Lua unlock scripts currently installed.</summary>
    public required IReadOnlyList<string> InstalledApps { get; init; }

    /// <summary>
    /// True when a pattern file matching the installed <c>steamclient.dll</c> exists.
    /// False means OpenSteamTool cannot hook into the current Steam version;
    /// re-installing the engine will download updated pattern files.
    /// </summary>
    public bool PatternUpToDate { get; init; }

    /// <summary>Lowercase hex SHA-256 of <c>steamclient.dll</c>, or null if not found.</summary>
    public string? SteamClientHash { get; init; }
}
