add_library(OSU_Core SHARED
    dllmain.cpp
    core.cpp
    hooks.cpp
)

target_include_directories(OSU_Core PRIVATE ../common)

target_link_libraries(OSU_Core
    PRIVATE
    winhttp
    bcrypt
    advapi32
    psapi
    z
    -static-libgcc
    -static-libstdc++
)

set_target_properties(OSU_Core PROPERTIES
    OUTPUT_NAME "OSU_Core"
    PREFIX ""
)
