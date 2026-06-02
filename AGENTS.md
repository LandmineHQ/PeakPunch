# BuddyClimb Project Notes

## Project Overview

- This repository contains PEAK BepInEx mods rebuilt from the PEAKModding BepInEx template.
- Gameplay mod name: `BuddyClimb`.
- Gameplay plugin GUID and assembly name: `com.github.LandmineHQ.PeakPunch`.
- Tooling mod name: `PeakDummyTools`.
- Tooling plugin GUID and assembly name: `com.github.LandmineHQ.PeakDummyTools`.
- Experimental optimization mod name: `PeakPlayerLOD`.
- Optimization plugin GUID and assembly name: `com.github.LandmineHQ.PeakPlayerLOD`.
- Target framework: `netstandard2.1`.
- Main solution: `BuddyClimb.slnx`.
- Gameplay project: `src/BuddyClimb/BuddyClimb.csproj`.
- Tooling project: `src/PeakDummyTools/PeakDummyTools.csproj`.
- Optimization project: `src/PeakPlayerLOD/PeakPlayerLOD.csproj`.
- These projects are independent mods. Do not add project references or BepInEx dependencies between them unless the user explicitly changes that boundary.
- Runtime dependency target is PEAK's BepInExPack, currently `BepInEx-BepInExPack_PEAK` `5.4.75301`.

## Build And Validation

- `Config.Build.user.props` is the local machine configuration file for this template, similar in role to a `.env` file. It is gitignored and should not be committed.
- Copy `Config.Build.user.props.template` to `Config.Build.user.props` and set `PEAKGameRootDir` there when the default Steam install path is not correct for the current machine.
- Use `scripts/build.ps1` for local validation. The script requires `Config.Build.user.props` and relies on its MSBuild properties instead of shell environment variables.
- Debug validation: `.\scripts\build.ps1`
- Release packaging validation: `.\scripts\build.ps1 -Configuration Release`
- Normal solution builds should produce three DLLs: `com.github.LandmineHQ.PeakPunch.dll`, `com.github.LandmineHQ.PeakDummyTools.dll`, and `com.github.LandmineHQ.PeakPlayerLOD.dll`.
- Pass `-Deploy` only when intentionally copying the built DLL into the local BepInEx directory. The script disables deployment by default even if the local props file enables it.
- Release package output is under `artifacts/thunderstore/release/`. `BuddyClimb` is Thunderstore-packable; `PeakDummyTools` and `PeakPlayerLOD` are local DLLs and are not packed unless their project metadata is intentionally changed.
- `artifacts/` is build output and should not be committed.
- When reading text files from PowerShell, use explicit UTF-8, for example `Get-Content -Encoding UTF8 <path>`.
- When checking PEAK internals, resolve the PEAK Managed directory from `Config.Build.user.props`, template defaults, or a discovered local Steam library; then inspect `PEAK_Data/Managed/Assembly-CSharp.dll`.
- If Codex has ILSpy tools exposed, use them directly. Otherwise, search likely VS Code extension directories for an `icsharpcode.ilspy-vscode-*` installation and use its bundled backend assemblies (`Mono.Cecil` / `ICSharpCode.Decompiler`) for metadata or decompilation before falling back to rough guesses.

## Implementation Notes

- Harmony comes from the template's NuGet dependency (`HarmonyX` `2.9.0`). Do not copy Harmony assemblies from old build outputs, and do not upgrade HarmonyX unless there is a concrete compatibility reason.
- The primary gameplay hook is `CharacterInteractiblePatch`.
- `BuddyClimbConfig` owns gameplay config entries. `EnableBackpackTransfer` defaults to false; when false, carrier backpacks still block BuddyClimb interactions.
- `BuddyClimbConfig` runtime config hot reload uses `FileSystemWatcher` to mark the config as dirty, then calls this mod's `Config.Reload()` on Unity's main thread from `Plugin.Update()`. Do not replace this with fixed-interval file polling.
- The climb prompt must go through `BuddyClimbLocalization`; do not hardcode player-facing prompt text in patches.
- Use a distinct `BuddyClimbTextKey` for each full prompt. Do not build player-facing prompt variants by concatenating localized fragments.
- Current localization supports English plus Simplified/Traditional Chinese, with unsupported languages falling back to English.
- Conscious local players carried through BuddyClimb can press Space to request a drop through the carrier's `CharacterCarrying.Drop` RPC. This Space handling must consume/suppress the same-frame jump input and must not activate for vanilla unconscious carry states.
- BuddyClimb-owned carried players should use PEAK's spectator camera locked to `Character.localCharacter`, so the carried player watches themselves while riding. Do not override PEAK's default ghost spectator zoom range. This camera patch is disabled when the `nakazora.peak.piggyback` plugin is detected, because Piggyback implements its own spectator behavior.
- BuddyClimb interaction stacking allows a player currently being carried to be climbed, but targets that are already carrying someone remain blocked.
- BuddyClimb does not redirect climb interactions from a carrier to that carrier's `data.carriedPlayer`. All carried players get a trigger interaction proxy because PEAK disables their ragdoll colliders through `ToggleCarryPhysics(true)`, so normal interaction raycasts otherwise cannot hit them directly. This proxy is not limited to BuddyClimb-owned carry states.
- PEAK's vanilla `E` drop prompt and `Interact` drop path must stay suppressed for BuddyClimb-carried targets; BuddyClimb drop remains the carried local player's Space action.
- Local players may start BuddyClimb while already carrying someone, but `StartCarry` must reject cyclic carry links where the proposed carrier is already carried by the proposed carried player.
- When `EnableBackpackTransfer` is true and the carrier has a backpack, move the carrier's backpack to the carried player during BuddyClimb carry start. If the carried player already has a backpack, create the dropped backpack from a snapshot of the carried player's backpack slot, immediately clear that slot, then move the carrier's backpack in the same interaction. Do not restore the transferred backpack to the carrier on drop.
- BuddyClimb climb interactions must not call the vanilla `RPCA_PassOut`; carried players should remain conscious. Send `RPCA_StartCarry` directly from the carrier's `PhotonView`, and let `CharacterCarryingPatch` apply `isCarried`, `carrier`, and `carriedPlayer`. Avoid `CharacterCarrying.StartCarry()` when the direct RPC path is needed because PEAK initializes its private `character` field in `Start()`.
- `PeakDummyTools` settings use BepInEx `Config.Bind` in `PeakDummyToolsConfig`.
- `PeakDummyTools` runtime config hot reload uses `FileSystemWatcher` to mark the config as dirty, then calls that mod's `Config.Reload()` on Unity's main thread from `Plugin.Update()`. Do not replace this with fixed-interval file polling.
- Dummy player spawning is host-only. It creates a marked test `Character` at the local player's center position; the `Character.Awake` patch must temporarily mark this instance as a bot before PEAK's original player registration runs, or the host player's real `Character` can be disabled by `PlayerHandler.RegisterCharacter`.
- Dummy characters cannot be real Photon room players. Keep their identity isolated with `PeakDummyTools`' synthetic local `Player` mapping, `Character.player` / `Player.character` / `NetworkingUtilities` patches, and UI name handling so they never resolve to the host player.
- Dummy names are generated through `PeakDummyToolsLocalization` and should remain incrementing and localized. Do not rely on `gameObject.name` alone, because PEAK resets names in `Character.Start`.
- Dummy spawning defaults to `LeftAlt+G`. Dummy control switching defaults to `LeftAlt+T`, is host-only, and switches `Character.localCharacter`, local `Player.character`, and the local actor's `PlayerHandler` character lookup to the targeted dummy without moving either character. Pressing the switch shortcut while controlling a dummy and targeting the original host character, or with no dummy target, returns control to the host character.
- Dummy control switching should show its own `Alt + T` interaction prompt row from the UI layer. Do not append this text through `CharacterInteractible.GetInteractionText`, because that reuses PEAK's normal `E` interaction slot.
- `PeakPlayerLOD` owns experimental player visual LOD/model optimization. Keep it isolated from BuddyClimb and PeakDummyTools so crashes or visual regressions can be disabled by removing only that DLL.
- `PeakPlayerLOD` should avoid disabling root `Character`, `PhotonView`, colliders, animation/IK components, or interaction components. Prefer renderer-only experiments plus explicit cleanup that restores renderer state.
- `PeakPlayerLOD` keeps the nearest configured non-local players on their original renderers and represents farther non-local players with a skin-only renderer proxy on the original character: keep the base skin/body and face expression renderers, hide clothing, hats, accessories, shadows, chicken, skeleton, and other cosmetic renderers. Do not create separate mesh proxy objects for this mode.
- `PeakPlayerLOD` LOD transitions use `PlayerVisualLodSwitchDebounceSeconds` so players near the full-detail boundary do not rapidly toggle renderer states.

## Compatibility Boundaries

- Keep the plugin GUID stable unless intentionally creating a new incompatible package identity.
- Keep Thunderstore metadata in MSBuild/ThunderPipe configuration; do not reintroduce the old `thunderstore.toml` workflow.
- Prefer PEAK's current `Assembly-CSharp.dll` metadata from the resolved local Managed directory when checking API compatibility.
- Before changing BuddyClimb interaction behavior, verify normal remote player targets. When changing PeakDummyTools, verify spawned dummies still behave like generic non-local player characters. When changing PeakPlayerLOD, verify original renderers are restored when the feature or plugin is disabled.

## AGENTS Maintenance

- Update this file in the same change whenever project structure, build commands, runtime dependencies, localization rules, debug tooling, or PEAK reverse-engineering workflow changes.
- Treat stale guidance here as a bug in the change. If code or scripts diverge from this file, update `AGENTS.md` automatically as part of the implementation before finalizing.
