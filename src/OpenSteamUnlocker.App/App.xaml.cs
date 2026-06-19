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

            // Core services.
            services.AddHttpClient();
            services.AddSingleton<ISteamLocator, SteamLocator>();
            services.AddSingleton<IEngineService>(sp =>
                new EngineService(
                    sp.GetRequiredService<ISteamLocator>(),
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<IConfigService, ConfigService>();
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
        })
        .Build();

    public static T GetService<T>() where T : class
        => _host.Services.GetService(typeof(T)) as T
           ?? throw new InvalidOperationException($"Service {typeof(T)} not registered.");

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        await _host.StartAsync();
        GetService<MainWindow>().Show();
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
