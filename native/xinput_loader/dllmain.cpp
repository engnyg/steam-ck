#include "loader.hpp"
#include <windows.h>

static DWORD WINAPI loader_thread(LPVOID)
{
    loader_run();
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        // 非阻塞：在背景執行緒執行載入，避免 DllMain 死鎖
        CloseHandle(CreateThread(nullptr, 0, loader_thread, nullptr, 0, nullptr));
    }
    return TRUE;
}
