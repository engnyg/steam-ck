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
            ?? "1.0.2");

    public string AppName    { get; } = "OpenSteam Unlocker";
    public string GitHubUrl  { get; } = "https://github.com/engnyg/steamtool-";
    public string CopyrightYear { get; } = DateTime.Now.Year.ToString();

    [RelayCommand]
    private void OpenGitHub() =>
        Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
}
