#include "auth.hpp"
#include <winhttp_client.hpp>

#include <windows.h>
#include <wincrypt.h>
#include <string>
#include <vector>
#include <ctime>
#include <cstdio>

// ── 金鑰與伺服器設定 ──────────────────────────────────────────────────────
// TODO: 填入實際 HMAC Secret（與 keygen.py 相同的 32 bytes）
static const uint8_t HMAC_SECRET[32] = {0};
static const wchar_t* API_URL = L"https://api.ourdomain.com/api/verify_cdkey";
static const wchar_t* CHECK_URL = L"https://api.ourdomain.com/api/check_app";

// Registry 路徑（存放已驗證的 LY 金鑰）
static const char* REG_PATH = "SOFTWARE\\OSU\\Auth";
static const char* REG_KEY  = "LicenseKey";

// ── Base36 ────────────────────────────────────────────────────────────────
static const char CHARSET[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

static std::string to_base36(uint64_t n, int len)
{
    std::string r(len, '0');
    for (int i = len - 1; i >= 0; i--) {
        r[i] = CHARSET[n % 36];
        n /= 36;
    }
    return r;
}

// ── HMAC-SHA256（使用 Windows CNG）───────────────────────────────────────
static std::vector<uint8_t> hmac_sha256(
    const uint8_t* key, size_t key_len,
    const uint8_t* msg, size_t msg_len)
{
    BCRYPT_ALG_HANDLE hAlg  = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;

    BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM,
                                 nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG);

    DWORD obj_len = 0, dummy = 0;
    BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH,
                      reinterpret_cast<PUCHAR>(&obj_len), sizeof(DWORD), &dummy, 0);
    std::vector<uint8_t> obj(obj_len);

    BCryptCreateHash(hAlg, &hHash, obj.data(), obj_len,
                     const_cast<PUCHAR>(key), static_cast<ULONG>(key_len), 0);
    BCryptHashData(hHash, const_cast<PUCHAR>(msg), static_cast<ULONG>(msg_len), 0);

    std::vector<uint8_t> digest(32);
    BCryptFinishHash(hHash, digest.data(), 32, 0);
    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);
    return digest;
}

// ── LY 金鑰格式驗證（離線 HMAC） ─────────────────────────────────────────
static bool verify_key_format(const std::string& key)
{
    // 正規化：移除 "LY-" 及 "-"
    std::string clean;
    for (char c : key) {
        if (c != '-') clean += static_cast<char>(toupper(c));
    }
    // 移除前綴 "LY"
    if (clean.size() >= 2 && clean[0] == 'L' && clean[1] == 'Y')
        clean = clean.substr(2);

    if (clean.size() != 20) return false;
    for (char c : clean) {
        bool ok = false;
        for (int i = 0; i < 36; i++) if (CHARSET[i] == c) { ok = true; break; }
        if (!ok) return false;
    }

    std::string serial = clean.substr(0, 12);
    std::string check  = clean.substr(12, 8);

    auto mac = hmac_sha256(HMAC_SECRET, 32,
                            reinterpret_cast<const uint8_t*>(serial.data()),
                            serial.size());

    uint64_t n = 0;
    for (int i = 0; i < 6; i++) n = (n << 8) | mac[i];

    return to_base36(n, 8) == check;
}

// ── Registry 讀寫 ─────────────────────────────────────────────────────────
static std::string read_registry_key()
{
    HKEY hk;
    if (RegOpenKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, KEY_READ, &hk) != ERROR_SUCCESS)
        return {};
    char buf[256] = {};
    DWORD len = sizeof(buf);
    RegQueryValueExA(hk, REG_KEY, nullptr, nullptr,
                     reinterpret_cast<LPBYTE>(buf), &len);
    RegCloseKey(hk);
    return std::string(buf);
}

static void write_registry_key(const std::string& key_val)
{
    HKEY hk;
    RegCreateKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, nullptr,
                    REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hk, nullptr);
    RegSetValueExA(hk, REG_KEY, 0, REG_SZ,
                   reinterpret_cast<const BYTE*>(key_val.c_str()),
                   static_cast<DWORD>(key_val.size() + 1));
    RegCloseKey(hk);
}

// ── 線上驗證 ──────────────────────────────────────────────────────────────
static bool verify_key_online(const std::string& lkey)
{
    try {
        // 計算請求簽章（防重放）
        long long ts = static_cast<long long>(std::time(nullptr));
        std::string ts_str = std::to_string(ts);
        std::string sig_msg = lkey + ts_str;

        auto sig = hmac_sha256(HMAC_SECRET, 32,
                                reinterpret_cast<const uint8_t*>(sig_msg.data()),
                                sig_msg.size());
        // 轉 hex
        char sig_hex[65] = {};
        static const char HX[] = "0123456789abcdef";
        for (int i = 0; i < 32; i++) {
            sig_hex[i*2]   = HX[sig[i]>>4];
            sig_hex[i*2+1] = HX[sig[i]&0xF];
        }

        std::string body = "{\"key\":\"" + lkey + "\",\"ts\":" + ts_str +
                           ",\"sig\":\"" + sig_hex + "\"}";
        auto resp = osu::http::post_json(API_URL, body);
        if (resp.status != 200) return false;

        // 簡單解析 "valid":true
        std::string r = resp.body_str();
        return r.find("\"valid\":true") != std::string::npos ||
               r.find("\"valid\": true") != std::string::npos;
    } catch (...) {
        return false;
    }
}

// ── 公開函式 ──────────────────────────────────────────────────────────────

bool auth_verify_key(const char* key)
{
    std::string k(key ? key : "");

    // 1. 離線格式驗證
    if (!verify_key_format(k)) return false;

    // 2. 線上驗證（失敗時回退到快取）
    if (verify_key_online(k)) {
        write_registry_key(k);
        return true;
    }

    // 3. 快取驗證（線上失敗時允許）
    std::string cached = read_registry_key();
    if (!cached.empty() && cached == k && verify_key_format(cached))
        return true;

    return false;
}

bool auth_check_cached_key()
{
    std::string cached = read_registry_key();
    if (cached.empty()) return false;
    return verify_key_format(cached);
}

bool auth_check_app_online(int appid)
{
    std::string cached = read_registry_key();
    if (cached.empty() || !verify_key_format(cached)) return false;
    try {
        std::string body = "{\"key\":\"" + cached +
                           "\",\"appid\":" + std::to_string(appid) + "}";
        auto resp = osu::http::post_json(CHECK_URL, body);
        if (resp.status != 200) return false;
        std::string r = resp.body_str();
        return r.find("\"allowed\":true") != std::string::npos ||
               r.find("\"allowed\": true") != std::string::npos;
    } catch (...) {
        return false;
    }
}
