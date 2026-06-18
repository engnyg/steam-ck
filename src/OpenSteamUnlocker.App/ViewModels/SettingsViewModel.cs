using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Config;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _config;

    // ── GitHub / API ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _githubPersonalToken = string.Empty;
    [ObservableProperty] private string _manifestApiKey      = string.Empty;

    // ── Unlocker behaviour ────────────────────────────────────────────────────

    /// <summary>auto / steamtools / greenluma / opensteamtool</summary>
    [ObservableProperty] private string _forceUnlockerType = "auto";

    [ObservableProperty] private bool _stFixedVersion     = true;
    [ObservableProperty] private bool _patchManifestDefault;

    // ── Network ───────────────────────────────────────────────────────────────

    [ObservableProperty] private int _downloadTimeoutSeconds = 30;
    [ObservableProperty] private int _dlcTimeout             = 60;

    // ── Application ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _debugMode;
    [ObservableProperty] private bool   _loggingFiles = true;
    [ObservableProperty] private string _language     = "system";

    // ── Save state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _showSavedBanner;
    [ObservableProperty] private string _savedMessage = string.Empty;

    public SettingsViewModel(IConfigService config)
    {
        _config = config;
        Load();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var cfg = _config.Current;
            cfg.GithubPersonalToken  = GithubPersonalToken;
            cfg.ManifestApiKey       = ManifestApiKey;
            cfg.ForceUnlockerType    = ForceUnlockerType;
            cfg.StFixedVersion       = StFixedVersion;
            cfg.PatchManifestDefault = PatchManifestDefault;
            cfg.DownloadTimeout      = DownloadTimeoutSeconds;
            cfg.DlcTimeout           = DlcTimeout;
            cfg.DebugMode            = DebugMode;
            cfg.LoggingFiles         = LoggingFiles;
            cfg.Language             = Language;
            await _config.SaveAsync();

            SavedMessage   = "設定已儲存。";
            ShowSavedBanner = true;
            // Auto-hide after 3 seconds
            _ = Task.Delay(3000).ContinueWith(_ => ShowSavedBanner = false,
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            SavedMessage    = $"儲存失敗：{ex.Message}";
            ShowSavedBanner = true;
        }
        finally { IsBusy = false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Load()
    {
        var cfg = _config.Current;
        GithubPersonalToken  = cfg.GithubPersonalToken;
        ManifestApiKey       = cfg.ManifestApiKey;
        ForceUnlockerType    = cfg.ForceUnlockerType;
        StFixedVersion       = cfg.StFixedVersion;
        PatchManifestDefault = cfg.PatchManifestDefault;
        DownloadTimeoutSeconds = cfg.DownloadTimeout;
        DlcTimeout           = cfg.DlcTimeout;
        DebugMode            = cfg.DebugMode;
        LoggingFiles         = cfg.LoggingFiles;
        Language             = cfg.Language;
    }
}
