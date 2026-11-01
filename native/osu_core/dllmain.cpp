#include "core.hpp"
#include <windows.h>

static DWORD WINAPI init_thread(LPVOID)
{
    // 等待 steamclient64.dll 載入完成
    for (int i = 0; i < 60; i++) {
        if (GetModuleHandleA("steamclient64.dll") ||
            GetModuleHandleA("steamclient.dll"))
            break;
        Sleep(500);
    }
    core_init();
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        CloseHandle(CreateThread(nullptr, 0, init_thread, nullptr, 0, nullptr));
    } else if (reason == DLL_PROCESS_DETACH) {
        hooks_uninstall();
    }
    return TRUE;
}
