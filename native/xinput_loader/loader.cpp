#include "loader.hpp"
#include <crypto.hpp>
#include <pe_loader.hpp>
#include <winhttp_client.hpp>
#include <osu_abi.hpp>

#include <windows.h>
#include <wincrypt.h>
#include <shlobj.h>
#include <string>
#include <vector>
#include <fstream>
#include <filesystem>
#include <stdexcept>
#include <cstring>

namespace fs = std::filesystem;

// ── 金鑰（以 3 段 XOR 儲存，避免靜態掃描）──────────────────────────────────
// TODO: 填入實際金鑰片段
static const uint8_t KEY_VER_A[32] = {0};
static const uint8_t KEY_VER_B[32] = {0};
static const uint8_t KEY_VER_C[32] = {0};

static const uint8_t KEY_CORE_A[32] = {0};
static const uint8_t KEY_CORE_B[32] = {0};
static const uint8_t KEY_CORE_C[32] = {0};

static const uint8_t KEY_AUTH_A[32] = {0};
static const uint8_t KEY_AUTH_B[32] = {0};
static const uint8_t KEY_AUTH_C[32] = {0};

static const uint8_t KEY_DEPOT_A[32] = {0};
static const uint8_t KEY_DEPOT_B[32] = {0};
static const uint8_t KEY_DEPOT_C[32] = {0};

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

// ── 計算 MD5 hex ──────────────────────────────────────────────────────────
static std::string md5_hex(const uint8_t* data, size_t len)
{
    HCRYPTPROV hp = 0; HCRYPTHASH hh = 0;
    CryptAcquireContextA(&hp, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT);
    CryptCreateHash(hp, CALG_MD5, 0, 0, &hh);
    CryptHashData(hh, data, static_cast<DWORD>(len), 0);
    BYTE md5[16] = {}; DWORD dl = 16;
    CryptGetHashParam(hh, HP_HASHVAL, md5, &dl, 0);
    CryptDestroyHash(hh); CryptReleaseContext(hp, 0);

    char hex[33] = {};
    static const char HX[] = "0123456789abcdef";
    for (int i = 0; i < 16; i++) {
        hex[i*2]   = HX[md5[i]>>4];
        hex[i*2+1] = HX[md5[i]&0xF];
    }
    return std::string(hex, 32);
}

// ── Manifest 條目 ─────────────────────────────────────────────────────────
struct ManifestEntry {
    std::string file_name;  // e.g. "appcache/httpcache/3b/<mCode>"
    std::string url;
    std::string hash;       // 加密檔的 MD5
};

// ── 簡易 JSON 解析（只處理 WindowsFile64Stable 陣列中的物件）──────────────
static std::string extract_string(const std::string& json, size_t start, const std::string& key)
{
    std::string search = "\"" + key + "\"";
    auto pos = json.find(search, start);
    if (pos == std::string::npos) return {};
    pos = json.find(':', pos);
    if (pos == std::string::npos) return {};
    pos = json.find('"', pos);
    if (pos == std::string::npos) return {};
    auto end = json.find('"', pos + 1);
    if (end == std::string::npos) return {};
    return json.substr(pos + 1, end - pos - 1);
}

static std::vector<ManifestEntry> parse_file_entries(const std::string& json)
{
    std::vector<ManifestEntry> entries;
    std::string arr_key = "\"WindowsFile64Stable\"";
    auto arr_pos = json.find(arr_key);
    if (arr_pos == std::string::npos) return entries;

    auto bracket = json.find('[', arr_pos);
    if (bracket == std::string::npos) return entries;

    size_t pos = bracket;
    while (true) {
        auto obj_start = json.find('{', pos);
        if (obj_start == std::string::npos) break;
        auto obj_end = json.find('}', obj_start);
        if (obj_end == std::string::npos) break;

        // 超出陣列範圍
        auto arr_end = json.find(']', bracket);
        if (arr_end != std::string::npos && obj_start > arr_end) break;

        ManifestEntry e;
        e.file_name = extract_string(json, obj_start, "FileName");
        e.url       = extract_string(json, obj_start, "URL");
        e.hash      = extract_string(json, obj_start, "Hash");
        entries.push_back(std::move(e));
        pos = obj_end + 1;
    }
    return entries;
}

static std::string extract_top_string(const std::string& json, const std::string& key)
{
    return extract_string(json, 0, key);
}

// ── 下載或讀取快取 ────────────────────────────────────────────────────────
static std::vector<uint8_t> fetch_or_cache(
    const ManifestEntry& entry, const std::string& mcode, const fs::path& steam_path)
{
    // 將 <mCode> 替換成實際值來得到本地路徑
    std::string local_rel = entry.file_name;
    auto placeholder = local_rel.find("<mCode>");
    if (placeholder != std::string::npos)
        local_rel.replace(placeholder, 7, mcode);

    fs::path cache_file = steam_path / local_rel;

    bool need_download = true;
    if (fs::exists(cache_file)) {
        std::ifstream f(cache_file, std::ios::binary);
        std::vector<uint8_t> buf(std::istreambuf_iterator<char>(f), {});
        std::string local_md5 = md5_hex(buf.data(), buf.size());
        if (_stricmp(local_md5.c_str(), entry.hash.c_str()) == 0) {
            need_download = false;
            return buf;
        }
    }

    // 下載
    std::wstring wurl(entry.url.begin(), entry.url.end());
    auto resp = osu::http::get(wurl);
    if (resp.status != 200)
        return {};

    // 寫入快取
    fs::create_directories(cache_file.parent_path());
    std::ofstream out(cache_file, std::ios::binary);
    out.write(reinterpret_cast<const char*>(resp.body.data()), resp.body.size());

    return resp.body;
}

// ── 主要載入流程 ──────────────────────────────────────────────────────────
static HMODULE g_core = nullptr;
static HMODULE g_auth = nullptr;

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

        // 3. 解析 manifest 條目
        auto entries = parse_file_entries(manifest);
        if (entries.size() < 3) return;

        std::string mcode      = compute_mcode();
        fs::path    steam_path = get_steam_path();

        // 按 FileName 前綴分辨：3b→Core, 3a→Auth, 3c→Depot
        ManifestEntry *core_entry = nullptr, *auth_entry = nullptr, *depot_entry = nullptr;
        for (auto& e : entries) {
            if      (e.file_name.find("/3b/") != std::string::npos) core_entry  = &e;
            else if (e.file_name.find("/3a/") != std::string::npos) auth_entry  = &e;
            else if (e.file_name.find("/3c/") != std::string::npos) depot_entry = &e;
        }
        if (!core_entry || !auth_entry || !depot_entry) return;

        // 4. 下載（或快取）各加密元件
        auto enc_core  = fetch_or_cache(*core_entry,  mcode, steam_path);
        auto enc_auth  = fetch_or_cache(*auth_entry,  mcode, steam_path);
        auto enc_depot = fetch_or_cache(*depot_entry, mcode, steam_path);
        if (enc_core.empty() || enc_auth.empty() || enc_depot.empty()) return;

        // 5. 解密各元件
        auto core_key  = osu::crypto::assemble_key(KEY_CORE_A,  KEY_CORE_B,  KEY_CORE_C,  32);
        auto auth_key  = osu::crypto::assemble_key(KEY_AUTH_A,  KEY_AUTH_B,  KEY_AUTH_C,  32);
        auto depot_key = osu::crypto::assemble_key(KEY_DEPOT_A, KEY_DEPOT_B, KEY_DEPOT_C, 32);

        auto core_dll  = osu::crypto::decrypt_payload(enc_core.data(),  enc_core.size(),
                                                       core_key.data(),  core_key.size());
        auto auth_dll  = osu::crypto::decrypt_payload(enc_auth.data(),  enc_auth.size(),
                                                       auth_key.data(),  auth_key.size());
        auto depot_raw = osu::crypto::decrypt_payload(enc_depot.data(), enc_depot.size(),
                                                       depot_key.data(), depot_key.size());

        // 6. 記憶體載入 OSU_Auth.dll
        g_auth = osu::pe::map_from_memory(auth_dll.data(), auth_dll.size());
        if (!g_auth) return;

        // 7. 建構 Auth API 表
        osu_auth_api auth_api = {};
        auth_api.size             = sizeof(osu_auth_api);
        auth_api.verify_key       = osu::pe::get_proc<osu_auth_verify_key_fn>      (g_auth, "auth_verify_key");
        auth_api.check_cached_key = osu::pe::get_proc<osu_auth_check_cached_key_fn>(g_auth, "auth_check_cached_key");
        auth_api.check_app_online = osu::pe::get_proc<osu_auth_check_app_online_fn>(g_auth, "auth_check_app_online");

        // 8. 記憶體載入 OSU_Core.dll
        g_core = osu::pe::map_from_memory(core_dll.data(), core_dll.size());
        if (!g_core) return;

        // 9. 把 Auth API 和 depot blob 推進 Core
        auto set_auth  = osu::pe::get_proc<osu_core_set_auth_impl_fn>       (g_core, "core_set_auth_impl");
        auto set_depot = osu::pe::get_proc<osu_core_set_depot_blob_fn>      (g_core, "core_set_depot_blob");
        auto set_apps  = osu::pe::get_proc<osu_core_set_online_apps_json_fn>(g_core, "core_set_online_apps_csv");
        auto init      = osu::pe::get_proc<osu_core_init_fn>                (g_core, "core_init");

        if (set_auth)  set_auth(&auth_api);
        if (set_depot) set_depot(depot_raw.data(), depot_raw.size());

        // 從 manifest 取 AppIdInOnline（CSV 字串）
        std::string online_csv = extract_top_string(manifest, "AppIdInOnline");
        if (set_apps && !online_csv.empty()) set_apps(online_csv.c_str());

        // 10. 啟動 Core（等 steamclient64.dll 載入後再裝 hook）
        if (init) init();

    } catch (...) {
        // 靜默失敗：不影響 Steam 正常啟動
    }
}
