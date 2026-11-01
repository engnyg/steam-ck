#pragma once
// 跨 DLL ABI：loader、OSU_Auth、OSU_Core 之間透過函式指標表通訊。
// 原因：OSU_Auth / OSU_Core 由 xinput_loader 反射式載入到記憶體，
// 不經過 OS LoadLibrary，所以不能用匯入表或靜態連結來綁定符號。

#include <cstdint>
#include <cstddef>

#ifdef __cplusplus
extern "C" {
#endif

// ── Auth 匯出 ─────────────────────────────────────────────────────────────
// OSU_Auth.dll 匯出此函式指標表（或 loader 透過 resolve_export 一一取得）。
typedef bool (*osu_auth_verify_key_fn)      (const char* key);
typedef bool (*osu_auth_check_cached_key_fn)(void);
typedef bool (*osu_auth_check_app_online_fn)(int appid);

struct osu_auth_api {
    uint32_t                     size;             // = sizeof(osu_auth_api)
    osu_auth_verify_key_fn       verify_key;
    osu_auth_check_cached_key_fn check_cached_key;
    osu_auth_check_app_online_fn check_app_online;
};

// ── Core 匯出 ─────────────────────────────────────────────────────────────
// loader 將 Auth API 與 depot blob 推進 Core，Core 再啟動 hook。
typedef void (*osu_core_set_auth_impl_fn)(const osu_auth_api* api);
typedef void (*osu_core_set_depot_blob_fn)(const uint8_t* data, size_t len);
typedef void (*osu_core_set_online_apps_json_fn)(const char* csv);
typedef void (*osu_core_init_fn)(void);

#ifdef __cplusplus
}
#endif
