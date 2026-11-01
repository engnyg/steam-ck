#include "core.hpp"

#include <windows.h>
#include <cstdint>
#include <cstring>
#include <vector>

// ── Steam 介面 vtable 索引（x64 IClientUser） ─────────────────────────────
constexpr int VT_BIsSubscribedApp      = 163;
constexpr int VT_GetDepotDecryptionKey = 243;

// ── 原始函式指標 ──────────────────────────────────────────────────────────
using BIsSubscribedApp_t      = bool(__thiscall*)(void*, uint32_t);
using GetDepotDecryptionKey_t = bool(__thiscall*)(void*, uint32_t, void*, uint32_t*);

static BIsSubscribedApp_t      orig_BIsSubscribedApp      = nullptr;
static GetDepotDecryptionKey_t orig_GetDepotDecryptionKey = nullptr;

// ── Hook 實作 ─────────────────────────────────────────────────────────────

static bool __fastcall hook_BIsSubscribedApp(void* this_, uint32_t appid)
{
    auto* auth = core_get_auth();
    if (!auth || !auth->check_cached_key || !auth->check_cached_key())
        return orig_BIsSubscribedApp(this_, appid);

    if (core_is_online_app(static_cast<int>(appid))) {
        if (!auth->check_app_online || !auth->check_app_online(static_cast<int>(appid)))
            return orig_BIsSubscribedApp(this_, appid);
    }
    return true;
}

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

// ── Vtable Patch ──────────────────────────────────────────────────────────

static void** find_iclientuser_vtable()
{
    HMODULE hSteam = GetModuleHandleA("steamclient64.dll");
    if (!hSteam) hSteam = GetModuleHandleA("steamclient.dll");
    if (!hSteam) return nullptr;

    // TODO: 填入實際 byte pattern
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

    vtable_hook(vtable, VT_GetDepotDecryptionKey,
                reinterpret_cast<void*>(hook_GetDepotDecryptionKey),
                reinterpret_cast<void**>(&orig_GetDepotDecryptionKey));
}

void hooks_uninstall()
{
    void** vtable = find_iclientuser_vtable();
    if (!vtable) return;

    if (orig_BIsSubscribedApp)
        vtable_hook(vtable, VT_BIsSubscribedApp,
                    reinterpret_cast<void*>(orig_BIsSubscribedApp), nullptr);

    if (orig_GetDepotDecryptionKey)
        vtable_hook(vtable, VT_GetDepotDecryptionKey,
                    reinterpret_cast<void*>(orig_GetDepotDecryptionKey), nullptr);
}
