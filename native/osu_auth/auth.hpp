#pragma once

#ifdef __cplusplus
extern "C" {
#endif

__declspec(dllexport) bool auth_verify_key(const char* key);
__declspec(dllexport) bool auth_check_cached_key(void);
__declspec(dllexport) bool auth_check_app_online(int appid);

#ifdef __cplusplus
}
#endif
