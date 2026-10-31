using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.ValheimPlusPlus.StationStorage;

/// <summary>
/// EpicLoot-Verzaubertisch aus Kisten (Gorduan, 30.10.2026: „der Verzauberungstisch von Epicloot [soll] auch das Material aus
/// Kisten im Bereich nehmen dürfen“, „nicht nur unsere Kisten-Mod … sondern eben die ValheimPlus-Original-Funktion“):
///  * Ohne Zuweisung wie ValheimPlus beim Craften (Craft from Chest): alle Kisten in Reichweite nach den V+-Einstellungen
///    (an/aus, Reichweite, vom Tisch oder vom Spieler aus, Ward-Prüfung, Karren und Schiffe), über V+s eigene Kistensuche.
///  * Mit Zuweisung (Umschalt+E am Tisch, Eingang „Material“): nur die gewählten Kisten, auch wenn Craft from Chest aus ist.
/// Es gelten dieselben Sperren wie bei V+: Mit Alt+L gesperrte Kisten bleiben außen vor, rot fixierte Items bezahlen nie
/// (die Filter der Schlösser hängen an V+s Kistenfunktionen). Angebunden über EpicLoot.API.RegisterInventoryProvider
/// (EpicLoot 0.15.4): Kisten bezahlen nur Kosten (Material, Münzen, Runen); ihre Items erscheinen nicht in den Listen zum
/// Opfern, Verzaubern usw. Ohne EpicLoot, ohne ValheimPlus oder mit älteren Fassungen geschieht nichts.
/// </summary>
internal static class EnchantingChests
{
	/// <summary>Eingangsschlüssel des Tischs: ein Satz Kisten für alles Material.</summary>
	internal const string TableKey = "mat";

	private const string ProviderId = "gorduan.gc.kisten.verzaubertisch";

	/// <summary>Anbindung steht (EpicLoot-Tisch, Inventar-Schnittstelle und V+-Kistenfunktionen gefunden).</summary>
	internal static bool Ready { get; private set; }

	internal static Type? TableType { get; private set; }

	private static PropertyInfo? uiInstance;
	private static PropertyInfo? uiSource;
	private static MethodInfo? uiVisible;
	private static MethodInfo? nearbyChests;
	private static MethodInfo? chestItems;
	private static MethodInfo? removeFromChest;
	private static PropertyInfo? configCurrent;

	private static MonoBehaviour? cachedTable;
	private static float cachedUntil;
	private static List<Container> cached = new();

	internal static void Register(Harmony harmony)
	{
		if (Ready || !Gc.VPlusLoaded)
		{
			return;
		}
		try
		{
			Type? api = AccessTools.TypeByName("EpicLoot.API");
			MethodInfo? provider = api?.GetMethod("RegisterInventoryProvider", BindingFlags.Public | BindingFlags.Static);
			TableType = AccessTools.TypeByName("EpicLoot_UnityLib.EnchantingTable");
			Type? ui = AccessTools.TypeByName("EpicLoot_UnityLib.EnchantingTableUI");
			Type? assistant = AccessTools.TypeByName("ValheimPlus.InventoryAssistant");
			if (provider == null || TableType == null || ui == null || assistant == null)
			{
				return; // ohne EpicLoot 0.15.4 oder ohne V+: der Tisch nimmt wie bisher nur aus dem Inventar
			}
			uiInstance = AccessTools.Property(ui, "instance");
			uiSource = AccessTools.Property(ui, "SourceTable");
			uiVisible = AccessTools.Method(ui, "IsVisible");
			nearbyChests = assistant.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.FirstOrDefault(m => m.Name == "GetNearbyChests" && m.GetParameters().Length >= 3 && m.GetParameters()[0].ParameterType == typeof(GameObject));
			chestItems = AccessTools.Method(assistant, "GetNearbyChestItemsByContainerList", new[] { typeof(List<Container>) });
			removeFromChest = AccessTools.Method(assistant, "RemoveItemFromChest", new[] { typeof(Container), typeof(string), typeof(int) });
			configCurrent = AccessTools.Property(AccessTools.TypeByName("ValheimPlus.Configurations.Configuration"), "Current");
			if (uiInstance == null || uiSource == null || uiVisible == null || nearbyChests == null || chestItems == null || removeFromChest == null || configCurrent == null)
			{
				Plugin.Log.LogWarning("Verzaubertisch aus Kisten: Teile von EpicLoot oder ValheimPlus nicht gefunden, der Tisch nimmt nur aus dem Inventar.");
				return;
			}
			harmony.Patch(AccessTools.Method(TableType, "Interact"), prefix: new HarmonyMethod(typeof(EnchantingChests), nameof(InteractPrefix)));
			harmony.Patch(AccessTools.Method(TableType, "GetHoverText"), postfix: new HarmonyMethod(typeof(EnchantingChests), nameof(HoverPostfix)));
			harmony.Patch(AccessTools.Method(TableType, "Start"), postfix: new HarmonyMethod(typeof(EnchantingChests), nameof(StartPostfix)));
			Func<string, int> count = Count;
			Func<string, int, int> remove = Remove;
			Func<ItemDrop.ItemData, int, int> exact = (item, amount) => 0; // Kisten bieten keine einzelnen Items an
			object?[] args = { ProviderId, null, count, remove, exact };
			Ready = provider.Invoke(null, args) is true;
			Plugin.Log.LogInfo(Ready ? "Verzaubertisch aus Kisten: EpicLoot und ValheimPlus erkannt, der Tisch bezahlt auch aus Kisten." : "Verzaubertisch aus Kisten: EpicLoot lehnte die Anmeldung ab.");
		}
		catch (Exception e)
		{
			Ready = false;
			Plugin.Log.LogWarning($"Verzaubertisch aus Kisten: nicht angebunden: {e.Message}");
		}
	}

	internal static bool IsTable(MonoBehaviour? component) => component != null && TableType != null && TableType.IsInstanceOfType(component);

	private static bool On => Ready && Plugin.ChestsOn && Plugin.TableChests.Value;

	/// <summary>Der Tisch, an dem der eigene Spieler gerade verzaubert (EpicLoots Fenster offen), sonst null.</summary>
	private static MonoBehaviour? CurrentTable()
	{
		if (!On || Player.m_localPlayer == null || uiVisible!.Invoke(null, null) is not true)
		{
			return null;
		}
		object? ui = uiInstance!.GetValue(null);
		return ui != null ? uiSource!.GetValue(ui) as MonoBehaviour : null;
	}

	/// <summary>V+ „Craft from Chest“: an, Reichweite (1–50 m wie V+), vom Tisch aus (checkFromWorkbench), Ward-Prüfung.</summary>
	internal static bool VPlusSettings(out float range, out bool fromTable, out bool checkWard)
	{
		range = 20f;
		fromTable = true;
		checkWard = true;
		try
		{
			object? current = configCurrent?.GetValue(null);
			object? section = current == null ? null : AccessTools.Property(current.GetType(), "CraftFromChest")?.GetValue(current);
			if (section == null)
			{
				return false;
			}
			Type type = section.GetType();
			range = Mathf.Clamp(AccessTools.Property(type, "range")?.GetValue(section) is float r ? r : 20f, 1f, 50f);
			fromTable = AccessTools.Property(type, "checkFromWorkbench")?.GetValue(section) is not false;
			checkWard = AccessTools.Property(type, "ignorePrivateAreaCheck")?.GetValue(section) is not true; // so übergibt V+ es beim Craften
			return AccessTools.Property(type, "IsEnabled")?.GetValue(section) is true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>Kisten für den Tisch: zugewiesene, sonst V+s Kisten in Reichweite. Zum Zählen höchstens einmal je Sekunde neu.</summary>
	private static List<Container> Chests(MonoBehaviour table, bool fresh)
	{
		if (!fresh && table == cachedTable && Time.time < cachedUntil)
		{
			return cached;
		}
		List<Container> chests = new();
		Station? station = Station.Of(table);
		List<string> assigned = station != null ? Plugin.Assigned(station, TableKey) : new List<string>();
		if (station != null && assigned.Count > 0)
		{
			chests = Plugin.Resolve(station, assigned).Where(c => Vector3.Distance(c.transform.position, station.Position) <= Plugin.MaxChestDistance.Value + 2f).ToList();
		}
		else if (VPlusSettings(out float range, out bool fromTable, out bool checkWard))
		{
			GameObject target = fromTable ? table.gameObject : Player.m_localPlayer.gameObject;
			ParameterInfo[] parameters = nearbyChests!.GetParameters();
			object?[] args = new object?[parameters.Length];
			for (int i = 0; i < parameters.Length; i++)
			{
				// Ziel, Reichweite, Ward-Prüfung; weitere Angaben (Karren und Schiffe) wie V+ beim Craften: Vorgabewert
				args[i] = i == 0 ? target : i == 1 ? range : i == 2 ? checkWard : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : true;
			}
			chests = nearbyChests.Invoke(null, args) as List<Container> ?? new List<Container>();
		}
		cachedTable = table;
		cachedUntil = Time.time + 1f;
		cached = chests;
		return chests;
	}

	/// <summary>EpicLoot zählt Kosten: Menge in den Kisten (wie V+ beim Craften: passende Weltstufe, ohne rot fixierte).</summary>
	private static int Count(string sharedName)
	{
		MonoBehaviour? table = CurrentTable();
		if (table == null || string.IsNullOrEmpty(sharedName))
		{
			return 0;
		}
		List<Container> chests = Chests(table, false);
		if (chests.Count == 0 || chestItems!.Invoke(null, new object[] { chests }) is not List<ItemDrop.ItemData> items)
		{
			return 0;
		}
		return items.Where(i => i.m_shared.m_name == sharedName && i.m_worldLevel >= Game.m_worldLevel).Sum(i => i.m_stack);
	}

	/// <summary>EpicLoot bezahlt: was Inventar und Tischlager nicht hatten, aus den Kisten (V+s Entnahme, gleiche Sperren).</summary>
	private static int Remove(string sharedName, int amount)
	{
		MonoBehaviour? table = CurrentTable();
		if (table == null || amount <= 0 || string.IsNullOrEmpty(sharedName))
		{
			return 0;
		}
		int removed = 0;
		foreach (Container chest in Chests(table, true))
		{
			if (removed >= amount)
			{
				break;
			}
			if (chest != null && removeFromChest!.Invoke(null, new object[] { chest, sharedName, amount - removed }) is int taken)
			{
				removed += taken;
			}
		}
		cachedUntil = -1f; // nächste Zählung neu
		return removed;
	}

	/// <summary>Umschalt+E am Tisch öffnet das Gebäude-Menü (E öffnet weiter EpicLoots Fenster).</summary>
	private static bool InteractPrefix(MonoBehaviour __instance, Humanoid user, bool repeat, bool alt, ref bool __result)
	{
		if (!On || !Plugin.MenuOn || !alt || repeat || user != Player.m_localPlayer)
		{
			return true;
		}
		return OpenMenu.Try(Station.Of(__instance), ref __result);
	}

	private static void HoverPostfix(ref string __result)
	{
		if (On && Plugin.MenuOn && !string.IsNullOrEmpty(__result))
		{
			__result += "\n" + OpenMenu.Hover;
		}
	}

	/// <summary>RPC für die Zuweisung auf jedem Tisch (ausgeführt beim Besitzer, wie bei den Schmelzen).</summary>
	private static void StartPostfix(MonoBehaviour __instance) => ProducerRpcs.Register(__instance.GetComponent<ZNetView>());

	/// <summary>Zustand für das Gebäude-Menü: woher der Tisch gerade Material nimmt.</summary>
	internal static string Describe(Station station)
	{
		int assigned = Plugin.Assigned(station, TableKey).Count;
		if (assigned > 0)
		{
			return GcLoc.Get("gcss_table_assigned", assigned);
		}
		return VPlusSettings(out float range, out _, out _) ? GcLoc.Get("gcss_table_vplus", range.ToString("0")) : GcLoc.Get("gcss_table_vplus_off");
	}
}
