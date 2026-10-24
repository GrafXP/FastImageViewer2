cmake_minimum_required(VERSION 3.20)
project(fivcodecs CXX)

set(CODECS "${CMAKE_CURRENT_SOURCE_DIR}/../codecs")
set(TURBO "${CMAKE_CURRENT_SOURCE_DIR}/../libjpeg-turbo")
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

# One DLL for JIT-run projects and the Native AOT executable alike: loaded only when an image of
# one of its formats is opened, so it costs the start-up of a JPEG nothing.
add_library(fivcodecs SHARED fivcodecs.cpp)
target_compile_features(fivcodecs PRIVATE cxx_std_17)
target_compile_options(fivcodecs PRIVATE /W4 /O2 /EHsc)
target_compile_definitions(fivcodecs PRIVATE _CRT_SECURE_NO_WARNINGS
    LIBHEIF_STATIC_BUILD LIBRAW_NODLL JXL_STATIC_DEFINE JXL_THREADS_STATIC_DEFINE JXL_CMS_STATIC_DEFINE)
target_include_directories(fivcodecs PRIVATE "${CODECS}/include")
foreach(library heif libde265 dav1d libwebp libwebpdemux libsharpyuv jxl jxl_cms jxl_threads hwy brotlidec brotlicommon raw tiff zs)
    target_link_libraries(fivcodecs PRIVATE "${CODECS}/lib/${library}.lib")
endforeach()
target_link_libraries(fivcodecs PRIVATE "${TURBO}/lib/jpeg-static.lib" ws2_32)

install(TARGETS fivcodecs RUNTIME DESTINATION bin)
