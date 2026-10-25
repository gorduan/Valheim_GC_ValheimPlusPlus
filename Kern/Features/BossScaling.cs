using BepInEx.Bootstrap;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Kern.Progression;

/// <summary>
/// Boss-Fortschritt (Gorduan, 20.10.2026), seit dem Kern für jede Auswahl an GC-Mods: Für jeden besiegten Hauptboss machen
/// alle Gegner und Bosse X % mehr Schaden und haben X % mehr Leben. Jeder Boss zählt einmal (nicht jeder Kill), die
/// Prozente werden addiert (3 Bosse × 10 % = +30 %).
///
/// Ziel (Gorduan): neben den Mods eine vergleichbare Schwierigkeit. Automatisch zählt deshalb der höchste Ausgleichswert der
/// laufenden Mods (GcAusgleich im Steckbrief, z. B. GC_Tiere 5 %), mit EpicLoot mindestens 10 %. Ohne erleichternde Mods
/// bleibt es bei 0 % wie in Valheim. Das entspricht der ersten Regel: Tiere 5 %, EpicLoot 10 %, auch mit Tieren.
/// Admins können einen eigenen Wert einstellen (Automatik aus).
///
/// Gorduan: „Für jeden Spieler zählt sein Fortschritt.“ Gegner treffen den Spieler um den Faktor härter, und er macht um
/// denselben Faktor weniger Schaden (wie mehr Leben). Gezählt wird über Valheims Schlüsselabfrage beim Spieler selbst:
/// mit World Advancement Progression (private Schlüssel) sein eigener Stand, mit Guilds_Progress samt geteiltem
/// Gilden-Fortschritt, ohne diese Mods die Welt. Zahme Tiere zählen mit dem Spieler, bei dem sie berechnet werden.
/// </summary>
internal static class BossScaling
{
	/// <summary>Die acht Hauptbosse (Schlüssel aus den Prefabs von Valheim 1.0.20; Tiefer Norden: erst die letzte Phase).</summary>
	internal static readonly string[] BossKeys =
	{
		"defeated_eikthyr", "defeated_gdking", "defeated_bonemass", "defeated_dragon",
		"defeated_goblinking", "defeated_queen", "defeated_fader", "defeated_frozenking_p3",
	};

	internal const string EpicLootGuid = "randyknapp.mods.epicloot";

	/// <summary>Ausgleich mit EpicLoot (Gorduan: 10 %).</summary>
	internal const float EpicLootPercent = 10f;

	internal static ConfigEntry<bool> Automatic = null!;
	internal static ConfigEntry<float> PercentPerBoss = null!;

	private static int count;
	private static float nextCount;
	private static Player? countedFor;
	private static float spawnedAt;
	private static bool announced;

	internal static void Init(ConfigFile config)
	{
		const string section = "13 - Boss Progression";
		Automatic = Gc.BindSynced(config, section, "Automatic", true, "Prozent je besiegtem Hauptboss automatisch: der höchste Ausgleichswert der laufenden GC-Mods (z. B. Tiere 5), mit EpicLoot mindestens 10, ohne erleichternde Mods 0 (wie Valheim). Aus: es gilt PercentPerBoss.");
		PercentPerBoss = Gc.BindSynced(config, section, "PercentPerBoss", 5f, "Gegner und Bosse haben je besiegtem Hauptboss (Eikthyr, Ältester, Bonemass, Moder, Yagluth, Königin, Fader, Kall Mandefimbul) so viel Prozent mehr Leben und machen so viel mehr Schaden, addiert. Gilt nur, wenn Automatic aus ist. Es zählt der eigene Fortschritt jedes Spielers (mit World Advancement Progression seine privaten Schlüssel, mit Guilds_Progress samt Gilden-Fortschritt, ohne diese Mods die Welt).", new AcceptableValueRange<float>(0f, 100f));
	}

	/// <summary>Höchster Ausgleich der laufenden Mods, mit EpicLoot mindestens 10; ohne Automatik der eigene Wert.</summary>
	internal static float Percent()
	{
		if (!Automatic.Value)
		{
			return Mathf.Max(0f, PercentPerBoss.Value);
		}
		float best = Chainloader.PluginInfos.ContainsKey(EpicLootGuid) ? EpicLootPercent : 0f;
		foreach (GcModule module in GcKern.Modules)
		{
			if (module.Info.Ausgleich > best && module.On)
			{
				best = module.Info.Ausgleich;
			}
		}
		return best;
	}

	/// <summary>Welche Mod den automatischen Wert bestimmt (für den Hinweis im Menü).</summary>
	internal static string Source()
	{
		if (!Automatic.Value)
		{
			return "";
		}
		float best = 0f;
		string source = "";
		if (Chainloader.PluginInfos.ContainsKey(EpicLootGuid))
		{
			best = EpicLootPercent;
			source = "EpicLoot";
		}
		foreach (GcModule module in GcKern.Modules)
		{
			if (module.Info.Ausgleich > best && module.On)
			{
				best = module.Info.Ausgleich;
				string key = "gck_mod_" + module.Info.Id;
				string title = GcLoc.Get(key);
				source = title == key ? module.Info.Name : title;
			}
		}
		return source;
	}

	internal static int Count => count;

	/// <summary>Stärke der Gegner für den eigenen Spieler: 1 + Prozent × Bosse (1 ohne eigenen Spieler, etwa auf dem Server).</summary>
	internal static float Factor => Player.m_localPlayer != null ? 1f + Percent() / 100f * count : 1f;

	/// <summary>Nur auf einem Server mit GC-Kern oder in der eigenen Welt; auf fremden Servern bleibt Valheims Stärke.</summary>
	internal static bool OnGcServer => ZNet.instance != null && (ZNet.instance.IsServer() || GcKern.Server != null);

	internal static bool Active => count > 0 && Player.m_localPlayer != null && OnGcServer && Percent() > 0f;

	/// <summary>Gegner: alles außer Spielern, zahmen Tieren (auch Beschwörungen und Lokis Trugbildern) und Trainingspuppen.</summary>
	internal static bool IsFoe(Character? character)
	{
		if (character == null || character.IsPlayer() || character.IsTamed())
		{
			return false;
		}
		Character.Faction faction = character.GetFaction();
		return faction != Character.Faction.Players && faction != Character.Faction.PlayerSpawned && faction != Character.Faction.TrainingDummy;
	}

	/// <summary>Auf der Seite des eigenen Spielers: er selbst oder ein zahmes Tier, das dieses Spiel berechnet.</summary>
	internal static bool Ours(Character? character)
	{
		if (character == null)
		{
			return false;
		}
		if (character == Player.m_localPlayer)
		{
			return true;
		}
		ZNetView? nview = character.m_nview;
		return !character.IsPlayer() && character.IsTamed() && nview != null && nview.IsValid() && nview.IsOwner();
	}

	/// <summary>Bosse zählen: Valheims Schlüsselabfrage (mit WAP die privaten Schlüssel des Spielers).</summary>
	private static int CountBosses()
	{
		ZoneSystem? zones = ZoneSystem.instance;
		if (zones == null)
		{
			return 0;
		}
		int n = 0;
		foreach (string key in BossKeys)
		{
			if (zones.GetGlobalKey(key))
			{
				n++;
			}
		}
		return n;
	}

	/// <summary>
	/// Jedes Bild (nur mit Spieler): alle 2 s neu zählen. Steigt die Zahl, kommt die Meldung (Gorduan). Nach dem Einloggen erst
	/// 15 s still mitzählen: WAP und Guilds_Progress tragen die Schlüssel dann noch nach.
	/// </summary>
	internal static void Tick()
	{
		Player? local = Player.m_localPlayer;
		if (local == null)
		{
			countedFor = null;
			count = 0;
			return;
		}
		float now = Time.time;
		if (local != countedFor)
		{
			countedFor = local;
			spawnedAt = now;
			announced = false;
			count = CountBosses();
			nextCount = now + 2f;
			return;
		}
		if (now < nextCount)
		{
			return;
		}
		nextCount = now + 2f;
		int fresh = CountBosses();
		bool settled = announced || now - spawnedAt > 15f;
		if (fresh > count && settled && OnGcServer)
		{
			float percent = Percent();
			if (percent > 0f)
			{
				string step = Mathf.RoundToInt(percent * (fresh - count)).ToString();
				string total = Mathf.RoundToInt(percent * fresh).ToString();
				MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, GcLoc.Get("gcbp_stronger", step, total));
			}
		}
		announced = settled;
		count = fresh;
	}

	/// <summary>Zusatz im Menü: was gerade gilt und welche Mod den Wert bestimmt.</summary>
	internal static string Note()
	{
		string percent = Mathf.RoundToInt(Percent()).ToString();
		string text = Player.m_localPlayer != null
			? GcLoc.Get("gcsv_bossscale_now", percent, count.ToString(), Mathf.RoundToInt(Percent() * count).ToString())
			: GcLoc.Get("gcsv_bossscale_rule", percent);
		string source = Source();
		return source.Length > 0 ? text + " " + GcLoc.Get("gck_bossscale_source", source) : text;
	}
}

/// <summary>Treffer des eigenen Spielers oder seiner Tiere an Gegnern (beim Angreifer): weniger Schaden, als hätten sie mehr Leben.</summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class BossScalingDealtPatch
{
	private static void Prefix(Character __instance, HitData hit)
	{
		if (hit == null || !BossScaling.Active || !BossScaling.IsFoe(__instance))
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker != null && attacker != __instance && BossScaling.Ours(attacker))
		{
			hit.ApplyModifier(1f / BossScaling.Factor);
		}
	}
}

/// <summary>Treffer von Gegnern am eigenen Spieler oder seinen Tieren (beim Getroffenen, der den Treffer abrechnet): mehr Schaden.</summary>
[HarmonyPatch(typeof(Character), "RPC_Damage")]
internal static class BossScalingTakenPatch
{
	private static void Prefix(Character __instance, HitData hit)
	{
		if (hit == null || !BossScaling.Active || !BossScaling.Ours(__instance))
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker != null && attacker != __instance && BossScaling.IsFoe(attacker))
		{
			hit.ApplyModifier(BossScaling.Factor);
		}
	}
}
