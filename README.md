# ValheimPlusPlus (Valheim mod)

Extends ValheimPlus: item and chest locks, linked chests, chests that feed smelters, auto-stack for armor and EpicLoot items. Also tree regrowth and less hunger at base. More on GitHub. Gorduan Community (GC). Free to use, modify, re-upload (MIT).

Version 1.6.0 · Author: Gorduan · License: MIT

## Included mods

All mods of this package are in GC_ValheimPlusPlus.dll, next to GC_Kern.dll (the shared core).

| Mod | Level | Runs on | Feature |
|---|---|---|---|
| GC_Kern | 0 Core | always, on server and clients | **GC core**: GC Option menu for all GC mods (pause and main menu), switch single mods off, server level, mod check on joining, optional boss progression. Explanations for every feature in DeepTip windows. |
| GC_Schloesser | 2 Vanilla | client, optional server part | **Locks**: White and red locks for items, plus a chest lock against ValheimPlus automation. |
| GC_Kisten | 3 Vanilla+ | client, optional server part | **Linked chests and station menu**: Linked chests show their contents together. The station menu shows smelters and co. and links input and output chests for ValheimPlus. The EpicLoot enchanting table also pays from chests. |
| GC_StackFix | 3 Vanilla+ | client only | **Auto stack fix for ValheimPlus**: ValheimPlus auto-stack also stores armor, trinkets and EpicLoot items. The hotbar stays protected. Needs ValheimPlus. |
| GC_Baeume | 4 GC light | server and all clients | **Trees regrow**: Tree stumps grow back into trees after a sapling's growth time. |
| GC_BasisEssen | 4 GC light | server and all clients | **Less hunger at base**: Food drains slower at your workbench and inside a ward. |

## Compatibility

Works together with these mods and extends some of them. All of them are optional, unless the table above says a mod requires one.

- **[EpicLoot](https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/)**
  - GC_Kern: Optional boss progression counts EpicLoot as at least +10 %.
  - GC_Schloesser: Locked items are protected in every enchanting-table window that consumes or strips an item (sacrifice, disenchant, identify, runes, sets); red locks never pay costs. EpicLoot's socket windows show no locks.
  - GC_Kisten: The enchanting table also pays from chests: like ValheimPlus Craft from Chest, or only from input chests chosen with Shift+E at the table.
  - GC_StackFix: Auto-stack also stores enchanted gear and EpicLoot materials.
- **[ValheimPlus](https://thunderstore.io/c/valheim/p/Grantapher/ValheimPlus_Grantapher_Temporary/)**
  - GC_Kern: Shows the ValheimPlus settings in the GC Option menu; change them in the main menu.
  - GC_Schloesser: Locked items and locked chests are skipped by Craft from Chest, auto-stack and machines that fetch fuel or ore.
  - GC_Kisten: Linked chests count as one chest while open. The station menu links input and output chests to smelters and kilns. With EpicLoot, Craft from Chest also works at the enchanting table.
  - GC_StackFix: Fixes auto-stack so that armor, shields and trinkets are stored again.
  - GC_BasisEssen: Its food duration setting still applies, less hunger at base comes on top.
- **[World Advancement Progression](https://thunderstore.io/c/valheim/p/VentureValheim/World_Advancement_Progression/)**
  - GC_Kern: Boss progression counts the bosses each player has defeated (private keys).
- **[PlantEverything](https://thunderstore.io/c/valheim/p/Advize/PlantEverything/)**
  - GC_Kern: Its texts come back after changing the language in game.
- **[ProtectiveWards](https://thunderstore.io/c/valheim/p/shudnal/ProtectiveWards/)**
  - GC_BasisEssen: Its food drain multiplier still applies, less hunger at base comes on top.
- **[Mod Menu](https://thunderstore.io/c/valheim/p/Dismonder/ModMenu/)**
  - GC_Kern: The GC Option menu replaces it: switch any mod on or off, with profiles and dependencies. Its list and profiles are taken over once.
- **All other mods**
  - GC_Kern: The GC Option menu shows and edits the settings of every installed mod, server rules (Jotunn, ServerSync) only for admins. On joining, the mod check lists every mod that differs from the server.

## Rules

- **Requires [DeepTip](https://thunderstore.io/c/valheim/p/Gorduan/DeepTip/)** (since 1.9.0): every GC feature explains itself in DeepTip windows. Rest the mouse on a function name in the GC Option menu or on a highlighted term in a tooltip.
- **GC_Kern.dll** must always stay. It ships in every GC package so that each package works on its own; BepInEx loads it once and reports the other copies as "duplicate". That is intended.
- **Switching mods off:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you switch such a mod off on the server, your players must switch it off too.
- **Settings:** GC Option menu (button in the pause and main menu) or `BepInEx/config/gorduan.gc.<id>.cfg`. On first start every mod takes over the values from the old cfg files.

## Requirements

- Valheim 1.0.20 with BepInExPack Valheim 5.4.2350 or newer
- Jötunn 2.30.2
- Optional: ValheimPlus (some mods do more when installed)

## Building

1. Install the .NET SDK 9 or newer.
2. Copy `GamePaths.user.props.example` to `GamePaths.user.props` and set `GamePath` (your Valheim folder with BepInEx) and `JotunnDll`.
3. Run `powershell -ExecutionPolicy Bypass -File build.ps1`. The package folder and a zip for r2modman ("Import local mod") end up in `build-output/`.

`Kern/` is the GC core shared by all GC packages (the GC Option menu, settings, texts, permissions). `Patcher/` holds the preloader that lets GC Option switch mods off, `Mods/` one folder per mod, `Pakete/` the project that merges the mods into one DLL (ILRepack). Code comments and `SDK.md` are in German.

## GC packages

- [Valheim_GC_Server](https://github.com/gorduan/Valheim_GC_Server)
- [Valheim_GC_Client](https://github.com/gorduan/Valheim_GC_Client)
- [Valheim_GC_ValheimPlusPlus](https://github.com/gorduan/Valheim_GC_ValheimPlusPlus) (this repository)

## License

MIT for the code, see [LICENSE](LICENSE).
