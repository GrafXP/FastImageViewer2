# LibRaw has no CMake build of its own; this builds the static library its Makefile.msvc builds,
# with lossy-JPEG DNG support from the libjpeg-turbo the viewer already has, Sigma's Foveon X3F,
# and OpenMP: demosaicing and the Fujifilm and Canon CR3 decompressors then use every core
# (an X-Trans raw: 15 s on one). The OpenMP runtime, vcomp140.dll, goes next to fivcodecs.dll.
cmake_minimum_required(VERSION 3.20)
project(libraw CXX)

set(LIBRAW_SOURCE "" CACHE PATH "The LibRaw source tree")
set(LIBJPEG_DIR "" CACHE PATH "The libjpeg-turbo install prefix")

# The *_ph.cpp files are placeholders that other sources include.
file(GLOB_RECURSE sources "${LIBRAW_SOURCE}/src/*.cpp")
list(FILTER sources EXCLUDE REGEX "_ph\\.cpp$")

add_library(raw STATIC ${sources})
target_include_directories(raw PRIVATE "${LIBRAW_SOURCE}" "${LIBJPEG_DIR}/include")
target_compile_definitions(raw PRIVATE WIN32 LIBRAW_NODLL LIBRAW_BUILDLIB USE_JPEG USE_X3FTOOLS _CRT_SECURE_NO_WARNINGS)
target_compile_options(raw PRIVATE /EHsc /W0 /openmp)

install(TARGETS raw ARCHIVE DESTINATION lib)
install(DIRECTORY "${LIBRAW_SOURCE}/libraw/" DESTINATION include/libraw)
