# DeviceMods

Files here are copied to the headset by **Push Mods + Config** (GUI) or `NightClubPatcher.Cli.exe --push-mods`.
They land in the folder the NCMK loader reads:

    /sdcard/Android/data/com.mandegames.NightClubSimulater/files/NCMKMods

The game has no storage permission, so this app-specific folder is the only one both adb and the game can use.
It is part of the game's external data: uninstalling the game deletes it (back it up with **Backup Game Data**).

| Local                           | On the headset          | Rule                                   |
|---------------------------------|-------------------------|----------------------------------------|
| `Mods/libncmkmod_<name>.so`     | `NCMKMods/Mods/`        | always replaced                        |
| `Config/config.json`            | `NCMKMods/Config/`      | only pushed if absent (keeps your edits) |
| `Config/<modId>.cfg`            | `NCMKMods/Config/`      | only pushed if absent                  |

Mods embedded in the APK at patch time (from `Native/out/arm64-v8a`) do not need to be pushed.
