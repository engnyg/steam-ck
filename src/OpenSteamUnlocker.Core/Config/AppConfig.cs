using System.Text.Json.Serialization;

namespace OpenSteamUnlocker.Core.Config;

/// <summary>
/// Application configuration. Ported from cai_backend's <c>DEFAULT_CONFIG</c>.
/// Serialized to <c>config/config.json</c>.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("Github_Personal_Token")]
    public string GithubPersonalToken { get; set; } = string.Empty;

    [JsonPropertyName("ManifestAPIKey")]
    public string ManifestApiKey { get; set; } = string.Empty;

    [JsonPropertyName("Custom_Steam_Path")]
    public string CustomSteamPath { get; set; } = string.Empty;

    [JsonPropertyName("debug_mode")]
    public bool DebugMode { get; set; }

    [JsonPropertyName("logging_files")]
    public bool LoggingFiles { get; set; } = true;

    [JsonPropertyName("background_image_path")]
    public string BackgroundImagePath { get; set; } = string.Empty;

    [JsonPropertyName("background_blur")]
    public int BackgroundBlur { get; set; }

    [JsonPropertyName("background_saturation")]
    public int BackgroundSaturation { get; set; } = 100;

    [JsonPropertyName("background_brightness")]
    public int BackgroundBrightness { get; set; } = 80;

    [JsonPropertyName("show_console_on_startup")]
    public bool ShowConsoleOnStartup { get; set; }

    /// <summary>"auto", "steamtools", "greenluma" or "opensteamtool".</summary>
    [JsonPropertyName("force_unlocker_type")]
    public string ForceUnlockerType { get; set; } = "auto";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "system";

    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "auto";

    [JsonPropertyName("theme_color")]
    public string ThemeColor { get; set; } = "#0078d4";

    [JsonPropertyName("window_effect")]
    public string WindowEffect { get; set; } = "mica";

    [JsonPropertyName("default_page")]
    public string DefaultPage { get; set; } = "home";

    [JsonPropertyName("Custom_Repos")]
    public CustomRepos CustomRepos { get; set; } = new();

    /// <summary>DLC retrieval / network timeout, in seconds.</summary>
    [JsonPropertyName("DLCTimeout")]
    public int DlcTimeout { get; set; } = 60;

    [JsonPropertyName("download_timeout")]
    public int DownloadTimeout { get; set; } = 30;

    /// <summary>SteamTools fixed-version mode (manifest pinning).</summary>
    [JsonPropertyName("ST_Fixed_Version")]
    public bool StFixedVersion { get; set; } = true;

    /// <summary>Fixed-version manifest repair mode: always / never / ask.</summary>
    [JsonPropertyName("ST_Fixed_Manifest_Mode")]
    public string StFixedManifestMode { get; set; } = "ask";

    [JsonPropertyName("patch_manifest_default")]
    public bool PatchManifestDefault { get; set; }

    /// <summary>
    /// GitHub repo slug ("owner/repo") to poll for new releases.
    /// Empty string disables the auto-update check.
    /// </summary>
    [JsonPropertyName("update_repo_slug")]
    public string UpdateRepoSlug { get; set; } = string.Empty;

    [JsonPropertyName("steam_license_key")]
    public string SteamLicenseKey { get; set; } = string.Empty;
}

public sealed class CustomRepos
{
    [JsonPropertyName("github")]
    public List<GithubRepoConfig> Github { get; set; } = new();

    [JsonPropertyName("zip")]
    public List<ZipRepoConfig> Zip { get; set; } = new();
}

public sealed class GithubRepoConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"owner/repo".</summary>
    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;
}

public sealed class ZipRepoConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Download URL with <c>{app_id}</c> placeholder.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
