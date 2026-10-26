# ValheimPlusPlus (Valheim mod)

Add-ons for ValheimPlus: item locks, linked chests with building overview, auto-stack fix for EpicLoot items, regrowing trees, slower food drain at base. Part of the GC Discord server's mods. Free for anyone to use, modify and re-upload (MIT).

Version 1.6.0 · Author: Gorduan · License: MIT

## Included mods

All mods of this package are in GC_ValheimPlusPlus.dll, next to GC_Kern.dll (the shared core). Each mod can be switched off on its own: GC Option menu, tab Mods (main menu), or its GUID in BepInEx/config/GC_Kern/aus.txt.

| Mod | Level | Runs on | Feature |
|---|---|---|---|
| GC_Kern | 0 Core | always, on server and clients | GC core |
| GC_Schloesser | 2 Vanilla | client, optional server part | Locks |
| GC_Kisten | 3 Vanilla+ | client, optional server part | Linked chests and station menu |
| GC_StackFix | 3 Vanilla+ | client only | Auto stack fix for ValheimPlus |
| GC_Baeume | 4 GC light | server and all clients | Trees regrow |
| GC_BasisEssen | 4 GC light | server and all clients | Less hunger at base |

## Rules

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
