using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.ValheimPlusPlus.ModKisten;

/// <summary>
/// GC_Kisten (Gorduan), Stufe 3 „Vanilla+“: Verbundene Kisten zeigen ihren Inhalt gemeinsam. Das Gebäude-Menü zeigt Schmelze und Co. und verknüpft Ein- und Ausgangskisten für ValheimPlus.
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
		LinkedChests.Plugin.Init(Config, harmony);
		StationStorage.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("vpp_" + key))
			.Main("linked", LinkedChests.Plugin.Enabled, () => LinkedChests.Plugin.Blocked, "GC_LinkedChests")
			.Sub("linked_key", LinkedChests.Plugin.LinkKey)
			.Sub("linked_distance", LinkedChests.Plugin.MaxDistance, 5, " m")
			.Main("stationmenu", StationStorage.Plugin.MenuEnabled, () => StationStorage.Plugin.Blocked, "GC_StationStorage")
			.Main("stationchests", StationStorage.Plugin.ChestsEnabled, () => StationStorage.Plugin.Blocked, "GC_StationStorage", requires: () => Gc.VPlusLoaded)
			.Sub("stationchests_distance", StationStorage.Plugin.MaxChestDistance, 5, " m")
			.Sub("stationchests_highlight", StationStorage.Plugin.HighlightWhileOpen)
			.Sub("stationchests_table", StationStorage.Plugin.TableChests);
	}

	private void Start() => StationStorage.EnchantingChests.Register(new Harmony(GcMod.Guid)); // EpicLoot-Verzaubertisch: erst wenn alle Mods geladen sind

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Verbundene Kisten", LinkedChests.Plugin.Update);
		Module.Guard("Gebäude-Menü", StationStorage.Plugin.Update);
	}
}
