using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.ValheimPlusPlus;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace GC.ValheimPlusPlus.ItemLock;

/// <summary>
/// Fixieren (früher GC_ItemLock): Items und Kisten vor Automatik schützen.
///
/// Weißes Schloss (Alt+Linksklick): Das Item bleibt auf seinem Platz. Es wird nicht in Kisten gestapelt (Vanilla
/// „Stapel ablegen", E halten, V+ Auto-Stack), nicht von „Alles nehmen" aus Kisten geholt, nicht sortiert und nicht
/// vernichtet. Beim Aufheben des eigenen Grabsteins landet es wieder auf seinem Platz. Liegt dort schon etwas,
/// wandert das auf einen freien Platz oder, wenn keiner frei ist, in den Grabstein.
/// Craften und Bauen dürfen weiß fixierte Items verbrauchen.
///
/// Rotes Schloss (Alt+Rechtsklick): wie weiß, zusätzlich verbrauchen Craften, Verbessern und Bauen das Item nie, auch
/// nicht über „Craft from Chest" von ValheimPlus. V+-Maschinen (Schmelze, Ofen, Feuer ...) holen es nicht aus Kisten.
///
/// Beides funktioniert im eigenen Inventar und in Kisten, Karren, Schiffen und anderen Behältern. Die Markierung steht
/// in den Item-Daten (m_customData) und bleibt beim Speichern, Ablegen, Aufnehmen und Verbessern erhalten. Ältere
/// GC-Mods kennen nur das weiße Schloss und behandeln Rot wie Weiß.
///
/// Kisten: Alt+L auf eine Kiste sperrt sie für Automatik. ValheimPlus (Maschinen holen Brennstoff/Erz, legen
/// Produkte ab, Craft from Chest, Auto-Stack) und die GC-Mods nutzen diese Kiste dann nicht mehr.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_itemlock";
	public const string ModName = "GC_ItemLock";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> ItemsEnabled = null!;
	internal static ConfigEntry<bool> ChestsEnabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool ItemsOn => ItemsEnabled.Value && !Blocked && VppPlugin.Allowed;
	internal static bool ChestsOn => ChestsEnabled.Value && !Blocked && VppPlugin.Allowed && VppPlugin.OnServer;
	internal static ConfigEntry<KeyCode> ItemModifier = null!;
	internal static ConfigEntry<KeyboardShortcut> ChestKey = null!;
	internal static ConfigEntry<bool> ProtectFromStacking = null!;
	internal static ConfigEntry<bool> ProtectFromTakeAll = null!;
	internal static ConfigEntry<bool> RestoreAfterDeath = null!;
	internal static ConfigEntry<bool> WhiteStacksTakeNew = null!;
	internal static ConfigEntry<bool> RedStacksTakeNew = null!;
	internal static Sprite? LockIcon;

	internal static readonly Color WhiteLock = new(1f, 1f, 1f, 0.95f);
	internal static readonly Color RedLock = new(1f, 0.28f, 0.22f, 1f);

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = VppPlugin.Log;
		ItemsEnabled = Gc.Bind(Config, "2 - Item Lock", "ItemsEnabled", true, "Items fixieren: Alt+Linksklick = weißes Schloss (nicht einlagern, nicht sortieren, Platz nach dem Tod), Alt+Rechtsklick = rotes Schloss (zusätzlich nie zum Craften oder Bauen verbrauchen). Im Inventar und in Kisten.");
		ChestsEnabled = Gc.BindSynced(Config, "2 - Item Lock", "ChestsEnabled", true, "Kisten mit Alt+L für Automatik sperren (wird vom Server vorgegeben).");
		ItemModifier = Gc.Bind(Config, "2 - Item Lock", "ItemLockModifier", KeyCode.LeftAlt, "Diese Taste halten: Linksklick auf ein Item setzt oder löst das weiße Schloss, Rechtsklick das rote.");
		ChestKey = Gc.Bind(Config, "2 - Item Lock", "ChestLockKey", new KeyboardShortcut(KeyCode.L, KeyCode.LeftAlt), "Auf eine Kiste schauen und diese Tasten drücken: Kiste für Automatik sperren oder freigeben.");
		ProtectFromStacking = Gc.Bind(Config, "2 - Item Lock - Rules", "ProtectFromStacking", true, "Fixierte Items nie in Kisten stapeln (Stapel ablegen, E halten, V+ Auto-Stack).");
		ProtectFromTakeAll = Gc.Bind(Config, "2 - Item Lock - Rules", "ProtectFromTakeAll", true, "Fixierte Items in Kisten und Fahrzeugen bleiben bei 'Alles nehmen' liegen (Grabsteine ausgenommen).");
		WhiteStacksTakeNew = Gc.Bind(Config, "2 - Item Lock - Rules", "WhiteStacksTakeNewItems", true, "Neue Items gleicher Art (Aufheben, Alles nehmen, Craften) dürfen in weiß fixierte Stapel. Aus = sie bekommen einen eigenen Stapel.");
		RedStacksTakeNew = Gc.Bind(Config, "2 - Item Lock - Rules", "RedStacksTakeNewItems", false, "Neue Items gleicher Art dürfen in rot fixierte Stapel und sind dann ebenfalls gesperrt. Aus = sie bekommen einen eigenen Stapel.");
		RestoreAfterDeath = Gc.Bind(Config, "2 - Item Lock - Rules", "RestoreAfterDeath", true, "Beim Leeren des eigenen Grabsteins kommen fixierte Items auf ihren alten Platz. Was dort liegt, wandert auf einen freien Platz oder in den Grabstein.");

		VPlusFilter.Apply(harmony);
	}

	internal static void Update()
	{
		if (!ChestsOn || Gc.IsHeadless || Player.m_localPlayer == null || InventoryGui.IsVisible())
		{
			return;
		}
		if (!Gc.Down(ChestKey))
		{
			return;
		}
		Container? container = Gc.Hovered<Container>();
		if (!GcWard.IsPlayerChest(container))
		{
			return;
		}
		if (!PrivateArea.CheckAccess(container!.transform.position))
		{
			return;
		}
		ZNetView nview = container.m_nview;
		if (nview == null || !nview.IsValid())
		{
			return;
		}
		// Review 27.10.2026: Die Sperre setzt der Besitzer der Kiste (ChestLockRpcPatch), statt dass wir den Besitz
		// übernehmen. Mit ClaimOwnership schrieben wir unseren womöglich veralteten Stand; hat der bisherige Besitzer schon
		// dieselbe DataRevision verschickt, verwirft der Server unsere Änderung und die Stände laufen auseinander. Über den
		// Besitzer geht das auch, während jemand die Kiste offen hat.
		bool locked = !nview.GetZDO().GetBool(GcKeys.ChestNoAuto);
		ChestLockRpcPatch.Send(nview, locked);
		Gc.Center(GcLoc.Get(locked ? "gcl_chest_locked" : "gcl_chest_unlocked"));
	}

	/// <summary>Schloss nur im eigenen Inventar und in der offenen Kiste (auch der Ansicht verbundener Kisten), nicht in fremden Rastern wie den Sockeln eines EpicLoot-Items.</summary>
	internal static bool Lockable(InventoryGui gui, InventoryGrid? grid)
	{
		Inventory? inventory = grid != null ? grid.GetInventory() : null;
		if (inventory == null)
		{
			return false;
		}
		Player player = Player.m_localPlayer;
		if (player != null && inventory == player.GetInventory())
		{
			return true;
		}
		Container container = gui.m_currentContainer;
		return container != null && inventory == container.GetInventory();
	}

	/// <summary>Darf in diesen Stapel nichts Neues gelegt werden (Einstellungen WhiteStacksTakeNewItems / RedStacksTakeNewItems)?</summary>
	internal static bool Closed(ItemDrop.ItemData? item)
	{
		if (!ItemsOn || !Gc.IsLocked(item))
		{
			return false;
		}
		return Gc.IsHardLocked(item) ? !RedStacksTakeNew.Value : !WhiteStacksTakeNew.Value;
	}

	internal static bool AnyClosed => ItemsOn && (!RedStacksTakeNew.Value || !WhiteStacksTakeNew.Value);

	/// <summary>0 = frei, 1 = weißes Schloss, 2 = rotes Schloss.</summary>
	internal static int Level(ItemDrop.ItemData? item) => Gc.IsHardLocked(item) ? 2 : Gc.IsLocked(item) ? 1 : 0;

	internal static void SetLevel(ItemDrop.ItemData item, int level)
	{
		if (level <= 0)
		{
			item.m_customData.Remove(GcKeys.ItemLocked);
			item.m_customData.Remove(GcKeys.ItemLockedHard);
			return;
		}
		item.m_customData[GcKeys.ItemLocked] = "1";
		if (level >= 2)
		{
			item.m_customData[GcKeys.ItemLockedHard] = "1";
		}
		else
		{
			item.m_customData.Remove(GcKeys.ItemLockedHard);
		}
	}

	/// <summary>Weiß: an/aus (Rot wird zu Weiß). Rot: an/aus.</summary>
	internal static void Toggle(ItemDrop.ItemData item, bool red)
	{
		int level = Level(item);
		int next = red ? (level == 2 ? 0 : 2) : (level == 1 ? 0 : 1);
		SetLevel(item, next);
		string key = next == 0 ? "gcl_item_unlocked" : next == 1 ? "gcl_item_locked" : "gcl_item_locked_red";
		Gc.TopLeft(GcLoc.Get(key, Localization.instance.Localize(item.m_shared.m_name)));
	}
}

/// <summary>
/// Alt + Linksklick auf ein Item: weißes Schloss setzen oder lösen, statt es aufzunehmen. Im Inventar und im geöffneten
/// Behälter. Review 27.10.2026: läuft als letzter Prefix und schaltet nur, wenn kein anderer den Klick schon übernommen
/// hat (__runOriginal, z. B. das Sockel-Fenster von EpicLoot), und nur in Spielerinventar und offener Kiste.
/// </summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class SelectPatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false; // ein anderer Prefix hat den Klick schon übernommen: kein Schloss schalten
		}
		if (!Plugin.ItemsOn || item == null || __instance.m_dragGo != null || !Input.GetKey(Plugin.ItemModifier.Value) || !Plugin.Lockable(__instance, grid))
		{
			return true; // beim Ziehen eines Items bleibt der Klick ein normales Ablegen
		}
		Plugin.Toggle(item, false);
		grid.GetInventory().Changed(); // bei Kisten speichert der Besitzer (wer die Kiste geöffnet hat) die Markierung
		return false;
	}
}

/// <summary>Alt + Rechtsklick auf ein Item: rotes Schloss setzen oder lösen, statt das Item zu benutzen (Bedingungen wie beim Linksklick).</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnRightClickItem))]
internal static class RightClickPatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false;
		}
		if (!Plugin.ItemsOn || item == null || !Input.GetKey(Plugin.ItemModifier.Value) || !Plugin.Lockable(__instance, grid))
		{
			return true;
		}
		Plugin.Toggle(item, true);
		grid.GetInventory().Changed();
		return false;
	}
}

/// <summary>Schloss-Symbol auf fixierten Items, weiß oder rot.</summary>
[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
internal static class GridIconPatch
{
	private const string IconName = "GC_LockIcon";
	private static readonly Dictionary<int, ItemDrop.ItemData> Locked = new();

	private static void Postfix(InventoryGrid __instance)
	{
		Inventory? inventory = __instance.m_inventory;
		if (inventory == null)
		{
			return;
		}
		Plugin.LockIcon ??= GcUi.LockSprite();
		Locked.Clear();
		if (Plugin.ItemsOn)
		{
			foreach (ItemDrop.ItemData item in inventory.m_inventory)
			{
				if (Gc.IsLocked(item))
				{
					Locked[item.m_gridPos.y * 1000 + item.m_gridPos.x] = item;
				}
			}
		}
		foreach (InventoryElement element in __instance.m_elements)
		{
			bool show = Locked.TryGetValue(element.Position.y * 1000 + element.Position.x, out ItemDrop.ItemData item);
			Transform? icon = element.transform.Find(IconName);
			if (icon == null)
			{
				if (!show)
				{
					continue;
				}
				GameObject go = new(IconName, typeof(RectTransform), typeof(Image));
				go.transform.SetParent(element.transform, false);
				RectTransform rect = (RectTransform)go.transform;
				rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
				rect.pivot = new Vector2(0f, 1f);
				rect.anchoredPosition = new Vector2(3f, -3f);
				rect.sizeDelta = new Vector2(16f, 16f);
				Image image = go.GetComponent<Image>();
				image.sprite = Plugin.LockIcon;
				image.raycastTarget = false;
				icon = go.transform;
			}
			if (show)
			{
				icon.GetComponent<Image>().color = Gc.IsHardLocked(item) ? Plugin.RedLock : Plugin.WhiteLock;
			}
			if (icon.gameObject.activeSelf != show)
			{
				icon.gameObject.SetActive(show);
			}
		}
	}
}

/// <summary>
/// Neue Items in fixierte Stapel? Vanilla legt Aufgehobenes, Gecraftetes und „Alles nehmen" zuerst auf vorhandene Stapel
/// gleicher Art (Inventory.FindFreeStackItem). Ist das für weiß oder rot fixierte Stapel abgeschaltet, wird ein anderer
/// Stapel oder ein freier Platz genommen. FindFreeStackSpace zählt den Platz in gesperrten Stapeln dann nicht mit,
/// damit „passt noch ins Inventar" stimmt.
/// </summary>
[HarmonyPatch]
internal static class StackMergePatches
{
	[HarmonyPatch(typeof(Inventory), nameof(Inventory.FindFreeStackItem))]
	[HarmonyPostfix]
	private static void FindItem(Inventory __instance, string name, int quality, float worldLevel, ref ItemDrop.ItemData? __result)
	{
		if (__result == null || !Plugin.Closed(__result))
		{
			return;
		}
		__result = null;
		foreach (ItemDrop.ItemData item in __instance.m_inventory)
		{
			if (item.m_shared.m_name == name && item.m_quality == quality && item.m_stack < item.m_shared.m_maxStackSize && item.m_worldLevel == worldLevel && !Plugin.Closed(item))
			{
				__result = item;
				return;
			}
		}
	}

	/// <summary>
	/// Vanilla MoveAll legt jedes Item zuerst auf seinen alten Platz (privates AddItem mit Position) und mischt es dort
	/// in einen gleichartigen Stapel, auch in einen fixierten. Während MoveAll wird das für gesperrte Stapel abgelehnt,
	/// das Item geht dann den zweiten Weg (AddItem ohne Position, siehe FindItem). Ziehen per Maus bleibt unberührt.
	/// </summary>
	private static int moveAllDepth;

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
	[HarmonyPrefix]
	private static void MoveAllEnter() => moveAllDepth++;

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
	[HarmonyFinalizer]
	private static void MoveAllLeave() => moveAllDepth = Math.Max(0, moveAllDepth - 1);

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })]
	[HarmonyPrefix]
	private static bool AddAtSlot(Inventory __instance, int x, int y, ref bool __result)
	{
		if (moveAllDepth <= 0 || !Plugin.AnyClosed || !Plugin.Closed(__instance.GetItemAt(x, y)))
		{
			return true;
		}
		__result = false;
		return false;
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.FindFreeStackSpace))]
	[HarmonyPostfix]
	private static void FindSpace(Inventory __instance, string name, float worldLevel, ref int __result)
	{
		if (__result <= 0 || !Plugin.AnyClosed)
		{
			return;
		}
		foreach (ItemDrop.ItemData item in __instance.m_inventory)
		{
			if (item.m_shared.m_name == name && item.m_stack < item.m_shared.m_maxStackSize && item.m_worldLevel == worldLevel && Plugin.Closed(item))
			{
				__result -= item.m_shared.m_maxStackSize - item.m_stack;
			}
		}
		__result = Math.Max(0, __result);
	}
}

/// <summary>Tooltip nennt die Art des Schlosses.</summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new[] { typeof(int) })]
internal static class TooltipPatch
{
	private static void Postfix(ItemDrop.ItemData __instance, ref string __result)
	{
		if (Plugin.ItemsOn && Gc.IsLocked(__instance))
		{
			__result += "\n\n" + GcLoc.Get(Gc.IsHardLocked(__instance) ? "gcl_tooltip_red" : "gcl_tooltip_white");
		}
	}
}

/// <summary>Stapeln in Kisten (Vanilla-Button, E halten, V+ Auto-Stack) lässt fixierte Items im Spielerinventar.</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
internal static class StackAllPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(Inventory fromInventory, out List<ItemDrop.ItemData>? __state)
	{
		__state = null;
		if (!Plugin.ItemsOn || !Plugin.ProtectFromStacking.Value)
		{
			return;
		}
		__state = fromInventory.m_inventory.FindAll(Gc.IsLocked);
		if (__state.Count == 0)
		{
			__state = null;
			return;
		}
		fromInventory.m_inventory.RemoveAll(Gc.IsLocked);
	}

	[HarmonyPriority(Priority.Last)]
	private static void Finalizer(Inventory fromInventory, List<ItemDrop.ItemData>? __state)
	{
		if (__state != null)
		{
			fromInventory.m_inventory.AddRange(__state);
			fromInventory.Changed();
		}
	}
}

/// <summary>
/// Merkt, dass gerade ein „Alles nehmen" läuft (Button, E halten, Grabstein). Kisten und Fahrzeuge: fixierte Items
/// bleiben liegen. Eigener Grabstein: alles kommt mit, fixierte Items zurück auf ihren Platz. Fremde Grabsteine: Vanilla.
/// </summary>
[HarmonyPatch]
internal static class TakeAllContext
{
	internal static int Depth;
	internal static int GraveDepth;

	private const int None = 0;
	private const int Chest = 1;
	private const int OwnGrave = 2;

	private static int Kind(Container? container)
	{
		if (container == null)
		{
			return None;
		}
		TombStone? tomb = container.GetComponent<TombStone>();
		if (tomb == null)
		{
			return Chest;
		}
		return tomb.m_nview != null && tomb.m_nview.IsValid() && tomb.IsOwner() ? OwnGrave : None;
	}

	private static void Enter(int kind)
	{
		if (kind == Chest)
		{
			Depth++;
		}
		else if (kind == OwnGrave)
		{
			GraveDepth++;
		}
	}

	private static void Leave(int kind)
	{
		if (kind == Chest)
		{
			Depth = Math.Max(0, Depth - 1);
		}
		else if (kind == OwnGrave)
		{
			GraveDepth = Math.Max(0, GraveDepth - 1);
		}
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTakeAll))]
	[HarmonyPrefix]
	private static void GuiPrefix(InventoryGui __instance, out int __state)
	{
		__state = Kind(__instance.m_currentContainer);
		Enter(__state);
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTakeAll))]
	[HarmonyFinalizer]
	private static void GuiFinalizer(int __state) => Leave(__state);

	[HarmonyPatch(typeof(Container), nameof(Container.RPC_TakeAllResponse))]
	[HarmonyPrefix]
	private static void RpcPrefix(Container __instance, out int __state)
	{
		__state = Kind(__instance);
		Enter(__state);
	}

	[HarmonyPatch(typeof(Container), nameof(Container.RPC_TakeAllResponse))]
	[HarmonyFinalizer]
	private static void RpcFinalizer(int __state) => Leave(__state);
}

/// <summary>„Alles nehmen" lässt fixierte Items in der Kiste. Nur in diesem Fall, nie bei Spiel-internen MoveAll-Aufrufen (z. B. Beutekisten beim Zerstören).</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
internal static class MoveAllPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(Inventory fromInventory, out List<ItemDrop.ItemData>? __state)
	{
		__state = null;
		if (!Plugin.ItemsOn || !Plugin.ProtectFromTakeAll.Value || TakeAllContext.Depth <= 0)
		{
			return;
		}
		__state = fromInventory.m_inventory.FindAll(Gc.IsLocked);
		if (__state.Count == 0)
		{
			__state = null;
			return;
		}
		fromInventory.m_inventory.RemoveAll(Gc.IsLocked);
	}

	[HarmonyPriority(Priority.Last)]
	private static void Finalizer(Inventory fromInventory, List<ItemDrop.ItemData>? __state)
	{
		if (__state != null)
		{
			fromInventory.m_inventory.AddRange(__state);
			fromInventory.Changed();
		}
	}
}

/// <summary>
/// Eigener Grabstein: fixierte Items zurück auf ihren alten Platz. Der Grabstein übernimmt beim Tod die Positionen
/// (Inventory.MoveInventoryToGrave). Vanilla legt beim Aufheben jedes Item zuerst dorthin zurück, aber nur auf einen
/// freien Platz, und ValheimPlus (mergeWithExistingStacks) mischt Stapel vorher in vorhandene Stapel.
/// Deshalb erledigt dieser Prefix das vor V+ und Vanilla: Was auf dem Platz eines fixierten Items liegt, rückt im
/// Inventar auf einen freien Platz (gleiches Objekt, bleibt ausgerüstet). Ist keiner frei, tauschen die beiden den Platz:
/// Das verdrängte Item liegt danach im Grabstein (Review 27.10.2026: vorher fiel es zu Boden, und Valheim räumt
/// Bodenitems u. U. nach einer Stunde weg, ItemDrop.cs:1405–1407). Ausgerüstete Items und Quest-Items bleiben im
/// Inventar; dann läuft das fixierte Item den normalen Weg und bleibt, wenn nichts frei ist, im Grabstein. Was von den
/// übrigen Grabstein-Items nicht mehr passt, bleibt wie in Vanilla im Grabstein.
/// </summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
internal static class GraveRestorePatch
{
	internal sealed class State
	{
		public int Restored;
		public int Moved;

		/// <summary>Verdrängte Items, die in den Grabstein getauscht wurden (MoveAll versucht sie danach noch einmal).</summary>
		public readonly List<ItemDrop.ItemData> Swapped = new();
	}

	[HarmonyPriority(Priority.First)]
	private static void Prefix(Inventory __instance, Inventory fromInventory, out State? __state)
	{
		__state = null;
		Player? player = Player.m_localPlayer;
		if (!Plugin.ItemsOn || !Plugin.RestoreAfterDeath.Value || TakeAllContext.GraveDepth <= 0 || player == null || __instance != player.GetInventory())
		{
			return;
		}
		List<ItemDrop.ItemData> locked = fromInventory.m_inventory.FindAll(item =>
			Gc.IsLocked(item) && item.m_gridPos.x >= 0 && item.m_gridPos.y >= 0 && item.m_gridPos.x < __instance.m_width && item.m_gridPos.y < __instance.m_height);
		if (locked.Count == 0)
		{
			return;
		}
		HashSet<int> reserved = new();
		foreach (ItemDrop.ItemData item in locked)
		{
			reserved.Add(Key(item.m_gridPos));
		}
		__state = new State();
		foreach (ItemDrop.ItemData item in locked)
		{
			ItemDrop.ItemData? occupant = __instance.GetItemAt(item.m_gridPos.x, item.m_gridPos.y);
			bool swap = false;
			if (occupant != null)
			{
				Vector2i free = FreeSlot(__instance, reserved, __instance.TopFirst(occupant));
				if (free.x >= 0)
				{
					occupant.m_gridPos = free; // nur umsetzen: gleiches Objekt, Ausrüstung und Stapel bleiben unverändert
					__state.Moved++;
				}
				else if (!player.IsItemEquiped(occupant) && !occupant.m_shared.m_questItem)
				{
					swap = true;
				}
				else
				{
					reserved.Remove(Key(item.m_gridPos));
					continue; // ausgerüstet oder Quest-Item und kein Platz frei: das fixierte Item geht den normalen Weg
				}
			}
			// gleiches Objekt, gleicher Platz (m_gridPos aus dem Grabstein): Schloss und Item-Daten bleiben erhalten
			fromInventory.m_inventory.Remove(item);
			if (swap)
			{
				// Platz tauschen: Das verdrängte Item kommt in den Grabstein, auf genau den Platz, den das fixierte Item dort
				// frei macht (gleiche Koordinaten, der Grabstein hat die Größe des Inventars, Inventory.MoveInventoryToGrave)
				__instance.m_inventory.Remove(occupant!);
				fromInventory.m_inventory.Add(occupant!);
				__state.Swapped.Add(occupant!);
			}
			__instance.m_inventory.Add(item);
			__state.Restored++;
		}
		__instance.Changed();
		fromInventory.Changed();
	}

	[HarmonyPriority(Priority.Last)]
	private static void Finalizer(Inventory fromInventory, State? __state)
	{
		if (__state == null || __state.Restored == 0)
		{
			return;
		}
		Gc.TopLeft(GcLoc.Get("gcl_grave_moved", __state.Restored, __state.Moved));
		int kept = 0;
		foreach (ItemDrop.ItemData item in __state.Swapped)
		{
			if (fromInventory.m_inventory.Contains(item))
			{
				kept++; // MoveAll kann einen Teil noch auf vorhandene Stapel verteilt haben
			}
		}
		if (kept > 0)
		{
			Gc.Center(GcLoc.Get("gcl_grave_kept", kept));
		}
	}

	private static int Key(Vector2i pos) => pos.y * 1000 + pos.x;

	/// <summary>Freier Platz wie Inventory.FindEmptySlot, aber nicht auf einem Platz, den ein fixiertes Grabstein-Item braucht.</summary>
	private static Vector2i FreeSlot(Inventory inventory, HashSet<int> reserved, bool topFirst)
	{
		for (int i = 0; i < inventory.m_height; i++)
		{
			int y = topFirst ? i : inventory.m_height - 1 - i;
			for (int x = 0; x < inventory.m_width; x++)
			{
				if (!reserved.Contains(y * 1000 + x) && inventory.GetItemAt(x, y) == null)
				{
					return new Vector2i(x, y);
				}
			}
		}
		return new Vector2i(-1, -1);
	}
}

/// <summary>
/// Rotes Schloss beim Craften, Bauen und bei V+-Maschinen. Solange ein solcher Vorgang läuft, ersetzen die Patches in
/// <see cref="RedInventoryPatches"/> Zählen, Suchen und Entnehmen nach Namen durch Varianten, die rot fixierte Items
/// überspringen. Die Inventarliste selbst wird nie verändert: das Spiel läuft an vielen Stellen über diese Liste,
/// während es Anforderungen prüft (z. B. Player.OnInventoryChanged → UpdateKnownRecipesList → HaveRequirements).
/// Ersetzt wird nur, wenn das Inventar überhaupt ein rotes Item enthält, sonst laufen Original und andere Mods.
/// </summary>
internal static class RedContext
{
	// Craften, Verbessern, Bauen und Mengenanzeige: Zähler GcHooks.RedCraft im Kern (auch das Gebäude-Menü von GC_Kisten zählt dort)

	/// <summary>V+-Maschinen (Kochstelle holt Rohes aus Kisten, Feuer holt Brennstoff aus Kisten): gilt nur für Kisten.</summary>
	internal static int Chests;

	internal static bool Applies(Inventory inventory)
	{
		if ((GcHooks.RedCraft <= 0 && Chests <= 0) || !Plugin.ItemsOn)
		{
			return false;
		}
		if (GcHooks.RedCraft <= 0 && Player.m_localPlayer != null && inventory == Player.m_localPlayer.GetInventory())
		{
			return false; // Feuer von Hand nachlegen darf das eigene Inventar nutzen, geschützt sind dabei nur Kisten
		}
		return inventory.m_inventory.Exists(Gc.IsHardLocked);
	}

	internal static void Leave(ref int depth) => depth = Math.Max(0, depth - 1);

	internal static bool Enter(ref int depth)
	{
		depth++;
		return true;
	}

	/// <summary>Nur zurückzählen, wenn der eigene Prefix gezählt hat (scheitert vorher der Prefix einer anderen Mod, bleibt __state false).</summary>
	internal static void Exit(ref int depth, bool entered)
	{
		if (entered)
		{
			Leave(ref depth);
		}
	}

	/// <summary>Menge ohne rot fixierte Items, für eigene Menüs (Gebäude-Menü).</summary>
	internal static int CountUsable(Inventory inventory, string name)
	{
		GcHooks.RedCraft++;
		try
		{
			return inventory.CountItems(name);
		}
		finally
		{
			Leave(ref GcHooks.RedCraft);
		}
	}
}

/// <summary>Zählen, Suchen und Entnehmen ohne rote Items. Nachbau der Vanilla-Methoden aus Valheim 1.0.17, aktiv nur im <see cref="RedContext"/>.</summary>
[HarmonyPatch]
internal static class RedInventoryPatches
{
	[HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems), new[] { typeof(string), typeof(int), typeof(bool) })]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static bool CountItems(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
	{
		if (!RedContext.Applies(__instance))
		{
			return true;
		}
		int count = 0;
		foreach (ItemDrop.ItemData item in __instance.m_inventory)
		{
			if (!Gc.IsHardLocked(item) && (name == null || item.m_shared.m_name == name) && (quality < 0 || quality == item.m_quality) && (!matchWorldLevel || item.m_worldLevel >= Game.m_worldLevel))
			{
				count += item.m_stack;
			}
		}
		__result = count;
		return false;
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), new[] { typeof(string), typeof(bool) })]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static bool HaveItem(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
	{
		if (!RedContext.Applies(__instance))
		{
			return true;
		}
		__result = __instance.m_inventory.Exists(item => !Gc.IsHardLocked(item) && item.m_shared.m_name == name && (!matchWorldLevel || item.m_worldLevel >= Game.m_worldLevel));
		return false;
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItem), new[] { typeof(string), typeof(int), typeof(bool) })]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static bool GetItem(Inventory __instance, string name, int quality, bool isPrefabName, ref ItemDrop.ItemData? __result)
	{
		if (!RedContext.Applies(__instance))
		{
			return true;
		}
		__result = null;
		foreach (ItemDrop.ItemData item in __instance.m_inventory)
		{
			bool named = isPrefabName ? item.m_dropPrefab != null && item.m_dropPrefab.name == name : item.m_shared.m_name == name;
			if (named && !Gc.IsHardLocked(item) && (quality < 0 || quality == item.m_quality) && item.m_worldLevel >= Game.m_worldLevel)
			{
				__result = item;
				break;
			}
		}
		return false;
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static bool RemoveItem(Inventory __instance, string name, int amount, int itemQuality, bool worldLevelBased)
	{
		if (!RedContext.Applies(__instance))
		{
			return true;
		}
		foreach (ItemDrop.ItemData item in __instance.m_inventory)
		{
			if (Gc.IsHardLocked(item) || item.m_shared.m_name != name || (itemQuality >= 0 && item.m_quality != itemQuality) || (worldLevelBased && item.m_worldLevel < Game.m_worldLevel))
			{
				continue;
			}
			int take = Mathf.Min(item.m_stack, amount);
			item.m_stack -= take;
			amount -= take;
			if (amount <= 0)
			{
				break;
			}
		}
		__instance.m_inventory.RemoveAll(item => item.m_stack <= 0);
		__instance.Changed();
		return false;
	}
}

/// <summary>Wann rote Items als nicht vorhanden gelten: Craften, Verbessern, Bauen, Mengenanzeige, Vanilla-Feuer (V+ holt dort aus Kisten).</summary>
[HarmonyPatch]
internal static class RedContextPatches
{
	[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
	[HarmonyPrefix]
	private static void RecipePrefix(bool discover, out bool __state)
	{
		__state = !discover && RedContext.Enter(ref GcHooks.RedCraft); // „discover" prüft nur bekannte Materialien und zählt nichts
	}

	[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
	[HarmonyFinalizer]
	private static void RecipeFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);

	[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
	[HarmonyPrefix]
	private static void PiecePrefix(Player.RequirementMode mode, out bool __state)
	{
		__state = mode != Player.RequirementMode.IsKnown && RedContext.Enter(ref GcHooks.RedCraft);
	}

	[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
	[HarmonyFinalizer]
	private static void PieceFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);

	[HarmonyPatch(typeof(Player), nameof(Player.GetFirstRequiredItem))]
	[HarmonyPrefix]
	private static void FirstPrefix(out bool __state) => __state = RedContext.Enter(ref GcHooks.RedCraft);

	[HarmonyPatch(typeof(Player), nameof(Player.GetFirstRequiredItem))]
	[HarmonyFinalizer]
	private static void FirstFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);

	[HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
	[HarmonyPrefix]
	private static void ConsumePrefix(out bool __state) => __state = RedContext.Enter(ref GcHooks.RedCraft);

	[HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
	[HarmonyFinalizer]
	private static void ConsumeFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);

	/// <summary>Anzeige der Mengen (Werkbank, Bau-Menü), auch wenn ValheimPlus die Methode per Prefix ersetzt.</summary>
	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void RequirementPrefix(out bool __state) => __state = RedContext.Enter(ref GcHooks.RedCraft);

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
	[HarmonyFinalizer]
	private static void RequirementFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);

	/// <summary>„Eine Zutat reicht"-Rezepte (Vanilla und V+ aus Kisten).</summary>
	[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetAmount))]
	[HarmonyPrefix]
	private static void AmountPrefix(out bool __state) => __state = RedContext.Enter(ref GcHooks.RedCraft);

	[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetAmount))]
	[HarmonyFinalizer]
	private static void AmountFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);

	/// <summary>Feuer und Öfen von Hand befüllen: V+ greift dabei auf Kisten zu, wenn das Inventar keinen Brennstoff hat.</summary>
	[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
	[HarmonyPrefix]
	private static void FireplacePrefix(out bool __state) => __state = RedContext.Enter(ref RedContext.Chests);

	[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
	[HarmonyFinalizer]
	private static void FireplaceFinalizer(bool __state) => RedContext.Exit(ref RedContext.Chests, __state);

	/// <summary>
	/// Herstellen und Verbessern. Beim Verbessern legt Valheim ein neues Item an und überträgt m_customData nicht, das
	/// Schloss wird deshalb auf das neue Item kopiert: nur wenn das alte Item weg ist und auf seinem Platz ein Item mit
	/// demselben Namen liegt (sonst z. B. abgebrochen oder das Item wurde während des Craftens verschoben).
	/// </summary>
	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	private static void CraftPrefix(InventoryGui __instance, out UpgradeLock? __state)
	{
		GcHooks.RedCraft++; // läuft als erster Prefix, der Finalizer zählt deshalb immer zurück
		ItemDrop.ItemData? upgrade = __instance.m_craftUpgradeItem;
		int level = upgrade != null ? Plugin.Level(upgrade) : 0;
		__state = level > 0 ? new UpgradeLock(upgrade!, level) : null;
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	[HarmonyFinalizer]
	private static void CraftFinalizer(Player player, UpgradeLock? __state)
	{
		RedContext.Leave(ref GcHooks.RedCraft);
		if (__state == null || player == null)
		{
			return;
		}
		Inventory inventory = player.GetInventory();
		if (inventory.ContainsItem(__state.Item))
		{
			return; // nicht verbessert
		}
		ItemDrop.ItemData? upgraded = inventory.GetItemAt(__state.Position.x, __state.Position.y);
		if (upgraded != null && upgraded.m_shared.m_name == __state.Name && Plugin.Level(upgraded) == 0)
		{
			Plugin.SetLevel(upgraded, __state.Level);
		}
	}

	internal sealed class UpgradeLock
	{
		public UpgradeLock(ItemDrop.ItemData item, int level)
		{
			Item = item;
			Name = item.m_shared.m_name;
			Position = item.m_gridPos;
			Level = level;
		}

		public ItemDrop.ItemData Item { get; }

		public string Name { get; }

		public Vector2i Position { get; }

		public int Level { get; }
	}
}

/// <summary>Hover-Text der Kiste zeigt die Sperre.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
internal static class HoverPatch
{
	private static void Postfix(Container __instance, ref string __result)
	{
		if (!Plugin.ChestsOn || string.IsNullOrEmpty(__result) || !GcWard.IsPlayerChest(__instance))
		{
			return;
		}
		if (Gc.IsAutoLocked(__instance))
		{
			__result += "\n" + GcLoc.Get("gcl_hover_locked");
		}
		__result += "\n" + GcLoc.Get("gcl_hover_key", Plugin.ChestKey.Value.ToString());
	}
}

/// <summary>
/// Kistensperre (Alt+L) beim Besitzer der Kiste setzen, wie Rpcs.Assign im Gebäude-Menü (Review 27.10.2026). Nur ohne
/// Besitzer übernimmt der Spieler die Kiste selbst; dann gibt es keinen fremden Stand, der verloren ginge.
/// </summary>
[HarmonyPatch(typeof(Container), nameof(Container.Awake))]
internal static class ChestLockRpcPatch
{
	private const string Rpc = "GC_ChestNoAuto";

	private static void Postfix(Container __instance)
	{
		ZNetView nview = __instance.m_nview;
		if (nview == null || nview.GetZDO() == null || nview.m_functions.ContainsKey(Rpc.GetStableHashCode()))
		{
			return; // mehrere Behälter an einem ZNetView (m_rootObjectOverride): nur einmal registrieren, doppelt wirft
		}
		nview.Register<bool>(Rpc, (sender, locked) => Apply(nview, locked));
	}

	internal static void Send(ZNetView nview, bool locked)
	{
		if (!nview.GetZDO().HasOwner())
		{
			nview.ClaimOwnership();
		}
		nview.InvokeRPC(Rpc, locked); // an den Besitzer; sind wir das selbst, läuft es sofort
	}

	private static void Apply(ZNetView nview, bool locked)
	{
		if (nview != null && nview.IsValid() && nview.IsOwner())
		{
			nview.GetZDO().Set(GcKeys.ChestNoAuto, locked);
		}
	}
}

/// <summary>
/// ValheimPlus, per Reflection und nur wenn V+ geladen ist:
///  * gesperrte Kisten aus allen Kistenlisten entfernen (Maschinen-Automatik, Craft from Chest, Auto-Stack),
///  * rot fixierte Items in Kisten weder zählen noch entnehmen (Craft from Chest, Schmelze, Ofen, Feuer, Fermenter).
/// </summary>
internal static class VPlusFilter
{
	/// <summary>
	/// Review 27.10.2026: Jede Einhängung ist einzeln abgesichert (VppPatch) und bindet V+-Parameter über ihre Position.
	/// Fehlt eine oder passt sie nicht, wirkt nur diese Stelle nicht; Awake und GcMod.PatchAll laufen weiter.
	/// </summary>
	internal static void Apply(Harmony harmony)
	{
		if (!Gc.VPlusLoaded)
		{
			return;
		}
		int total = 0;
		int active = 0;
		void Hook(Type? type, string name, Type[]? args, string? prefix = null, string? postfix = null, string? finalizer = null, int priority = Priority.Normal)
		{
			total++;
			if (type == null)
			{
				return; // die fehlende V+-Klasse steht schon im Log
			}
			if (VppPatch.Try(harmony, VppPatch.Find(type, name, args), type.Name + "." + name, Handler(prefix, priority), Handler(postfix, priority), Handler(finalizer, priority)))
			{
				active++;
			}
		}
		try
		{
			Type? assistant = Class("ValheimPlus.InventoryAssistant");
			Hook(assistant, "GetNearbyChests", null, postfix: nameof(FilterChests));
			Hook(assistant, "GetNearbyChestsForMachine", null, postfix: nameof(FilterChests));
			Hook(assistant, "GetNearbyChestItemsByContainerList", null, postfix: nameof(FilterItems));
			Hook(assistant, "GetNearbyChestItems", null, postfix: nameof(FilterItems));
			// Nach dem Filter der Ein-/Ausgangskisten (StationStorage, normale Priorität): hat der die Entnahme abgelehnt, bleibt die Kiste unberührt
			Hook(assistant, "RemoveItemFromChest", new[] { typeof(Container), typeof(ItemDrop.ItemData), typeof(int) }, prefix: nameof(RemoveByItem), priority: Priority.Low);
			Hook(assistant, "RemoveItemFromChest", new[] { typeof(Container), typeof(string), typeof(int) }, prefix: nameof(RemoveByName), priority: Priority.Low);
			Hook(assistant, "ChestContainsItem", new[] { typeof(Container), typeof(ItemDrop.ItemData) }, postfix: nameof(ContainsByItem));
			Hook(assistant, "ChestContainsItem", new[] { typeof(Container), typeof(string) }, postfix: nameof(ContainsByName));
			// Kochstelle holt Rohes aus Kisten: V+ prüft mit HaveItem, entnimmt und legt ein neues Item auf, auch wenn nichts
			// entnommen wurde. Prefix und Finalizer in einem Schritt: gelänge nur der Prefix, bliebe der Zähler hängen.
			Hook(Class("ValheimPlus.GameClasses.CookingStation_FindCookableItem_Transpiler"), "PullCookableItemFromNearbyChests", null, prefix: nameof(ChestsEnter), finalizer: nameof(ChestsLeave));
			Hook(Class("ValheimPlus.GameClasses.Inventory_MoveAll_Patch"), "Prefix", null, prefix: nameof(MergePrefix));
			// Fermenter füllt aus Kisten nach: Vanilla FindCookableItem prüft mit HaveItem
			Hook(Class("ValheimPlus.GameClasses.Fermenter_SlowUpdate_Transpiler"), "AddItemFromNearbyChests", null, prefix: nameof(ChestsEnter), finalizer: nameof(ChestsLeave));
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"ValheimPlus: Einhängen abgebrochen: {e.Message}");
		}
		string text = $"ValheimPlus erkannt: Kistensperre und rotes Schloss, {active} von {total} V+-Einhängungen aktiv.";
		if (active == total)
		{
			Plugin.Log.LogInfo(text);
		}
		else
		{
			Plugin.Log.LogWarning(text + " An den fehlenden Stellen (Warnungen oben) wirken sie bei der V+-Automatik nicht.");
		}
	}

	private static Type? Class(string name)
	{
		Type? type = AccessTools.TypeByName(name);
		if (type == null)
		{
			Plugin.Log.LogWarning($"ValheimPlus: {name} nicht gefunden.");
		}
		return type;
	}

	private static HarmonyMethod? Handler(string? name, int priority) => name == null ? null : new HarmonyMethod(typeof(VPlusFilter), name) { priority = priority };

	/// <summary>
	/// Ersetzt V+ Inventory_MoveAll_Patch.Prefix (Stapel beim „Alles nehmen" zusammenführen), solange fixierte Stapel
	/// keine neuen Items aufnehmen sollen. Gleiche Logik wie V+, nur ohne gesperrte Zielstapel. Die Parameter von V+
	/// heißen __instance und fromInventory, deshalb hier per Position (__0, __1).
	/// </summary>
	private static bool MergePrefix(ref Inventory __0, ref Inventory __1)
	{
		if (!Plugin.AnyClosed || __0 == null || __1 == null || !VPlusMergeEnabled())
		{
			return true;
		}
		Inventory target = __0;
		Inventory from = __1;
		foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(from.GetAllItems()))
		{
			if (item.m_shared.m_maxStackSize <= 1)
			{
				continue;
			}
			foreach (ItemDrop.ItemData stack in target.m_inventory)
			{
				if (stack.m_shared.m_name != item.m_shared.m_name || stack.m_quality != item.m_quality || Plugin.Closed(stack))
				{
					continue;
				}
				int moved = Math.Min(stack.m_shared.m_maxStackSize - stack.m_stack, item.m_stack);
				stack.m_stack += moved;
				if (item.m_stack == moved)
				{
					from.RemoveItem(item);
					break;
				}
				item.m_stack -= moved;
			}
		}
		return false;
	}

	private static PropertyInfo? configCurrent;

	private static bool VPlusMergeEnabled()
	{
		try
		{
			configCurrent ??= AccessTools.Property(AccessTools.TypeByName("ValheimPlus.Configurations.Configuration"), "Current");
			object? current = configCurrent?.GetValue(null);
			object? inventory = current == null ? null : AccessTools.Property(current.GetType(), "Inventory")?.GetValue(current);
			if (inventory == null)
			{
				return false;
			}
			bool enabled = AccessTools.Property(inventory.GetType(), "IsEnabled")?.GetValue(inventory) is true;
			bool merge = AccessTools.Property(inventory.GetType(), "mergeWithExistingStacks")?.GetValue(inventory) is true;
			return enabled && merge;
		}
		catch
		{
			return false;
		}
	}

	private static void ChestsEnter(out bool __state) => __state = RedContext.Enter(ref RedContext.Chests);

	private static void ChestsLeave(bool __state) => RedContext.Exit(ref RedContext.Chests, __state);

	private static void FilterChests(ref List<Container> __result)
	{
		if (Plugin.ChestsOn && __result != null && __result.Count > 0)
		{
			__result.RemoveAll(Gc.IsAutoLocked);
		}
	}

	private static void FilterItems(ref List<ItemDrop.ItemData> __result)
	{
		if (Plugin.ItemsOn && __result != null && __result.Count > 0)
		{
			__result.RemoveAll(Gc.IsHardLocked);
		}
	}

	// V+ RemoveItemFromChest(Container chest, ItemData/string needle, int amount): Parameter per Position (Review 27.10.2026)
	private static bool RemoveByItem(Container __0, ItemDrop.ItemData __1, int __2, ref int __result, bool __runOriginal) => !__runOriginal ? false : Remove(__0, __1?.m_shared.m_name, __2, ref __result);

	private static bool RemoveByName(Container __0, string __1, int __2, ref int __result, bool __runOriginal) => !__runOriginal ? false : Remove(__0, __1, __2, ref __result);

	/// <summary>Wie V+ RemoveItemFromChest, aber rote Items bleiben. Nur wenn die Kiste wirklich ein rotes Item mit dem Namen hat, sonst läuft V+ selbst.</summary>
	private static bool Remove(Container chest, string? name, int amount, ref int __result)
	{
		Inventory? inventory = chest != null ? chest.GetInventory() : null;
		if (!Plugin.ItemsOn || inventory == null || name == null || !inventory.m_inventory.Exists(i => i.m_shared.m_name == name && Gc.IsHardLocked(i)))
		{
			return true;
		}
		int removed = 0;
		foreach (ItemDrop.ItemData item in inventory.m_inventory)
		{
			if (amount <= 0)
			{
				break;
			}
			if (item.m_shared.m_name != name || Gc.IsHardLocked(item))
			{
				continue;
			}
			int take = Mathf.Min(item.m_stack, amount);
			item.m_stack -= take;
			amount -= take;
			removed += take;
		}
		if (removed > 0)
		{
			inventory.m_inventory.RemoveAll(i => i.m_stack <= 0);
			chest!.Save();
			inventory.Changed();
		}
		__result = removed;
		return false;
	}

	// V+ ChestContainsItem(Container chest, ItemData/string needle): Parameter per Position
	private static void ContainsByItem(Container __0, ItemDrop.ItemData __1, ref bool __result)
	{
		if (__result && __1 != null)
		{
			__result = HasFree(__0, __1.m_shared.m_name);
		}
	}

	private static void ContainsByName(Container __0, string __1, ref bool __result)
	{
		if (__result)
		{
			__result = HasFree(__0, __1);
		}
	}

	private static bool HasFree(Container chest, string name)
	{
		return !Plugin.ItemsOn || chest == null || chest.GetInventory() == null || chest.GetInventory().m_inventory.Exists(i => i.m_shared.m_name == name && !Gc.IsHardLocked(i));
	}
}

/// <summary>
/// EpicLoot (Gorduan, 30.10.2026: „Schlösser schützen Items in allen EpicLoot-Fenstern“): Jedes Fenster des Verzaubertischs, das
/// ein Item verbraucht oder ihm etwas nimmt (Opfern, Entzaubern, Identifizieren, Rune herauslösen, Runen und Set-Runen als
/// Quelle), fragt EpicLoot.Compatibility.ItemFavorites.IsProtected (gedacht für die Mod Item Favorite Framework). Items mit
/// Schloss (weiß oder rot) gelten dort jetzt als geschützt und erscheinen nicht zur Auswahl. Verzaubern und Verbessern eines
/// Items mit Schloss bleibt erlaubt (es wird verändert, nicht verbraucht). Bezahlen wie beim Craften: Weiße Schlösser
/// dürfen Kosten tragen, rote nie (InventoryManagement.CountItem und RemoveItem im roten Kontext).
/// Ohne EpicLoot oder mit einer Fassung ohne diese Stellen geschieht nichts. Angemeldet in Start, wenn alle Mods geladen sind.
/// </summary>
internal static class EpicLootGuard
{
	private static bool patched;

	internal static void Patch(Harmony harmony)
	{
		if (patched)
		{
			return;
		}
		patched = true;
		try
		{
			Type? favorites = AccessTools.TypeByName("EpicLoot.Compatibility.ItemFavorites");
			MethodInfo? isProtected = favorites != null ? AccessTools.Method(favorites, "IsProtected", new[] { typeof(ItemDrop.ItemData) }) : null;
			if (isProtected == null)
			{
				return;
			}
			harmony.Patch(isProtected, postfix: new HarmonyMethod(typeof(EpicLootGuard), nameof(Protected)));
			Type? inventory = AccessTools.TypeByName("EpicLoot_UnityLib.InventoryManagement");
			int costs = 0;
			foreach (MethodInfo? method in new[]
			{
				inventory != null ? AccessTools.Method(inventory, "CountItem", new[] { typeof(string) }) : null,
				inventory != null ? AccessTools.Method(inventory, "RemoveItem", new[] { typeof(string), typeof(int) }) : null,
			})
			{
				if (method != null)
				{
					harmony.Patch(method, prefix: new HarmonyMethod(typeof(EpicLootGuard), nameof(CostPrefix)), finalizer: new HarmonyMethod(typeof(EpicLootGuard), nameof(CostFinalizer)));
					costs++;
				}
			}
			VppPlugin.Log.LogInfo($"Schlösser: Items mit Schloss sind in den EpicLoot-Fenstern geschützt{(costs == 2 ? ", rote bezahlen nie" : "")}.");
		}
		catch (Exception e)
		{
			VppPlugin.Log.LogWarning($"Schlösser: EpicLoot-Schutz nicht angemeldet: {e.Message}");
		}
	}

	private static void Protected(ItemDrop.ItemData item, ref bool __result)
	{
		if (!__result && Plugin.ItemsOn && Gc.IsLocked(item))
		{
			__result = true;
		}
	}

	private static void CostPrefix(out bool __state) => __state = Plugin.ItemsOn && RedContext.Enter(ref GcHooks.RedCraft);

	private static void CostFinalizer(bool __state) => RedContext.Exit(ref GcHooks.RedCraft, __state);
}

/// <summary>
/// EpicLoot 0.15.2 (Gorduan, 27.10.2026: „alle aktuellen Tools für das aktuelle EpicLoot umsetzen“): Items mit Schloss (weiß oder
/// rot) lassen sich am Verzaubertisch nicht opfern (API.RegisterSacrificeFilter). Ohne EpicLoot oder mit älterem EpicLoot
/// geschieht nichts. Angemeldet in Start, wenn alle Mods geladen sind (EpicLoot ist keine feste Abhängigkeit).
/// </summary>
internal static class SacrificeGuard
{
	private static bool registered;

	internal static void Register()
	{
		if (registered)
		{
			return;
		}
		registered = true;
		try
		{
			Type? api = AccessTools.TypeByName("EpicLoot.API");
			MethodInfo? add = api != null ? AccessTools.Method(api, "RegisterSacrificeFilter", new[] { typeof(string), typeof(Func<ItemDrop.ItemData, bool>) }) : null;
			if (add == null)
			{
				return;
			}
			Func<ItemDrop.ItemData, bool> allowed = item => !Plugin.ItemsOn || !Gc.IsLocked(item);
			if (add.Invoke(null, new object[] { "gorduan.gc.schloesser", allowed }) is true)
			{
				VppPlugin.Log.LogInfo("Schlösser: Items mit Schloss lassen sich am EpicLoot-Verzaubertisch nicht opfern.");
			}
		}
		catch (Exception e)
		{
			VppPlugin.Log.LogWarning($"Schlösser: EpicLoot-Opferschutz nicht angemeldet: {e.Message}");
		}
	}
}
