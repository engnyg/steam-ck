using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Config;
using OpenSteamUnlocker.Core.Manifest.Models;
using OpenSteamUnlocker.Core.Manifest.Services;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly IGameSearchService  _search;
    private readonly IManifestInstaller  _installer;
    private readonly IConfigService      _config;

    public SearchViewModel(
        IGameSearchService search,
        IManifestInstaller installer,
        IConfigService     config)
    {
        _search    = search;
        _installer = installer;
        _config    = config;
    }

    // ── Inputs ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _query = string.Empty;

    [ObservableProperty] private bool _autoUpdate  = false;
    [ObservableProperty] private bool _addAllDlc   = true;

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = string.Empty;

    // Install result
    [ObservableProperty] private bool   _showResultSuccess;
    [ObservableProperty] private bool   _showResultError;
    [ObservableProperty] private string _resultText = string.Empty;

    public ObservableCollection<GameInfo> SearchResults { get; } = new();

    // ── Search ────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        IsBusy = true;
        ShowResultSuccess = false;
        ShowResultError   = false;
        StatusText = "搜尋中…";
        SearchResults.Clear();

        try
        {
            var term  = Query.Trim();
            var appId = _search.ExtractAppId(term);

            if (appId is not null)
            {
                // Bare AppID / URL — show a stub card immediately so the user
                // can install without waiting for info lookup.
                SearchResults.Add(new GameInfo(
                    appId, $"AppID {appId}",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg"));
            }
            else
            {
                var results = await _search.SearchAsync(term);
                foreach (var r in results)
                    SearchResults.Add(r);
            }

            StatusText = SearchResults.Count == 0 ? "未找到結果。" : $"找到 {SearchResults.Count} 個結果";
        }
        catch (Exception ex)
        {
            StatusText = $"搜尋失敗：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanSearch() => !IsBusy && !string.IsNullOrWhiteSpace(Query);

    // ── Install ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(GameInfo? game)
    {
        if (game is null) return;

        IsBusy = true;
        ShowResultSuccess = false;
        ShowResultError   = false;
        ResultText        = string.Empty;
        var progress      = new Progress<string>(msg => StatusText = msg);

        try
        {
            var cfg     = _config.Current;
            var options = new ManifestInstallOptions(
                AutoUpdate:         AutoUpdate,
                AddFreeDlc:         AddAllDlc,
                GithubPersonalToken: cfg.GithubPersonalToken,
                ExtraGitHubRepos:   cfg.CustomRepos.Github
                    .Where(r => !string.IsNullOrWhiteSpace(r.Repo))
                    .Select(r => r.Repo)
                    .ToList());

            var result = await _installer.InstallAsync(game.AppId, options, progress);

            if (result.Success)
            {
                ShowResultSuccess = true;
                ResultText = BuildSuccessMessage(game.Name, result);
            }
            else
            {
                ShowResultError = true;
                ResultText = result.Error ?? "安裝失敗，原因不明。";
            }
        }
        catch (Exception ex)
        {
            ShowResultError = true;
            ResultText      = ex.Message;
        }
        finally
        {
            IsBusy     = false;
            StatusText = string.Empty;
        }
    }

    private bool CanInstall(GameInfo? _) => !IsBusy;

    public void DismissResult()
    {
        ShowResultSuccess = false;
        ShowResultError   = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSuccessMessage(string name, ManifestInstallResult r)
    {
        var parts = new List<string> { $"《{name}》 已透過 {r.ProviderUsed} 入庫。" };
        if (r.ManifestsCopied > 0) parts.Add($"複製了 {r.ManifestsCopied} 個 manifest 檔案。");
        if (r.DlcsAdded > 0)       parts.Add($"加入了 {r.DlcsAdded} 個 DLC。");
        return string.Join(" ", parts);
    }
}
