using System.Windows;
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

    public MainWindowViewModel(IUpdateService updates)
    {
        _updates = updates;

        var v = updates.CurrentVersion;
        ApplicationTitle = v.Major == 0 && v.Minor == 0
            ? "OpenSteam Unlocker"
            : $"OpenSteam Unlocker  v{v.Major}.{v.Minor}.{v.Build}";

        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void DismissUpdate() => ShowUpdateBanner = false;

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await _updates.CheckForUpdateAsync();
            if (info is null) return;

            if (info.DownloadUrl is not null)
            {
                UpdateBannerTitle = $"正在自動更新至 {info.Version}…";
                ShowUpdateBanner = true;
                await _updates.ApplyUpdateAsync(info);
                Application.Current.Shutdown();
                return;
            }

            UpdateBannerTitle = $"新版本可用：{info.Version}";
            ShowUpdateBanner = true;
        }
        catch { /* non-fatal */ }
    }
}
