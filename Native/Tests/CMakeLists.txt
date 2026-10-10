# Host-only test harness: a fake "game" that mimics Unity's load order on Android.
add_library(fake_il2cpp SHARED fake_il2cpp.c)
set_target_properties(fake_il2cpp PROPERTIES OUTPUT_NAME il2cpp)

add_library(fake_unity SHARED fake_unity.c)
set_target_properties(fake_unity PROPERTIES OUTPUT_NAME unity)

# Unpatched stand-in for Unity's libmain.so; the C# ELF patcher adds DT_NEEDED libncsloader.so.
add_library(fake_main SHARED fake_main.c)
set_target_properties(fake_main PROPERTIES OUTPUT_NAME main_unpatched)
target_link_options(fake_main PRIVATE -Wl,--build-id)

add_executable(host_game host_game.c)
target_link_libraries(host_game PRIVATE dl pthread)
set_target_properties(host_game PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${NCS_OUT})
