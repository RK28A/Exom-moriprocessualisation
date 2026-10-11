// Scene loader diagnostics + optional recovery for NightClub Simulator's bl_SceneLoaderManager
// (Lovatto "Loading Screen" asset). Diagnostic-first: with recovery off, game behaviour is unchanged.
//
//   Worker thread (after STAGE 06):  Init()     metadata dump of bl_SceneLoaderManager and the
//                                                bl_SceneLoader* classes, probes on their Awake().
//   Main thread (dispatcher frames): OnFrame()  state dumps (static fields, loaded instances,
//                                                IsGlobalLoadingScreen() result) and, when enabled,
//                                                the one-shot SceneManager.LoadSceneAsync fallback.
//
// Any reflection or invoke failure disables the module (logged) and the game continues normally.
#pragma once

namespace ncmk {
namespace diag {

struct SceneLoaderOptions {
    bool diagnostics = true;
    bool recovery = false;
    int recoveryScene = -1;    // build index; -1 = active index + 1
    int recoveryDelay = 10;    // seconds stuck in the same scene after a confirmed failure
};

void SceneLoaderInit(const SceneLoaderOptions& options);

/// Main thread, every dispatched frame. `sceneLoaded` is non-null on the frame the active scene changed.
void SceneLoaderOnFrame(const char* sceneLoaded, float unscaledDeltaTime);

/// Restores the Awake probes (unload / process exit).
void SceneLoaderRestore();

}  // namespace diag
}  // namespace ncmk
