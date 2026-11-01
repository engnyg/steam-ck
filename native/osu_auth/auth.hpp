#pragma once

// 從外部呼叫 OSU_Auth.dll 的函式宣告

#ifdef __cplusplus
extern "C" {
#endif

// 驗證 LY 金鑰（線上 + 快取）；成功時寫入 Registry
bool auth_verify_key(const char* key);

// 快取金鑰格式驗證（不需網路）
bool auth_check_cached_key();

// 對 AppIdInOnline 遊戲進行線上驗證
bool auth_check_app_online(int appid);

#ifdef __cplusplus
}
#endif
