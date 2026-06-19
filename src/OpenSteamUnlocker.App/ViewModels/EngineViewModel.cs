using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.Manifest.Lua;
using OpenSteamUnlocker.Core.Manifest.Models;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class EngineViewModel : ObservableObject
{
    private readonly IEngineService _engine;

    [ObservableProperty]
    private string _steamPath = "—";

    [ObservableProperty]
    private bool _installed;

    [ObservableProperty]
    private bool _binariesBundled;

    [ObservableProperty]
    private bool _showBinariesWarning;

    [ObservableProperty]
    private bool _showPatternWarning;

    [ObservableProperty]
    private string _statusText = "未知";

    [ObservableProperty]
    private string _luaDirectory = "—";

    [ObservableProperty]
    private string _newAppId = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    public ObservableCollection<string> InstalledApps { get; } = new();

    public EngineViewModel(IEngineService engine)
    {
        _engine = engine;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var status = _engine.GetStatus();
        SteamPath = status.SteamPath ?? "未偵測到 Steam";
        Installed = status.Installed;
        BinariesBundled = status.BinariesBundled;
        ShowBinariesWarning = !status.BinariesBundled;
        ShowPatternWarning = status.Installed && !status.PatternUpToDate;
        LuaDirectory = status.LuaDirectory ?? "—";

        StatusText = !status.SteamFound
            ? "未偵測到 Steam"
            : status.Installed
                ? "引擎已安裝"
                : status.PresentFiles.Count == 0
                    ? "引擎未安裝"
                    : $"部分安裝（缺少 {string.Join(", ", status.MissingFiles)}）";

        InstalledApps.Clear();
        foreach (var app in status.InstalledApps)
            InstalledApps.Add(app);

        // Auto-generate opensteamtool.toml when engine DLLs are detected.
        if (status.Installed)
            _ = TryEnsureTomlAsync();
    }

    private async Task TryEnsureTomlAsync()
    {
        try { await _engine.EnsureTomlAsync(); }
        catch { /* non-fatal — TOML is optional */ }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        Message = string.Empty;
        var progress = new Progress<string>(msg => Message = msg);
        try
        {
            await _engine.InstallAsync(progress);
        }
        catch
        {
            Message = "初始化失敗";
        }
        Refresh();
    }

    [RelayCommand]
    private async Task UpdatePatternsAsync()
    {
        Message = string.Empty;
        var progress = new Progress<string>(msg => Message = msg);
        try
        {
            await _engine.UpdatePatternsAsync(progress);
        }
        catch (Exception ex)
        {
            Message = $"更新 Pattern 失敗：{ex.Message}";
        }
        Refresh();
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        try
        {
            await _engine.UninstallAsync();
            Message = "引擎已移除。";
        }
        catch (Exception ex)
        {
            Message = $"移除失敗：{ex.Message}";
        }
        Refresh();
    }

    [RelayCommand]
    private async Task AddTestLuaAsync()
    {
        var appId = NewAppId.Trim();
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
        {
            Message = "請輸入有效的數字 AppID。";
            return;
        }

        try
        {
            // Minimal unlock script (no depots) — demonstrates the write path.
            var lua = LuaManifestBuilder.Build(appId, Array.Empty<DepotInfo>(), autoUpdate: true);
            await _engine.WriteLuaAsync(appId, lua);
            Message = $"已寫入 {appId}.lua 到引擎的 lua 目錄。";
            NewAppId = string.Empty;
        }
        catch (Exception ex)
        {
            Message = $"寫入失敗：{ex.Message}";
        }
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveLuaAsync(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
            return;
        try
        {
            await _engine.RemoveLuaAsync(appId);
            Message = $"已移除 {appId}.lua。";
        }
        catch (Exception ex)
        {
            Message = $"移除失敗：{ex.Message}";
        }
        Refresh();
    }
}
