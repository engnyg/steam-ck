using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string Version { get; } =
        "v" + (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]
            ?? "1.0.3");

    public string AppName { get; } = "OpenSteam Unlocker";

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo("https://engnyg.win/") { UseShellExecute = true });

    [RelayCommand]
    private void OpenDiscord() =>
        Process.Start(new ProcessStartInfo("https://dc.engnyg.win/") { UseShellExecute = true });
}
