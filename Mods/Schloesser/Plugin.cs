using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.ValheimPlusPlus.ModSchloesser;

/// <summary>
/// GC_Schloesser (Gorduan), Stufe 2 „Vanilla“: Weißes und rotes Schloss für Items, dazu eine Sperre für Kisten gegen die Automatik von ValheimPlus.
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
		ItemLock.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("vpp_" + key))
			.Main("itemlock", ItemLock.Plugin.ItemsEnabled, () => ItemLock.Plugin.Blocked, "GC_ItemLock")
			.Sub("itemlock_key", ItemLock.Plugin.ItemModifier)
			.Sub("itemlock_stacking", ItemLock.Plugin.ProtectFromStacking)
			.Sub("itemlock_takeall", ItemLock.Plugin.ProtectFromTakeAll)
			.Sub("itemlock_grave", ItemLock.Plugin.RestoreAfterDeath)
			.Sub("itemlock_white_new", ItemLock.Plugin.WhiteStacksTakeNew)
			.Sub("itemlock_red_new", ItemLock.Plugin.RedStacksTakeNew)
			.Main("chestlock", ItemLock.Plugin.ChestsEnabled, () => ItemLock.Plugin.Blocked, "GC_ItemLock", requires: () => Gc.VPlusLoaded)
			.Sub("chestlock_key", ItemLock.Plugin.ChestKey);
	}

	private void Start()
	{
		ItemLock.SacrificeGuard.Register(); // EpicLoot 0.15.2: kein Opfern von Items mit Schloss
		ItemLock.EpicLootGuard.Patch(new Harmony(GcMod.Guid)); // EpicLoot 0.15.4: Schutz in allen Fenstern des Verzaubertischs
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Schlösser", ItemLock.Plugin.Update);
	}
}
