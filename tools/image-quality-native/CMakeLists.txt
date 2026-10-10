cmake_minimum_required(VERSION 3.27)
if(CMAKE_HOST_APPLE)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "14.0" CACHE STRING "Match the macOS sidecar" FORCE)
endif()
project(OptimisarrImageQuality LANGUAGES C CXX)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
include(FetchContent)
FetchContent_Declare(libjxl
    GIT_REPOSITORY https://github.com/libjxl/libjxl.git
    GIT_TAG a7a9c787341cf703dede03c2009fa460cae5e5df
    GIT_SUBMODULES third_party/highway third_party/brotli third_party/lcms
        third_party/libpng third_party/zlib third_party/libjpeg-turbo
    SOURCE_SUBDIR optimisarr-deferred
)
FetchContent_MakeAvailable(libjxl)

set(ENABLE_SHARED OFF CACHE BOOL "" FORCE)
set(ENABLE_STATIC ON CACHE BOOL "" FORCE)
set(WITH_TURBOJPEG OFF CACHE BOOL "" FORCE)
set(WITH_TOOLS OFF CACHE BOOL "" FORCE)
set(WITH_TESTS OFF CACHE BOOL "" FORCE)
set(WITH_SIMD OFF CACHE BOOL "Portable baseline without an assembler dependency" FORCE)
add_subdirectory("${libjxl_SOURCE_DIR}/third_party/libjpeg-turbo" jpeg EXCLUDE_FROM_ALL)
set(JPEG_LIBRARY jpeg-static CACHE STRING "Pinned static JPEG decoder" FORCE)
set(JPEG_LIBRARY_RELEASE jpeg-static CACHE STRING "" FORCE)
set(JPEG_LIBRARY_DEBUG jpeg-static CACHE STRING "" FORCE)
set(JPEG_INCLUDE_DIR "${libjxl_SOURCE_DIR}/third_party/libjpeg-turbo" CACHE PATH "" FORCE)

set(JPEGXL_STATIC ON CACHE BOOL "" FORCE)
# Only the pinned PNG/JPEG/WebP decoders are part of the qualified payload.
# Runner-installed optional codecs must not become undeclared runtime dependencies.
set(CMAKE_DISABLE_FIND_PACKAGE_GIF ON CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_OPENEXR OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TCMALLOC OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_SKCMS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DEVTOOLS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_TOOLS ON CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_BENCHMARK OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_JNI OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_SJPEG OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_PLUGINS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_VIEWERS OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_DOXYGEN OFF CACHE BOOL "" FORCE)
set(JPEGXL_ENABLE_MANPAGES OFF CACHE BOOL "" FORCE)
set(JPEGXL_BUNDLE_LIBPNG ON CACHE BOOL "" FORCE)
# Use libpng's shipped configuration. Its AWK preprocessing path in this pinned
# libjxl release points at a placeholder zlib include directory on clean hosts.
set(AWK OFF CACHE FILEPATH "Use the pinned prebuilt libpng configuration" FORCE)
add_subdirectory("${libjxl_SOURCE_DIR}" jxl EXCLUDE_FROM_ALL)
target_include_directories(jxl_extras_core-obj BEFORE PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/jpeg"
    "${libjxl_SOURCE_DIR}/third_party/libjpeg-turbo")

set(WEBP_BUILD_ANIM_UTILS OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_CWEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_DWEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_GIF2WEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_IMG2WEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_VWEBP OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_WEBPINFO OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_WEBPMUX OFF CACHE BOOL "" FORCE)
set(WEBP_BUILD_EXTRAS OFF CACHE BOOL "" FORCE)
FetchContent_Declare(webp
    GIT_REPOSITORY https://chromium.googlesource.com/webm/libwebp
    GIT_TAG b7e29b9d75bd31422b00c2a446d49d7af06c328d
)
FetchContent_MakeAvailable(webp)
add_library(image_metric STATIC "${libjxl_SOURCE_DIR}/tools/ssimulacra2.cc")
target_include_directories(image_metric SYSTEM PUBLIC "${libjxl_SOURCE_DIR}")
target_link_libraries(image_metric PUBLIC jxl_extras-internal jxl_gauss_blur jxl_tool)
add_executable(optimisarr-image-quality main.cpp)
target_link_libraries(optimisarr-image-quality PRIVATE image_metric webpdemux)
# libpng already links the pinned zlib; expose its headers for bounded metadata inflation.
target_include_directories(optimisarr-image-quality SYSTEM PRIVATE "${libjxl_SOURCE_DIR}/third_party/zlib"
    "${CMAKE_CURRENT_BINARY_DIR}/jxl/third_party/zlib")
if(MSVC)
    target_compile_options(optimisarr-image-quality PRIVATE /W4 /WX)
else()
    target_compile_options(optimisarr-image-quality PRIVATE -Wall -Wextra -Werror)
endif()
enable_testing()
find_package(Python3 REQUIRED COMPONENTS Interpreter)
add_test(NAME native_image_quality COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/test_native.py"
    "$<TARGET_FILE:optimisarr-image-quality>")
install(TARGETS optimisarr-image-quality RUNTIME DESTINATION bin COMPONENT ImageQuality)
install(FILES "${libjxl_SOURCE_DIR}/LICENSE" "${libjxl_SOURCE_DIR}/PATENTS"
    DESTINATION share/optimisarr-image-quality COMPONENT ImageQuality)
foreach(dependency highway brotli lcms libpng zlib libjpeg-turbo)
    file(GLOB dependency_licenses "${libjxl_SOURCE_DIR}/third_party/${dependency}/LICENSE*"
        "${libjxl_SOURCE_DIR}/third_party/${dependency}/COPYING*"
        "${libjxl_SOURCE_DIR}/third_party/${dependency}/README.ijg")
    install(FILES ${dependency_licenses}
        DESTINATION "share/optimisarr-image-quality/${dependency}"
        COMPONENT ImageQuality)
endforeach()
install(FILES sources.json DESTINATION share/optimisarr-image-quality COMPONENT ImageQuality)
install(FILES "${webp_SOURCE_DIR}/COPYING" "${webp_SOURCE_DIR}/PATENTS"
    DESTINATION share/optimisarr-image-quality/webp COMPONENT ImageQuality)
