#include "core.hpp"

#include <windows.h>
#include <psapi.h>
#include <cstdint>
#include <cstring>
#include <cstdlib>

#pragma comment(lib, "psapi.lib")

// ── IClientUser vtable 索引（x64，由 RE 分析 steamclient64.dll 確認） ─────
constexpr int VT_BIsSubscribedApp       = 171;
constexpr int VT_GetSubscribedApps      = 172;
constexpr int VT_BRegisterActivationCode = 76; // TODO: 每次 steamclient64.dll 更新後需 RE 重新確認

// ── 原始函式指標 ──────────────────────────────────────────────────────────
using BIsSubscribedApp_t        = bool    (__thiscall*)(void*, uint32_t);
using GetSubscribedApps_t       = uint32_t(__thiscall*)(void*, uint32_t*, uint32_t, bool);

// EActivationCodeResult（僅列出本專案使用的值）
enum class EActResult : uint32_t { OK = 0, Invalid = 8 };

using BIsSubscribedApp_t         = bool    (__thiscall*)(void*, uint32_t);
using GetSubscribedApps_t        = uint32_t(__thiscall*)(void*, uint32_t*, uint32_t, bool);
using BRegisterActivationCode_t  = uint32_t(__thiscall*)(void*, const char*);

static BIsSubscribedApp_t         orig_BIsSubscribedApp         = nullptr;
static GetSubscribedApps_t        orig_GetSubscribedApps        = nullptr;
static BRegisterActivationCode_t  orig_BRegisterActivationCode  = nullptr;

// 快取找到的 vtable 指標，避免重複掃描
static void** g_cached_vtable = nullptr;

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

// ── Hook：BRegisterActivationCode ────────────────────────────────────────

static uint32_t __fastcall hook_BRegisterActivationCode(void* this_, const char* key)
{
    // LY- 開頭才攔截，其他格式透傳給 Steam 原本處理
    if (!key || strncmp(key, "LY-", 3) != 0)
        return orig_BRegisterActivationCode(this_, key);

    auto* auth = core_get_auth();
    if (!auth || !auth->verify_key)
        return static_cast<uint32_t>(EActResult::Invalid);

    int appids[512] = {};
    int count = 512;
    if (!auth->verify_key(key, appids, &count) || count <= 0)
        return static_cast<uint32_t>(EActResult::Invalid);

    core_update_unlocked_apps(appids, count);
    return static_cast<uint32_t>(EActResult::OK);
}

// ── Hook：GetSubscribedApps ───────────────────────────────────────────────

static uint32_t __fastcall hook_GetSubscribedApps(
    void* this_, uint32_t* pvecAppIDs, uint32_t unMaxAppIDs, bool bIncludePlayedFreeGames)
{
    uint32_t real_count = 0;
    if (orig_GetSubscribedApps)
        real_count = orig_GetSubscribedApps(this_, pvecAppIDs, unMaxAppIDs, bIncludePlayedFreeGames);

    auto* auth = core_get_auth();
    if (!auth || !auth->check_cached_key || !auth->check_cached_key())
        return real_count;

    int  unlocked[512] = {};
    int  ucount        = core_get_unlocked_apps(unlocked, 512);
    if (ucount <= 0) return real_count;

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

// ── RTTI vtable 搜尋 ────────────────────────────────────────────────────

static void** find_vtable_by_rtti(HMODULE mod, const char* type_name)
{
    MODULEINFO mi = {};
    GetModuleInformation(GetCurrentProcess(), mod, &mi, sizeof(mi));
    auto* base = static_cast<uint8_t*>(mi.lpBaseOfDll);
    size_t size = mi.SizeOfImage;

    size_t name_len = strlen(type_name);

    // 1. 搜尋 RTTI type descriptor 的名稱字串
    uint8_t* td_name = nullptr;
    for (size_t i = 0; i + name_len < size; i++) {
        if (memcmp(base + i, type_name, name_len + 1) == 0) {
            td_name = base + i;
            break;
        }
    }
    if (!td_name) return nullptr;

    // TypeDescriptor = { void* pVFTable, void* spare, char name[] }
    // name 在 TD 起始 +16 bytes（x64）
    uint8_t* td = td_name - 2 * sizeof(void*);
    auto td_rva = static_cast<uint32_t>(td - base);

    // 2. 搜尋 RTTI Complete Object Locator 引用此 TD
    // COL = { uint32 sig=1, uint32 offset, uint32 cdOffset, uint32 pTD_rva, uint32 pCHD_rva, uint32 pSelf_rva }
    for (size_t i = 0; i + 24 <= size; i += 4) {
        auto sig = *reinterpret_cast<uint32_t*>(base + i);
        if (sig != 1) continue;

        auto col_td = *reinterpret_cast<uint32_t*>(base + i + 12);
        if (col_td != td_rva) continue;

        // 額外驗證：pSelf_rva 應指回 COL 本身
        auto self_rva = *reinterpret_cast<uint32_t*>(base + i + 20);
        if (self_rva != static_cast<uint32_t>(i)) continue;

        // 3. vtable[-1] 存放指向此 COL 的指標
        auto col_va = reinterpret_cast<uintptr_t>(base + i);
        for (size_t j = 0; j + 8 <= size; j += 8) {
            if (*reinterpret_cast<uintptr_t*>(base + j) == col_va) {
                return reinterpret_cast<void**>(base + j + 8);
            }
        }
    }
    return nullptr;
}

static void** find_iclientuser_vtable()
{
    if (g_cached_vtable) return g_cached_vtable;

    HMODULE hSteam = GetModuleHandleA("steamclient64.dll");
    if (!hSteam) hSteam = GetModuleHandleA("steamclient.dll");
    if (!hSteam) return nullptr;

    // 依序嘗試：IClientUserMap（dispatch 層）→ CUser（實作層）
    static const char* rtti_names[] = {
        ".?AVIClientUserMap@@",
        ".?AVCUser@@",
    };

    for (auto* name : rtti_names) {
        void** vt = find_vtable_by_rtti(hSteam, name);
        if (vt) {
            g_cached_vtable = vt;
            return vt;
        }
    }
    return nullptr;
}

// ── Vtable Patch ──────────────────────────────────────────────────────────

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
    core_inject_depot_keys_to_registry();

    void** vtable = find_iclientuser_vtable();
    if (!vtable) return;

    vtable_hook(vtable, VT_BIsSubscribedApp,
                reinterpret_cast<void*>(hook_BIsSubscribedApp),
                reinterpret_cast<void**>(&orig_BIsSubscribedApp));

    vtable_hook(vtable, VT_GetSubscribedApps,
                reinterpret_cast<void*>(hook_GetSubscribedApps),
                reinterpret_cast<void**>(&orig_GetSubscribedApps));

    vtable_hook(vtable, VT_BRegisterActivationCode,
                reinterpret_cast<void*>(hook_BRegisterActivationCode),
                reinterpret_cast<void**>(&orig_BRegisterActivationCode));
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
    if (orig_BRegisterActivationCode)
        vtable_hook(vtable, VT_BRegisterActivationCode,
                    reinterpret_cast<void*>(orig_BRegisterActivationCode), nullptr);
}
