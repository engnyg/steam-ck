using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace OpenSteamUnlocker.Core.License;

public sealed class LicenseService : ILicenseService
{
    private const string FunctionUrl =
        "https://us-central1-mas-ray-bfc55.cloudfunctions.net/verifySteamLicense";

    private readonly HttpClient _http;

    public LicenseService(HttpClient http) => _http = http;

    public string GetHwid()
    {
        try
        {
            var guid = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", null) as string;
            if (!string.IsNullOrEmpty(guid))
                return guid;
        }
        catch { }

        return Environment.MachineName;
    }

    public async Task<LicenseResult> VerifyAsync(string key)
    {
        try
        {
            var hwid = GetHwid();
            using var response = await _http.PostAsJsonAsync(
                FunctionUrl, new { key, hwid });

            var body = await response.Content.ReadFromJsonAsync<FbResponse>();
            return body?.Status == "success"
                ? new LicenseResult(true, null)
                : new LicenseResult(false, body?.Message ?? "驗證失敗");
        }
        catch (Exception ex)
        {
            return new LicenseResult(false, $"網路錯誤：{ex.Message}");
        }
    }

    private sealed class FbResponse
    {
        [JsonPropertyName("status")]  public string? Status  { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
