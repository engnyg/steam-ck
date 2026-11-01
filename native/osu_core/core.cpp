#include "core.hpp"
#include <osu_abi.hpp>

#include <windows.h>
#include <string>
#include <unordered_map>
#include <vector>
#include <sstream>
#include <algorithm>
#include <cstring>
#include <cstdlib>

// ── Loader 傳入的 Auth API（反射載入，不能靜態連結）─────────────────────
static const osu_auth_api* g_auth = nullptr;

// ── Depot Key 快取 ────────────────────────────────────────────────────────
static std::unordered_map<uint32_t, std::vector<uint8_t>> g_depot_keys;

// ── AppIdInOnline ─────────────────────────────────────────────────────────
static std::vector<int> g_online_app_ids;

// ── 簡易 JSON depot key 解析 ──────────────────────────────────────────────
static void parse_depot_keys(const char* json, size_t len)
{
    std::string s(json, len);
    size_t pos = 0;
    while (pos < s.size()) {
        size_t ks = s.find('"', pos);
        if (ks == std::string::npos) break;
        size_t ke = s.find('"', ks + 1);
        if (ke == std::string::npos) break;

        std::string id_str = s.substr(ks + 1, ke - ks - 1);

        size_t colon = s.find(':', ke);
        if (colon == std::string::npos) break;
        size_t vs = s.find('"', colon);
        if (vs == std::string::npos) break;
        size_t ve = s.find('"', vs + 1);
        if (ve == std::string::npos) break;

        std::string hex = s.substr(vs + 1, ve - vs - 1);

        try {
            uint32_t depot_id = static_cast<uint32_t>(std::stoul(id_str));
            if (hex.size() == 64) {
                std::vector<uint8_t> key(32);
                for (int i = 0; i < 32; i++) {
                    key[i] = static_cast<uint8_t>(
                        std::stoul(hex.substr(i * 2, 2), nullptr, 16));
                }
                g_depot_keys[depot_id] = std::move(key);
            }
        } catch (...) {}

        pos = ve + 1;
    }
}

// ── Loader 呼叫的注入介面（extern "C" 匯出）────────────────────────────

extern "C" __declspec(dllexport)
void core_set_auth_impl(const osu_auth_api* api)
{
    if (api && api->size >= sizeof(osu_auth_api))
        g_auth = api;
}

extern "C" __declspec(dllexport)
void core_set_depot_blob(const uint8_t* data, size_t len)
{
    if (data && len > 2) {
        parse_depot_keys(reinterpret_cast<const char*>(data), len);
    }
}

extern "C" __declspec(dllexport)
void core_set_online_apps_csv(const char* csv)
{
    g_online_app_ids.clear();
    if (!csv) return;
    std::string s(csv);
    std::istringstream ss(s);
    std::string tok;
    while (std::getline(ss, tok, ',')) {
        int id = std::atoi(tok.c_str());
        if (id > 0) g_online_app_ids.push_back(id);
    }
}

// ── 公開函式 ──────────────────────────────────────────────────────────────

extern "C" __declspec(dllexport)
void core_init()
{
    // Auth 檢查（透過 loader 傳入的函式指標）
    if (!g_auth || !g_auth->check_cached_key || !g_auth->check_cached_key())
        return;

    // 安裝 vtable hooks（等 steamclient64.dll 載入後再做）
    for (int i = 0; i < 60; i++) {
        if (GetModuleHandleA("steamclient64.dll") ||
            GetModuleHandleA("steamclient.dll"))
            break;
        Sleep(500);
    }
    hooks_install();
}

const uint8_t* core_get_depot_key(uint32_t depot_id, size_t* out_len)
{
    auto it = g_depot_keys.find(depot_id);
    if (it == g_depot_keys.end()) {
        if (out_len) *out_len = 0;
        return nullptr;
    }
    if (out_len) *out_len = it->second.size();
    return it->second.data();
}

bool core_is_online_app(int appid)
{
    return std::find(g_online_app_ids.begin(), g_online_app_ids.end(), appid)
           != g_online_app_ids.end();
}

const osu_auth_api* core_get_auth()
{
    return g_auth;
}
