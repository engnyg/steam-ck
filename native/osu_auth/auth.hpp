#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// 驗證 LY 金鑰，取得解鎖的 AppID 清單
// out_appids: 呼叫者提供的 int 陣列
// out_count:  輸入容量，輸出實際數量
__declspec(dllexport) bool auth_verify_key(const char* key, int* out_appids, int* out_count);

// 離線快取金鑰格式驗證
__declspec(dllexport) bool auth_check_cached_key(void);

// 取得已快取的解鎖 AppID 清單，回傳實際數量
__declspec(dllexport) int  auth_get_cached_appids(int* out_appids, int max_count);

// 對 AppIdInOnline 遊戲進行線上驗證
__declspec(dllexport) bool auth_check_app_online(int appid);

#ifdef __cplusplus
}
#endif
