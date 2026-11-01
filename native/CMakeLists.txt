cmake_minimum_required(VERSION 3.20)
project(OSU_Native CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 共用編譯選項（MinGW Windows DLL）
add_compile_options(-Wall -O2 -ffunction-sections -fdata-sections)
add_link_options(-Wl,--gc-sections -static-libgcc -static-libstdc++)

add_subdirectory(xinput_loader)
add_subdirectory(osu_auth)
add_subdirectory(osu_core)
