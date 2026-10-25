using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.ValheimPlusPlus;
using HarmonyLib;
using UnityEngine;

namespace GC.ValheimPlusPlus.BaseFood;

/// <summary>
/// Weniger Hunger in der Basis (neu): Im Einflussbereich einer Werkbank oder eines aktiven Wächtersteins läuft Essen
/// langsamer ab, einstellbar von 0 % (wie Vanilla) bis 100 % (gar kein Hungerverlust). Stehst du in beiden Bereichen,
/// gilt der größere Wert.
///
/// Umsetzung: Player.UpdateFood zieht jede Sekunde Zeit von jedem Essen ab. Danach wird ein Anteil der tatsächlich
/// abgezogenen Zeit zurückgegeben. Andere Mods, die den Verbrauch ändern (z. B. „Food drain multiplier" von
/// ProtectiveWards, foodDurationMultiplier von ValheimPlus), wirken dadurch weiter und werden nur multipliziert.
/// Werkbank-Bereich = Bau-Reichweite der Station inklusive Erweiterungen (wie beim Bauen), Wächterstein-Bereich =
/// Ward-Radius, nur aktive Wards und standardmäßig nur solche, auf die du Zugriff hast.
/// Läuft beim Spieler, die Werte gibt der Server vor.
/// </summary>
internal static class Plugin
{
	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<int> WorkbenchPercent = null!;
	internal static ConfigEntry<int> WardPercent = null!;
	internal static ConfigEntry<string> Stations = null!;
	internal static ConfigEntry<bool> WardNeedsAccess = null!;
	internal static bool On => Enabled.Value;

	private static float nextCheck;
	private static float cached;
	private static HashSet<string>? stationSet;
	private static string stationText = "";

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = VppPlugin.Log;
		Enabled = Gc.BindSynced(Config, "6 - Base Hunger", "Enabled", true, "Weniger Hungerverlust im Bereich von Werkbänken und Wächtersteinen (wird vom Server vorgegeben).");
		WorkbenchPercent = Gc.BindSynced(Config, "6 - Base Hunger", "WorkbenchPercent", 50, "Um wie viel Prozent der Hungerverlust im Bereich einer Werkbank sinkt. 0 = wie Vanilla, 100 = kein Hungerverlust.", new AcceptableValueRange<int>(0, 100));
		WardPercent = Gc.BindSynced(Config, "6 - Base Hunger", "WardPercent", 50, "Um wie viel Prozent der Hungerverlust im Bereich eines aktiven Wächtersteins sinkt. 0 = wie Vanilla, 100 = kein Hungerverlust.", new AcceptableValueRange<int>(0, 100));
		Stations = Gc.BindSynced(Config, "6 - Base Hunger", "Stations", "piece_workbench", "Kommagetrennte Prefab-Namen der Stationen, deren Bereich zählt (z. B. piece_workbench, forge, piece_stonecutter, piece_artisanstation, blackforge, piece_magetable).");
		WardNeedsAccess = Gc.BindSynced(Config, "6 - Base Hunger", "WardNeedsAccess", true, "Nur Wächtersteine zählen, auf die der Spieler Zugriff hat (eigene oder freigegebene).");
	}

	/// <summary>Anteil 0..1, um den der Verbrauch gerade sinkt. Einmal pro Sekunde neu berechnet.</summary>
	internal static float Reduction(Player player)
	{
		if (Time.time < nextCheck)
		{
			return cached;
		}
		nextCheck = Time.time + 1f;
		Vector3 position = player.transform.position;
		int best = 0;
		if (WorkbenchPercent.Value > best && NearStation(position))
		{
			best = WorkbenchPercent.Value;
		}
		if (WardPercent.Value > best && InWard(position))
		{
			best = WardPercent.Value;
		}
		cached = Mathf.Clamp01(best / 100f);
		return cached;
	}

	private static bool NearStation(Vector3 position)
	{
		if (stationSet == null || stationText != Stations.Value)
		{
			stationText = Stations.Value;
			stationSet = Gc.ParseList(stationText);
		}
		foreach (CraftingStation station in CraftingStation.m_allStations)
		{
			if (station == null || !stationSet.Contains(Utils.GetPrefabName(station.gameObject)))
			{
				continue;
			}
			Vector3 point = position;
			point.y = station.transform.position.y; // wie CraftingStation.HaveBuildStationInRange
			if (Vector3.Distance(station.transform.position, point) < station.GetStationBuildRange())
			{
				return true;
			}
		}
		return false;
	}

	private static bool InWard(Vector3 position)
	{
		foreach (PrivateArea area in PrivateArea.m_allAreas)
		{
			if (area != null && area.IsEnabled() && area.IsInside(position, 0f) && (!WardNeedsAccess.Value || area.HaveLocalAccess()))
			{
				return true;
			}
		}
		return false;
	}
}

/// <summary>
/// Einen Teil der in UpdateFood abgezogenen Essenszeit zurückgeben. Gemessen wird die tatsächliche Änderung, die
/// Liste wird wiederverwendet (UpdateFood läuft jeden Physik-Frame). Höchstens bis zur Zeit vor dem Abzug, damit
/// längere Essenszeiten anderer Mods (z. B. V+ foodDurationMultiplier) nicht gekürzt werden.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.UpdateFood))]
internal static class UpdateFoodPatch
{
	private static readonly List<KeyValuePair<Player.Food, float>> Before = new();

	private static void Prefix(Player __instance, out bool __state)
	{
		__state = false;
		if (!Plugin.On || __instance != Player.m_localPlayer || __instance.m_foods.Count == 0)
		{
			return;
		}
		if (Plugin.Reduction(__instance) <= 0f)
		{
			return;
		}
		Before.Clear();
		foreach (Player.Food food in __instance.m_foods)
		{
			Before.Add(new KeyValuePair<Player.Food, float>(food, food.m_time));
		}
		__state = true;
	}

	private static void Postfix(Player __instance, bool __state)
	{
		if (!__state)
		{
			return;
		}
		float share = Plugin.Reduction(__instance);
		foreach (KeyValuePair<Player.Food, float> entry in Before)
		{
			Player.Food food = entry.Key;
			float lost = entry.Value - food.m_time;
			if (lost <= 0f || !__instance.m_foods.Contains(food))
			{
				continue;
			}
			food.m_time = Mathf.Min(entry.Value, food.m_time + lost * share);
		}
		Before.Clear();
	}
}
