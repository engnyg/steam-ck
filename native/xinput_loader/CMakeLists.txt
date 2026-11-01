add_library(xinput1_4 SHARED
    dllmain.cpp
    loader.cpp
)

target_include_directories(xinput1_4 PRIVATE ../common)

target_link_libraries(xinput1_4
    PRIVATE
    winhttp
    bcrypt
    z          # zlib 靜態
    -static-libgcc
    -static-libstdc++
)

# DEF 檔：轉發真實 xinput1_3 的導出
set_target_properties(xinput1_4 PROPERTIES
    OUTPUT_NAME "xinput1_4"
    PREFIX ""
    LINK_FLAGS "-Wl,--kill-at"
)

# 若有 .def 就加上
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/xinput1_4.def")
    target_link_options(xinput1_4 PRIVATE
        "-Wl,--output-def,${CMAKE_CURRENT_BINARY_DIR}/xinput1_4.def"
    )
endif()
