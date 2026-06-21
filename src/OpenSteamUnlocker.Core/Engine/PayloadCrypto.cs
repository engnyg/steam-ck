using System.Security.Cryptography;

namespace OpenSteamUnlocker.Core.Engine;

internal static class PayloadCrypto
{
    private static readonly byte[] _k =
    [
        0x4C, 0x59, 0x53, 0x54, 0x4F, 0x52, 0x45, 0x2D,
        0x38, 0x66, 0x37, 0x62, 0x32, 0x63, 0x31, 0x64,
        0x39, 0x65, 0x30, 0x61, 0x34, 0x66, 0x35, 0x38,
        0x62, 0x33, 0x37, 0x64, 0x36, 0x63, 0x32, 0x65,
    ];

    public static byte[] Encrypt(byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
        var result = new byte[aes.IV.Length + cipher.Length];
        aes.IV.CopyTo(result, 0);
        cipher.CopyTo(result, aes.IV.Length);
        return result;
    }

    public static byte[] Decrypt(byte[] payload)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        var iv = new byte[16];
        Array.Copy(payload, 0, iv, 0, 16);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(payload, 16, payload.Length - 16);
    }

    private static byte[] DeriveKey()
    {
        var salt = "osu-engine-v1"u8.ToArray();
        return Rfc2898DeriveBytes.Pbkdf2(_k, salt, 10000, HashAlgorithmName.SHA256, 32);
    }
}
