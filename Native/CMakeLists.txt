cmake_minimum_required(VERSION 3.18)
project(NightClubModKitNative CXX)

# Android build:
#   cmake -S Native -B Native/build -G Ninja \
#     -DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake \
#     -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release
# Host test build (Linux, validates the loader against a fake libil2cpp):
#   cmake -S Native -B build-host -DNCS_HOST_TEST=ON

option(NCS_HOST_TEST "Build for the host with test hooks (not for the Quest)" OFF)
option(NCS_BUILD_MODS "Build the sample mods in Mods/" ON)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(NCS_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/..)

if(ANDROID AND NOT ANDROID_ABI STREQUAL "arm64-v8a")
  message(FATAL_ERROR "NightClub Simulator is arm64-v8a only (got ${ANDROID_ABI})")
endif()

set(NCS_OUT ${CMAKE_CURRENT_SOURCE_DIR}/out/arm64-v8a)
if(NCS_HOST_TEST)
  set(NCS_OUT ${CMAKE_BINARY_DIR}/out)
endif()
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${NCS_OUT})

set(NCS_COMMON_FLAGS -fvisibility=hidden -fno-exceptions -Wall -Wextra -Wno-unused-parameter)

add_library(ncsloader SHARED
  Loader/loader.cpp
  Loader/environment.cpp
  Logging/ncs_log.cpp
  Il2Cpp/il2cpp_api.cpp
  Unity/unity_runtime.cpp
  Plugins/plugin_manager.cpp)
target_compile_options(ncsloader PRIVATE ${NCS_COMMON_FLAGS})
target_include_directories(ncsloader PRIVATE ${NCS_ROOT})
set_target_properties(ncsloader PROPERTIES OUTPUT_NAME ncsloader)

if(ANDROID)
  target_link_libraries(ncsloader PRIVATE log dl)
  # Built with ANDROID_STL=c++_static (see build scripts): no dependency on the game's libc++_shared.
  target_link_options(ncsloader PRIVATE -Wl,--gc-sections -Wl,-z,max-page-size=16384)
else()
  target_link_libraries(ncsloader PRIVATE dl pthread)
endif()
if(NCS_HOST_TEST)
  target_compile_definitions(ncsloader PRIVATE NCS_HOST_TEST=1)
endif()

function(ncs_add_mod target soname)
  add_library(${target} SHARED ${ARGN})
  target_compile_options(${target} PRIVATE ${NCS_COMMON_FLAGS})
  target_include_directories(${target} PRIVATE ${NCS_ROOT})
  set_target_properties(${target} PROPERTIES OUTPUT_NAME ${soname})
  if(ANDROID)
    target_link_options(${target} PRIVATE -Wl,-z,max-page-size=16384)
  endif()
endfunction()

if(NCS_BUILD_MODS)
  ncs_add_mod(ncsmod_helloworld ncsmod_helloworld ${NCS_ROOT}/Mods/HelloWorld/helloworld.cpp)
  ncs_add_mod(ncsmod_debugoverlay ncsmod_debugoverlay ${NCS_ROOT}/Mods/DebugOverlay/debug_overlay.cpp)
endif()

if(NCS_HOST_TEST)
  enable_language(C)
  add_subdirectory(Tests)
endif()
