cmake_minimum_required(VERSION 3.18)
project(NCMKNative CXX)

# Android build:
#   cmake -S Native -B Native/build -G Ninja \
#     -DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake \
#     -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release
# Host test build (Linux, validates the loader against a fake libil2cpp):
#   cmake -S Native -B build-host -DNCMK_HOST_TEST=ON

option(NCMK_HOST_TEST "Build for the host with test hooks (not for the Quest)" OFF)
option(NCMK_BUILD_MODS "Build the sample mods in Mods/" ON)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(NCMK_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/..)

if(ANDROID AND NOT ANDROID_ABI STREQUAL "arm64-v8a")
  message(FATAL_ERROR "NightClub Simulator is arm64-v8a only (got ${ANDROID_ABI})")
endif()

set(NCMK_OUT ${CMAKE_CURRENT_SOURCE_DIR}/out/arm64-v8a)
if(NCMK_HOST_TEST)
  set(NCMK_OUT ${CMAKE_BINARY_DIR}/out)
endif()
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${NCMK_OUT})

set(NCMK_COMMON_FLAGS -fvisibility=hidden -fno-exceptions -Wall -Wextra -Wno-unused-parameter)

add_library(ncmkloader SHARED
  Loader/loader.cpp
  Loader/environment.cpp
  Logging/ncmk_log.cpp
  Il2Cpp/il2cpp_api.cpp
  Unity/unity_runtime.cpp
  Plugins/plugin_manager.cpp
  Hooks/main_thread.cpp)
target_compile_options(ncmkloader PRIVATE ${NCMK_COMMON_FLAGS})
# The dispatcher entry sits between Unity and a managed method: a managed exception thrown by the
# original (IL2CPP raises it as a C++ exception) must be able to unwind through this frame.
set_source_files_properties(Hooks/main_thread.cpp PROPERTIES COMPILE_OPTIONS "-fexceptions;-funwind-tables")
target_include_directories(ncmkloader PRIVATE ${NCMK_ROOT})
set_target_properties(ncmkloader PROPERTIES OUTPUT_NAME ncmkloader)

if(ANDROID)
  target_link_libraries(ncmkloader PRIVATE log dl)
  # Built with ANDROID_STL=c++_static (see build scripts): no dependency on the game's libc++_shared.
  target_link_options(ncmkloader PRIVATE -Wl,--gc-sections -Wl,-z,max-page-size=16384)
else()
  target_link_libraries(ncmkloader PRIVATE dl pthread)
endif()
if(NCMK_HOST_TEST)
  target_compile_definitions(ncmkloader PRIVATE NCMK_HOST_TEST=1)
endif()

function(ncmk_add_mod target soname)
  add_library(${target} SHARED ${ARGN})
  target_compile_options(${target} PRIVATE ${NCMK_COMMON_FLAGS})
  target_include_directories(${target} PRIVATE ${NCMK_ROOT})
  set_target_properties(${target} PROPERTIES OUTPUT_NAME ${soname})
  if(ANDROID)
    target_link_options(${target} PRIVATE -Wl,-z,max-page-size=16384)
  endif()
endfunction()

# Every folder Mods/<Name>/ containing .cpp/.c files becomes libncmkmod_<name>.so (name lowercased).
# A missing or empty Mods/ folder only skips the mods: the loader is always built.
if(NCMK_BUILD_MODS)
  file(GLOB NCMK_MOD_DIRS LIST_DIRECTORIES true "${NCMK_ROOT}/Mods/*")
  set(NCMK_MOD_COUNT 0)
  foreach(dir ${NCMK_MOD_DIRS})
    if(IS_DIRECTORY "${dir}")
      file(GLOB_RECURSE srcs CONFIGURE_DEPENDS "${dir}/*.cpp" "${dir}/*.c")
      get_filename_component(mod_name "${dir}" NAME)
      string(TOLOWER "${mod_name}" mod_name)
      string(REGEX REPLACE "[^a-z0-9_]" "_" mod_name "${mod_name}")
      if(srcs)
        if(srcs MATCHES "\\.c(;|$)")
          enable_language(C)
        endif()
        ncmk_add_mod(ncmkmod_${mod_name} ncmkmod_${mod_name} ${srcs})
        math(EXPR NCMK_MOD_COUNT "${NCMK_MOD_COUNT} + 1")
        message(STATUS "NCMK mod: libncmkmod_${mod_name}.so (${dir})")
      else()
        message(WARNING "NCMK: ${dir} has no .cpp/.c sources - skipped")
      endif()
    endif()
  endforeach()
  if(NCMK_MOD_COUNT EQUAL 0)
    message(WARNING "NCMK: no mod sources found in ${NCMK_ROOT}/Mods - building the loader only")
  endif()
endif()

if(NCMK_HOST_TEST)
  enable_language(C)
  add_subdirectory(Tests)
endif()
