#include "core.hpp"
#include <auth.hpp>
#include <winhttp_client.hpp>

#include <windows.h>
#include <string>
#include <unordered_map>
#include <vector>
#include <sstream>
#include <algorithm>

// ── Depot Key 快取 ────────────────────────────────────────────────────────
static std::unordered_map<uint32_t, std::vector<uint8_t>> g_depot_keys;

// ManifestHub depot keys URL（與 DepotKeyService.cs 相同來源）
static const wchar_t* DEPOT_KEY_URL =
    L"https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json";

// AppIdInOnline（這些 AppID 每次啟動需線上驗證）
// 由 version manifest 下載後更新；此處為預設值
static std::vector<int> g_online_app_ids = {
    730, 570, 1172470, 413150, 3097560,
    717690, 2737070, 2336220, 1084600, 1203620, 632360,
};

// ── 簡易 JSON depot key 解析 ──────────────────────────────────────────────
// 格式：{ "123456": "aabbccdd...", ... }
static void parse_depot_keys(const std::string& json)
{
    // 逐行尋找 "depotId": "hexKey" 對
    size_t pos = 0;
    while (pos < json.size()) {
        size_t ks = json.find('"', pos);
        if (ks == std::string::npos) break;
        size_t ke = json.find('"', ks + 1);
        if (ke == std::string::npos) break;

        std::string id_str = json.substr(ks + 1, ke - ks - 1);

        size_t colon = json.find(':', ke);
        if (colon == std::string::npos) break;
        size_t vs = json.find('"', colon);
        if (vs == std::string::npos) break;
        size_t ve = json.find('"', vs + 1);
        if (ve == std::string::npos) break;

        std::string hex = json.substr(vs + 1, ve - vs - 1);

        // 解析 depot ID
        try {
            uint32_t depot_id = static_cast<uint32_t>(std::stoul(id_str));
            // 解析 hex key
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

// ── 公開函式 ──────────────────────────────────────────────────────────────

void core_init()
{
    // 驗證授權
    if (!auth_check_cached_key()) return;

    // 下載 Depot Keys
    try {
        auto resp = osu::http::get(DEPOT_KEY_URL);
        if (resp.status == 200) {
            parse_depot_keys(resp.body_str());
        }
    } catch (...) {}

    // 安裝 vtable hooks
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

void core_set_online_apps(const int* ids, int count)
{
    g_online_app_ids.assign(ids, ids + count);
}
