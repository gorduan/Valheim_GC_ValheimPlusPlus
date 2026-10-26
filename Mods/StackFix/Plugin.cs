using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.ValheimPlusPlus.ModStackFix;

/// <summary>
/// GC_StackFix (Gorduan), Stufe 3 „Vanilla+“: Das Auto-Stack von ValheimPlus lagert auch Rüstung, Schmuck und EpicLoot-Items ein. Die Schnellleiste bleibt geschützt. Wirkt nur mit ValheimPlus.
/// Früher Teil von ValheimPlusPlus. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		GC.ValheimPlusPlus.VppPlugin.Log = Logger;
		GC.ValheimPlusPlus.VppPlugin.Module = Module;
		Harmony harmony = new(GcMod.Guid);
		StackFix.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("vpp_" + key))
			.Main("stackfix", StackFix.Plugin.Enabled, () => StackFix.Plugin.Blocked, "GC_StackFix", requires: () => Gc.VPlusLoaded)
			.Sub("hotbar", StackFix.Plugin.ProtectHotbar, blocked: () => StackFix.Plugin.Blocked, oldMod: "GC_StackFix");
	}
}
