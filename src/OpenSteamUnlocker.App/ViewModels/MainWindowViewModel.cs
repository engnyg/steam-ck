using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Update;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IUpdateService _updates;

    [ObservableProperty] private string _applicationTitle  = "OpenSteam Unlocker";
    [ObservableProperty] private bool   _showUpdateBanner;
    [ObservableProperty] private string _updateBannerTitle = string.Empty;
    [ObservableProperty] private string _updateDownloadUrl = string.Empty;

    public MainWindowViewModel(IUpdateService updates)
    {
        _updates = updates;

        var v = updates.CurrentVersion;
        // Don't show build/revision if zero (default 0.0.0.0 before versioning is set).
        ApplicationTitle = v.Major == 0 && v.Minor == 0
            ? "OpenSteam Unlocker"
            : $"OpenSteam Unlocker  v{v.Major}.{v.Minor}.{v.Build}";

        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void OpenUpdateDownload()
    {
        if (string.IsNullOrEmpty(UpdateDownloadUrl)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName        = UpdateDownloadUrl,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void DismissUpdate() => ShowUpdateBanner = false;

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await _updates.CheckForUpdateAsync();
            if (info is null) return;

            UpdateBannerTitle  = $"新版本可用：{info.Version}";
            UpdateDownloadUrl  = info.DownloadUrl ?? info.ReleaseUrl;
            ShowUpdateBanner   = true;
        }
        catch { /* non-fatal */ }
    }
}
