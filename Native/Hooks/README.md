# Hooks (reserved)

Intentionally empty for loader API 1.

The first milestones require that **nothing** in the game is hooked: the loader only observes
(`dlopen(RTLD_NOLOAD)`, `dlsym`, `il2cpp_runtime_invoke` on thread-safe getters).

Planned for API 2:

- a main-thread dispatcher (hook on a Unity player loop entry point resolved by name through
  IL2CPP, never by hardcoded offset), enabling `NCSMod_OnSceneLoaded`, `NCSMod_OnFixedUpdate`,
  `NCSMod_OnGUI` and the scene-graph helpers of `NCS::Unity`;
- an inline-hook backend (e.g. Dobby / ShadowHook, ARM64) wrapped behind `NCS::Hooks::Install`.
