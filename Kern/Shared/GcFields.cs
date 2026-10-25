using System;
using System.Collections.Generic;

namespace GC.Shared;

/// <summary>
/// Felder für den Kultivator (Gorduan, 16.10.2026): gemeinsame Namen zwischen GC_Server und GC_Client. GC_Server legt das
/// unsichtbare Welt-Objekt „GC_Field“ an (Valheim speichert und verteilt es wie ein Bauteil) und gibt die Einstellungen
/// vor. GC_Client zeichnet, zeigt und bepflanzt die Felder. Keine Mod setzt eine andere voraus: Die Einstellungen liegen
/// als Dictionary aus BCL-Typen im AppDomain, die Felddaten in den ZDO-Schlüsseln unten.
/// </summary>
public static class GcFields
{
	/// <summary>Welt-Objekt eines Feldes (GC_Server), Position = Mittelpunkt der Fläche.</summary>
	public const string Prefab = "GC_Field";

	/// <summary>Eckpunkte: ZPackage mit int Anzahl, dann je Punkt ein Vector3 (Weltkoordinaten, Reihenfolge wie gezeichnet).</summary>
	public const string KeyPoints = "gc_field_pts";

	/// <summary>Spieler-ID (Player.GetPlayerID) und Name des Erstellers.</summary>
	public const string KeyCreator = "gc_field_creator";
	public const string KeyCreatorName = "gc_field_creator_name";

	/// <summary>Gilde des Erstellers beim Anlegen (ID aus der Gilden-Mod, −1 = keine oder ohne Gilden-Mod).</summary>
	public const string KeyGuild = "gc_field_guild";

	/// <summary>Einstellungen von GC_Server: Schlüssel → Func&lt;object&gt;.</summary>
	public const string SettingsSlot = "GC.Fields.Settings.v1";

	/// <summary>Wer ein Feld aufheben darf (Wert der Einstellung „removeBy“).</summary>
	public const int RemoveEveryone = 0;
	public const int RemoveCreatorOrAdmin = 1;
	public const int RemoveCreatorGuildOrAdmin = 2;
	public const int RemoveAdminOnly = 3;

	public static void Publish(Dictionary<string, Func<object>> settings)
	{
		AppDomain.CurrentDomain.SetData(SettingsSlot, settings);
	}

	/// <summary>Einstellung von GC_Server lesen; ohne GC_Server oder bei falschem Typ der Standardwert.</summary>
	public static T Get<T>(string key, T fallback)
	{
		try
		{
			if (AppDomain.CurrentDomain.GetData(SettingsSlot) is Dictionary<string, Func<object>> settings && settings.TryGetValue(key, out Func<object> getter))
			{
				object value = getter();
				return value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T));
			}
		}
		catch
		{
			// Standardwert
		}
		return fallback;
	}
}

/// <summary>
/// Eigene Gilde des Spielers über Mod-Grenzen (GC_Guilds meldet sie an, wenn die Gilden-Mod läuft). Ohne GC_Guilds gibt
/// es keine Gilde: Regeln mit Gildenleitung wirken dann wie „Ersteller und Admins“.
/// </summary>
public static class GcGuildInfo
{
	public const string Slot = "GC.Guilds.Self.v1";

	public static void Register(Func<int> guildId, Func<string> guildName, Func<bool> isLeader)
	{
		AppDomain.CurrentDomain.SetData(Slot, new Dictionary<string, object>
		{
			["guildId"] = guildId,
			["guildName"] = guildName,
			["isLeader"] = isLeader,
		});
	}

	/// <summary>ID der eigenen Gilde, −1 ohne Gilde oder ohne GC_Guilds.</summary>
	public static int OwnGuildId() => Call<int>("guildId", -1);

	public static string OwnGuildName() => Call("guildName", "");

	/// <summary>Leiter oder Co-Leiter der eigenen Gilde?</summary>
	public static bool IsLeader() => Call("isLeader", false);

	private static T Call<T>(string key, T fallback)
	{
		try
		{
			if (AppDomain.CurrentDomain.GetData(Slot) is Dictionary<string, object> info && info.TryGetValue(key, out object value) && value is Func<T> getter)
			{
				return getter();
			}
		}
		catch
		{
			// ohne Gilde
		}
		return fallback;
	}
}
