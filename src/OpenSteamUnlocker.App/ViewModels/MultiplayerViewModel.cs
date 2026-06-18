using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class MultiplayerViewModel : ObservableObject
{
    private readonly ISteamLocator _locator;

    // ── Game ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _appId           = "480";
    [ObservableProperty] private string _gameExePath     = string.Empty;

    // ── Player ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _playerName      = "Player";
    [ObservableProperty] private string _steamId         = "76561198000000000";
    [ObservableProperty] private bool   _offlineMode;

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _showSuccess;
    [ObservableProperty] private bool   _showError;
    [ObservableProperty] private string _resultText      = string.Empty;
    [ObservableProperty] private string _configFolderPath= string.Empty;

    public MultiplayerViewModel(ISteamLocator locator) => _locator = locator;

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseGame()
    {
        var dlg = new OpenFileDialog
        {
            Title       = "選擇遊戲執行檔",
            Filter      = "可執行檔 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            GameExePath = dlg.FileName;
    }

    [RelayCommand]
    private async Task GenerateConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(GameExePath) || !File.Exists(GameExePath))
        {
            ShowError  = true;
            ResultText = "請先選擇有效的遊戲執行檔。";
            return;
        }

        IsBusy       = true;
        ShowSuccess  = false;
        ShowError    = false;

        try
        {
            var gameDir     = Path.GetDirectoryName(GameExePath)!;
            var settingsDir = Path.Combine(gameDir, "steam_settings");
            Directory.CreateDirectory(settingsDir);

            // Goldberg config files
            await File.WriteAllTextAsync(Path.Combine(settingsDir, "steam_appid.txt"),    AppId.Trim());
            await File.WriteAllTextAsync(Path.Combine(settingsDir, "account_name.txt"),   PlayerName.Trim());
            await File.WriteAllTextAsync(Path.Combine(settingsDir, "user_steam_id.txt"),  SteamId.Trim());

            var offlineFile = Path.Combine(settingsDir, "offline.txt");
            if (OfflineMode)
                await File.WriteAllTextAsync(offlineFile, "");
            else if (File.Exists(offlineFile))
                File.Delete(offlineFile);

            // Launch BAT
            var exeName = Path.GetFileName(GameExePath);
            var batPath = Path.Combine(gameDir, "launch_multiplayer.bat");
            await File.WriteAllTextAsync(batPath,
                $"@echo off\r\ncd /d \"%~dp0\"\r\nstart \"\" \"{exeName}\"\r\n");

            ConfigFolderPath = settingsDir;
            ShowSuccess  = true;
            ResultText   = $"設定已寫入 steam_settings\\  並建立 launch_multiplayer.bat。\n請將 Goldberg 的 steam_api(64).dll 複製到遊戲目錄後再啟動。";
        }
        catch (Exception ex)
        {
            ShowError  = true;
            ResultText = $"產生設定失敗：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenGoldbergDownload()
    {
        // Opens Goldberg emulator GitHub releases in the default browser.
        Process.Start(new ProcessStartInfo
        {
            FileName        = "https://github.com/nicowillis/goldberg_emulator/releases",
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void LaunchGame()
    {
        if (string.IsNullOrWhiteSpace(GameExePath) || !File.Exists(GameExePath))
        {
            ShowError  = true;
            ResultText = "請先選擇有效的遊戲執行檔。";
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName         = GameExePath,
            WorkingDirectory = Path.GetDirectoryName(GameExePath),
            UseShellExecute  = true,
        });
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        if (!string.IsNullOrEmpty(ConfigFolderPath) && Directory.Exists(ConfigFolderPath))
            Process.Start("explorer.exe", ConfigFolderPath);
    }
}
