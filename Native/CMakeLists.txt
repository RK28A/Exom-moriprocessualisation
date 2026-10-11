cmake_minimum_required(VERSION 3.18)
project(NCMKNative CXX)

# Android build:
#   cmake -S Native -B Native/build -G Ninja \
#     -DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake \
#     -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release
# Host test build (Linux, validates the loader against a fake libil2cpp):
#   cmake -S Native -B build-host -DNCMK_HOST_TEST=ON

option(NCMK_HOST_TEST "Build for the host with test hooks (not for the Quest)" OFF)
option(NCMK_BUILD_MODS "Build every mod folder in Mods/" ON)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
get_filename_component(NCMK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)

# Native/ needs SDK/ and Mods/ next to it (a Release folder too).
foreach(required SDK Mods)
  if(NOT IS_DIRECTORY "${NCMK_ROOT}/${required}")
    message(FATAL_ERROR "NCMK: ${NCMK_ROOT}/${required} is missing. In a Release folder, re-run Build-Windows.ps1 "
                        "(it copies Native\\, SDK\\ and Mods\\ next to NightClubPatcher.exe).")
  endif()
endforeach()

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
  Unity/input_pump.cpp
  Plugins/plugin_manager.cpp
  Hooks/main_thread.cpp
  Diagnostics/il2cpp_inspect.cpp
  Diagnostics/SceneLoaderDiagnostics.cpp
  Diagnostics/PlatformDiagnostics.cpp
  Diagnostics/il2cpp_dump.cpp
  Remote/command_server.cpp)
target_compile_options(ncmkloader PRIVATE ${NCMK_COMMON_FLAGS})
# The dispatcher entry sits between Unity and a managed method: a managed exception thrown by the
# original (IL2CPP raises it as a C++ exception) must be able to unwind through this frame.
set_source_files_properties(Hooks/main_thread.cpp PROPERTIES COMPILE_OPTIONS "-fexceptions;-funwind-tables")
# Probe entries sit between IL2CPP frames: managed exceptions (C++ exceptions) must unwind through them.
set_source_files_properties(Diagnostics/il2cpp_inspect.cpp PROPERTIES COMPILE_OPTIONS "-fexceptions;-funwind-tables")
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
  set_property(GLOBAL APPEND PROPERTY NCMK_MOD_OUTPUTS "lib${soname}.so")
endfunction()

# ------------------------------------------------------------------------------------------------
# Mod discovery: a mod is ONE folder Mods/<Name>/ containing a mod.json. Nothing else to edit.
#   sources  : every *.cpp under the folder (re-globbed when files are added)
#   output   : mod.json "library" ("libncmkmod_pulse.so" -> target ncmkmod_pulse),
#              default ncmkmod_<folder name in lower case>
#   includes : the kit root (#include "SDK/...")
#   Mods/<Name>/.disabled  -> skipped
# ------------------------------------------------------------------------------------------------
if(NCMK_BUILD_MODS)
  file(GLOB NCMK_MOD_DIRS LIST_DIRECTORIES true "${NCMK_ROOT}/Mods/*")
  list(SORT NCMK_MOD_DIRS)
  set(NCMK_SEEN_OUTPUTS "")
  set(NCMK_MOD_COUNT 0)
  foreach(dir ${NCMK_MOD_DIRS})
    if(NOT IS_DIRECTORY "${dir}")
      continue()
    endif()
    get_filename_component(folder "${dir}" NAME)
    if(NOT EXISTS "${dir}/mod.json")
      message(STATUS "NCMK mod  : Mods/${folder} ignored (no mod.json)")
      continue()
    endif()
    if(EXISTS "${dir}/.disabled")
      message(STATUS "NCMK mod  : Mods/${folder} DISABLED (.disabled file) - not built")
      continue()
    endif()

    # Only "library" is read (regex, so CMake 3.18 from the Android SDK works too); the loader
    # takes the real metadata from NCMKMod_GetInfo at run time.
    file(READ "${dir}/mod.json" json)
    string(STRIP "${json}" json)
    if(NOT json MATCHES "^{.*}$")
      message(FATAL_ERROR "NCMK: Mods/${folder}/mod.json is not a JSON object ({ ... }).")
    endif()
    if(json MATCHES "\"library\"[ \t\r\n]*:[ \t\r\n]*\"([^\"]*)\"")
      set(library "${CMAKE_MATCH_1}")
    else()
      string(TOLOWER "${folder}" lower)
      string(REGEX REPLACE "[^a-z0-9_]" "_" lower "${lower}")
      set(library "libncmkmod_${lower}.so")
    endif()
    if(NOT library MATCHES "^libncmkmod_[A-Za-z0-9_]+\\.so$")
      message(FATAL_ERROR "NCMK: Mods/${folder}/mod.json \"library\" is \"${library}\"; it must look like "
                          "libncmkmod_<name>.so (letters, digits, underscore) so the loader can find it.")
    endif()
    string(REGEX REPLACE "^lib(.*)\\.so$" "\\1" soname "${library}")

    list(FIND NCMK_SEEN_OUTPUTS "${library}" dup)
    if(NOT dup EQUAL -1)
      math(EXPR owner_index "${dup} + 1")
      list(GET NCMK_SEEN_OUTPUTS ${owner_index} owner)
      message(FATAL_ERROR "NCMK: Mods/${owner} and Mods/${folder} both build ${library}. "
                          "Give one of them a different \"library\" in its mod.json.")
    endif()

    file(GLOB_RECURSE srcs CONFIGURE_DEPENDS "${dir}/*.cpp")
    if(NOT srcs)
      message(WARNING "NCMK mod  : Mods/${folder} has a mod.json but no .cpp file - skipped")
      continue()
    endif()
    list(APPEND NCMK_SEEN_OUTPUTS "${library}" "${folder}")
    ncmk_add_mod(${soname} ${soname} ${srcs})
    list(LENGTH srcs nsrc)
    math(EXPR NCMK_MOD_COUNT "${NCMK_MOD_COUNT} + 1")
    message(STATUS "NCMK mod  : Mods/${folder} -> ${library} (${nsrc} source file(s))")
  endforeach()
  if(NCMK_MOD_COUNT EQUAL 0)
    message(STATUS "NCMK mod  : no mods in ${NCMK_ROOT}/Mods - building the loader only")
  endif()
endif()

if(NCMK_HOST_TEST)
  enable_language(C)
  add_subdirectory(Tests)
endif()

# A removed (or disabled) mod must not stay in the output folder: the patcher embeds every
# libncmkmod_*.so it finds there.
get_property(NCMK_KEEP GLOBAL PROPERTY NCMK_MOD_OUTPUTS)
file(GLOB NCMK_EXISTING_MODS "${NCMK_OUT}/libncmkmod_*.so")
foreach(f ${NCMK_EXISTING_MODS})
  get_filename_component(n "${f}" NAME)
  list(FIND NCMK_KEEP "${n}" keep)
  if(keep EQUAL -1)
    file(REMOVE "${f}")
    message(STATUS "NCMK mod  : removed stale ${n} from the output folder (its Mods/ folder is gone or disabled)")
  endif()
endforeach()
