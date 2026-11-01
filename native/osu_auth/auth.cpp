#include "auth.hpp"
#include <winhttp_client.hpp>

#include <windows.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <string>
#include <vector>
#include <ctime>
#include <cstdio>
#include <cstring>

// ── 金鑰與伺服器設定 ──────────────────────────────────────────────────────
// TODO: 填入實際 HMAC Secret（與 keygen.py 相同的 32 bytes）
static const uint8_t HMAC_SECRET[32] = {0};
static const wchar_t* API_URL   = L"https://api.ourdomain.com/api/verify_cdkey";
static const wchar_t* CHECK_URL = L"https://api.ourdomain.com/api/check_app";

// Registry 路徑（對齊計畫書 §二）
static const char* REG_PATH     = "SOFTWARE\\OSU";
static const char* REG_VAL_KEY  = "CDKey";
static const char* REG_VAL_TS   = "LastOnlineOK";   // DWORD: unix timestamp

static const int CACHE_TTL_SECS = 7 * 24 * 3600;    // 7 天

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
    BCRYPT_ALG_HANDLE  hAlg  = nullptr;
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
    std::string clean;
    for (char c : key) {
        if (c != '-') clean += static_cast<char>(toupper(c));
    }
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
static std::string read_reg_string(const char* val_name)
{
    HKEY hk;
    if (RegOpenKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, KEY_READ, &hk) != ERROR_SUCCESS)
        return {};
    char buf[256] = {};
    DWORD len = sizeof(buf);
    RegQueryValueExA(hk, val_name, nullptr, nullptr,
                     reinterpret_cast<LPBYTE>(buf), &len);
    RegCloseKey(hk);
    return std::string(buf);
}

static DWORD read_reg_dword(const char* val_name)
{
    HKEY hk;
    if (RegOpenKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, KEY_READ, &hk) != ERROR_SUCCESS)
        return 0;
    DWORD val = 0, len = sizeof(val);
    RegQueryValueExA(hk, val_name, nullptr, nullptr,
                     reinterpret_cast<LPBYTE>(&val), &len);
    RegCloseKey(hk);
    return val;
}

static void write_reg_string(const char* val_name, const std::string& val)
{
    HKEY hk;
    RegCreateKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, nullptr,
                    REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hk, nullptr);
    RegSetValueExA(hk, val_name, 0, REG_SZ,
                   reinterpret_cast<const BYTE*>(val.c_str()),
                   static_cast<DWORD>(val.size() + 1));
    RegCloseKey(hk);
}

static void write_reg_dword(const char* val_name, DWORD val)
{
    HKEY hk;
    RegCreateKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, nullptr,
                    REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hk, nullptr);
    RegSetValueExA(hk, val_name, 0, REG_DWORD,
                   reinterpret_cast<const BYTE*>(&val), sizeof(val));
    RegCloseKey(hk);
}

// ── 線上驗證 ──────────────────────────────────────────────────────────────
static bool verify_key_online(const std::string& lkey)
{
    try {
        long long ts = static_cast<long long>(std::time(nullptr));
        std::string ts_str = std::to_string(ts);
        std::string sig_msg = lkey + ts_str;

        auto sig = hmac_sha256(HMAC_SECRET, 32,
                                reinterpret_cast<const uint8_t*>(sig_msg.data()),
                                sig_msg.size());
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

        std::string r = resp.body_str();
        return r.find("\"valid\":true") != std::string::npos ||
               r.find("\"valid\": true") != std::string::npos;
    } catch (...) {
        return false;
    }
}

// ── 公開函式（以 extern "C" 匯出，供反射載入後解析）──────────────────────

extern "C" __declspec(dllexport)
bool auth_verify_key(const char* key)
{
    std::string k(key ? key : "");

    if (!verify_key_format(k)) return false;

    if (verify_key_online(k)) {
        write_reg_string(REG_VAL_KEY, k);
        write_reg_dword(REG_VAL_TS, static_cast<DWORD>(std::time(nullptr)));
        return true;
    }

    // 離線備援：快取金鑰 + TTL 檢查
    std::string cached = read_reg_string(REG_VAL_KEY);
    if (!cached.empty() && cached == k && verify_key_format(cached)) {
        DWORD last_ok = read_reg_dword(REG_VAL_TS);
        DWORD now     = static_cast<DWORD>(std::time(nullptr));
        if (last_ok > 0 && (now - last_ok) < static_cast<DWORD>(CACHE_TTL_SECS))
            return true;
    }

    return false;
}

extern "C" __declspec(dllexport)
bool auth_check_cached_key()
{
    std::string cached = read_reg_string(REG_VAL_KEY);
    if (cached.empty()) return false;
    if (!verify_key_format(cached)) return false;

    // TTL 檢查
    DWORD last_ok = read_reg_dword(REG_VAL_TS);
    DWORD now     = static_cast<DWORD>(std::time(nullptr));
    if (last_ok == 0 || (now - last_ok) >= static_cast<DWORD>(CACHE_TTL_SECS))
        return false;

    return true;
}

extern "C" __declspec(dllexport)
bool auth_check_app_online(int appid)
{
    std::string cached = read_reg_string(REG_VAL_KEY);
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
