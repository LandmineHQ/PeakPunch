# Changelog

## 0.1.0

- Forked PeakPunch as BuddyClimb.
- Rebuilt on the PEAKModding BepInEx template.
- Added teammate climb interaction for current PEAK builds.
- Added English and Chinese localization for the climb prompt.
- Added BepInEx debug config options and a host-only debug player spawn shortcut.
- Added automatic runtime reload for this mod's BepInEx config file.
- Fixed debug bot spawning so it no longer replaces or disables the host player's character.
- Localized debug bot names and made them increment for each spawn.
- Added a `Config.Build.user.props`-based local build script.
- Isolated debug dummy identity from the host player and aligned debug spawns to the host character center.
- Fixed debug dummy climb interactions so the local player is carried instead of only being passed out.
