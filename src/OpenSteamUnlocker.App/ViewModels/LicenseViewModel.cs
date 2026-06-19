using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Config;
using OpenSteamUnlocker.Core.Engine;
using OpenSteamUnlocker.Core.License;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class LicenseViewModel : ObservableObject
{
    private readonly ILicenseService  _license;
    private readonly IConfigService   _config;
    private readonly IEngineService   _engine;

    [ObservableProperty] private string _licenseKey     = string.Empty;
    [ObservableProperty] private string _statusMessage  = string.Empty;
    [ObservableProperty] private bool   _isError;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _autoLogin;

    public event Action? LicenseAccepted;

    public LicenseViewModel(ILicenseService license, IConfigService config, IEngineService engine)
    {
        _license   = license;
        _config    = config;
        _engine    = engine;
        LicenseKey = config.Current.SteamLicenseKey;
        AutoLogin  = config.Current.AutoLogin;
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (string.IsNullOrWhiteSpace(LicenseKey))
        {
            StatusMessage = "請輸入授權金鑰";
            IsError = true;
            return;
        }

        IsBusy = true;
        StatusMessage = "驗證中...";
        IsError = false;

        try
        {
            var result = await _license.VerifyAsync(LicenseKey.Trim());
            if (result.IsValid)
            {
                _config.Current.SteamLicenseKey = LicenseKey.Trim();
                _config.Current.AutoLogin = AutoLogin;
                await _config.SaveAsync();
                StatusMessage = "驗證成功！";
                LicenseAccepted?.Invoke();
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "驗證失敗";
                IsError = true;
                try { await _engine.UninstallAsync(CancellationToken.None); } catch { }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
