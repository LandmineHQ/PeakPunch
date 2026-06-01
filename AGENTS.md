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

- `Config.Build.user.props` is the local machine configuration file for this template, similar in role to a `.env` file. It is gitignored and should not be committed.
- Copy `Config.Build.user.props.template` to `Config.Build.user.props` and set `PEAKGameRootDir` there when the default Steam install path is not correct for the current machine.
- Use `scripts/build.ps1` for local validation. The script requires `Config.Build.user.props` and relies on its MSBuild properties instead of shell environment variables.
- Debug validation: `.\scripts\build.ps1`
- Release packaging validation: `.\scripts\build.ps1 -Configuration Release`
- Pass `-Deploy` only when intentionally copying the built DLL into the local BepInEx directory. The script disables deployment by default even if the local props file enables it.
- Release package output is under `artifacts/thunderstore/release/`.
- `artifacts/` is build output and should not be committed.
- When reading text files from PowerShell, use explicit UTF-8, for example `Get-Content -Encoding UTF8 <path>`.
- When checking PEAK internals, resolve the PEAK Managed directory from `Config.Build.user.props`, template defaults, or a discovered local Steam library; then inspect `PEAK_Data/Managed/Assembly-CSharp.dll`.
- If Codex has ILSpy tools exposed, use them directly. Otherwise, search likely VS Code extension directories for an `icsharpcode.ilspy-vscode-*` installation and use its bundled backend assemblies (`Mono.Cecil` / `ICSharpCode.Decompiler`) for metadata or decompilation before falling back to rough guesses.

## Implementation Notes

- Harmony comes from the template's NuGet dependency (`HarmonyX` `2.9.0`). Do not copy Harmony assemblies from old build outputs, and do not upgrade HarmonyX unless there is a concrete compatibility reason.
- The primary gameplay hook is `CharacterInteractiblePatch`.
- The climb prompt must go through `BuddyClimbLocalization`; do not hardcode player-facing prompt text in patches.
- Current localization supports English plus Simplified/Traditional Chinese, with unsupported languages falling back to English.
- Debug settings use BepInEx `Config.Bind` in `BuddyClimbConfig`.
- Runtime config hot reload uses `FileSystemWatcher` to mark the config as dirty, then calls this mod's `Config.Reload()` on Unity's main thread from `Plugin.Update()`. Do not replace this with fixed-interval file polling.
- Debug player spawning is host-only. It creates a marked test `Character` at the local player's center position; the `Character.Awake` patch must mark this instance as a bot before PEAK's original player registration runs, or the host player's real `Character` can be disabled by `PlayerHandler.RegisterCharacter`.
- Debug dummy characters cannot be real Photon room players. Keep their identity isolated with BuddyClimb's synthetic local `Player` mapping, `Character.player` / `Player.character` / `NetworkingUtilities` patches, and UI name handling so they never resolve to the host player.
- Debug bot names are generated through `BuddyClimbLocalization` and should remain incrementing and localized. Do not rely on `gameObject.name` alone, because PEAK resets bot/player names in `Character.Start` and `Character.characterName` hardcodes normal bots as `Bot`.

## Compatibility Boundaries

- Keep the plugin GUID stable unless intentionally creating a new incompatible package identity.
- Keep Thunderstore metadata in MSBuild/ThunderPipe configuration; do not reintroduce the old `thunderstore.toml` workflow.
- Prefer PEAK's current `Assembly-CSharp.dll` metadata from the resolved local Managed directory when checking API compatibility.
- Before changing interaction behavior, verify both normal remote player targets and debug-spawned test targets still work.

## AGENTS Maintenance

- Update this file in the same change whenever project structure, build commands, runtime dependencies, localization rules, debug tooling, or PEAK reverse-engineering workflow changes.
- Treat stale guidance here as a bug in the change. If code or scripts diverge from this file, update `AGENTS.md` automatically as part of the implementation before finalizing.
