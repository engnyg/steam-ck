#pragma once
#include <cstdint>
#include <cstddef>

void           core_init();
const uint8_t* core_get_depot_key(uint32_t depot_id, size_t* out_len);
bool           core_is_online_app(int appid);
void           core_set_online_apps(const int* ids, int count);

// hooks.cpp
void           hooks_install();
void           hooks_uninstall();
