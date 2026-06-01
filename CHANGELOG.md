# Changelog

## 0.1.0

- Forked PeakPunch as BuddyClimb.
- Rebuilt on the PEAKModding BepInEx template.
- Added teammate climb interaction for current PEAK builds.
- Added English and Chinese localization for the climb prompt.
- Split host-only dummy player tooling into the independent `PeakDummyTools` mod and DLL.
- Added BepInEx config options and a host-only dummy player spawn shortcut to `PeakDummyTools`.
- Added automatic runtime reload for `PeakDummyTools`' BepInEx config file.
- Fixed dummy spawning so it no longer replaces or disables the host player's character.
- Localized dummy player names and made them increment for each spawn.
- Added a `Config.Build.user.props`-based local build script.
- Isolated dummy player identity from the host player and aligned dummy spawns to the host character center.
- Made spawned dummies behave like generic non-local player characters for interaction testing.
- Added `PeakPlayerLOD` as a separate experimental player visual LOD/model optimization mod.
- Added Space drop input for the carried local player.
- Kept climbed players conscious instead of marking them as passed out.
