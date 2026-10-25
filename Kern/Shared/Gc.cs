using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace GC.Shared;

/// <summary>
/// Gemeinsame Schlüssel der GC-Mods. Die Mods kennen sich nicht gegenseitig (keine harten Abhängigkeiten),
/// lesen aber dieselben Markierungen, damit sie zusammenspielen.
/// </summary>
public static class GcKeys
{
	/// <summary>ItemData.m_customData: Item ist fixiert (GC_ItemLock). Sortieren, Stapeln, Vernichten und Automatik lassen es in Ruhe.</summary>
	public const string ItemLocked = "gc_locked";

	/// <summary>
	/// ItemData.m_customData: rotes Schloss, zusätzlich zu <see cref="ItemLocked"/>. Das Item wird auch nicht zum Craften
	/// oder Bauen verbraucht. Ältere GC-Mods sehen nur ItemLocked und behandeln es wie ein weißes Schloss.
	/// </summary>
	public const string ItemLockedHard = "gc_locked_red";

	/// <summary>ZDO-Bool an einer Kiste: Kiste ist für Automatik gesperrt (GC_ItemLock). V+-Maschinen, Craft-from-Chest und GC-Mods nutzen sie nicht.</summary>
	public const string ChestNoAuto = "gc_noauto";

	/// <summary>ZDO-String an einer Kiste: dauerhafte ID (GcIds.Key) der Hauptkiste, deren Inventar diese Kiste teilt (GC_LinkedChests).</summary>
	public const string LinkMaster = "gc_link_master_uid";

	/// <summary>ZDO-String am Portal: vorheriger Name (GC_Portalschutz), den GC_Portalnamen im Dialog anbietet.</summary>
	public const string PortalPrevious = "gc_portal_prev";

	/// <summary>ZDO-Long am Tier: Spieler-ID dessen, der es an der Leine hält (GC_Tiere). Andere Mods lesen es ohne GC_Tiere zu kennen.</summary>
	public const string LeashHolder = "gc_leash_holder";

	/// <summary>ZDO-Int am Tier: 1 = an einen Pfosten gebunden (GC_Tiere).</summary>
	public const string LeashTied = "gc_leash_tied";

	/// <summary>ZDO-Vec3 am Tier: Stelle des Pfostens, an den es gebunden ist (GC_Tiere).</summary>
	public const string LeashPost = "gc_leash_post";

	/// <summary>ZDO-Float am Tier: Höchstlänge der Leine, an der es hängt oder angebunden ist (GC_Tiere).</summary>
	public const string LeashMax = "gc_leash_max";

	/// <summary>ZDO-Float am Tier: Stufe aus Zucht und Erfahrung, die „(Lvl X)“ hinter dem Namen (GC_Tiere), 0 bei allen anderen.</summary>
	public const string CareLevel = "gc_care_level";

	/// <summary>
	/// ZDO-Float an einer Leiche (Ragdoll): ihre Größe, damit sie bei allen Spielern gleich groß liegt (Valheim gleicht das
	/// nicht ab). Der Kern setzt sie beim Entstehen der Leiche. Name aus dem Walküren-Transport (Wächter), auch für Bärenjunge.
	/// </summary>
	public const string RagdollScale = "gc_courier_guard_ragdoll";

	public static readonly int RagdollScaleHash = RagdollScale.GetStableHashCode();

	public const string VPlusGuid = "org.bepinex.plugins.valheim_plus";
	public const string GuildsVehicleGuid = "gorduan.guilds_vehicle";

	/// <summary>AppDomain-Platz: Func&lt;bool&gt; von ValheimPlusPlus, gehören alle Kisten der offenen verbundenen Ansicht noch uns (sonst schließt sie)?</summary>
	public const string LinkedViewHealthySlot = "GC.LinkedChests.Healthy.v1";
}

public static class Gc
{
	public static bool IsHeadless => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

	public static bool VPlusLoaded => Chainloader.PluginInfos.ContainsKey(GcKeys.VPlusGuid);

	public static bool IsLocked(ItemDrop.ItemData? item)
	{
		return item?.m_customData != null && item.m_customData.TryGetValue(GcKeys.ItemLocked, out string value) && value == "1";
	}

	/// <summary>Rotes Schloss: fixiert und für Craften und Bauen gesperrt.</summary>
	public static bool IsHardLocked(ItemDrop.ItemData? item)
	{
		return IsLocked(item) && item!.m_customData.TryGetValue(GcKeys.ItemLockedHard, out string value) && value == "1";
	}

	/// <summary>Verbundene Kisten (ValheimPlusPlus): Darf die offene Kiste jetzt verändert werden? Ohne ValheimPlusPlus immer ja.</summary>
	public static bool LinkedViewHealthy()
	{
		return AppDomain.CurrentDomain.GetData(GcKeys.LinkedViewHealthySlot) is not Func<bool> check || check();
	}

	public static bool IsAutoLocked(Container? container)
	{
		if (container == null)
		{
			return false;
		}
		ZNetView? nview = container.m_nview;
		ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
		return zdo != null && zdo.GetBool(GcKeys.ChestNoAuto);
	}

	/// <summary>Config-Eintrag, den Jötunn vom Server an alle Clients verteilt (nur Admins dürfen ihn ändern).</summary>
	public static ConfigEntry<T> BindSynced<T>(ConfigFile config, string section, string key, T value, string description, AcceptableValueBase? range = null)
	{
		return config.Bind(section, key, value, new ConfigDescription(description, range, new ConfigurationManagerAttributes { IsAdminOnly = true }));
	}

	public static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T value, string description, AcceptableValueBase? range = null)
	{
		return config.Bind(section, key, value, new ConfigDescription(description, range));
	}

	public static void Center(string text)
	{
		Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
	}

	public static void TopLeft(string text)
	{
		Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, text);
	}

	/// <summary>True, wenn gerade getippt wird oder ein Menü offen ist, das Tasten für sich braucht.</summary>
	public static bool TextInputActive()
	{
		if (global::Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || StoreGui.IsVisible())
		{
			return true;
		}
		if (Chat.instance != null && Chat.instance.HasFocus())
		{
			return true;
		}
		if (Minimap.InTextInput() || UnifiedPopup.IsVisible())
		{
			return true;
		}
		return false;
	}

	/// <summary>Taste gerade gedrückt, Modifier aus der Konfiguration müssen gehalten sein. Blockiert bei Texteingabe.</summary>
	public static bool Down(ConfigEntry<KeyboardShortcut> shortcut)
	{
		return !TextInputActive() && shortcut.Value.MainKey != KeyCode.None && shortcut.Value.IsDown();
	}

	public static bool Held(ConfigEntry<KeyboardShortcut> shortcut)
	{
		return !TextInputActive() && shortcut.Value.MainKey != KeyCode.None && shortcut.Value.IsPressed();
	}

	public static string KeyName(ConfigEntry<KeyboardShortcut> shortcut)
	{
		return shortcut.Value.ToString();
	}

	public static T? Hovered<T>() where T : Component
	{
		GameObject? hover = Player.m_localPlayer != null ? Player.m_localPlayer.GetHoverObject() : null;
		return hover != null ? hover.GetComponentInParent<T>() : null;
	}

	/// <summary>Einfärben wie die Bau-Markierung von Valheim (Farbe plus Leuchten). Muss jedes Frame erneuert werden oder mit <see cref="ResetHighlight"/> zurückgesetzt werden.</summary>
	public static void Highlight(GameObject? go, Color color)
	{
		if (go == null || MaterialMan.instance == null)
		{
			return;
		}
		MaterialMan.instance.SetValue(go, ShaderProps._EmissionColor, color * 0.4f);
		MaterialMan.instance.SetValue(go, ShaderProps._Color, color);
	}

	public static void ResetHighlight(GameObject? go)
	{
		if (go == null || MaterialMan.instance == null)
		{
			return;
		}
		MaterialMan.instance.ResetValue(go, ShaderProps._Color);
		MaterialMan.instance.ResetValue(go, ShaderProps._EmissionColor);
	}

	/// <summary>ZDO-Besitz übernehmen, damit lokale Änderungen gespeichert und verteilt werden.</summary>
	public static bool Own(ZNetView? nview)
	{
		if (nview == null || !nview.IsValid())
		{
			return false;
		}
		if (!nview.IsOwner())
		{
			nview.ClaimOwnership();
		}
		return nview.IsOwner();
	}

	public static string PrefabName(GameObject go) => Utils.GetPrefabName(go);

	public static HashSet<string> ParseList(string csv)
	{
		HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
		foreach (string part in csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string trimmed = part.Trim();
			if (trimmed.Length > 0)
			{
				set.Add(trimmed);
			}
		}
		return set;
	}

	public static string ZdoidToString(ZDOID id) => id.IsNone() ? "" : $"{id.UserID}:{id.ID}";

	public static ZDOID ZdoidFromString(string text)
	{
		string[] parts = text.Split(':');
		if (parts.Length == 2 && long.TryParse(parts[0], out long user) && uint.TryParse(parts[1], out uint id))
		{
			return new ZDOID(user, id);
		}
		return ZDOID.None;
	}

	public static T? FindInstance<T>(ZDOID id) where T : Component
	{
		if (id.IsNone() || ZNetScene.instance == null)
		{
			return null;
		}
		GameObject? go = ZNetScene.instance.FindInstance(id);
		return go != null ? go.GetComponentInChildren<T>() : null;
	}
}
