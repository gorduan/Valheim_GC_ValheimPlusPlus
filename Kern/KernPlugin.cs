using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace GC.Shared;

/// <summary>
/// GC_Kern (Gorduan): Laufzeit des GC-SDK. Seit 20.10.2026 ist jede GC-Funktion eine eigene Mod mit eigenem Ordner;
/// alle brauchen diesen Kern. Er bringt:
///  * das Menü „GC Option“ (Knopf im Pause-Menü) mit einem Abschnitt je Stufe, von 1 Sehr Vanilla bis 5 GC;
///    Spieler ohne Rechte sehen nur ihre eigenen Optionen;
///  * Texte (alle GC-Texte, Deutsch und Englisch) und die Übernahme der Einstellungen aus den alten Sammelpaketen;
///  * die Stufe des Servers: Spieler-Mods über dieser Stufe schalten sich auf dem Server ab (Gorduan);
///  * den Mod-Abgleich mit dem Server (früher GC_ModCheck) und den Schutz der Mod-Texte beim Sprachwechsel.
/// Laufen die alten Sammelpakete GC_Server, GC_Client oder ValheimPlusPlus noch, startet der Kern nicht (und damit
/// keine der neuen Mods), damit nichts doppelt wirkt.
/// </summary>
[BepInPlugin(GcKern.Guid, GcKern.Name, GcKern.Version)]
[BepInDependency(Jotunn.Main.ModGuid)]
[BepInIncompatibility("gorduan.gc_server")]
[BepInIncompatibility("gorduan.gc_client")]
[BepInIncompatibility("gorduan.valheimplusplus")]
[NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
public class KernPlugin : BaseUnityPlugin
{
	internal static ManualLogSource Log = null!;
	internal static GcModule Module = null!;
	internal static GcMenu Menu = null!;
	/// <summary>Taste für GC Option (aus Mod Menu übernommen), Standard: keine.</summary>
	internal static ConfigEntry<KeyboardShortcut>? MenuKey;

	private void Awake()
	{
		Log = Logger;
		Module = GcKern.Register(this, Logger, new GcModInfo
		{
			Id = "kern",
			Guid = GcKern.Guid,
			Name = GcKern.Name,
			Version = GcKern.Version,
			Stufe = 0,
			Seite = GcSeite.Spieler,
			AlteConfig = "gorduan.gc_server.cfg", // Mod-Abgleich stand früher in GC_Server
		});
		GcLoc.Init(GcKern.Name, Logger);

		GcKern.StufeSetting = Gc.BindSynced(Config, "1 - Server", "SpielerStufe", 0,
			"Bis zu welcher Stufe Mods, die nur beim Spieler laufen, auf diesem Server wirken dürfen (1 Sehr Vanilla, 2 Vanilla, " +
			"3 Vanilla+, 4 GC leicht, 5 GC). 0 = automatisch: die höchste Stufe der GC-Mods auf dem Server.",
			new AcceptableValueRange<int>(0, 5));
		Kern.ModCheck.Plugin.Init(Config);
		Kern.Progression.BossScaling.Init(Config);
		MenuKey = Config.Bind("2 - Menue", "Taste", KeyboardShortcut.Empty,
			"Taste für das Menü GC Option, im Hauptmenü und im Spiel (nicht im Chat oder in der Konsole). Leer = nur über den Knopf.");
		Module.TakeOverOldSettings();

		Harmony harmony = new(GcKern.Guid);
		harmony.PatchAll(typeof(KernPlugin).Assembly);

		Menu = new GcMenu(Log, GcMenu.UiText(""));
		Module.MenuGroup(KernText)
			.Main("stufe", GcKern.StufeSetting, step: 1, note: StufeNote)
			.Main("bossscale", Kern.Progression.BossScaling.Automatic, note: Kern.Progression.BossScaling.Note)
			.Sub("bossscale_percent", Kern.Progression.BossScaling.PercentPerBoss, 1, " %")
			.Main("modcheck", Kern.ModCheck.Plugin.Enabled, () => Kern.ModCheck.Plugin.Blocked, "GC_ModCheck")
			.Sub("modcheck_failure", Kern.ModCheck.Plugin.ShowOnFailure)
			.Sub("modcheck_join", Kern.ModCheck.Plugin.ShowOnJoinIfMissing)
			.Main("menukey", MenuKey);
		Log.LogInfo($"{GcKern.Name} {GcKern.Version} geladen.");
	}

	/// <summary>Texte der Kern-Gruppe: eigene (gck_) vor denen des alten GC_Server (gcsv_, Mod-Abgleich).</summary>
	private static string KernText(string key)
	{
		string own = GcLoc.Get("gck_" + key);
		return own != "gck_" + key ? own : GcLoc.Get("gcsv_" + key);
	}

	private static string StufeNote()
	{
		ZNet? net = ZNet.instance;
		int effective;
		if (net != null && !net.IsServer())
		{
			effective = GcKern.ServerStufe; // verbunden: was der Server vorgibt (6 = keine Grenze, Server ohne Kern)
		}
		else
		{
			int stufe = GcKern.StufeSetting?.Value ?? 0;
			effective = stufe > 0 ? stufe : Math.Max(1, GcKern.HighestLocalStufe);
		}
		return effective >= 6 ? GcLoc.Get("gck_stufe_now_free") : GcLoc.Get("gck_stufe_now", effective, GcKern.StufeName(effective));
	}

	private void Start()
	{
		Menu.LogBlocked();
		if (!Gc.IsHeadless)
		{
			Module.Guard("ValheimPlus im Menü", () => Kern.VPlusOptions.Register(Menu)); // in Start: alle Mods sind dann geladen
		}
		foreach (GcModule module in GcKern.Modules)
		{
			foreach (string id in module.Info.Partner)
			{
				if (!GcKern.ModLoaded(id))
				{
					Log.LogInfo($"{module.Info.Name}: Partner {GcKern.GuidPrefix}{id} fehlt, die Mod läuft ohne dessen Teil.");
				}
			}
		}
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Menü-Taste", () => Menu.CheckHotkey());
		Module.Guard("Menü", () => Menu.Update());
		Module.Guard("Boss-Fortschritt", Kern.Progression.BossScaling.Tick);
	}
}
