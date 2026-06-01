# BuddyClimb Project Notes

## Project Overview

- This repository is a PEAK BepInEx mod rebuilt from the PEAKModding BepInEx template.
- Public mod name: `BuddyClimb`.
- Plugin GUID and assembly name: `com.github.LandmineHQ.PeakPunch`.
- Target framework: `netstandard2.1`.
- Main solution: `BuddyClimb.slnx`.
- Main project: `src/BuddyClimb/BuddyClimb.csproj`.
- Runtime dependency target is PEAK's BepInExPack, currently `BepInEx-BepInExPack_PEAK` `5.4.75301`.

## Build And Validation

- Use the local PEAK install when validating against game assemblies:
  `dotnet build .\BuddyClimb.slnx -p:PEAKGameRootDir="D:\SteamLibrary\steamapps\common\PEAK\" -p:DeployModFiles=false -v:minimal`
- Validate Release packaging with:
  `dotnet build .\BuddyClimb.slnx -c Release -p:PEAKGameRootDir="D:\SteamLibrary\steamapps\common\PEAK\" -p:DeployModFiles=false -v:minimal`
- Release package output is under `artifacts/thunderstore/release/`.
- `artifacts/` is build output and should not be committed.
- When reading text files from PowerShell, use explicit UTF-8, for example `Get-Content -Encoding UTF8 <path>`.

## Implementation Notes

- Harmony comes from the template's NuGet dependency (`HarmonyX` `2.9.0`). Do not copy Harmony assemblies from old build outputs, and do not upgrade HarmonyX unless there is a concrete compatibility reason.
- The primary gameplay hook is `CharacterInteractiblePatch`.
- The climb prompt must go through `BuddyClimbLocalization`; do not hardcode player-facing prompt text in patches.
- Current localization supports English plus Simplified/Traditional Chinese, with unsupported languages falling back to English.
- Debug settings use BepInEx `Config.Bind` in `BuddyClimbConfig`.
- Runtime config hot reload uses `FileSystemWatcher` to mark the config as dirty, then calls this mod's `Config.Reload()` on Unity's main thread from `Plugin.Update()`. Do not replace this with fixed-interval file polling.
- Debug player spawning is host-only. It creates a test `Character` at the local player's position and restores the host's real local player mapping immediately afterward; be careful not to let test spawns replace the host player's `Character` registration.

## Compatibility Boundaries

- Keep the plugin GUID stable unless intentionally creating a new incompatible package identity.
- Keep Thunderstore metadata in MSBuild/ThunderPipe configuration; do not reintroduce the old `thunderstore.toml` workflow.
- Prefer PEAK's current `Assembly-CSharp.dll` metadata under `D:\SteamLibrary\steamapps\common\PEAK\PEAK_Data\Managed` when checking API compatibility.
- Before changing interaction behavior, verify both normal remote player targets and debug-spawned test targets still work.
