#pragma once
#include <osu_abi.hpp>
#include <cstdint>
#include <cstddef>

// Loader 注入介面
extern "C" __declspec(dllexport) void core_set_auth_impl(const osu_auth_api* api);
extern "C" __declspec(dllexport) void core_set_depot_blob(const uint8_t* data, size_t len);
extern "C" __declspec(dllexport) void core_set_online_apps_csv(const char* csv);
extern "C" __declspec(dllexport) void core_init();

// 內部用
const uint8_t*      core_get_depot_key(uint32_t depot_id, size_t* out_len);
bool                core_is_online_app(int appid);
const osu_auth_api* core_get_auth();

// hooks.cpp
void hooks_install();
void hooks_uninstall();
