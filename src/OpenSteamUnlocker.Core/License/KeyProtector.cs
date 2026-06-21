using System.Security.Cryptography;
using System.Text;

namespace OpenSteamUnlocker.Core.License;

public static class KeyProtector
{
    public static string Protect(string plainText)
    {
        var data = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string encrypted)
    {
        try
        {
            var data = Convert.FromBase64String(encrypted);
            var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsProtected(string value) =>
        !string.IsNullOrEmpty(value) && !value.StartsWith("STEAM-", StringComparison.OrdinalIgnoreCase);
}
