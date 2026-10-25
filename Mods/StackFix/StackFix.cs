using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.ValheimPlusPlus;
using HarmonyLib;

namespace GC.ValheimPlusPlus.StackFix;

/// <summary>
/// GC_StackFix: V+ Auto-Stack lagert Rüstung, Schilde und Schmuck (auch EpicLoot) und EpicLoot-Materialien wieder ein.
///
/// Ursache (V+ Grantapher 0.10.2, Inventory_StackAll_Patch.ContainsItemByName): Bei
/// [AutoStack] autoStackAllIgnoreEquipment = true überspringt V+ jedes ausrüstbare Item. EpicLoot-Items sind
/// fast immer Ausrüstung, deshalb landen sie nie in der Kiste, auch wenn dort dasselbe Item liegt.
/// Vanilla „Stapel ablegen" hat diese Sperre nicht, V+ hat sie eingeführt, damit die eigene Ausrüstung nicht
/// in Kisten verschwindet.
///
/// Diese Mod erlaubt seit ValheimPlusPlus 1.2.0 nur Ausrüstung, die keine Waffe und kein Werkzeug ist (Rüstung,
/// Schilde, Gürtel, Ringe und andere Schmuckstücke). Waffen, Fackeln und Werkzeuge folgen weiter der V+-Einstellung.
///
/// EpicLoot-Materialien (Staub, Essenzen, Reagenzien, Runensteine, Splittersteine, Meißel): EpicLoot kennzeichnet sie
/// über das Feld m_ammoType („…|MagicCraftingMaterial", „…|Runestone", „…|ShardStone", EpicLoot
/// CraftingItemExtensions). V+ hält jedes Item mit m_ammoType für Munition (ItemDataExtensions.IsAmmo) und lässt sie
/// bei [AutoStack] ignoreAmmo = true im Inventar, in der offenen Kiste wie in den Kisten ringsum (Gorduan, Spieltest
/// 13.10.2026: „Keine Gegenstände zum Stapeln"). Diese Mod lässt sie wieder einlagern; echte Munition (Typ Ammo,
/// AmmoNonEquipable) folgt weiter der V+-Einstellung.
/// Geschützt bleiben:
///  * angelegte Items (wie Vanilla),
///  * die Schnellleiste (oberste Inventarreihe, einstellbar),
///  * fixierte Items (GC_ItemLock).
/// Munition, Essen und Met behalten die V+-Einstellungen.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_stackfix";
	public const string ModName = "GC_StackFix";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<bool> ProtectHotbar = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && VppPlugin.Allowed;
	internal static bool HotbarOn => ProtectHotbar.Value && !Blocked && VppPlugin.Allowed;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = VppPlugin.Log;
		Enabled = Gc.Bind(Config, "1 - Auto-Stack Fix", "Enabled", true, "V+ Auto-Stack lagert Rüstung, Schilde und Schmuck (auch EpicLoot) und EpicLoot-Materialien wieder ein, wenn die Kiste dasselbe Item enthält. Waffen, Werkzeuge und echte Munition folgen der V+-Einstellung.");
		ProtectHotbar = Gc.Bind(Config, "1 - Auto-Stack Fix", "ProtectHotbar", true, "Items in der Schnellleiste (oberste Inventarreihe) nie in Kisten stapeln.");

		if (Gc.VPlusLoaded)
		{
			Type? patch = AccessTools.TypeByName("ValheimPlus.GameClasses.Inventory_StackAll_Patch");
			MethodInfo? method = patch == null ? null : AccessTools.Method(patch, "ContainsItemByName", new[] { typeof(Inventory), typeof(string) });
			if (method != null)
			{
				harmony.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), nameof(ContainsItemByNamePostfix)));
				Log.LogInfo("ValheimPlus erkannt: Auto-Stack lagert Rüstung, Schilde, Schmuck und EpicLoot-Materialien wieder ein (Waffen, Werkzeuge und Munition nach V+-Einstellung).");
			}
			else
			{
				Log.LogWarning("ValheimPlus gefunden, aber Inventory_StackAll_Patch.ContainsItemByName nicht. Ausrüstungs-Fix inaktiv.");
			}
		}
	}

	private static void ContainsItemByNamePostfix(Inventory inventory, string name, ref bool __result)
	{
		if (__result || !On)
		{
			return;
		}
		foreach (ItemDrop.ItemData item in inventory.m_inventory)
		{
			if (item.m_shared.m_name != name)
			{
				continue;
			}
			if (TaggedMaterial(item) || (item.IsEquipable() && !IsAmmo(item) && !item.IsWeapon() && item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Tool))
			{
				__result = true;
				return;
			}
		}
	}

	/// <summary>
	/// Hat ein m_ammoType (für V+ also „Munition"), ist aber keine: weder Munition noch Ausrüstung, Essen oder Getränk.
	/// So kennzeichnet EpicLoot seine Materialien; andere Mods, die das Feld genauso nutzen, gehören dazu.
	/// </summary>
	private static bool TaggedMaterial(ItemDrop.ItemData item)
	{
		ItemDrop.ItemData.SharedData shared = item.m_shared;
		return !string.IsNullOrEmpty(shared.m_ammoType) && !IsAmmo(item) && !item.IsEquipable() && !shared.m_isDrink
			&& !(shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable && (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f));
	}

	private static bool IsAmmo(ItemDrop.ItemData item)
	{
		return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable;
	}
}

/// <summary>Schnellleiste und fixierte Items beim Stapeln aus dem Spielerinventar heraushalten.</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
internal static class StackAllPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(Inventory fromInventory, out List<ItemDrop.ItemData>? __state)
	{
		__state = null;
		if (!Plugin.HotbarOn || Player.m_localPlayer == null || fromInventory != Player.m_localPlayer.GetInventory())
		{
			return;
		}
		bool Protect(ItemDrop.ItemData item) => item.m_gridPos.y == 0; // fixierte Items schützt die Funktion ItemLock
		__state = fromInventory.m_inventory.FindAll(Protect);
		if (__state.Count == 0)
		{
			__state = null;
			return;
		}
		fromInventory.m_inventory.RemoveAll(Protect);
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
