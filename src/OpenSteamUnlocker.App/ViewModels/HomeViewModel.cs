using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.Manifest.Services;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.App.ViewModels;

/// <summary>An installed game entry displayed on the Home page.</summary>
public partial class InstalledGame : ObservableObject
{
    [ObservableProperty] private string        _name;
    [ObservableProperty] private BitmapImage?  _headerImage;

    public string AppId      { get; }
    public string FallbackUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";

    public InstalledGame(string appId)
    {
        AppId  = appId;
        _name  = "載入中…";
    }
}

public partial class HomeViewModel : ObservableObject
{
    private readonly ISteamLocator     _locator;
    private readonly IEngineService    _engine;
    private readonly ISteamDataService _steamData;

    [ObservableProperty] private string _steamPath     = "偵測中…";
    [ObservableProperty] private bool   _steamInstalled;
    [ObservableProperty] private string _statsText     = string.Empty;
    [ObservableProperty] private bool   _isBusy;

    public ObservableCollection<InstalledGame> InstalledGames { get; } = new();

    public HomeViewModel(
        ISteamLocator     locator,
        IEngineService    engine,
        ISteamDataService steamData)
    {
        _locator   = locator;
        _engine    = engine;
        _steamData = steamData;
        Refresh();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Refresh()
    {
        _locator.Refresh();
        SteamInstalled = _locator.IsInstalled;
        SteamPath      = _locator.SteamPath ?? "未偵測到 Steam 安裝";
        LoadInstalledGames();
    }

    [RelayCommand]
    private async Task RemoveGameAsync(InstalledGame? game)
    {
        if (game is null) return;
        try
        {
            await _engine.RemoveLuaAsync(game.AppId);
            InstalledGames.Remove(game);
            UpdateStats();
        }
        catch { /* non-fatal */ }
    }

    // ── Installed games ───────────────────────────────────────────────────────

    private void LoadInstalledGames()
    {
        InstalledGames.Clear();
        var status = _engine.GetStatus();

        foreach (var appId in status.InstalledApps)
            InstalledGames.Add(new InstalledGame(appId));

        UpdateStats();

        // Load game names in the background — images come from CDN URL directly.
        _ = LoadNamesAsync();
    }

    private async Task LoadNamesAsync()
    {
        var snapshot = InstalledGames.ToList();
        foreach (var game in snapshot)
        {
            try
            {
                var info = await _steamData.GetGameInfoAsync(game.AppId);
                if (!string.IsNullOrEmpty(info.Name))
                    game.Name = info.Name;

                var imgUrl = !string.IsNullOrEmpty(info.HeaderImageUrl)
                    ? info.HeaderImageUrl
                    : game.FallbackUrl;

                var bytes = await _steamData.DownloadImageAsync(imgUrl);
                if (bytes is { Length: > 0 })
                {
                    var bmp = await Task.Run(() =>
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.StreamSource = new System.IO.MemoryStream(bytes);
                        b.CacheOption  = BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        return b;
                    });
                    game.HeaderImage = bmp;
                }
            }
            catch { /* skip — AppID text fallback is fine */ }
        }
    }

    private void UpdateStats()
    {
        StatsText = InstalledGames.Count == 0
            ? "尚未入庫任何遊戲"
            : $"共 {InstalledGames.Count} 款遊戲已入庫";
    }
}
