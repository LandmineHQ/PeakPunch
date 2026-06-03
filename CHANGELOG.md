# Changelog

## 0.1.8

- You can climb the player when thery holding someting whatever.

## 0.1.7

- Corrected BuddyClimb's assembly/config identity to `com.github.LandmineHQ.BuddyClimb`.
- Switched all mod config hot reload paths from `Update()` polling to debounced `FileSystemWatcher` callbacks.
- Documented that BuddyClimb config changes apply without restarting the game.
- Updated the local build script to support `debug`, `release`, and `push` modes without overriding `DeployModFiles`.
- Kept `push` mode in the current PowerShell process instead of requesting UAC elevation.
- Fixed the Thunderstore token lookup command in `Config.Build.user.props.template` so MSBuild does not split it across lines.

## 0.1.6

- Updated README wording to describe making teammates carry you, including backpack support.
- Replaced the package icon with the current BuddyClimb carry-focused icon.
- Bumped the BuddyClimb package version to `0.1.6`.

## 0.1.5

- Added a carried-player interaction proxy for all carry states so `isCarried` targets can be hit directly by PEAK's interaction raycasts.
- Removed carrier-to-carried climb target redirection.
- Allowed local players who are already carrying someone to climb while blocking cyclic carry links.

## 0.1.4

- Allowed carried players to be climbed while blocking targets that are already carrying someone.
- Redirected climb interactions from a carrier to their carried player so carried players can still be targeted when the carrier is hit first.
- Suppressed PEAK's vanilla `E` drop prompt and interaction for BuddyClimb-carried players.

## 0.1.3

- Changed BuddyClimb carry spectate to watch the carried local player and use PEAK's default spectator zoom range.

## 0.1.2

- Fixed backpack-transfer ordering so the carried player's backpack is snapshotted and dropped before the carrier's backpack is moved.

## 0.1.1

- Added an optional BuddyClimb backpack-transfer config for climbing onto backpack-wearing teammates.
- Added localized climb prompt text when backpack transfer will drop the carried player's backpack.
- Added runtime hot reload for BuddyClimb's BepInEx config file.

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
