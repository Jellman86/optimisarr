cmake_minimum_required(VERSION 3.27)
project(OptimisarrAudioQuality LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
include(FetchContent)

# Build the selected metric core only. Upstream's full build also pulls unrelated adapters.
FetchContent_Declare(zimtohrli
    GIT_REPOSITORY https://github.com/google/zimtohrli.git
    GIT_TAG f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3
    GIT_SUBMODULES ""
    SOURCE_SUBDIR optimisarr-core-only
)
FetchContent_MakeAvailable(zimtohrli)
set(HWY_ENABLE_TESTS OFF CACHE BOOL "" FORCE)
set(HWY_ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(HWY_ENABLE_CONTRIB OFF CACHE BOOL "" FORCE)
FetchContent_Declare(highway
    GIT_REPOSITORY https://github.com/google/highway.git
    GIT_TAG 7fff3c667fb62475cdfb2b6c441452c8223c1a67
)
FetchContent_MakeAvailable(highway)

add_library(zimtohrli_metric STATIC "${zimtohrli_SOURCE_DIR}/cpp/zimt/zimtohrli.cc")
target_include_directories(zimtohrli_metric SYSTEM PUBLIC "${zimtohrli_SOURCE_DIR}/cpp")
target_link_libraries(zimtohrli_metric PRIVATE hwy)
add_executable(optimisarr-audio-quality main.cpp)
target_link_libraries(optimisarr-audio-quality PRIVATE zimtohrli_metric)
if(MSVC)
    target_compile_options(optimisarr-audio-quality PRIVATE /W4 /WX)
else()
    target_compile_options(optimisarr-audio-quality PRIVATE -Wall -Wextra -Werror)
endif()

enable_testing()
find_package(Python3 REQUIRED COMPONENTS Interpreter)
add_test(NAME native_audio_quality
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/test_native.py"
    "$<TARGET_FILE:optimisarr-audio-quality>")
install(TARGETS optimisarr-audio-quality RUNTIME DESTINATION bin)
install(FILES "${zimtohrli_SOURCE_DIR}/LICENSE" DESTINATION share/optimisarr-audio-quality RENAME ZIMTOHRLI-LICENSE)
install(FILES "${highway_SOURCE_DIR}/LICENSE" DESTINATION share/optimisarr-audio-quality RENAME HIGHWAY-LICENSE)
