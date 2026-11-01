using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSteamUnlocker.Core.Steam;

namespace OpenSteamUnlocker.App.ViewModels;

public partial class CdkActivationViewModel : ObservableObject
{
    private readonly ICdkActivationService _cdk;

    public CdkActivationViewModel(ICdkActivationService cdk) => _cdk = cdk;

    // ── Input ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    private string _cdkKey = string.Empty;

    // ── Status ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _showSuccess;
    [ObservableProperty] private bool   _showError;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Paste the clipboard content into the CDK input field.
    /// </summary>
    [RelayCommand]
    private void Paste()
    {
        try
        {
            var text = Clipboard.GetText()?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(text))
                CdkKey = text;
        }
        catch { /* clipboard unavailable */ }
    }

    /// <summary>
    /// Clear the input and dismiss any status banners.
    /// </summary>
    [RelayCommand]
    private void Clear()
    {
        CdkKey        = string.Empty;
        ShowSuccess   = false;
        ShowError     = false;
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// Copy the CDK to the clipboard and open Steam's "Activate a Product" dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActivate))]
    private void Activate()
    {
        ShowSuccess   = false;
        ShowError     = false;
        StatusMessage = string.Empty;

        var key = CdkKey.Trim().ToUpperInvariant();

        // Warn when format looks wrong, but still allow activation in case
        // Steam itself accepts unusual key formats.
        if (!_cdk.ValidateKey(key))
        {
            ShowError     = true;
            StatusMessage = "序號格式不符（Steam CDK 格式：XXXXX-XXXXX-XXXXX），請確認後再試。";
            return;
        }

        // Put the key on the clipboard so the user can paste it in Steam.
        try { Clipboard.SetText(key); } catch { /* clipboard unavailable */ }

        var result = _cdk.Launch();
        if (result.Launched)
        {
            ShowSuccess   = true;
            StatusMessage = "Steam 啟動視窗已開啟！請在對話框中按 Ctrl+V 貼上序號，然後按「下一步」。";
        }
        else
        {
            ShowError     = true;
            StatusMessage = result.Error ?? "無法開啟 Steam，請確認 Steam 已安裝並正在執行。";
        }
    }

    private bool CanActivate() => !string.IsNullOrWhiteSpace(CdkKey);
}
