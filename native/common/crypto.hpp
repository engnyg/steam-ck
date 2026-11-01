#pragma once
// AES-256-CBC + zlib 解密（與 SteamTools 相同格式）
// 格式：[ IV 16B ][ uint32_LE 原始長度 ][ zlib 資料 ][ PKCS7 padding ]

#include <windows.h>
#include <wincrypt.h>
#include <vector>
#include <cstdint>
#include <cstring>
#include <stdexcept>

// zlib inflate（靜態連結或使用 Windows 內建）
#include <zlib.h>

namespace osu::crypto {

// XOR 混淆拆分的金鑰重組
// 用法：auto key = assemble_key(PART_A, PART_B, PART_C);
inline std::vector<uint8_t> assemble_key(
    const uint8_t* a, const uint8_t* b, const uint8_t* c, size_t len)
{
    std::vector<uint8_t> key(len);
    for (size_t i = 0; i < len; i++)
        key[i] = a[i] ^ b[i] ^ c[i];
    return key;
}

// AES-256-CBC 解密（使用 Windows CNG）
inline std::vector<uint8_t> aes_cbc_decrypt(
    const uint8_t* data, size_t data_len,
    const uint8_t* key,  size_t key_len)
{
    if (data_len < 16)
        throw std::runtime_error("data too short");

    const uint8_t* iv      = data;
    const uint8_t* payload = data + 16;
    size_t         pay_len = data_len - 16;

    BCRYPT_ALG_HANDLE  hAlg  = nullptr;
    BCRYPT_KEY_HANDLE  hKey  = nullptr;
    NTSTATUS           status;

    struct Guard {
        BCRYPT_ALG_HANDLE* alg; BCRYPT_KEY_HANDLE* key;
        ~Guard() {
            if (*key) BCryptDestroyKey(*key);
            if (*alg) BCryptCloseAlgorithmProvider(*alg, 0);
        }
    } guard{&hAlg, &hKey};

    status = BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_AES_ALGORITHM, nullptr, 0);
    if (!BCRYPT_SUCCESS(status)) throw std::runtime_error("BCryptOpenAlgorithmProvider");

    // 設定 CBC 模式
    status = BCryptSetProperty(hAlg, BCRYPT_CHAINING_MODE,
        (PUCHAR)BCRYPT_CHAIN_MODE_CBC, sizeof(BCRYPT_CHAIN_MODE_CBC), 0);
    if (!BCRYPT_SUCCESS(status)) throw std::runtime_error("BCryptSetProperty CBC");

    // 匯入金鑰
    DWORD keyObjSize = 0, dummy = 0;
    BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH, (PUCHAR)&keyObjSize, sizeof(DWORD), &dummy, 0);
    std::vector<uint8_t> keyObj(keyObjSize);

    status = BCryptGenerateSymmetricKey(hAlg, &hKey, keyObj.data(), keyObjSize,
                                        (PUCHAR)key, (ULONG)key_len, 0);
    if (!BCRYPT_SUCCESS(status)) throw std::runtime_error("BCryptGenerateSymmetricKey");

    // 解密（IV 複製一份，BCrypt 會修改它）
    std::vector<uint8_t> iv_copy(iv, iv + 16);
    std::vector<uint8_t> out(pay_len);
    ULONG out_len = 0;

    status = BCryptDecrypt(hKey, (PUCHAR)payload, (ULONG)pay_len,
                           nullptr, iv_copy.data(), 16,
                           out.data(), (ULONG)pay_len, &out_len, BCRYPT_BLOCK_PADDING);
    if (!BCRYPT_SUCCESS(status)) throw std::runtime_error("BCryptDecrypt");

    out.resize(out_len);
    return out;
}

// zlib inflate
inline std::vector<uint8_t> zlib_inflate(const uint8_t* data, size_t len, size_t orig_len)
{
    std::vector<uint8_t> out(orig_len);
    uLongf dest_len = (uLongf)orig_len;
    int ret = ::uncompress(out.data(), &dest_len, data, (uLong)len);
    if (ret != Z_OK)
        throw std::runtime_error("zlib uncompress failed");
    out.resize(dest_len);
    return out;
}

// 完整解密：AES-CBC → zlib inflate
inline std::vector<uint8_t> decrypt_payload(
    const uint8_t* enc, size_t enc_len,
    const uint8_t* key, size_t key_len)
{
    auto dec = aes_cbc_decrypt(enc, enc_len, key, key_len);
    if (dec.size() < 4) throw std::runtime_error("decrypted too short");

    uint32_t orig_len;
    std::memcpy(&orig_len, dec.data(), 4);
    return zlib_inflate(dec.data() + 4, dec.size() - 4, orig_len);
}

} // namespace osu::crypto
