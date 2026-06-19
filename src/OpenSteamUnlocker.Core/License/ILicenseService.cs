namespace OpenSteamUnlocker.Core.License;

public interface ILicenseService
{
    string GetHwid();
    Task<LicenseResult> VerifyAsync(string key);
}

public sealed record LicenseResult(bool IsValid, string? ErrorMessage);
