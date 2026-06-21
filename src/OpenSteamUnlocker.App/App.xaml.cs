using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenSteamUnlocker.App.ViewModels;
using OpenSteamUnlocker.App.Views;
using OpenSteamUnlocker.App.Views.Pages;
using OpenSteamUnlocker.Core.Config;
using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.Http;
using OpenSteamUnlocker.Core.License;
using OpenSteamUnlocker.Core.Manifest.Providers;
using OpenSteamUnlocker.Core.Manifest.Services;
using OpenSteamUnlocker.Core.Steam;
using OpenSteamUnlocker.Core.Update;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace OpenSteamUnlocker.App;

/// <summary>
/// Application entry point. Builds the generic host and wires up DI for
/// views, view-models and Core services.
/// </summary>
public partial class App : Application
{
    private static GuardService? _guard;

    private static readonly IHost _host = Host
        .CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            // WPF-UI navigation / theming infrastructure.
            services.AddNavigationViewPageProvider();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();

            // Shell.
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();

            // Pages + view-models.
            services.AddSingleton<HomePage>();
            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<SearchPage>();
            services.AddSingleton<SearchViewModel>();
            services.AddSingleton<SettingsPage>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MultiplayerPage>();
            services.AddSingleton<MultiplayerViewModel>();
            services.AddSingleton<TrainersPage>();
            services.AddSingleton<TrainersViewModel>();
            services.AddSingleton<EnginePage>();
            services.AddSingleton<EngineViewModel>();
            services.AddSingleton<AboutPage>();
            services.AddSingleton<AboutViewModel>();

            // Core services.
            services.AddHttpClient();
            services.AddSingleton<ISteamLocator, SteamLocator>();
            services.AddSingleton<IConfigService, ConfigService>();
            services.AddSingleton<IEngineService>(sp =>
            {
                var config = sp.GetRequiredService<IConfigService>();
                var license = sp.GetRequiredService<ILicenseService>();
                return new EngineService(
                    sp.GetRequiredService<ISteamLocator>(),
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                    license,
                    () => ResolveKey(config.Current.SteamLicenseKey));
            });
            services.AddSingleton<IRegionDetector>(sp =>
                new RegionDetector(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<IGitHubMirrorClient>(sp =>
                new GitHubMirrorClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                    sp.GetRequiredService<IRegionDetector>()));

            // Phase 1b: manifest providers
            services.AddSingleton<ISteamDataService>(sp =>
                new SteamDataService(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<IDepotKeyService>(sp =>
                new DepotKeyService(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<IGameSearchService>(sp =>
                new GameSearchService(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<IGitHubManifestProvider>(sp =>
                new GitHubManifestProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                    sp.GetRequiredService<IGitHubMirrorClient>()));
            services.AddSingleton<IManifestInstaller>(sp =>
                new ManifestInstaller(
                    sp.GetRequiredService<IGitHubManifestProvider>(),
                    sp.GetRequiredService<ISteamDataService>(),
                    sp.GetRequiredService<IDepotKeyService>(),
                    sp.GetRequiredService<IEngineService>(),
                    sp.GetRequiredService<ISteamLocator>(),
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient()));

            // Phase 5: auto-update check
            services.AddSingleton<IUpdateService>(sp =>
                new UpdateService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                    sp.GetRequiredService<IConfigService>()));

            // License verification
            services.AddSingleton<ILicenseService>(sp =>
                new LicenseService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddTransient<LicenseViewModel>();
            services.AddTransient<LicenseWindow>();
        })
        .Build();

    public static T GetService<T>() where T : class
        => _host.Services.GetService(typeof(T)) as T
           ?? throw new InvalidOperationException($"Service {typeof(T)} not registered.");

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var blocked = GuardService.DetectBlockedProcess();
        if (blocked is not null)
        {
            MessageBox.Show(
                $"請先關閉 {blocked} 後再啟動。",
                "無法啟動", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (GuardService.IsBeingDebugged())
        {
            Shutdown();
            return;
        }

        await _host.StartAsync();

        var config = GetService<IConfigService>();
        await config.LoadAsync();

        // Forced auto-update
        try
        {
            var updates = GetService<IUpdateService>();
            var info = await updates.CheckForUpdateAsync();
            if (info?.DownloadUrl is not null)
            {
                await updates.ApplyUpdateAsync(info);
                Shutdown();
                return;
            }
        }
        catch { }

        if (config.Current.AutoLogin && !string.IsNullOrEmpty(config.Current.SteamLicenseKey))
        {
            var key = ResolveKey(config.Current.SteamLicenseKey);
            if (key is not null)
            {
                var license = GetService<ILicenseService>();
                var result = await license.VerifyAsync(key);
                if (result.IsValid)
                {
                    StartGuard(config, key);
                    GetService<MainWindow>().Show();
                    return;
                }
            }

            try { await GetService<IEngineService>().UninstallAsync(CancellationToken.None); } catch { }
            config.Current.SteamLicenseKey = string.Empty;
            config.Current.AutoLogin = false;
            await config.SaveAsync();
        }

        GetService<LicenseWindow>().Show();
    }

    public static void StartGuard(IConfigService config, string plainKey)
    {
        _guard?.Dispose();
        _guard = new GuardService(
            GetService<ILicenseService>(),
            () => ResolveKey(config.Current.SteamLicenseKey),
            async () =>
            {
                try { await GetService<IEngineService>().UninstallAsync(CancellationToken.None); } catch { }
                config.Current.SteamLicenseKey = string.Empty;
                config.Current.AutoLogin = false;
                await config.SaveAsync();
                Current.Dispatcher.Invoke(() => Current.Shutdown());
            });
    }

    private static string? ResolveKey(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (KeyProtector.IsProtected(stored))
            return KeyProtector.Unprotect(stored);
        return stored;
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // TODO: route to a logging service once available.
    }
}
