#include "core.hpp"

#include <windows.h>
#include <cstdint>
#include <cstring>
#include <vector>

// ── Steam EResult ─────────────────────────────────────────────────────────
enum EResult : int {
    k_EResultOK                    = 1,
    k_EResultFail                  = 2,
    k_EResultInvalidParam          = 8,
    k_EResultProductNotFound       = 65,
    k_EResultDuplicateActivationCode = 113,
};

// ── Steam 介面 vtable 索引（x64 IClientUser） ─────────────────────────────
// 透過 IDA / 模式掃描確認後填入實際值
constexpr int VT_BIsSubscribedApp        = 163;
constexpr int VT_GetDepotDecryptionKey   = 243;
// ActivateGameWithKey：用戶在 Steam 「啟動產品」對話框按下確認時呼叫
// TODO: 對目標版本 steamclient64.dll 確認實際索引
constexpr int VT_ActivateGameWithKey     = 194;

// ── 原始函式指標 ──────────────────────────────────────────────────────────
using BIsSubscribedApp_t      = bool   (__thiscall*)(void*, uint32_t);
using GetDepotDecryptionKey_t = bool   (__thiscall*)(void*, uint32_t, void*, uint32_t*);
using ActivateGameWithKey_t   = EResult(__thiscall*)(void*, const char*);

static BIsSubscribedApp_t      orig_BIsSubscribedApp      = nullptr;
static GetDepotDecryptionKey_t orig_GetDepotDecryptionKey = nullptr;
static ActivateGameWithKey_t   orig_ActivateGameWithKey   = nullptr;

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

// ── CDK Hook：攔截 Steam「啟動產品」對話框 ───────────────────────────────
// 流程：用戶在 Steam → 遊戲 → 在 Steam 上啟動產品 → 輸入 LY-XXXXX... → 確認
// Steam 呼叫 IClientUser::ActivateGameWithKey(key)，我們在這裡攔截

static EResult __fastcall hook_ActivateGameWithKey(void* this_, const char* key)
{
    if (!key) {
        if (orig_ActivateGameWithKey)
            return orig_ActivateGameWithKey(this_, key);
        return k_EResultInvalidParam;
    }

    // 判斷是否為 LY 格式（LY- 開頭，長度 22）
    bool is_ly = (strncmp(key, "LY-", 3) == 0 || strncmp(key, "ly-", 3) == 0)
                 && strlen(key) == 22;

    if (is_ly) {
        auto* auth = core_get_auth();
        if (!auth || !auth->verify_key) {
            // Auth 模組未載入，拒絕
            return k_EResultFail;
        }

        bool ok = auth->verify_key(key);
        if (ok) {
            // 驗證成功：Steam 會顯示「已成功啟動」
            return k_EResultOK;
        }
        // 格式錯誤或伺服器拒絕：Steam 顯示「找不到產品」
        return k_EResultProductNotFound;
    }

    // 不是 LY 金鑰，轉發給 Steam 原始處理（正版序號）
    if (orig_ActivateGameWithKey)
        return orig_ActivateGameWithKey(this_, key);
    return k_EResultFail;
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

    // CDK hook：攔截 Steam 啟動產品對話框
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

    if (orig_GetDepotDecryptionKey)
        vtable_hook(vtable, VT_GetDepotDecryptionKey,
                    reinterpret_cast<void*>(orig_GetDepotDecryptionKey), nullptr);

    if (orig_ActivateGameWithKey)
        vtable_hook(vtable, VT_ActivateGameWithKey,
                    reinterpret_cast<void*>(orig_ActivateGameWithKey), nullptr);
}
