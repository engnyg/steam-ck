#include "core.hpp"

#include <windows.h>
#include <psapi.h>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <vector>
#include <algorithm>

#pragma comment(lib, "psapi.lib")

// ── Steam EResult ─────────────────────────────────────────────────────────
enum EResult : int {
    k_EResultOK                      = 1,
    k_EResultFail                    = 2,
    k_EResultInvalidParam            = 8,
    k_EResultProductNotFound         = 65,
    k_EResultDuplicateActivationCode = 113,
};

// ── Steam 介面 vtable 索引（x64 IClientUser） ─────────────────────────────
// TODO: 對目標版本 steamclient64.dll 以 IDA/模式掃描確認實際索引
constexpr int VT_BIsSubscribedApp      = 163;
constexpr int VT_GetSubscribedApps     = 167;  // 讓遊戲出現在遊戲庫
constexpr int VT_GetDepotDecryptionKey = 243;
constexpr int VT_ActivateGameWithKey   = 194;

// ── 原始函式指標 ──────────────────────────────────────────────────────────
using BIsSubscribedApp_t      = bool    (__thiscall*)(void*, uint32_t);
using GetSubscribedApps_t     = uint32_t(__thiscall*)(void*, uint32_t*, uint32_t, bool);
using GetDepotDecryptionKey_t = bool    (__thiscall*)(void*, uint32_t, void*, uint32_t*);
using ActivateGameWithKey_t   = EResult (__thiscall*)(void*, const char*);

static BIsSubscribedApp_t      orig_BIsSubscribedApp      = nullptr;
static GetSubscribedApps_t     orig_GetSubscribedApps     = nullptr;
static GetDepotDecryptionKey_t orig_GetDepotDecryptionKey = nullptr;
static ActivateGameWithKey_t   orig_ActivateGameWithKey   = nullptr;

// ── Hook：BIsSubscribedApp ────────────────────────────────────────────────

static bool __fastcall hook_BIsSubscribedApp(void* this_, uint32_t appid)
{
    auto* auth = core_get_auth();
    if (!auth || !auth->check_cached_key || !auth->check_cached_key())
        return orig_BIsSubscribedApp(this_, appid);

    if (!core_is_unlocked_app(static_cast<int>(appid)))
        return orig_BIsSubscribedApp(this_, appid);

    if (core_is_online_app(static_cast<int>(appid))) {
        if (!auth->check_app_online || !auth->check_app_online(static_cast<int>(appid)))
            return orig_BIsSubscribedApp(this_, appid);
    }
    return true;
}

// ── Hook：GetSubscribedApps ───────────────────────────────────────────────
// Steam 建立遊戲庫時呼叫此函式取得 AppID 清單
// 我們把解鎖的 AppID 注入到回傳清單，讓遊戲直接出現在遊戲庫

static uint32_t __fastcall hook_GetSubscribedApps(
    void* this_, uint32_t* pvecAppIDs, uint32_t unMaxAppIDs, bool bIncludePlayedFreeGames)
{
    // 先取得用戶真實擁有的遊戲
    uint32_t real_count = 0;
    if (orig_GetSubscribedApps)
        real_count = orig_GetSubscribedApps(this_, pvecAppIDs, unMaxAppIDs, bIncludePlayedFreeGames);

    auto* auth = core_get_auth();
    if (!auth || !auth->check_cached_key || !auth->check_cached_key())
        return real_count;

    // 取得解鎖的 AppID 清單
    int  unlocked[512] = {};
    int  ucount        = core_get_unlocked_apps(unlocked, 512);
    if (ucount <= 0) return real_count;

    // 注入（避免重複）
    uint32_t total = real_count;
    for (int i = 0; i < ucount && total < unMaxAppIDs; i++) {
        uint32_t id = static_cast<uint32_t>(unlocked[i]);
        bool already = false;
        for (uint32_t j = 0; j < real_count; j++) {
            if (pvecAppIDs[j] == id) { already = true; break; }
        }
        if (!already) pvecAppIDs[total++] = id;
    }
    return total;
}

// ── Hook：GetDepotDecryptionKey ───────────────────────────────────────────

static bool __fastcall hook_GetDepotDecryptionKey(
    void* this_, uint32_t depot_id, void* key_buf, uint32_t* key_len)
{
    size_t klen = 0;
    const uint8_t* key = core_get_depot_key(depot_id, &klen);
    if (key && key_buf && key_len) {
        std::memcpy(key_buf, key, klen);
        *key_len = static_cast<uint32_t>(klen);
        return true;
    }
    if (orig_GetDepotDecryptionKey)
        return orig_GetDepotDecryptionKey(this_, depot_id, key_buf, key_len);
    return false;
}

// ── Hook：ActivateGameWithKey（Steam「啟動產品」對話框） ─────────────────

static EResult __fastcall hook_ActivateGameWithKey(void* this_, const char* key)
{
    if (!key) {
        if (orig_ActivateGameWithKey) return orig_ActivateGameWithKey(this_, key);
        return k_EResultInvalidParam;
    }

    bool is_ly = (strncmp(key, "LY-", 3) == 0 || strncmp(key, "ly-", 3) == 0)
                 && strlen(key) == 22;

    if (is_ly) {
        auto* auth = core_get_auth();
        if (!auth || !auth->verify_key) return k_EResultFail;

        int  appids[512] = {};
        int  count       = 512;
        bool ok = auth->verify_key(key, appids, &count);

        if (ok && count > 0) {
            // 熱更新解鎖清單，GetSubscribedApps hook 下次被呼叫時自動反映
            core_update_unlocked_apps(appids, count);
            return k_EResultOK;
        }
        return k_EResultProductNotFound;
    }

    if (orig_ActivateGameWithKey) return orig_ActivateGameWithKey(this_, key);
    return k_EResultFail;
}

// ── Pattern Scan ─────────────────────────────────────────────────────────

static uint8_t* pattern_scan(HMODULE mod, const char* pattern)
{
    MODULEINFO mi = {};
    GetModuleInformation(GetCurrentProcess(), mod, &mi, sizeof(mi));
    auto* base = static_cast<uint8_t*>(mi.lpBaseOfDll);
    size_t size = mi.SizeOfImage;

    // 解析 "48 8B ?? 0F 84 ?? ?? ?? ??" 格式（?? 為萬用位元組）
    struct PatByte { uint8_t val; bool wild; };
    std::vector<PatByte> bytes;
    for (const char* p = pattern; *p;) {
        while (*p == ' ') p++;
        if (!*p) break;
        if (*p == '?') {
            bytes.push_back({0, true});
            while (*p == '?') p++;
        } else {
            char hex[3] = {p[0], p[1], 0};
            bytes.push_back({static_cast<uint8_t>(strtoul(hex, nullptr, 16)), false});
            p += 2;
        }
    }
    if (bytes.empty()) return nullptr;

    for (size_t i = 0; i + bytes.size() <= size; i++) {
        bool match = true;
        for (size_t j = 0; j < bytes.size(); j++) {
            if (!bytes[j].wild && base[i + j] != bytes[j].val) {
                match = false;
                break;
            }
        }
        if (match) return base + i;
    }
    return nullptr;
}

// ── Vtable Patch ──────────────────────────────────────────────────────────

static void** find_iclientuser_vtable()
{
    HMODULE hSteam = GetModuleHandleA("steamclient64.dll");
    if (!hSteam) hSteam = GetModuleHandleA("steamclient.dll");
    if (!hSteam) return nullptr;

    // ────────────────────────────────────────────────────────────────────
    // TODO: 用 IDA 對目標版本 steamclient64.dll 確認以下 byte pattern
    //
    // 尋找方法：
    //   1. IDA 載入 steamclient64.dll
    //   2. 搜尋字串 "BIsSubscribedApp" 的 xref
    //   3. 找到包含該函式的 vtable（連續函式指標陣列）
    //   4. 取 vtable 起始前的 unique byte sequence 作為 pattern
    //   5. 確認 VT_BIsSubscribedApp 等索引
    //
    // 範例（需替換為實際 pattern）：
    // const char* sig = "48 8D 05 ?? ?? ?? ?? 48 89 01 48 8B C1 C3";
    // auto* found = pattern_scan(hSteam, sig);
    // if (!found) return nullptr;
    // int32_t offset = *reinterpret_cast<int32_t*>(found + 3);
    // return reinterpret_cast<void**>(found + 7 + offset);
    // ────────────────────────────────────────────────────────────────────

    return nullptr;
}

static bool vtable_hook(void** vtable, int index, void* new_fn, void** orig_fn)
{
    if (!vtable) return false;
    DWORD old_prot = 0;
    void** entry = &vtable[index];
    if (!VirtualProtect(entry, sizeof(void*), PAGE_READWRITE, &old_prot))
        return false;
    if (orig_fn) *orig_fn = *entry;
    *entry = new_fn;
    VirtualProtect(entry, sizeof(void*), old_prot, &old_prot);
    return true;
}

void hooks_install()
{
    void** vtable = find_iclientuser_vtable();
    if (!vtable) return;

    vtable_hook(vtable, VT_BIsSubscribedApp,
                reinterpret_cast<void*>(hook_BIsSubscribedApp),
                reinterpret_cast<void**>(&orig_BIsSubscribedApp));

    // 遊戲庫注入：讓解鎖的遊戲直接出現在 Steam 遊戲庫
    vtable_hook(vtable, VT_GetSubscribedApps,
                reinterpret_cast<void*>(hook_GetSubscribedApps),
                reinterpret_cast<void**>(&orig_GetSubscribedApps));

    vtable_hook(vtable, VT_GetDepotDecryptionKey,
                reinterpret_cast<void*>(hook_GetDepotDecryptionKey),
                reinterpret_cast<void**>(&orig_GetDepotDecryptionKey));

    vtable_hook(vtable, VT_ActivateGameWithKey,
                reinterpret_cast<void*>(hook_ActivateGameWithKey),
                reinterpret_cast<void**>(&orig_ActivateGameWithKey));
}

void hooks_uninstall()
{
    void** vtable = find_iclientuser_vtable();
    if (!vtable) return;

    if (orig_BIsSubscribedApp)
        vtable_hook(vtable, VT_BIsSubscribedApp,
                    reinterpret_cast<void*>(orig_BIsSubscribedApp), nullptr);
    if (orig_GetSubscribedApps)
        vtable_hook(vtable, VT_GetSubscribedApps,
                    reinterpret_cast<void*>(orig_GetSubscribedApps), nullptr);
    if (orig_GetDepotDecryptionKey)
        vtable_hook(vtable, VT_GetDepotDecryptionKey,
                    reinterpret_cast<void*>(orig_GetDepotDecryptionKey), nullptr);
    if (orig_ActivateGameWithKey)
        vtable_hook(vtable, VT_ActivateGameWithKey,
                    reinterpret_cast<void*>(orig_ActivateGameWithKey), nullptr);
}
