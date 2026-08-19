# Source Repository Map

This document provides a high-level map of the `src/` directory structure for the BuddyClimb solution. Each mod is an independent BepInEx plugin with its own project file and no inter-project dependencies.

---

## Directory Structure

```
src/
├── BuddyClimb/                    # Gameplay mod: buddy climbing mechanics
│   ├── BuddyClimb.csproj
│   ├── Plugin.cs                  # Entry point, Harmony patching setup
│   ├── Configuration/
│   │   └── BuddyClimbConfig.cs    # BepInEx config entries, hot-reload via FileSystemWatcher
│   ├── Localization/
│   │   └── BuddyClimbLocalization.cs  # English, Simplified/Traditional Chinese
│   ├── Gameplay/
│   │   ├── BackpackCarryTransfer.cs     # Backpack slot migration on carry start
│   │   ├── BuddyClimbCarryStarter.cs    # CanCreateCarryLink cyclic check, RPCA_StartCarry send flow
│   │   ├── BuddyClimbDiagnostics.cs     # Debug logging helpers
│   │   ├── BuddyClimbRemotePassOutSync.cs # PassOut/UnPassOut RPCs for remote clients
│   │   ├── CarriedBackpackVisuals.cs    # Force item renderers visible for carried spectator
│   │   ├── CarriedPlayerDropper.cs      # Space key drop request, jump suppression
│   │   └── CarryInteractionProxy.cs     # Trigger proxy for carried players (disabled ragdoll colliders)
│   ├── Compatibility/
│   │   └── ModCompatibility.cs          # Piggyback detection, disables spectator patch
│   └── Patches/
│       ├── CarrySpectatePatch.cs              # Self-spectator locked to localCharacter, disabled when Piggyback detected
│       ├── CharacterBackpackHandlerPatch.cs  # Backpack visibility during carry/spectate
│       ├── CharacterCarryingPatch.cs         # RPCA_StartCarry/Drop, isCarried/carrier state
│       ├── CharacterInteractiblePatch.cs     # Primary hook: climb prompt, interaction stacking
│       ├── CharacterMovementPatch.cs         # Jump suppression while carried
│       ├── ItemBackpackVisualPatch.cs        # Item renderer management in backpack
│       └── PlayerInventoryPatch.cs           # Inventory slot sync during backpack transfer
│
├── PeakDummyTools/                # Tooling mod: dummy player spawning & control
│   ├── PeakDummyTools.csproj
│   ├── Plugin.cs
│   ├── Configuration/
│   │   └── PeakDummyToolsConfig.cs
│   ├── Localization/
│   │   └── PeakDummyToolsLocalization.cs
│   ├── DummyPlayers/
│   │   ├── DummyPlayerSpawner.cs           # LeftAlt+G spawn, MasterClient only
│   │   ├── DummyControlSwitcher.cs         # LeftAlt+T switch control (localCharacter, Voice, PhotonView)
│   │   ├── DummyControlPhotonViewAuthority.cs # Temporarily mark PhotonView as local
│   │   ├── DummyControlMovementStateDriver.cs
│   │   ├── DummyControlLookSyncDriver.cs
│   │   ├── DummyControlItemRpcDriver.cs    # Unified EquipSlotRpc path
│   │   ├── DummyControlItemSelectionSyncDriver.cs
│   │   ├── DummyControlInteractionStateDriver.cs # Clear Interaction hover/held on switch
│   │   ├── DummyControlVoiceDriver.cs      # Recorder TransmitEnabled/RecordingEnabled preserve/restore
│   │   └── DummySwitchPromptUi.cs          # Custom Alt+T / Alt+D prompt rows via GUIManager
│   └── Patches/
│       ├── CharacterPatch.cs               # Awake: mark dummy as bot before registration
│       ├── CharacterCustomizationPatch.cs
│       ├── CharacterInteractiblePatch.cs   # IsInteractible for switch/delete targets
│       ├── CharacterItemsPatch.cs          # EquipSlotRpc currentSelectedSlot fix
│       ├── CharacterMovementPatch.cs
│       ├── CharacterSyncerPatch.cs         # OnDataReceived: skip remote interp during local control
│       ├── CharacterVoiceHandlerPatch.cs   # PushToTalk mute for disabled recorders
│       ├── GUIManagerPatch.cs              # RefreshInteractablePrompt hook for custom rows
│       ├── NetworkingUtilitiesPatch.cs     # Synthetic Player mapping for dummies
│       ├── PlayerPatch.cs                  # Player.character / Character.player isolation
│       └── UIPlayerNamesPatch.cs           # Dummy name prefix handling
│
├── PeakPlayerLOD/                 # Experimental: player visual LOD optimization
│   ├── PeakPlayerLOD.csproj
│   ├── Plugin.cs
│   ├── Configuration/
│   │   └── PeakPlayerLodConfig.cs
│   └── VisualLod/
│       └── PlayerVisualLodManager.cs   # Renderer proxy: skin/face on, clothing/hats/accessories off
│
└── PeakRoutePlanner/              # Experimental: surface sampling & route preview
    ├── PeakRoutePlanner.csproj
    ├── Plugin.cs
    ├── Configuration/
    │   └── PeakRoutePlannerConfig.cs
    ├── Planning/
    │   ├── GuideProjection.cs          # Guide-path projection: progress/distance metrics
    │   ├── PlannerDefaults.cs          # Tunable defaults + PlannerConfig from runtime fields
    │   ├── PriorityQueue.cs            # Generic min-heap (route graph, air flood fill, frontier seeds)
    │   ├── RoutePlannerRuntime.cs      # Main Update driver: shortcuts, sampling steps, rendering
    │   ├── RouteSearchRun.cs           # Incremental search steps, edge validation cache reuse
    │   ├── RouteTypes.cs               # SurfacePoint/SurfaceKind, RouteEdgeKind, PlannerConfig, etc.
    │   ├── SurfaceAirField.cs          # Air flood fill, boundary probes
    │   ├── SurfaceMeshField.cs         # Mesh snapshot field for pocket/segment clearance checks
    │   ├── SurfaceProbeBody.cs         # Standing/move capsule probe validation
    │   ├── SurfaceSampler.cs           # Main sampling: standable, climbable, air cells
    │   ├── VanillaStaminaModel.cs      # Trigger-time stamina snapshot, cost model
    │   └── VanillaSurfaceRules.cs      # Standable/climbable classification rules
    └── Visualization/
        ├── SamplingWindowRenderer.cs   # 60x40x60 translucent ellipsoid
        ├── SurfaceDebugRenderOptions.cs
        └── SurfaceSampleDebugRenderer.cs # Markers, air cells, probes, route lines
```

---

## Key Conventions

| Aspect | Convention |
|--------|------------|
| **Config hot-reload** | All mods use `FileSystemWatcher` + debounced timer → `Config.Reload()`. No `Update()` polling. |
| **Localization** | Each mod has its own `*Localization.cs` with distinct text keys per full prompt. Fallback to English. |
| **Harmony** | `HarmonyX 2.9.0` from NuGet. Patches in `Patches/` folders, prefixed with target class name. |
| **Client-side only** | BuddyClimb uses PEAK's existing RPCs only; no custom `[PunRPC]`/`RPCA_*`. |
| **Isolation** | Mods are independent. No project references between them. |
| **Build output** | Four DLLs: `com.github.LandmineHQ.BuddyClimb.dll`, `com.github.LandmineHQ.PeakDummyTools.dll`, `com.github.LandmineHQ.PeakPlayerLOD.dll`, `com.github.LandmineHQ.PeakRoutePlanner.dll`. |

---

## Entry Points by Mod

| Mod | Plugin Class | Main Hooks |
|-----|--------------|------------|
| BuddyClimb | `BuddyClimb.Plugin` | `CarrySpectatePatch` (`MainCameraMovement`), `CharacterInteractiblePatch`, `CharacterCarryingPatch`, `CharacterBackpackHandlerPatch` |
| PeakDummyTools | `PeakDummyTools.Plugin` | `Character.Awake` (bot marking), `CharacterSyncer.OnDataReceived`, `GUIManager.RefreshInteractablePrompt` |
| PeakPlayerLOD | `PeakPlayerLOD.Plugin` | Renderer enable/disable on distance, debounced transitions |
| PeakRoutePlanner | `PeakRoutePlanner.Plugin` | `LeftAlt+Comma` → `RoutePlannerRuntime.StartRoutePlanner()` |

---

## Cross-Mod Compatibility Notes

- **BuddyClimb + PeakDummyTools**: Dummy characters are valid climb targets (they have `CharacterInteractible`). Carried dummies get interaction proxy same as players.
- **BuddyClimb + PeakPlayerLOD**: No direct interaction. LOD should not disable interaction components.
- **PeakDummyTools + PeakRoutePlanner**: No direct interaction. Route planner samples from `Character.localCharacter` which may be a dummy when Alt+T active.
- **All mods**: Config hot-reload uses same pattern; patches apply via Harmony on `Plugin.Awake()`.


