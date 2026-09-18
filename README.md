# [Duty Mechanic][github-repo]

[![Download][download-badge]][download-link]
[![Discord][discord-badge]][discord-invite]

🌎 **English**

**Duty Mechanic** is a plugin for [RebornBuddy][rebornbuddy]. It contains custom logic to handle most dungeons supported by the Duty Support system in FFXIV.

Duty Mechanic is a fork and eventual successor to [RBTrust](https://github.com/athlon18/RBtrust), originally developed by [Athlon](https://github.com/athlon18). Originally it was intended to contain profiles and mechanic support for the Trust system in FFXIV. However, Athlon has not actively contributed to the project in many years and it is now maintained and developed by the [LlamaMagic](https://github.com/LlamaMagic) team.

`Duty Mechanic` will not support OrderBot profiles for the duties, but instead contains custom logic to handle boss encounters inside the duties. Using this plugin, users of RebornBuddy can create their own profiles to handle traversing Duties and `Duty Mechanic` will handle the advanced boss encounters. It can also be used with plugins such as [Panda Farmer](https://llamamagic.net/plugins/pandafarmer/). 

There are still a few profiles left over from the original development located in the `\Profiles` folder, but they are not maintained and there are currently no plans to add to those profiles.

[github-repo]: https://github.com/LlamaMagic/DutyMechanic "Duty Mechanic on GitHub"
[download-badge]: https://img.shields.io/badge/-Download-brightgreen
[download-link]: #installation "Download"
[discord-badge]: https://img.shields.io/badge/Discord-7389D8?logo=discord&logoColor=ffffff&labelColor=6A7EC2
[discord-invite]: https://discord.gg/bmgCq39 "Discord"
[rebornbuddy]: https://www.rebornbuddy.com/ "RebornBuddy"

## Installation

### Prerequisites

-   [RebornBuddy][rebornbuddy] with active license (paid)
-   [Platypus](https://rbplatypus.com/) Handles revives on death as well as Self-repair, food, and much more.
-   (Optional) Better combat routine, such as [Magitek][magitek-discord] (free)

[magitek-discord]: https://discord.gg/rDsFbKr "Magitek Discord"
[llama-plugins]: https://github.com/nt153133/LlamaPlugins "AutoRepairLisbeth"
[gluttony]: https://github.com/domesticwarlord86/Gluttony "Gluttony"

### Automatic Setup (recommended)

The easiest way to install LlamaLibrary is to install the [updateBuddy](https://loader.updatebuddy.net/UpdateBuddy.zip) plugin. It would be installed in the **/plugins** folder of your RebornBuddy folder as such:
```
RebornBuddy
└── Plugins
    └── updateBuddy
        ├── git2-a2bde63.dll
        ├── LibGit2Sharp.dll
        ├── Loader.cs
        └── UpdateBuddy.dll
```

It will automatically install the files into the correct folders and keep them up to date.

## Usage

### Crucible of the Unbroken

First and Second Board mechanics live in `Dungeons/Crucible`, with separate territory registrations for 1339 and 1340. The folder keeps encounter-specific movement lifetimes and pure geometry planners together. Third Board is not implemented. Entry, board navigation, purchases and Beastmaster rotation belong to the calling profile/plugin and combat routine; these handlers own encounter mechanics.

When updating an installation that previously received development sources from PandaCrucible, close that RB instance normally and remove the old flat `Dungeons` copies of the ten files now in `Dungeons/Crucible` after backing them up outside the plugin. Keeping both layouts causes duplicate type definitions. PandaCrucible no longer installs or patches DutyMechanic.

Offline geometry replays are under `Tests/Crucible`: run `dotnet run --project Tests/Crucible/ArchPathReplay`, and likewise `FirePlannerReplay`, `ManticoreReplay`, and `WyvernPlannerReplay`. Fixtures use `.cs.test` so RB's recursive source compiler does not load console entry points. Each replay links the production planner; it verifies captured geometry, not live client timing or unattended reliability.

⚠️ Some classes may not survive certain bosses. ⚠️ If you can't clear even after tuning combat routine settings, try running the previous dungeon until you out-level and can skip the "difficult" one. Also, tank privilege is real. Tanks will have the best success.

