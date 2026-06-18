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

    // ── Search inputs ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _query = string.Empty;

    // ── Install options ───────────────────────────────────────────────────────

    [ObservableProperty] private bool _addAllDlc    = true;
    [ObservableProperty] private bool _autoUpdate   = false;

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy      = false;
    [ObservableProperty] private string _statusText  = string.Empty;
    [ObservableProperty] private string _resultText  = string.Empty;

    public ObservableCollection<GameInfo> SearchResults { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        IsBusy = true;
        StatusText = "Searching…";
        SearchResults.Clear();
        ResultText = string.Empty;

        try
        {
            var term    = Query.Trim();
            var appId   = _search.ExtractAppId(term);
            var results = appId is not null
                ? new[] { await GetSingleGameAsync(appId) }
                : (await _search.SearchAsync(term)).ToArray();

            foreach (var r in results)
                if (r is not null) SearchResults.Add(r);

            StatusText = SearchResults.Count == 0
                ? "No results found."
                : $"{SearchResults.Count} result(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanSearch() => !IsBusy && !string.IsNullOrWhiteSpace(Query);

    partial void OnQueryChanged(string value) => SearchCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(GameInfo? game)
    {
        if (game is null) return;

        IsBusy = true;
        var progress = new Progress<string>(msg => StatusText = msg);
        ResultText = string.Empty;

        try
        {
            var cfg     = _config.Current;
            var options = new ManifestInstallOptions(
                AutoUpdate: AutoUpdate,
                AddFreeDlc: AddAllDlc,
                GithubPersonalToken: cfg.GithubPersonalToken,
                ExtraGitHubRepos: cfg.CustomRepos?.Github?
                    .Select(r => r.Repo)
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList());

            var result = await _installer.InstallAsync(game.AppId, options, progress);
            ResultText = result.Success
                ? $"✓ {game.Name} installed via {result.ProviderUsed}." +
                  (result.ManifestsCopied > 0 ? $" {result.ManifestsCopied} manifest(s) copied." : "") +
                  (result.DlcsAdded > 0 ? $" {result.DlcsAdded} DLC(s) added." : "")
                : $"✗ {result.Error}";
        }
        catch (Exception ex)
        {
            ResultText = $"✗ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }
    }

    private bool CanInstall(GameInfo? _) => !IsBusy;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<GameInfo?> GetSingleGameAsync(string appId)
    {
        // If the user typed a bare AppID or URL, look up the game name.
        var existing = SearchResults.FirstOrDefault(g => g.AppId == appId);
        if (existing is not null) return existing;

        // Fall back to a stub — the install flow doesn't require the name.
        return new GameInfo(appId, $"AppID {appId}");
    }
}
