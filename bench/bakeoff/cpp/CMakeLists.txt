cmake_minimum_required(VERSION 3.20)
project(bakeoff_cpp CXX)

set(REPO "${CMAKE_CURRENT_SOURCE_DIR}/../../..")
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)

add_executable(bakeoff_cpp WIN32 main.cpp)
target_compile_definitions(bakeoff_cpp PRIVATE UNICODE _UNICODE)
target_compile_options(bakeoff_cpp PRIVATE /W4 /O2)
target_include_directories(bakeoff_cpp PRIVATE "${REPO}/native/libjpeg-turbo/include" "${REPO}/shaders/out")
target_link_libraries(bakeoff_cpp PRIVATE "${REPO}/native/libjpeg-turbo/lib/turbojpeg-static.lib" d3d11 dxgi dwmapi)
