using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.ValheimPlusPlus.ModBaeume;

/// <summary>
/// GC_Baeume (Gorduan), Stufe 4 „GC leicht“: Baumstümpfe wachsen nach der Wachstumszeit eines Setzlings wieder zum Baum.
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
		TreeRegrow.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("vpp_" + key))
			.Main("treeregrow", TreeRegrow.Plugin.Enabled, () => TreeRegrow.Plugin.Blocked, "GC_TreeRegrow")
			.Sub("treeregrow_override", TreeRegrow.Plugin.OverrideMinutes, 10, " min")
			.Sub("treeregrow_fallback", TreeRegrow.Plugin.FallbackMinutes, 10, " min")
			.Sub("treeregrow_spread", TreeRegrow.Plugin.Spread, 0.05)
			.Sub("treeregrowhover", TreeRegrow.Plugin.ShowHover);
	}
}
