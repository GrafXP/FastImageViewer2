cmake_minimum_required(VERSION 3.20)
project(fivjpeg C)

set(TURBO "${CMAKE_CURRENT_SOURCE_DIR}/../libjpeg-turbo")
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
# C4324: jmp_buf is over-aligned, so the struct that holds it is padded. Expected.
add_compile_options(/W4 /O2 /wd4324)
add_compile_definitions(_CRT_SECURE_NO_WARNINGS)

# For the Native AOT link: just the object code; libjpeg comes from turbojpeg-static.lib there.
add_library(fivjpeg_static STATIC fivjpeg.c)
target_include_directories(fivjpeg_static PRIVATE "${TURBO}/include")
set_target_properties(fivjpeg_static PROPERTIES OUTPUT_NAME fivjpeg-static)

# For JIT-run projects: self-contained.
add_library(fivjpeg SHARED fivjpeg.c)
target_compile_definitions(fivjpeg PRIVATE FIVJPEG_DLL)
target_include_directories(fivjpeg PRIVATE "${TURBO}/include")
target_link_libraries(fivjpeg PRIVATE "${TURBO}/lib/turbojpeg-static.lib")

install(TARGETS fivjpeg_static fivjpeg ARCHIVE DESTINATION bin RUNTIME DESTINATION bin)
