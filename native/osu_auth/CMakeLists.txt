add_library(OSU_Auth SHARED
    dllmain.cpp
    auth.cpp
)

target_include_directories(OSU_Auth PRIVATE ../common)

target_link_libraries(OSU_Auth
    PRIVATE
    winhttp
    bcrypt
    advapi32
    -static-libgcc
    -static-libstdc++
)

set_target_properties(OSU_Auth PROPERTIES
    OUTPUT_NAME "OSU_Auth"
    PREFIX ""
)
