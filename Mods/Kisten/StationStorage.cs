using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.ValheimPlusPlus;
using HarmonyLib;
using UnityEngine;

namespace GC.ValheimPlusPlus.StationStorage;

/// <summary>
/// GC_StationStorage: Gebäude-Menü (Umschalt+E am Gebäude, wie Valheims „alternative Benutzung").
///
/// Produktionsgebäude mit der Vanilla-Klasse Smelter (Schmelze, Hochofen, Köhlerei, Windmühle, Spinnrad,
/// Eitr-Raffinerie und Gebäude anderer Mods mit dieser Klasse):
///  * Warteschlange ansehen und einzelne Rohstoffe zurücknehmen,
///  * Brennstoff zurücknehmen, fertige Produkte direkt ins Inventar holen,
///  * Rohstoffe und Brennstoff aus dem Inventar auf einmal einfüllen,
///  * mit ValheimPlus: pro Rohstoff und Brennstoff eigene Eingangskisten und eigene Ausgangskisten wählen.
/// Erzeuger ohne Eingang (Bienenstock, Vogelnest und alles mit der Klasse Beehive, Saftzapfer mit SapCollector):
///  * Füllstand ansehen und ernten,
///  * mit ValheimPlus: Ausgangskisten wählen, in die V+ den Ertrag ablegt.
/// Kistenwahl: „Wählen" im Menü, dann eine Kiste anklicken. Danach erscheint das Menü wieder (Rechtsklick bricht ab).
/// Farben: Eingang grün, Ausgang rot, eine Kiste, die beides ist, blau. Eine verbundene Kiste zählt mit ihrer ganzen
/// Gruppe.
/// Alle Änderungen am Gebäude laufen über den Besitzer des Gebäudes (RPC), wie bei Vanilla.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_stationstorage";
	public const string ModName = "GC_StationStorage";
	public const string Version = "1.0.0";

	internal const string RpcTake = "GC_SS_Take";
	internal const string RpcTakeFuel = "GC_SS_TakeFuel";
	internal const string RpcTakeOutput = "GC_SS_TakeOutput";
	internal const string RpcGive = "GC_SS_Give";
	internal const string RpcAssign = "GC_SS_Assign";
	internal const string RpcAddOre = "GC_SS_AddOre";
	internal const string RpcAddFuel = "GC_SS_AddFuel";
	internal const string KeyPrefix = "gc_ss_";
	internal const string OutputKey = "out";
	internal const string FuelKey = "fuel";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> MenuEnabled = null!;
	internal static ConfigEntry<bool> ChestsEnabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool MenuOn => MenuEnabled.Value && !Blocked && VppPlugin.Allowed && VppPlugin.OnServer;
	internal static bool ChestsOn => ChestsEnabled.Value && !Blocked && VppPlugin.Allowed && VppPlugin.OnServer;
	internal static ConfigEntry<float> MaxChestDistance = null!;
	internal static ConfigEntry<bool> HighlightWhileOpen = null!;
	internal static ConfigEntry<bool> TableChests = null!;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = VppPlugin.Log;
		MenuEnabled = Gc.BindSynced(Config, "4 - Station Storage", "MenuEnabled", true, "Gebäude-Menü mit Umschalt+E: Inhalt ansehen, zurücknehmen, einfüllen, Kisten wählen (wird vom Server vorgegeben).");
		ChestsEnabled = Gc.BindSynced(Config, "4 - Station Storage", "ChestsEnabled", true, "Ein- und Ausgangskisten pro Gebäude für die ValheimPlus-Automatik, auch für Bienenstock, Vogelnest und Saftzapfer (wird vom Server vorgegeben).");
		MaxChestDistance = Gc.BindSynced(Config, "4 - Station Storage", "MaxChestDistance", 30f, "Größte Entfernung zwischen Gebäude und zugewiesener Kiste in Metern.", new AcceptableValueRange<float>(2f, 100f));
		HighlightWhileOpen = Gc.Bind(Config, "4 - Station Storage - Interface", "HighlightWhileOpen", true, "Zugewiesene Kisten leuchten, solange das Menü offen ist.");
		TableChests = Gc.BindSynced(Config, "4 - Station Storage", "EpicLootTableChests", true, "EpicLoot-Verzaubertisch bezahlt auch aus Kisten: ohne Zuweisung wie ValheimPlus beim Craften (Craft from Chest), mit Eingangskisten (Umschalt+E am Tisch) nur aus diesen (wird vom Server vorgegeben).");
		VPlusBridge.Apply(harmony);
	}

	internal static void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		if (!MenuOn)
		{
			if (StationWindow.IsOpen)
			{
				StationWindow.Close();
			}
			if (ChestPicker.Active)
			{
				ChestPicker.Cancel(false);
			}
			return;
		}
		ChestPicker.Update();
		StationWindow.Update();
	}

	// ---------- Hilfen rund um die Warteschlange (nur Smelter) ----------

	internal static List<string> Queue(ZDO zdo)
	{
		int count = zdo.GetInt(ZDOVars.s_queued);
		List<string> list = new(count);
		for (int i = 0; i < count; i++)
		{
			list.Add(zdo.GetString("item" + i));
		}
		return list;
	}

	internal static void WriteQueue(ZDO zdo, List<string> queue)
	{
		for (int i = 0; i < queue.Count; i++)
		{
			zdo.Set("item" + i, queue[i]);
		}
		zdo.Set(ZDOVars.s_queued, queue.Count);
	}

	internal static string ItemName(string prefab)
	{
		GameObject? go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
		ItemDrop? drop = go != null ? go.GetComponent<ItemDrop>() : null;
		return drop != null ? Localization.instance.Localize(drop.m_itemData.m_shared.m_name) : prefab;
	}

	/// <summary>Dauerhafte Kisten-IDs (GcIds), die dem Schlüssel zugewiesen sind. ZDOIDs taugen nicht, sie ändern sich bei jedem Weltstart.</summary>
	internal static List<string> Assigned(Station station, string key)
	{
		ZDO? zdo = station.Valid ? station.View.GetZDO() : null;
		if (zdo == null)
		{
			return new List<string>();
		}
		return zdo.GetString(KeyPrefix + key).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
	}

	/// <summary>
	/// Geladene, erlaubte Kisten zu den IDs: öffentlich (wie V+ für Maschinen verlangt), nicht für Automatik gesperrt,
	/// in denselben Wards wie das Gebäude. Eine verbundene Kiste bringt ihre ganze Gruppe mit (die gewählte zuerst).
	/// Solange jemand die Gruppe gerade offen hat, steht die geöffnete Kiste für die Gruppe. Ausgelassen werden Kisten,
	/// deren Inhalt noch nicht geladen ist (ein Ablegen würde sie beim Speichern leeren) und Kisten, die gerade jemand
	/// anderes offen hat (dessen Stand gilt, das Ablegen ginge verloren).
	/// </summary>
	internal static List<Container> Resolve(Station station, IEnumerable<string> ids)
	{
		List<Container> result = new();
		HashSet<Container> seen = new();
		foreach (string id in ids)
		{
			Container? chosen = LinkedChests.Links.Find(id); // auch IDs, die ein anderer Spieler nach dem Laden vergeben hat
			if (chosen == null)
			{
				continue;
			}
			foreach (Container member in LinkedChests.Links.Group(chosen))
			{
				if (member == null || !LinkedChests.LinkedView.Usable(member) || Gc.IsAutoLocked(member))
				{
					continue;
				}
				Container chest = LinkedChests.LinkedView.Canonical(member);
				if (chest == null || chest.GetInventory() == null || Gc.IsAutoLocked(chest) || LinkedChests.LinkedView.LockedView(chest) || chest.m_privacy != Container.PrivacySetting.Public)
				{
					continue;
				}
				if (!GcWard.SameWards(station.Position, chest.transform.position) || !seen.Add(chest))
				{
					continue;
				}
				result.Add(chest);
			}
		}
		return result;
	}

	/// <summary>Alle Eingangsschlüssel des Gebäudes: Brennstoff und jede Rohstoffart (Prefab-Name), am EpicLoot-Verzaubertisch einer für alles Material. Erzeuger haben keine.</summary>
	internal static List<string> InputKeys(Station station)
	{
		List<string> keys = new();
		if (station.IsTable)
		{
			keys.Add(EnchantingChests.TableKey);
			return keys;
		}
		Smelter? smelter = station.Smelter;
		if (smelter == null)
		{
			return keys;
		}
		if (smelter.m_fuelItem != null)
		{
			keys.Add(FuelKey);
		}
		foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
		{
			if (conversion.m_from != null && !keys.Contains(conversion.m_from.gameObject.name))
			{
				keys.Add(conversion.m_from.gameObject.name);
			}
		}
		return keys;
	}

	internal static string KeyForItem(Smelter smelter, ItemDrop.ItemData item)
	{
		if (smelter.m_fuelItem != null && item.m_shared.m_name == smelter.m_fuelItem.m_itemData.m_shared.m_name)
		{
			return FuelKey;
		}
		return item.m_dropPrefab != null ? item.m_dropPrefab.name : KeyForSharedName(smelter, item.m_shared.m_name);
	}

	internal static string KeyForSharedName(Smelter smelter, string sharedName)
	{
		if (smelter.m_fuelItem != null && smelter.m_fuelItem.m_itemData.m_shared.m_name == sharedName)
		{
			return FuelKey;
		}
		foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
		{
			if (conversion.m_from != null && conversion.m_from.m_itemData.m_shared.m_name == sharedName)
			{
				return conversion.m_from.gameObject.name;
			}
		}
		return sharedName;
	}

	/// <summary>Rolle einer Kiste für das Gebäude: Eingang, Ausgang oder beides (für die Farbe).</summary>
	internal static (bool Input, bool Output) Roles(Station station, Container chest)
	{
		bool input = InputKeys(station).Any(key => Resolve(station, Assigned(station, key)).Contains(chest));
		bool output = Resolve(station, Assigned(station, OutputKey)).Contains(chest);
		return (input, output);
	}
}

/// <summary>
/// Ein Gebäude mit Menü: Smelter (mit Eingang und Ausgang) oder ein Erzeuger ohne Eingang (Beehive für Bienenstock und
/// Vogelnest, SapCollector für den Saftzapfer). Gleicht die Unterschiede der Vanilla-Klassen aus.
/// </summary>
internal sealed class Station
{
	public readonly MonoBehaviour Component;
	public readonly ZNetView View;

	private Station(MonoBehaviour component, ZNetView view)
	{
		Component = component;
		View = view;
	}

	public static Station? Of(Component? component)
	{
		if (component == null)
		{
			return null;
		}
		Smelter? smelter = component.GetComponentInParent<Smelter>();
		if (smelter != null && smelter.m_nview != null)
		{
			return new Station(smelter, smelter.m_nview);
		}
		Beehive? hive = component.GetComponentInParent<Beehive>();
		if (hive != null && hive.m_nview != null)
		{
			return new Station(hive, hive.m_nview);
		}
		SapCollector? sap = component.GetComponentInParent<SapCollector>();
		if (sap != null && sap.m_nview != null)
		{
			return new Station(sap, sap.m_nview);
		}
		if (EnchantingChests.TableType != null && component.GetComponentInParent(EnchantingChests.TableType) is MonoBehaviour table && table.GetComponent<ZNetView>() is ZNetView view)
		{
			return new Station(table, view); // EpicLoot-Verzaubertisch: nur Eingangskisten für Material
		}
		return null;
	}

	public Smelter? Smelter => Component as Smelter;
	public Beehive? Hive => Component as Beehive;
	public SapCollector? Sap => Component as SapCollector;
	public bool IsProducer => Hive != null || Sap != null;
	public bool IsTable => EnchantingChests.IsTable(Component);
	public bool Valid => Component != null && View != null && View.IsValid();
	public GameObject GameObject => Component.gameObject;
	public Vector3 Position => Component.transform.position;

	public string Name => Smelter != null ? Smelter.m_name : Hive != null ? Hive.m_name : Sap != null ? Sap.m_name
		: IsTable ? (Component.GetComponent<Piece>() is Piece piece ? piece.m_name : "$mod_epicloot_assets_enchantingtable") : "";

	/// <summary>Erzeuger: aktueller Füllstand und Höchststand.</summary>
	public (int Level, int Max) Level => Hive != null ? (Hive.GetHoneyLevel(), Hive.m_maxHoney) : Sap != null ? (Sap.GetLevel(), Sap.m_maxLevel) : (0, 0);

	/// <summary>Erzeuger: Name des Ertrags (Honig, Federn, Saft …).</summary>
	public string ProductName
	{
		get
		{
			ItemDrop? item = Hive != null ? Hive.m_honeyItem : Sap != null ? Sap.m_spawnItem : null;
			return item != null ? Localization.instance.Localize(item.m_itemData.m_shared.m_name) : "";
		}
	}

	/// <summary>Erzeuger ernten wie mit E (Vanilla-RPC beim Besitzer; mit ValheimPlus geht der Ertrag in die Kisten).</summary>
	public void Extract()
	{
		if (Hive != null)
		{
			Hive.Extract();
		}
		else if (Sap != null)
		{
			Sap.Extract();
		}
	}

	public override bool Equals(object? obj) => obj is Station other && other.Component == Component;

	public override int GetHashCode() => Component != null ? Component.GetHashCode() : 0;
}

/// <summary>RPCs auf jedem Gebäude registrieren (auf allen Clients, ausgeführt beim Besitzer).</summary>
[HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
internal static class SmelterAwakePatch
{
	private static void Postfix(Smelter __instance)
	{
		ZNetView nview = __instance.m_nview;
		if (nview == null || nview.GetZDO() == null || Plugin.Blocked)
		{
			return; // läuft die alte Einzelmod noch, registriert sie dieselben RPCs (doppelt registrieren wirft)
		}
		Smelter smelter = __instance;
		nview.Register<string, int>(Plugin.RpcTake, (sender, item, amount) => Rpcs.Take(smelter, sender, item, amount));
		nview.Register<int>(Plugin.RpcTakeFuel, (sender, amount) => Rpcs.TakeFuel(smelter, sender, amount));
		nview.Register(Plugin.RpcTakeOutput, sender => Rpcs.TakeOutput(smelter, sender));
		nview.Register<string, int>(Plugin.RpcGive, (sender, item, amount) => Rpcs.Give(item, amount));
		nview.Register<string, string>(Plugin.RpcAssign, (sender, key, value) => Rpcs.Assign(nview, key, value));
		nview.Register<string, int, bool>(Plugin.RpcAddOre, (sender, item, amount, cheated) => Rpcs.AddOre(smelter, sender, item, amount, cheated));
		nview.Register<int>(Plugin.RpcAddFuel, (sender, amount) => Rpcs.AddFuel(smelter, sender, amount));
	}
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.Awake))]
internal static class BeehiveAwakePatch
{
	private static void Postfix(Beehive __instance) => ProducerRpcs.Register(__instance.m_nview);
}

[HarmonyPatch(typeof(SapCollector), nameof(SapCollector.Awake))]
internal static class SapAwakePatch
{
	private static void Postfix(SapCollector __instance) => ProducerRpcs.Register(__instance.m_nview);
}

internal static class ProducerRpcs
{
	public static void Register(ZNetView? nview)
	{
		if (nview == null || nview.GetZDO() == null || Plugin.Blocked)
		{
			return;
		}
		nview.Register<string, string>(Plugin.RpcAssign, (sender, key, value) => Rpcs.Assign(nview, key, value));
	}
}

internal static class Rpcs
{
	public static void Take(Smelter smelter, long sender, string item, int amount)
	{
		ZNetView nview = smelter.m_nview;
		if (!nview.IsOwner())
		{
			return;
		}
		ZDO zdo = nview.GetZDO();
		List<string> queue = Plugin.Queue(zdo);
		List<string> keep = new();
		int removed = 0;
		foreach (string name in queue)
		{
			if (name == item && removed < amount)
			{
				removed++;
			}
			else
			{
				keep.Add(name);
			}
		}
		if (removed == 0)
		{
			return;
		}
		Plugin.WriteQueue(zdo, keep);
		nview.InvokeRPC(sender, Plugin.RpcGive, item, removed);
	}

	public static void TakeFuel(Smelter smelter, long sender, int amount)
	{
		ZNetView nview = smelter.m_nview;
		if (!nview.IsOwner() || smelter.m_fuelItem == null)
		{
			return;
		}
		ZDO zdo = nview.GetZDO();
		float fuel = zdo.GetFloat(ZDOVars.s_fuel);
		int n = Mathf.Min(amount, Mathf.FloorToInt(fuel));
		if (n <= 0)
		{
			return;
		}
		zdo.Set(ZDOVars.s_fuel, fuel - n);
		nview.InvokeRPC(sender, Plugin.RpcGive, smelter.m_fuelItem.gameObject.name, n);
	}

	public static void TakeOutput(Smelter smelter, long sender)
	{
		ZNetView nview = smelter.m_nview;
		if (!nview.IsOwner())
		{
			return;
		}
		ZDO zdo = nview.GetZDO();
		int amount = zdo.GetInt(ZDOVars.s_spawnAmount);
		string ore = zdo.GetString(ZDOVars.s_spawnOre);
		Smelter.ItemConversion? conversion = smelter.GetItemConversion(ore);
		if (amount <= 0 || conversion?.m_to == null)
		{
			return;
		}
		zdo.Set(ZDOVars.s_spawnOre, "");
		zdo.Set(ZDOVars.s_spawnAmount, 0);
		nview.InvokeRPC(sender, Plugin.RpcGive, conversion.m_to.gameObject.name, amount);
	}

	/// <summary>Items ins Inventar legen. Was nicht passt, fällt vor dem Spieler auf den Boden, nichts geht verloren.</summary>
	public static void Give(string item, int amount)
	{
		Player player = Player.m_localPlayer;
		GameObject? prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
		ItemDrop? template = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
		if (player == null || template == null || amount <= 0)
		{
			return;
		}
		Inventory inventory = player.GetInventory();
		int max = Mathf.Max(1, template.m_itemData.m_shared.m_maxStackSize);
		int remaining = amount;
		int dropped = 0;
		while (remaining > 0)
		{
			int chunk = Mathf.Min(remaining, max);
			remaining -= chunk;
			ItemDrop.ItemData data = template.m_itemData.Clone();
			data.m_dropPrefab = prefab;
			data.m_stack = chunk;
			data.m_durability = data.GetMaxDurability();
			data.m_worldLevel = (byte)Game.m_worldLevel; // wie Vanilla Inventory.AddItem(prefab, amount), Review 27.10.2026: sonst stapelt es nicht und zählt beim Craften nicht
			int before = inventory.NrOfItemsIncludingStacks();
			if (inventory.CanAddItem(data, chunk))
			{
				inventory.AddItem(data);
			}
			int added = inventory.NrOfItemsIncludingStacks() - before;
			int rest = chunk - added;
			if (rest > 0)
			{
				ItemDrop.ItemData restData = template.m_itemData.Clone();
				restData.m_dropPrefab = prefab;
				restData.m_durability = restData.GetMaxDurability();
				restData.m_worldLevel = (byte)Game.m_worldLevel;
				Vector3 at = player.transform.position + player.transform.forward * 0.7f + Vector3.up;
				ItemDrop.DropItem(restData, rest, at, Quaternion.identity);
				dropped += rest;
			}
		}
		Gc.TopLeft(GcLoc.Get("gcss_received", amount, Plugin.ItemName(item)));
		if (dropped > 0)
		{
			Gc.Center(GcLoc.Get("gcss_dropped", dropped));
		}
		StationWindow.Refresh();
	}

	public static void Assign(ZNetView nview, string key, string value)
	{
		if (nview.IsOwner())
		{
			nview.GetZDO().Set(Plugin.KeyPrefix + key, value);
		}
	}

	/// <summary>
	/// Beim Besitzer: Rohstoffe aus „Alles einfüllen" in die Warteschlange (Review 27.10.2026: ein Sammel-RPC statt eines RPC
	/// je Item). Was nicht erlaubt ist, nicht mehr passt oder ankommt, nachdem der Besitz gewechselt hat, geht über
	/// GC_SS_Give an den Spieler zurück; vorher waren solche Items verloren.
	/// </summary>
	public static void AddOre(Smelter smelter, long sender, string item, int amount, bool cheated)
	{
		ZNetView nview = smelter.m_nview;
		if (amount <= 0 || string.IsNullOrEmpty(item))
		{
			return;
		}
		int accepted = 0;
		if (nview.IsOwner() && smelter.IsItemAllowed(item))
		{
			accepted = Mathf.Clamp(smelter.m_maxOre - smelter.GetQueueSize(), 0, amount);
			for (int i = 0; i < accepted; i++)
			{
				smelter.QueueOre(item, cheated);
			}
			if (accepted > 0)
			{
				smelter.m_oreAddedEffects.Create(smelter.transform.position, smelter.transform.rotation);
			}
		}
		if (amount > accepted)
		{
			nview.InvokeRPC(sender, Plugin.RpcGive, item, amount - accepted);
		}
	}

	/// <summary>Beim Besitzer: Brennstoff aus „Alles einfüllen" auf einmal, Überschuss zurück an den Spieler (wie <see cref="AddOre"/>).</summary>
	public static void AddFuel(Smelter smelter, long sender, int amount)
	{
		ZNetView nview = smelter.m_nview;
		if (amount <= 0 || smelter.m_fuelItem == null)
		{
			return;
		}
		int accepted = 0;
		if (nview.IsOwner())
		{
			float fuel = smelter.GetFuel();
			accepted = Mathf.Clamp(Mathf.FloorToInt(smelter.m_maxFuel - fuel), 0, amount);
			if (accepted > 0)
			{
				smelter.SetFuel(fuel + accepted);
				smelter.m_fuelAddedEffects.Create(smelter.transform.position, smelter.transform.rotation, smelter.transform);
			}
		}
		if (amount > accepted)
		{
			nview.InvokeRPC(sender, Plugin.RpcGive, smelter.m_fuelItem.gameObject.name, amount - accepted);
		}
	}

	/// <summary>
	/// RPC an den Besitzer eines Gebäudes. Hat es gerade keinen, übernimmt der Spieler es selbst: Ein RPC an „niemand"
	/// ginge an alle (ZRoutedRpc.Everybody), und beim Einfüllen gäbe dann jeder die Items zurück.
	/// </summary>
	public static void ToOwner(ZNetView nview, string method, params object[] args)
	{
		if (!nview.GetZDO().HasOwner())
		{
			nview.ClaimOwnership();
		}
		nview.InvokeRPC(method, args);
	}
}

// Das Verzeichnis dauerhafte Kisten-ID -> geladene Kiste pflegt die Funktion LinkedChests (Container.Awake).

/// <summary>Umschalt + E an einem Gebäude-Schalter (Smelter) öffnet das Menü.</summary>
[HarmonyPatch(typeof(Switch), nameof(Switch.Interact))]
internal static class SwitchInteractPatch
{
	private static bool Prefix(Switch __instance, Humanoid character, bool hold, bool alt, ref bool __result)
	{
		if (!Plugin.MenuOn || !alt || hold || character != Player.m_localPlayer)
		{
			return true;
		}
		Smelter? smelter = __instance.GetComponentInParent<Smelter>();
		if (smelter == null)
		{
			return true;
		}
		return OpenMenu.Try(Station.Of(smelter), ref __result);
	}
}

/// <summary>Umschalt + E an Bienenstock oder Vogelnest öffnet das Menü (E erntet weiter wie in Vanilla).</summary>
[HarmonyPatch(typeof(Beehive), nameof(Beehive.Interact))]
internal static class BeehiveInteractPatch
{
	private static bool Prefix(Beehive __instance, Humanoid character, bool repeat, bool alt, ref bool __result)
	{
		if (!Plugin.MenuOn || !alt || repeat || character != Player.m_localPlayer)
		{
			return true;
		}
		return OpenMenu.Try(Station.Of(__instance), ref __result);
	}
}

/// <summary>Umschalt + E am Saftzapfer öffnet das Menü.</summary>
[HarmonyPatch(typeof(SapCollector), nameof(SapCollector.Interact))]
internal static class SapInteractPatch
{
	private static bool Prefix(SapCollector __instance, Humanoid character, bool repeat, bool alt, ref bool __result)
	{
		if (!Plugin.MenuOn || !alt || repeat || character != Player.m_localPlayer)
		{
			return true;
		}
		return OpenMenu.Try(Station.Of(__instance), ref __result);
	}
}

internal static class OpenMenu
{
	/// <summary>false = Menü geöffnet (Vanilla-Benutzen überspringen).</summary>
	public static bool Try(Station? station, ref bool result)
	{
		if (station == null || !station.Valid)
		{
			return true;
		}
		if (!PrivateArea.CheckAccess(station.Position))
		{
			return true; // fremder Ward: kein Menü, Vanilla-Verhalten bleibt
		}
		StationWindow.Open(station);
		result = true;
		return false;
	}

	public static string Hover => Localization.instance.Localize("[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] ") + GcLoc.Get("gcss_hover");
}

[HarmonyPatch(typeof(Switch), nameof(Switch.GetHoverText))]
internal static class SwitchHoverPatch
{
	private static void Postfix(Switch __instance, ref string __result)
	{
		if (Plugin.MenuOn && !string.IsNullOrEmpty(__result) && __instance.GetComponentInParent<Smelter>() != null)
		{
			__result += "\n" + OpenMenu.Hover;
		}
	}
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.GetHoverText))]
internal static class BeehiveHoverPatch
{
	private static void Postfix(ref string __result)
	{
		if (Plugin.MenuOn && !string.IsNullOrEmpty(__result))
		{
			__result += "\n" + OpenMenu.Hover;
		}
	}
}

[HarmonyPatch(typeof(SapCollector), nameof(SapCollector.GetHoverText))]
internal static class SapHoverPatch
{
	private static void Postfix(ref string __result)
	{
		if (Plugin.MenuOn && !string.IsNullOrEmpty(__result))
		{
			__result += "\n" + OpenMenu.Hover;
		}
	}
}

/// <summary>Während der Kistenwahl greift der Spieler nicht an.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
internal static class AttackBlockPatch
{
	private static bool Prefix(Humanoid __instance) => !(ChestPicker.Active && __instance == Player.m_localPlayer);
}

/// <summary>Während der Kistenwahl baut ein Linksklick im Baumodus nichts (der Klick wählt die Kiste).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
internal static class PlacementBlockPatch
{
	private static void Prefix(Player __instance, ref bool takeInput)
	{
		if (ChestPicker.Active && __instance == Player.m_localPlayer)
		{
			takeInput = false;
		}
	}
}

/// <summary>
/// ValheimPlus-Brücke: Die Maschinen-Automatik von V+ fragt Kisten über InventoryAssistant.GetNearbyChestsForMachine ab.
/// Während Smelter.UpdateSmelter (Einfüllen) bzw. Smelter.Spawn (Ablegen) läuft, ersetzt diese Mod die Liste durch
/// die zugewiesenen Kisten und filtert pro Rohstoff. Bienenstock, Vogelnest und Saftzapfer fragen nur zum Ablegen:
/// Dort gelten die Ausgangskisten.
/// Review 27.10.2026: Jede Einhängung ist einzeln abgesichert (VppPatch, V+-Parameter per Position). Eingang und Ausgang
/// wirken nur, wenn alle ihre Teile eingehängt sind; sonst bleibt V+ dort unverändert und das Menü zeigt die Zeilen
/// nicht. Erlaubte Kisten je Schlüssel werden einmal je Durchlauf (Smelter-Tick) berechnet, nicht bei jeder Entnahme.
/// </summary>
internal static class VPlusBridge
{
	private enum Mode
	{
		None,
		Input,
		Output
	}

	/// <summary>Ein laufender Durchlauf eines Gebäudes (UpdateSmelter = Eingang, Spawn = Ausgang) mit seinem Zwischenspeicher.</summary>
	private sealed class Pass
	{
		public readonly Mode Mode;
		public readonly Smelter Smelter;
		private Station? station;

		public Pass(Mode mode, Smelter smelter)
		{
			Mode = mode;
			Smelter = smelter;
		}

		public Station Station => station ??= Station.Of(Smelter)!;

		/// <summary>V+-Kistenliste vor dem Hinzufügen der zugewiesenen Kisten: gilt für Rohstoffe ohne eigene Zuweisung.</summary>
		public HashSet<Container> Original = new();

		/// <summary>Zugewiesene Eingangskisten aller Rohstoffe und des Brennstoffs, einmal je Durchlauf.</summary>
		public List<Container>? Inputs;

		/// <summary>Zugewiesene Ausgangskisten, einmal je Durchlauf.</summary>
		public List<Container>? Outputs;

		/// <summary>Erlaubte Kisten je Schlüssel, einmal je Durchlauf. null = keine (geladene) Zuweisung, dann gilt <see cref="Original"/>.</summary>
		public readonly Dictionary<string, HashSet<Container>?> Allowed = new();
	}

	// Stapel, weil Smelter.Spawn innerhalb von Smelter.UpdateSmelter aufgerufen wird
	private static readonly Stack<Pass> Context = new();

	/// <summary>Eingangskisten der Schmelzen wirken (alle Einhängungen dafür aktiv).</summary>
	internal static bool InputReady { get; private set; }

	/// <summary>Ausgangskisten der Schmelzen wirken.</summary>
	internal static bool OutputReady { get; private set; }

	/// <summary>Ausgangskisten von Bienenstock, Vogelnest und Saftzapfer wirken.</summary>
	internal static bool ProducerReady { get; private set; }

	/// <summary>Wirken Ausgangskisten für dieses Gebäude?</summary>
	internal static bool OutputFor(Station station) => station.IsProducer ? ProducerReady : OutputReady;

	internal static void Apply(Harmony harmony)
	{
		if (!Gc.VPlusLoaded)
		{
			return;
		}
		int total = 0;
		int active = 0;
		bool Hook(MethodBase? method, string what, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
		{
			total++;
			bool ok = VppPatch.Try(harmony, method, what, prefix, postfix, finalizer);
			if (ok)
			{
				active++;
			}
			return ok;
		}
		try
		{
			Type? assistant = AccessTools.TypeByName("ValheimPlus.InventoryAssistant");
			if (assistant == null)
			{
				Plugin.Log.LogWarning("ValheimPlus: ValheimPlus.InventoryAssistant nicht gefunden. Kistenzuweisung wirkt nicht.");
				return;
			}
			bool input = Hook(AccessTools.Method(typeof(Smelter), nameof(Smelter.UpdateSmelter)), "Smelter.UpdateSmelter",
				prefix: new HarmonyMethod(typeof(VPlusBridge), nameof(BeginInput)) { priority = Priority.First },
				finalizer: new HarmonyMethod(typeof(VPlusBridge), nameof(End)));
			bool output = Hook(AccessTools.Method(typeof(Smelter), nameof(Smelter.Spawn)), "Smelter.Spawn",
				prefix: new HarmonyMethod(typeof(VPlusBridge), nameof(BeginOutput)) { priority = Priority.First },
				finalizer: new HarmonyMethod(typeof(VPlusBridge), nameof(End)));
			bool machine = Hook(VppPatch.Find(assistant, "GetNearbyChestsForMachine"), "InventoryAssistant.GetNearbyChestsForMachine",
				postfix: new HarmonyMethod(typeof(VPlusBridge), nameof(MachineChests)) { priority = Priority.Last });
			bool list = Hook(VppPatch.Find(assistant, "RemoveItemInAmountFromChests"), "InventoryAssistant.RemoveItemInAmountFromChests",
				prefix: new HarmonyMethod(typeof(VPlusBridge), nameof(FilterChestList)));
			MethodInfo? byItem = null;
			MethodInfo? byName = null;
			foreach (MethodInfo method in assistant.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "RemoveItemFromChest"))
			{
				ParameterInfo[] parameters = method.GetParameters();
				if (parameters.Length == 3 && parameters[1].ParameterType == typeof(ItemDrop.ItemData))
				{
					byItem = method;
				}
				else if (parameters.Length == 3 && parameters[1].ParameterType == typeof(string))
				{
					byName = method;
				}
			}
			bool singleItem = Hook(byItem, "InventoryAssistant.RemoveItemFromChest(ItemData)", prefix: new HarmonyMethod(typeof(VPlusBridge), nameof(FilterSingleItem)));
			bool singleName = Hook(byName, "InventoryAssistant.RemoveItemFromChest(string)", prefix: new HarmonyMethod(typeof(VPlusBridge), nameof(FilterSingleName)));
			// Ohne die Abfrage der Maschinen-Kisten wirkt nichts; ohne alle Filter würden zugewiesene Kisten jeden Rohstoff liefern
			// bzw. ohne die Liste (Original) jede Entnahme blockieren: dann den Eingang ganz abschalten.
			ProducerReady = machine;
			OutputReady = machine && output;
			InputReady = machine && input && list && singleItem && singleName;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"ValheimPlus: Einhängen abgebrochen: {e.Message}");
			InputReady = false;
			OutputReady = false;
			ProducerReady = false;
		}
		string text = $"ValheimPlus erkannt: Ein- und Ausgangskisten, {active} von {total} V+-Einhängungen aktiv (Eingang {(InputReady ? "an" : "aus")}, Ausgang {(OutputReady ? "an" : "aus")}, Bienenstock/Vogelnest/Saftzapfer {(ProducerReady ? "an" : "aus")}).";
		if (active == total)
		{
			Plugin.Log.LogInfo(text);
		}
		else
		{
			Plugin.Log.LogWarning(text);
		}
	}

	private static void BeginInput(Smelter __instance) => Context.Push(new Pass(Mode.Input, __instance));

	private static void BeginOutput(Smelter __instance) => Context.Push(new Pass(Mode.Output, __instance));

	private static void End()
	{
		if (Context.Count > 0)
		{
			Context.Pop();
		}
	}

	/// <summary>Der laufende Eingangs-Durchlauf, wenn die Kistenzuweisung dort wirkt, sonst null (schnell für alle anderen V+-Aufrufe).</summary>
	private static Pass? InputPass()
	{
		if (Context.Count == 0 || !InputReady)
		{
			return null;
		}
		Pass pass = Context.Peek();
		return pass.Mode == Mode.Input && Plugin.ChestsOn ? pass : null;
	}

	/// <summary>V+ GetNearbyChestsForMachine(GameObject machine, float range, bool checkWard): Parameter per Position.</summary>
	private static void MachineChests(GameObject __0, ref List<Container> __result)
	{
		GameObject machine = __0;
		if (machine == null || __result == null || !Plugin.ChestsOn)
		{
			return;
		}
		if (Context.Count == 0)
		{
			// Bienenstock, Vogelnest, Saftzapfer: V+ fragt nur zum Ablegen des Ertrags
			Station? producer = Station.Of(machine.transform);
			if (producer != null && producer.IsProducer && producer.GameObject == machine)
			{
				List<Container> targets = Plugin.Resolve(producer, Plugin.Assigned(producer, Plugin.OutputKey));
				if (targets.Count > 0)
				{
					__result = targets;
				}
			}
			return;
		}
		Pass pass = Context.Peek();
		if (machine != pass.Smelter.gameObject)
		{
			return;
		}
		if (pass.Mode == Mode.Output)
		{
			if (!OutputReady)
			{
				return;
			}
			pass.Outputs ??= Plugin.Resolve(pass.Station, Plugin.Assigned(pass.Station, Plugin.OutputKey));
			if (pass.Outputs.Count > 0)
			{
				__result = new List<Container>(pass.Outputs);
			}
			return;
		}
		if (!InputReady)
		{
			return;
		}
		pass.Original = new HashSet<Container>(__result.Where(c => c != null));
		pass.Inputs ??= Plugin.Resolve(pass.Station, Plugin.InputKeys(pass.Station).SelectMany(key => Plugin.Assigned(pass.Station, key)).Distinct());
		if (pass.Inputs.Count == 0)
		{
			return;
		}
		List<Container> combined = new(__result);
		HashSet<Container> seen = new(pass.Original);
		foreach (Container chest in pass.Inputs)
		{
			if (seen.Add(chest))
			{
				combined.Add(chest);
			}
		}
		__result = combined;
	}

	/// <summary>Erlaubte Kisten für einen Schlüssel, einmal je Durchlauf berechnet (Review 27.10.2026: vorher bei jeder Entnahme neu).</summary>
	private static HashSet<Container> AllowedFor(Pass pass, string key)
	{
		if (!pass.Allowed.TryGetValue(key, out HashSet<Container>? allowed))
		{
			List<string> ids = Plugin.Assigned(pass.Station, key);
			List<Container> resolved = ids.Count > 0 ? Plugin.Resolve(pass.Station, ids) : new List<Container>();
			allowed = resolved.Count > 0 ? new HashSet<Container>(resolved) : null; // zugewiesene Kisten weg oder nicht geladen: wie ohne Zuweisung
			pass.Allowed[key] = allowed;
		}
		return allowed ?? pass.Original;
	}

	/// <summary>V+ RemoveItemInAmountFromChests(List&lt;Container&gt; chests, ItemData needle, int amount): Parameter per Position.</summary>
	private static void FilterChestList(ref List<Container> __0, ItemDrop.ItemData __1)
	{
		Pass? pass = InputPass();
		if (pass == null || __0 == null || __1 == null)
		{
			return;
		}
		HashSet<Container> allowed = AllowedFor(pass, Plugin.KeyForItem(pass.Smelter, __1));
		__0 = __0.Where(c => c != null && allowed.Contains(c)).ToList();
	}

	/// <summary>V+ RemoveItemFromChest(Container chest, ItemData needle, int amount): Parameter per Position.</summary>
	private static bool FilterSingleItem(Container __0, ItemDrop.ItemData __1, ref int __result)
	{
		Pass? pass = InputPass();
		if (pass == null || __0 == null || __1 == null)
		{
			return true;
		}
		if (!AllowedFor(pass, Plugin.KeyForItem(pass.Smelter, __1)).Contains(__0))
		{
			__result = 0;
			return false;
		}
		return true;
	}

	/// <summary>V+ RemoveItemFromChest(Container chest, string needle, int amount): Parameter per Position.</summary>
	private static bool FilterSingleName(Container __0, string __1, ref int __result)
	{
		Pass? pass = InputPass();
		if (pass == null || __0 == null || __1 == null)
		{
			return true;
		}
		if (!AllowedFor(pass, Plugin.KeyForSharedName(pass.Smelter, __1)).Contains(__0))
		{
			__result = 0;
			return false;
		}
		return true;
	}
}
