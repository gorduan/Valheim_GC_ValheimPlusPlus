using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Kern.ModCheck;

/// <summary>
/// GC_ModCheck: Vergleicht beim Verbinden alle BepInEx-Mods mit dem Server.
///
/// Jötunn prüft nur Mods mit NetworkCompatibility-Attribut und hat keinen Kopieren-Knopf. Diese Mod schickt
/// gleich beim Verbindungsaufbau (ZNet.OnNewConnection, noch vor jeder Versionsprüfung) die komplette
/// Plugin-Liste des Servers an den Client, inklusive r2modman-Paketordner (Autor-Paket). Scheitert die
/// Verbindung, zeigt das Hauptmenü ein Fenster mit fehlenden Mods, abweichenden Versionen und Mods, die nur
/// beim Spieler laufen. „In Zwischenablage kopieren" legt die Liste als Text ab, z. B. für Discord.
/// Im Spiel öffnet der Konsolenbefehl modcheck dasselbe Fenster.
///
/// Seit dem Kern (20.10.2026) geht die Liste immer an die Spieler, auch wenn der Mod-Abgleich aus ist: Sie sagt dem
/// Kern, welche GC-Mods der Server hat und welche Stufe er vorgibt (GcKern.Server). Das Fenster gibt es nur, wenn
/// der Mod-Abgleich eingeschaltet ist.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_modcheck";
	public const string ModName = "GC_ModCheck";
	public const string Version = "1.0.0";
	internal const string Rpc = "GC_Kern_ServerInfo_v1";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked;
	internal static ConfigEntry<bool> ShowOnFailure = null!;
	internal static ConfigEntry<bool> ShowOnJoinIfMissing = null!;
	internal static ConfigEntry<string> HiddenOnServer = null!;
	internal static ConfigEntry<string> IgnoreClientOnly = null!;

	internal static List<ModInfo>? ServerMods;
	internal static Report? LastReport;

	internal static void Init(ConfigFile Config)
	{
		Log = KernPlugin.Log;
		Enabled = Gc.Bind(Config, "5 - ModCheck", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		ShowOnFailure = Gc.Bind(Config, "5 - ModCheck", "ShowOnFailedConnection", true, "Nach einer fehlgeschlagenen Verbindung das Vergleichsfenster im Hauptmenü zeigen.");
		ShowOnJoinIfMissing = Gc.Bind(Config, "5 - ModCheck", "ShowOnJoinIfMissing", true, "Nach erfolgreichem Beitritt das Fenster zeigen, wenn dir Mods des Servers fehlen.");
		HiddenOnServer = Gc.Bind(Config, "5 - ModCheck - Server", "HiddenGuids", "", "Nur Server: kommagetrennte Plugin-GUIDs, die nicht an Spieler gemeldet werden (reine Server-Mods).");
		IgnoreClientOnly = Gc.Bind(Config, "5 - ModCheck - Client", "ClientOnlyGuids", "", "Kommagetrennte GUIDs eigener Client-Mods, die nicht als 'nur bei dir' gelistet werden.");
		CommandManager.Instance.AddConsoleCommand(new ConsoleCommand());
	}

	internal static List<ModInfo> LocalMods()
	{
		List<ModInfo> list = new();
		foreach (PluginInfo info in Chainloader.PluginInfos.Values)
		{
			string package = "";
			try
			{
				string? folder = Path.GetDirectoryName(info.Location);
				string? name = folder != null ? Path.GetFileName(folder) : null;
				if (name != null && !name.Equals("plugins", StringComparison.OrdinalIgnoreCase))
				{
					package = name;
				}
			}
			catch
			{
				// Ort unbekannt, dann ohne Paketnamen
			}
			list.Add(new ModInfo(info.Metadata.GUID, info.Metadata.Name, info.Metadata.Version.ToString(), package));
		}
		return list.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	internal static ZPackage BuildPackage()
	{
		// Mod-Abgleich aus: nur die GC-Mods (der Kern braucht sie für Stufe und Server-Teile), keine fremden Namen.
		// HiddenGuids versteckt nie GC-Mods, sonst hielten die Spieler sie für fehlend.
		HashSet<string> hidden = Gc.ParseList(HiddenOnServer.Value);
		List<ModInfo> mods = LocalMods().Where(mod => IsGc(mod.Guid) || (On && !hidden.Contains(mod.Guid))).ToList();
		ZPackage pkg = new();
		pkg.Write(mods.Count);
		foreach (ModInfo mod in mods)
		{
			pkg.Write(mod.Guid);
			pkg.Write(mod.Name);
			pkg.Write(mod.Version);
			pkg.Write(mod.Package);
		}
		pkg.Write(Math.Max(1, GcKern.HighestLocalStufe)); // automatische Stufe des Servers, mindestens 1
		pkg.Write(On); // volle Liste (Mod-Abgleich an) oder nur GC-Mods
		pkg.Write(GcKern.Modules.Count);
		foreach (GcModule module in GcKern.Modules)
		{
			pkg.Write(module.Info.Guid);
			pkg.Write((int)module.Info.Seite);
		}
		return pkg;
	}

	internal static bool IsGc(string guid) => guid == GcKern.Guid || guid.StartsWith(GcKern.GuidPrefix, StringComparison.Ordinal);

	/// <summary>GC-Mods, die nur beim Spieler laufen: freiwillig, fehlen also nicht (Meldung des Servers).</summary>
	internal static HashSet<string> OptionalOnServer = new(StringComparer.Ordinal);

	internal static void Receive(ZPackage pkg)
	{
		List<ModInfo> list = new();
		int count = pkg.ReadInt();
		for (int i = 0; i < count; i++)
		{
			list.Add(new ModInfo(pkg.ReadString(), pkg.ReadString(), pkg.ReadString(), pkg.ReadString()));
		}
		int stufe = 5;
		bool full = true;
		HashSet<string> optional = new(StringComparer.Ordinal);
		try
		{
			stufe = pkg.ReadInt();
			full = pkg.ReadBool();
			int sides = pkg.ReadInt();
			for (int i = 0; i < sides; i++)
			{
				string guid = pkg.ReadString();
				if (pkg.ReadInt() == (int)GcSeite.Spieler)
				{
					optional.Add(guid);
				}
			}
		}
		catch
		{
			// kürzere Meldung: ohne diese Angaben weiter
		}
		OptionalOnServer = optional;
		GcServerInfo info = new() { Stufe = stufe };
		foreach (ModInfo mod in list)
		{
			info.Guids.Add(mod.Guid);
		}
		GcKern.Server = info;
		Log.LogInfo($"Server: {list.Count} Mods, Stufe {stufe} ({GcKern.StufeName(stufe)}).");
		ServerMods = list;
		if (!On || !full)
		{
			return; // Mod-Abgleich hier oder auf dem Server aus: kein Bericht
		}
		HashSet<string> ignore = Gc.ParseList(IgnoreClientOnly.Value);
		LastReport = Report.Create(list, LocalMods(), ignore, optional);
		Log.LogInfo($"Server meldet {list.Count} Mods. Fehlend: {LastReport.Missing.Count}, andere Version: {LastReport.Different.Count}, nur hier: {LastReport.ClientOnly.Count}.");
		foreach (string line in LastReport.ToText().Split('\n'))
		{
			if (line.Trim().Length > 0)
			{
				Log.LogInfo(line);
			}
		}
	}
}

internal sealed class ModInfo
{
	public ModInfo(string guid, string name, string version, string package)
	{
		Guid = guid;
		Name = name;
		Version = version;
		Package = package;
	}

	public string Guid { get; }
	public string Name { get; }
	public string Version { get; }
	public string Package { get; }

	public string Describe() => Package.Length > 0 ? $"{Name} {Version}  [{Package}]" : $"{Name} {Version}  ({Guid})";
}

internal sealed class Report
{
	public List<ModInfo> Missing = new();
	public List<(ModInfo Server, ModInfo Client)> Different = new();
	public List<ModInfo> ClientOnly = new();

	public bool HasProblems => Missing.Count > 0 || Different.Count > 0;

	public static Report Create(List<ModInfo> server, List<ModInfo> client, HashSet<string> ignoreClientOnly, HashSet<string> optional)
	{
		Report report = new();
		Dictionary<string, ModInfo> clientByGuid = client.GroupBy(mod => mod.Guid).ToDictionary(group => group.Key, group => group.First());
		Dictionary<string, ModInfo> serverByGuid = server.GroupBy(mod => mod.Guid).ToDictionary(group => group.Key, group => group.First());
		foreach (ModInfo mod in server)
		{
			if (optional.Contains(mod.Guid))
			{
				continue; // GC-Mod nur für Spieler: freiwillig
			}
			if (!clientByGuid.TryGetValue(mod.Guid, out ModInfo mine))
			{
				report.Missing.Add(mod);
			}
			else if (mine.Version != mod.Version)
			{
				report.Different.Add((mod, mine));
			}
		}
		foreach (ModInfo mod in client)
		{
			if (!serverByGuid.ContainsKey(mod.Guid) && !ignoreClientOnly.Contains(mod.Guid))
			{
				report.ClientOnly.Add(mod);
			}
		}
		return report;
	}

	public string ToText()
	{
		StringBuilder sb = new();
		sb.AppendLine(GcLoc.Get("gcm_title"));
		sb.AppendLine();
		sb.AppendLine(GcLoc.Get("gcm_missing", Missing.Count));
		foreach (ModInfo mod in Missing)
		{
			sb.AppendLine("- " + mod.Describe());
		}
		sb.AppendLine();
		sb.AppendLine(GcLoc.Get("gcm_different", Different.Count));
		foreach ((ModInfo server, ModInfo client) in Different)
		{
			sb.AppendLine("- " + GcLoc.Get("gcm_version_line", server.Name, server.Version, client.Version) + (server.Package.Length > 0 ? $"  [{server.Package}]" : ""));
		}
		sb.AppendLine();
		sb.AppendLine(GcLoc.Get("gcm_client_only", ClientOnly.Count));
		foreach (ModInfo mod in ClientOnly)
		{
			sb.AppendLine("- " + mod.Describe());
		}
		return sb.ToString();
	}
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
internal static class NewConnectionPatch
{
	private static void Postfix(ZNet __instance, ZNetPeer peer)
	{
		peer.m_rpc.Register<ZPackage>(Plugin.Rpc, (_, pkg) =>
		{
			if (!ZNet.instance.IsServer())
			{
				Plugin.Receive(pkg);
			}
		});
		if (__instance.IsServer())
		{
			peer.m_rpc.Invoke(Plugin.Rpc, Plugin.BuildPackage());
		}
		else
		{
			Plugin.ServerMods = null;
			Plugin.LastReport = null;
			GcKern.Server = null;
		}
	}
}

/// <summary>Zurück im Hauptmenü: keine Server-Meldung mehr.</summary>
[HarmonyPatch(typeof(ZNet), "OnDestroy")]
internal static class ServerInfoResetPatch
{
	private static void Postfix() => GcKern.Server = null;
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
internal static class ConnectErrorPatch
{
	private static void Postfix()
	{
		if (Plugin.On && Plugin.ShowOnFailure.Value && Plugin.LastReport != null)
		{
			ReportWindow.Show(Plugin.LastReport);
		}
	}
}

[HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
internal static class SpawnPatch
{
	private static bool shown;

	private static void Postfix(Player __instance)
	{
		if (__instance != Player.m_localPlayer || shown || !Plugin.On || !Plugin.ShowOnJoinIfMissing.Value)
		{
			return;
		}
		if (Plugin.LastReport != null && Plugin.LastReport.Missing.Count > 0)
		{
			shown = true;
			ReportWindow.Show(Plugin.LastReport);
		}
	}
}

internal class ConsoleCommand : Jotunn.Entities.ConsoleCommand
{
	public override string Name => "modcheck";

	public override string Help => "GC_Kern: Mod-Abgleich mit dem Server anzeigen";

	public override void Run(string[] args)
	{
		if (Plugin.LastReport == null)
		{
			global::Console.instance.Print("Keine Serverliste empfangen (Server ohne GC_Kern, Mod-Abgleich aus oder nicht verbunden).");
			return;
		}
		ReportWindow.Show(Plugin.LastReport);
	}
}

internal static class ReportWindow
{
	private static GameObject? window;
	private static Text? header;
	private static Text? body;
	private static string text = "";

	public static void Show(Report report)
	{
		if (Gc.IsHeadless || GUIManager.Instance == null || GUIManager.CustomGUIFront == null)
		{
			return;
		}
		Build();
		text = report.ToText();
		header!.text = GcLoc.Get(report.HasProblems ? "gcm_header_problems" : "gcm_header_ok");
		body!.text = Colorize(report);
		window!.SetActive(true);
		window.transform.SetAsLastSibling();
		GUIManager.BlockInput(true);
	}

	private static string Colorize(Report report)
	{
		StringBuilder sb = new();
		sb.AppendLine($"<color=#ff8866><b>{GcLoc.Get("gcm_missing", report.Missing.Count)}</b></color>");
		foreach (ModInfo mod in report.Missing)
		{
			sb.AppendLine("  • " + mod.Describe());
		}
		sb.AppendLine();
		sb.AppendLine($"<color=#ffd166><b>{GcLoc.Get("gcm_different", report.Different.Count)}</b></color>");
		foreach ((ModInfo server, ModInfo client) in report.Different)
		{
			sb.AppendLine("  • " + GcLoc.Get("gcm_version_line", server.Name, server.Version, client.Version));
		}
		sb.AppendLine();
		sb.AppendLine($"<color=#9fd3ff><b>{GcLoc.Get("gcm_client_only", report.ClientOnly.Count)}</b></color>");
		foreach (ModInfo mod in report.ClientOnly)
		{
			sb.AppendLine("  • " + mod.Describe());
		}
		sb.AppendLine();
		sb.AppendLine($"<i>{GcLoc.Get("gcm_note")}</i>");
		return sb.ToString();
	}

	private static void Build()
	{
		if (window != null)
		{
			return;
		}
		window = GcUi.CreateWindow("GC_ModCheck_Window", 900f, 620f, out header, out RectTransform inner);
		RectTransform content = GcUi.CreateScroll(inner, 820f, 440f);
		GameObject textGo = GUIManager.Instance.CreateText("", content, new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero,
			GUIManager.Instance.AveriaSerifBold, 18, Color.white, true, Color.black, 780f, 0f, true);
		body = textGo.GetComponent<Text>();
		body.supportRichText = true;
		body.horizontalOverflow = HorizontalWrapMode.Wrap;
		body.verticalOverflow = VerticalWrapMode.Overflow;

		GcUi.CreateButton(window.transform, GcLoc.Get("gcm_copy"), new Vector2(0.5f, 0f), new Vector2(-130f, 44f), 240f, 44f, () =>
		{
			GUIUtility.systemCopyBuffer = text;
			Gc.Center(GcLoc.Get("gcm_copied"));
			if (Player.m_localPlayer == null)
			{
				header!.text = GcLoc.Get("gcm_copied");
			}
		});
		GcUi.CreateButton(window.transform, GcLoc.Get("gcm_close"), new Vector2(0.5f, 0f), new Vector2(130f, 44f), 200f, 44f, () =>
		{
			window!.SetActive(false);
			GUIManager.BlockInput(false);
		});
	}
}
