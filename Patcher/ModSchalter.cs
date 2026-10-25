using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;

namespace GC.Patcher;

/// <summary>
/// Vorlader für das Ein- und Ausschalten von Mods im Menü „GC Option“ (Gorduan, 25.10.2026: „Übernehme alle Features von Mod
/// Menu und verbessere sie“). Er ändert keine Datei einer Mod: Ein Postfix auf TypeLoader.FindPluginTypes nimmt beim Start
/// die Plugins, deren GUID in BepInEx/config/GC_Kern/aus.txt steht, aus der Liste des Chainloaders. Ihre DLL wird dann gar
/// nicht geladen. Die Liste schreibt GC_Kern (Ansicht Mods).
///
/// Sicherungen: GC_Kern und alles, was er hart braucht (Jötunn), lässt sich nie ausschalten. Fehlt GC_Kern unter den
/// Plugins oder liegt noch ein altes Sammelpaket daneben (dann startet GC_Kern nicht), gilt die Liste nicht: So kommt man
/// ohne Menü wieder an alle Mods. Jeder Fehler lädt einfach alle Mods. Von Hand rückgängig: aus.txt löschen.
/// Jedes Paket bringt eine Kopie mit eigenem Assembly-Namen mit; die mit der höchsten Version hängt sich ein.
///
/// Für das Menü (AppDomain-Daten, weil Vorlader und Plugins verschiedene Assemblies sind): alle gefundenen Plugins (auch die
/// später an fehlenden Abhängigkeiten scheitern; kein zweiter Scan nötig wie bei Mod Menu), die ausgeschalteten, die
/// geschützten GUIDs und der Pfad der Liste (zeigt: Vorlader aktiv).
/// </summary>
public static class ModSchalter
{
	public const string KernGuid = "gorduan.gc.kern";
	/// <summary>Alte Sammelpakete: Laufen sie, startet GC_Kern nicht (BepInIncompatibility), und niemand könnte die Liste zurücknehmen.</summary>
	private static readonly string[] OldPackages = { "gorduan.gc_server", "gorduan.gc_client", "gorduan.valheimplusplus" };
	public const string DataInstalled = "GC.ModSchalter.Installed";
	public const string DataBest = "GC.ModSchalter.Best";
	public const string DataAll = "GC.ModSchalter.All";
	public const string DataSkipped = "GC.ModSchalter.Skipped";
	public const string DataProtected = "GC.ModSchalter.Protected";
	public const string DataFile = "GC.ModSchalter.File";

	private static readonly ManualLogSource Log = Logger.CreateLogSource("GC_ModSchalter");

	/// <summary>BepInEx-Vorlader: keine Assembly zu ändern, nur Finish.</summary>
	public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();

	public static string ListFile => Path.Combine(Path.Combine(Paths.ConfigPath, "GC_Kern"), "aus.txt");

	public static void Patch(AssemblyDefinition assembly)
	{
	}

	private static System.Version Own => typeof(ModSchalter).Assembly.GetName().Version;

	/// <summary>BepInEx ruft Initialize aller Vorlader vor jedem Finish auf: Hier merkt sich jede Kopie die höchste Version.</summary>
	public static void Initialize()
	{
		AppDomain domain = AppDomain.CurrentDomain;
		if (domain.GetData(DataBest) is not System.Version best || Own > best)
		{
			domain.SetData(DataBest, Own);
		}
	}

	public static void Finish()
	{
		AppDomain domain = AppDomain.CurrentDomain;
		if (domain.GetData(DataInstalled) is bool installed && installed)
		{
			return; // jedes GC-Paket bringt eine Kopie mit, nur eine hängt sich ein
		}
		if (domain.GetData(DataBest) is System.Version best && Own < best)
		{
			return; // eine neuere Kopie aus einem anderen Paket übernimmt
		}
		domain.SetData(DataInstalled, true);
		try
		{
			MethodInfo find = AccessTools.Method(typeof(TypeLoader), nameof(TypeLoader.FindPluginTypes)).MakeGenericMethod(typeof(PluginInfo));
			new Harmony("gorduan.gc.modschalter").Patch(find, postfix: new HarmonyMethod(typeof(ModSchalter), nameof(FindPluginTypesPostfix)));
			domain.SetData(DataFile, ListFile);
		}
		catch (Exception e)
		{
			Log.LogError($"Konnte sich nicht in BepInEx einhängen, alle Mods laden: {e}");
		}
	}

	private static void FindPluginTypesPostfix(string cacheName, Dictionary<string, List<PluginInfo>> __result)
	{
		if (cacheName != "chainloader" || __result == null)
		{
			return; // nur die Suche des Chainloaders im Ordner plugins
		}
		try
		{
			List<PluginInfo> all = __result.Values.SelectMany(list => list).ToList();
			AppDomain.CurrentDomain.SetData(DataAll, all);
			HashSet<string> off = ReadList(ListFile);
			if (off.Count == 0)
			{
				return;
			}
			if (!all.Any(info => string.Equals(info.Metadata.GUID, KernGuid, StringComparison.OrdinalIgnoreCase)))
			{
				Log.LogWarning("GC_Kern fehlt: aus.txt gilt nicht, alle Mods laden.");
				return;
			}
			if (all.Any(info => OldPackages.Contains(info.Metadata.GUID, StringComparer.OrdinalIgnoreCase)))
			{
				Log.LogWarning("Ein altes GC-Sammelpaket liegt noch im Ordner, GC_Kern startet dann nicht: aus.txt gilt nicht, alle Mods laden.");
				return;
			}
			HashSet<string> keep = Protected(all);
			List<PluginInfo> skipped = new();
			PropertyInfo? location = AccessTools.Property(typeof(PluginInfo), nameof(PluginInfo.Location));
			foreach (KeyValuePair<string, List<PluginInfo>> pair in __result.ToList())
			{
				foreach (PluginInfo info in pair.Value.ToList())
				{
					if (!off.Contains(info.Metadata.GUID) || keep.Contains(info.Metadata.GUID))
					{
						continue;
					}
					location?.SetValue(info, pair.Key, null); // die DLL, für die Anzeige im Menü
					pair.Value.Remove(info);
					skipped.Add(info);
					Log.LogMessage($"Im Menü GC Option ausgeschaltet, wird nicht geladen: [{info}]");
				}
				if (pair.Value.Count == 0)
				{
					__result.Remove(pair.Key);
				}
			}
			AppDomain.CurrentDomain.SetData(DataSkipped, skipped);
			AppDomain.CurrentDomain.SetData(DataProtected, keep.ToArray());
		}
		catch (Exception e)
		{
			Log.LogError($"Ausgeschaltete Mods nicht gefiltert, alle Mods laden: {e}");
		}
	}

	/// <summary>GC_Kern und alles, was er hart braucht (Jötunn), auch über mehrere Stufen.</summary>
	private static HashSet<string> Protected(List<PluginInfo> all)
	{
		Dictionary<string, PluginInfo> byGuid = new(StringComparer.OrdinalIgnoreCase);
		foreach (PluginInfo info in all)
		{
			byGuid[info.Metadata.GUID] = info;
		}
		HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase) { KernGuid };
		Queue<string> todo = new(keep);
		while (todo.Count > 0)
		{
			if (!byGuid.TryGetValue(todo.Dequeue(), out PluginInfo info))
			{
				continue;
			}
			foreach (BepInDependency dependency in info.Dependencies)
			{
				if ((dependency.Flags & BepInDependency.DependencyFlags.HardDependency) != 0 && keep.Add(dependency.DependencyGUID))
				{
					todo.Enqueue(dependency.DependencyGUID);
				}
			}
		}
		return keep;
	}

	/// <summary>Eine GUID je Zeile, # leitet Kommentare ein; Groß- und Kleinschreibung egal.</summary>
	public static HashSet<string> ReadList(string file)
	{
		HashSet<string> list = new(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(file))
		{
			return list;
		}
		foreach (string line in File.ReadAllLines(file))
		{
			string text = line.Trim();
			if (text.Length > 0 && !text.StartsWith("#", StringComparison.Ordinal))
			{
				list.Add(text);
			}
		}
		return list;
	}
}
