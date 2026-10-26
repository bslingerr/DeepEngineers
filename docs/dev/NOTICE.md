# NOTICE

DeepEngineers, a fan project that runs Space Engineers and Subnautica together. This file credits the games and the
software it builds on, says what is ours, and says how it was made. It is not legal advice.

## Modified software notice (Space Engineers EULA 1.5)

**DeepEngineers modifies Space Engineers.** Its plugin (`plugin.dll`, loaded by Pulsar) changes the game's behaviour
in memory while it runs: the picture and window under water, sound levels, swimming and oxygen rules on the planet
`DE_4546B`, the seabed of the dive site, and what is sent to Subnautica. It changes no file of the game. Space
Engineers is © Keen Software House; this project is not made, supported or endorsed by Keen Software House.
DeepEngineers ships no file of Space Engineers and works only for people who own it: everything it uses from the game
is read from the player's own install while it runs.

**DeepEngineers also modifies Subnautica** in memory through a BepInEx plugin (`DeepEngineers.SN.dll`). Subnautica is
© Unknown Worlds Entertainment; this project is not made, supported or endorsed by Unknown Worlds. No file of
Subnautica is shipped; its terrain, models and textures are read from the player's own install.

Both plugins are single-player only and stay inert in multiplayer sessions.

## What DeepEngineers uses (not included in the download unless stated)

| Project | Author | Licence | How it is used |
|---|---|---|---|
| Space Engineers 1.210.014 | Keen Software House | Space Engineers EULA | the game; must be owned |
| Subnautica 1.22.83031 | Unknown Worlds Entertainment | Subnautica EULA | the game; must be owned |
| Pulsar 2.4.3 | SpaceGT and contributors (github.com/SpaceGT/Pulsar) | MIT | the Space Engineers plugin loader; downloaded by the installer from its GitHub release, SHA-256 checked |
| Lib.Harmony 2.4.2 | Andreas Pardeike | MIT | method patching in Space Engineers; comes with Pulsar, not shipped by us |
| Tobey's BepInEx Pack for Subnautica 5.4.23-pack.3.1.1 | toebeann (github.com/toebeann/BepInEx.Subnautica) | the pack's own licence files | the Subnautica mod loader; downloaded by the installer from its GitHub release, SHA-256 checked |
| BepInEx 5.4.23.5 | the BepInEx team | LGPL-2.1 | inside the pack above |
| HarmonyX 2.9.0 | the BepInEx team | MIT | method patching in Subnautica; inside the pack above |
| Water Mod 3.23 | Jakaria | no licence file in the mod | the ocean of Space Engineers; used through its Steam Workshop item (2200451495) and its public message API only. Our API client is our own code, written from the API's behaviour; no file of the mod is copied or shipped |
| Text HUD API | Draygo | Steam Workshop terms | needed by the Water Mod; used through its Steam Workshop item (758597413) |
| Python 3.10+ | Python Software Foundation | PSF License | runs the installer and the launcher; installed by the player |

## Ideas re-implemented in our own code (MIT)

DeepEngineers' bridge between two running games (a shared-memory section, heartbeats, a camera and pose stream,
window layering) re-implements, in its own code, mechanisms first shown by these MIT-licensed projects. No code was
copied from them; they are credited here with thanks:

- **SkyCraft** (github.com/chasmlol/SkyCraft, v0.1.2), MIT.
- **Killcraft** (github.com/goonsn/Killcraft, v1.0.1), MIT.
- **LibertyCraft** (github.com/mrborghini/libertycraft, v0.1.8), its MIT-licensed parts (protocol, fabric, tools)
 only; its GPL-3.0 part was not read.

Read for research only, nothing reused: **ValCraft** (no licence file). Read for reference only, nothing copied:
**Vehicle Framework** and **Nitrox** (both GPL-3.0).

## What is ours

The two plugins, the shared wire library (`DeepEngineers.Wire.dll`), the launcher and installer scripts, the world
mod `DeepEngineers.World` (data only: a planet definition and all-flat heightmap images we generated) and the template
world. The world mod and the template world contain no file of either game.

## Decompilation (Space Engineers EULA 2.2.4)

To make the two games work together, their compiled code was decompiled **locally, for interoperability only**. No
decompiled code is in this download or in the repository: the release is checked by `tools/release/check_release.py`,
which fails on any 12-token sequence shared with the decompiled sources, on decompiler markers and on files that are
not on its allowlist. The decompiled output itself was never published. Whether notes about the games' internal
behaviour are published is the author's decision, taken with the EULA's terms in mind

## Made with AI coding tool

DeepEngineers was designed, written, tested and documented by AI coding tool, directed, reviewed and play-tested by its human author on his own PC. The author
decided what to build, approved every change to his games and saves, and judged every result in game. Expect the
rough edges of a project that was built fast and tested on one machine.
