#pragma once
// 跨 DLL ABI：loader、OSU_Auth、OSU_Core 之間透過函式指標表通訊

#include <cstdint>
#include <cstddef>

#ifdef __cplusplus
extern "C" {
#endif

// ── Auth 匯出 ─────────────────────────────────────────────────────────────
// verify_key：驗證金鑰並取得解鎖的 AppID 清單
//   - key_out_appids：呼叫者提供的 int 陣列，函式填入解鎖的 AppID
//   - key_out_count：輸入陣列容量，輸出實際填入數量
//   - 回傳 true = 驗證成功
typedef bool (*osu_auth_verify_key_fn)(
    const char* key,
    int*        out_appids,   // 輸出：解鎖的 AppID 陣列
    int*        out_count     // 輸入容量 / 輸出實際數量
);

// check_cached_key：離線檢查 Registry 金鑰格式（不驗證 appids）
typedef bool (*osu_auth_check_cached_key_fn)(void);

// get_cached_appids：取得已快取的解鎖 AppID 清單
typedef int  (*osu_auth_get_cached_appids_fn)(
    int* out_appids,   // 輸出陣列
    int  max_count     // 陣列容量
);

// check_app_online：對 AppIdInOnline 遊戲進行線上驗證
typedef bool (*osu_auth_check_app_online_fn)(int appid);

struct osu_auth_api {
    uint32_t                       size;
    osu_auth_verify_key_fn         verify_key;
    osu_auth_check_cached_key_fn   check_cached_key;
    osu_auth_get_cached_appids_fn  get_cached_appids;
    osu_auth_check_app_online_fn   check_app_online;
};

// ── Core 匯出 ─────────────────────────────────────────────────────────────
typedef void (*osu_core_set_auth_impl_fn)       (const osu_auth_api* api);
typedef void (*osu_core_set_depot_blob_fn)      (const uint8_t* data, size_t len);
typedef void (*osu_core_set_online_apps_csv_fn) (const char* csv);
typedef void (*osu_core_init_fn)                (void);

#ifdef __cplusplus
}
#endif
