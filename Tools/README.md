# Tools

The patcher looks here first, then in the Android SDK (`ANDROID_HOME`, `ANDROID_SDK_ROOT`,
`%LOCALAPPDATA%\Android\Sdk`), then in `PATH`.

| Folder | Put here | Source |
|---|---|---|
| `adb/` | `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` | Android SDK *platform-tools* |
| `zipalign/` | `zipalign.exe` (+ `libwinpthread-1.dll` if present) | Android SDK *build-tools/&lt;ver&gt;* |
| `apksigner/` | `apksigner.jar` (from `build-tools/<ver>/lib/`) | Android SDK *build-tools/&lt;ver&gt;* |
| `jdk/` (optional) | a JDK 17+ (`jdk/bin/java.exe`, `keytool.exe`) | any OpenJDK build; otherwise `JAVA_HOME`/`PATH` is used |
| `apktool/` (optional, FULL PATCH later) | `apktool.jar` | https://apktool.org |

Run `NightClubPatcher.exe --tools` to see what was found.
