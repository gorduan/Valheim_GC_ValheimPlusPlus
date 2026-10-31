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
/// EpicLoot-Materialien (Staub, Essenzen, Reagenzien, Runensteine, Splittersteine, Unidentifiziertes, Meißel): EpicLoot
/// kennzeichnet sie über das Feld m_ammoType („…|MagicCraftingMaterial", „…|Runestone", „…|ShardStone",
/// „…|Unidentified", „…|ShardSlotChisel", EpicLoot CraftingItemExtensions und Shards.IsShard). V+ hält jedes Item mit
/// m_ammoType für Munition (ItemDataExtensions.IsAmmo) und lässt sie bei [AutoStack] ignoreAmmo = true im Inventar, in
/// der offenen Kiste wie in den Kisten ringsum (Gorduan, Spieltest 13.10.2026: „Keine Gegenstände zum Stapeln"). Diese Mod
/// lässt sie wieder einlagern; echte Munition (Typ Ammo, AmmoNonEquipable) und Items anderer Mods mit m_ammoType folgen
/// weiter der V+-Einstellung.
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
			// Review 27.10.2026: abgesichert und über Positionsparameter, eine geänderte V+-Signatur schaltet nur diesen Fix ab
			Type? patch = AccessTools.TypeByName("ValheimPlus.GameClasses.Inventory_StackAll_Patch");
			MethodInfo? method = VppPatch.Find(patch, "ContainsItemByName", new[] { typeof(Inventory), typeof(string) });
			if (VppPatch.Try(harmony, method, "Inventory_StackAll_Patch.ContainsItemByName", postfix: new HarmonyMethod(typeof(Plugin), nameof(ContainsItemByNamePostfix))))
			{
				Log.LogInfo("ValheimPlus erkannt: 1 von 1 V+-Einhängungen aktiv. Auto-Stack lagert Rüstung, Schilde, Schmuck und EpicLoot-Materialien wieder ein (Waffen, Werkzeuge und Munition nach V+-Einstellung).");
			}
			else
			{
				Log.LogWarning("ValheimPlus: 0 von 1 V+-Einhängungen aktiv (Inventory_StackAll_Patch.ContainsItemByName). Ausrüstungs-Fix inaktiv.");
			}
		}
	}

	/// <summary>V+ Inventory_StackAll_Patch.ContainsItemByName(Inventory inventory, string name), Parameter per Position.</summary>
	private static void ContainsItemByNamePostfix(Inventory __0, string __1, ref bool __result)
	{
		if (__result || !On || __0 == null)
		{
			return;
		}
		foreach (ItemDrop.ItemData item in __0.m_inventory)
		{
			if (item.m_shared.m_name != __1)
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

	/// <summary>Endungen von m_ammoType, mit denen EpicLoot 0.15.2 seine Materialien (Typ Material) kennzeichnet.</summary>
	private static readonly string[] EpicLootMaterialTags = { "MagicCraftingMaterial", "Runestone", "ShardStone", "ShardSlotChisel", "Unidentified" };

	/// <summary>
	/// EpicLoot-Material, genau wie EpicLoot 0.15.2 es erkennt (Review 27.10.2026: vorher zählte jedes nicht ausrüstbare
	/// Item mit m_ammoType): Typ Material mit m_ammoType auf „MagicCraftingMaterial" (Staub, Essenzen, Reagenzien, alte
	/// Splitter), „Runestone" (auch geätzte Runensteine), „ShardStone", „ShardSlotChisel" oder „Unidentified"
	/// (CraftingItemExtensions.IsMagicCraftingMaterial/IsRunestone/IsUnidentifiedMaterial, Shards.IsShard). Den Meißel
	/// erkennt EpicLoot (IsShardSlotChisel) ohne Typprüfung; welchen Typ er im Asset-Bundle hat, steht nicht im Code. Damit er
	/// sicher dabei bleibt, gilt er wie bei EpicLoot auch ohne Typ Material, aber nie als Munition oder Ausrüstung.
	/// </summary>
	private static bool TaggedMaterial(ItemDrop.ItemData item)
	{
		string? tag = item.m_shared.m_ammoType;
		if (string.IsNullOrEmpty(tag))
		{
			return false;
		}
		if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Material)
		{
			foreach (string suffix in EpicLootMaterialTags)
			{
				if (tag!.EndsWith(suffix, StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}
		return tag!.EndsWith("|ShardSlotChisel", StringComparison.Ordinal) && !IsAmmo(item) && !item.IsEquipable();
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
