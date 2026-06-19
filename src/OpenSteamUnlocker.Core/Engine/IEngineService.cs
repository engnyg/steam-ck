namespace OpenSteamUnlocker.Core.Engine;

/// <summary>
/// Manages the OpenSteamTool native unlock engine: deploying its DLLs into the
/// Steam root, writing its <c>opensteamtool.toml</c>, and writing/removing the
/// per-app Lua unlock scripts it consumes from <c>&lt;Steam&gt;/config/lua</c>.
/// </summary>
public interface IEngineService
{
    /// <summary>Inspects the current engine installation state.</summary>
    EngineStatus GetStatus();

    /// <summary>
    /// Deploys the engine DLLs into the Steam root. If bundled DLLs are
    /// unavailable, downloads the latest release from GitHub automatically.
    /// </summary>
    /// <exception cref="InvalidOperationException">Steam not found.</exception>
    Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Removes the engine DLLs from the Steam root, restoring backups when present.
    /// </summary>
    Task UninstallAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes (or overwrites) <c>&lt;appId&gt;.lua</c> in the engine's Lua directory.
    /// </summary>
    Task WriteLuaAsync(string appId, string luaContent, CancellationToken ct = default);

    /// <summary>Removes <c>&lt;appId&gt;.lua</c> from the engine's Lua directory.</summary>
    Task RemoveLuaAsync(string appId, CancellationToken ct = default);

    /// <summary>Reads the Lua script for an app, or null if not installed.</summary>
    Task<string?> ReadLuaAsync(string appId, CancellationToken ct = default);

    /// <summary>Ensures <c>opensteamtool.toml</c> exists in the Steam root.</summary>
    Task EnsureTomlAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the latest OpenSteamTool release and installs only the pattern
    /// files into <c>&lt;Steam&gt;/opensteamtool/pattern/steamclient/</c>.
    /// Does not touch the engine DLLs already installed in the Steam root.
    /// </summary>
    Task UpdatePatternsAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
