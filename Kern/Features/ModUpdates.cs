using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine.Networking;

namespace GC.Kern;

/// <summary>
/// Update-Prüfung der Mod-Liste (aus Mod Menu übernommen, Gorduan 25.10.2026), ohne Konto oder Schlüssel. Nur Anzeige und Link,
/// nichts wird heruntergeladen.
///  * Thunderstore: je r2modman-Paket einmal (nicht je DLL), GET thunderstore.io/api/experimental/package/{ns}/{name}/,
///    neueste Version gegen die im manifest.json. Eigene Pakete (Namespace Gorduan) gibt es dort nicht.
///  * Nexus: nur DLLs, die Vortex verteilt hat (vortex.deployment*.json nennt Mod-ID und Version), per GraphQL
///    legacyModsByDomain. Keine Namenssuche wie in Mod Menu: die brachte falsche Treffer.
///  * Ergebnisse liegen 6 Stunden in BepInEx/config/GC_Kern/updates.txt; „Updates prüfen“ fragt sofort neu.
/// Die Antworten liest ein kleiner eigener Leser (Regex): Unitys JsonUtility kann in einer Mod keine verschachtelten Objekte
/// lesen oder schreiben (Test 25.10.2026: kein einziges Ergebnis, obwohl Thunderstore EpicLoot 0.15.1 meldete).
/// </summary>
internal static class ModUpdates
{
	internal sealed class Info
	{
		public string Latest = "";
		public string Url = "";
		public bool Newer;
	}

	private const string UserAgent = "GC_Kern (Valheim mod by Gorduan; update check)";
	private static readonly TimeSpan CacheAge = TimeSpan.FromHours(6);
	private static readonly Dictionary<string, Info> Results = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, (int Id, string Version)> Nexus = new(StringComparer.OrdinalIgnoreCase);
	private static bool loaded;
	private static DateTime checkedAt = DateTime.MinValue;

	internal static bool Running { get; private set; }
	internal static int Done { get; private set; }
	internal static int Total { get; private set; }
	internal static string LastError { get; private set; } = "";

	/// <summary>Mindestens einmal ohne Fehler geprüft (oder aus der Datei gelesen).</summary>
	internal static bool HasResults
	{
		get
		{
			Load(); // auch wenn die automatische Prüfung aus ist und noch niemand Stale gefragt hat
			return Results.Count > 0 || checkedAt > DateTime.MinValue;
		}
	}

	/// <summary>Geprüft, aber zu keiner Mod etwas gefunden.</summary>
	internal static bool NothingFound => HasResults && Results.Count == 0;

	internal static bool Stale
	{
		get
		{
			Load();
			return DateTime.UtcNow - checkedAt > CacheAge;
		}
	}

	private static string CacheFile => Path.Combine(ModSwitch.Folder, "updates.txt");

	internal static bool IsOwn(ModSwitch.Mod mod) => mod.Namespace.Equals("Gorduan", StringComparison.OrdinalIgnoreCase) || mod.Guid.StartsWith("gorduan.", StringComparison.OrdinalIgnoreCase);

	/// <summary>Ergebnis für eine Mod (über ihr Paket bzw. ihre Nexus-ID), sonst null.</summary>
	internal static Info? For(ModSwitch.Mod mod)
	{
		Load();
		string? key = Key(mod);
		return key != null && Results.TryGetValue(key, out Info info) ? info : null;
	}

	/// <summary>Seite der Mod (Thunderstore oder Nexus), auch ohne Prüfung.</summary>
	internal static string Page(ModSwitch.Mod mod)
	{
		if (IsOwn(mod))
		{
			return "";
		}
		if (mod.Namespace.Length > 0)
		{
			return $"https://thunderstore.io/c/valheim/p/{mod.Namespace}/{mod.PackageName}/";
		}
		return NexusOf(mod) is (int id, _) && id > 0 ? $"https://www.nexusmods.com/valheim/mods/{id}" : "";
	}

	private static string? Key(ModSwitch.Mod mod)
	{
		if (IsOwn(mod))
		{
			return null;
		}
		if (mod.Namespace.Length > 0)
		{
			return "ts:" + mod.Namespace + "-" + mod.PackageName;
		}
		return NexusOf(mod) is (int id, _) && id > 0 ? "nexus:" + id : null;
	}

	private static string Installed(ModSwitch.Mod mod) => mod.PackageVersion.Length > 0 ? mod.PackageVersion : NexusOf(mod)?.Version ?? mod.Version;

	/// <summary>Prüfen (Koroutine). changed wird nach jedem Ergebnis aufgerufen, damit die Liste mitwächst.</summary>
	internal static IEnumerator Check(IReadOnlyList<ModSwitch.Mod> mods, Action changed)
	{
		if (Running)
		{
			yield break;
		}
		Running = true;
		try
		{
			yield return CheckAll(mods, changed);
		}
		finally
		{
			Running = false; // auch wenn unterwegs etwas schiefging
			changed();
		}
	}

	private static IEnumerator CheckAll(IReadOnlyList<ModSwitch.Mod> mods, Action changed)
	{
		Load();
		LastError = "";
		ReadVortex();
		List<ModSwitch.Mod> thunderstore = mods.Where(m => !IsOwn(m) && m.Namespace.Length > 0).GroupBy(m => Key(m)).Select(g => g.First()).ToList();
		List<ModSwitch.Mod> nexus = mods.Where(m => !IsOwn(m) && m.Namespace.Length == 0 && NexusOf(m) != null).GroupBy(m => Key(m)).Select(g => g.First()).ToList();
		Total = thunderstore.Count + (nexus.Count > 0 ? 1 : 0);
		Done = 0;
		// Thunderstore: vier Anfragen gleichzeitig
		Queue<ModSwitch.Mod> todo = new(thunderstore);
		List<(UnityWebRequest Request, ModSwitch.Mod Mod)> active = new();
		while (todo.Count > 0 || active.Count > 0)
		{
			while (todo.Count > 0 && active.Count < 4)
			{
				ModSwitch.Mod mod = todo.Dequeue();
				UnityWebRequest request = UnityWebRequest.Get($"https://thunderstore.io/api/experimental/package/{Uri.EscapeDataString(mod.Namespace)}/{Uri.EscapeDataString(mod.PackageName)}/");
				request.SetRequestHeader("User-Agent", UserAgent);
				request.timeout = 15;
				request.SendWebRequest();
				active.Add((request, mod));
			}
			yield return null;
			foreach ((UnityWebRequest request, ModSwitch.Mod mod) in active.Where(a => a.Request.isDone).ToList())
			{
				active.Remove((request, mod));
				Done++;
				if (request.result == UnityWebRequest.Result.Success)
				{
					string text = request.downloadHandler.text;
					string latest = LatestVersion(text);
					if (latest.Length > 0)
					{
						string url = JsonString(text, "package_url");
						Results[Key(mod)!] = new Info { Latest = latest, Url = url.Length > 0 ? url : Page(mod), Newer = IsNewer(latest, Installed(mod)) };
					}
				}
				else if (request.responseCode != 404)
				{
					LastError = request.error ?? "";
				}
				request.Dispose();
				changed();
			}
		}
		if (nexus.Count > 0)
		{
			yield return CheckNexus(nexus);
			Done++;
			changed();
		}
		if (LastError.Length == 0)
		{
			checkedAt = DateTime.UtcNow; // nur ein Durchlauf ohne Fehler zählt als geprüft (offline: beim nächsten Öffnen neu)
		}
		Save();
	}

	private static IEnumerator CheckNexus(List<ModSwitch.Mod> mods)
	{
		string ids = string.Join(", ", mods.Select(m => $"{{gameDomain: \\\"valheim\\\", modId: {NexusOf(m)!.Value.Id}}}"));
		string body = "{\"query\": \"{ legacyModsByDomain(ids: [" + ids + "]) { nodes { modId version } } }\"}";
		using UnityWebRequest request = new("https://api.nexusmods.com/v2/graphql", "POST")
		{
			uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
			downloadHandler = new DownloadHandlerBuffer(),
			timeout = 20,
		};
		request.SetRequestHeader("Content-Type", "application/json");
		request.SetRequestHeader("User-Agent", UserAgent);
		yield return request.SendWebRequest();
		if (request.result != UnityWebRequest.Result.Success)
		{
			LastError = request.error ?? "";
			yield break;
		}
		// jedes Objekt in nodes ist flach: {"modId":123,"version":"1.2.3"}
		foreach (Match node in Regex.Matches(request.downloadHandler.text, @"\{[^{}]*""modId""[^{}]*\}"))
		{
			Match id = Regex.Match(node.Value, @"""modId""\s*:\s*(\d+)");
			string version = JsonString(node.Value, "version");
			ModSwitch.Mod? mod = id.Success ? mods.FirstOrDefault(m => NexusOf(m)?.Id.ToString(CultureInfo.InvariantCulture) == id.Groups[1].Value) : null;
			if (mod != null && version.Length > 0)
			{
				Results["nexus:" + id.Groups[1].Value] = new Info { Latest = version, Url = Page(mod), Newer = IsNewer(version, Installed(mod)) };
			}
		}
	}

	/// <summary>Jede Zahlengruppe als Zahl vergleichen (1.10 ist neuer als 1.9).</summary>
	internal static bool IsNewer(string remote, string local)
	{
		int[] a = Numbers(remote);
		int[] b = Numbers(local);
		for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
		{
			int x = i < a.Length ? a[i] : 0;
			int y = i < b.Length ? b[i] : 0;
			if (x != y)
			{
				return x > y;
			}
		}
		return false;
	}

	private static int[] Numbers(string version) => Regex.Matches(version ?? "", @"\d+").Cast<Match>().Select(m => int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();

	// ------------------------------------------------------------------ kleiner JSON-Leser

	/// <summary>„latest“ einer Thunderstore-Antwort: die erste version_number nach "latest".</summary>
	private static string LatestVersion(string json)
	{
		int latest = json.IndexOf("\"latest\"", StringComparison.Ordinal);
		return latest < 0 ? "" : JsonString(json.Substring(latest), "version_number");
	}

	/// <summary>Erster Text-Wert eines Schlüssels, mit aufgelösten Escapes.</summary>
	private static string JsonString(string json, string key)
	{
		Match match = Regex.Match(json, "\"" + Regex.Escape(key) + @"""\s*:\s*""((?:[^""\\]|\\.)*)""");
		return match.Success ? Unescape(match.Groups[1].Value) : "";
	}

	private static string Unescape(string text)
	{
		return Regex.Replace(text, @"\\(u[0-9a-fA-F]{4}|.)", m =>
		{
			string code = m.Groups[1].Value;
			return code.Length == 5 ? ((char)Convert.ToInt32(code.Substring(1), 16)).ToString() : code switch
			{
				"n" => "\n",
				"t" => "\t",
				"r" => "\r",
				"b" => "\b",
				"f" => "\f",
				_ => code, // \" \\ \/
			};
		});
	}

	// ------------------------------------------------------------------ Vortex (Nexus-ID und Version je Datei)

	private static (int Id, string Version)? NexusOf(ModSwitch.Mod mod)
	{
		return mod.Location.Length > 0 && Nexus.TryGetValue(Path.GetFullPath(mod.Location), out (int Id, string Version) value) ? value : null;
	}

	private static readonly Regex VortexNameIdVersionDate = new(@"^(?<name>.+?) (?<id>\d+) (?<ver>\S+) \d{4}-\d{2}-\d{2}T", RegexOptions.Compiled);
	private static readonly Regex VortexDashed = new(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>[\d-]+?)-(?<stamp>\d{9,})$", RegexOptions.Compiled);

	private static void ReadVortex()
	{
		Nexus.Clear();
		try
		{
			foreach (string file in Directory.GetFiles(Paths.PluginPath, "vortex.deployment*.json"))
			{
				string json = File.ReadAllText(file);
				string target = JsonString(json, "targetPath");
				if (target.Length == 0)
				{
					target = Paths.PluginPath;
				}
				// jeder Eintrag in files ist flach: {"relPath": "...", "source": "...", ...}
				foreach (Match entry in Regex.Matches(json, @"\{[^{}]*""relPath""[^{}]*\}"))
				{
					string relPath = JsonString(entry.Value, "relPath");
					string source = JsonString(entry.Value, "source");
					Match match = VortexNameIdVersionDate.Match(source);
					if (!match.Success)
					{
						match = VortexDashed.Match(source);
					}
					if (relPath.Length > 0 && match.Success && int.TryParse(match.Groups["id"].Value, out int id))
					{
						Nexus[Path.GetFullPath(Path.Combine(target, relPath))] = (id, match.Groups["ver"].Value.Replace('-', '.'));
					}
				}
			}
		}
		catch
		{
			// ohne Vortex weiter
		}
	}

	// ------------------------------------------------------------------ Zwischenspeicher (Text, eine Zeile je Paket)

	/// <summary>
	/// updates.txt: erste Zeile „geprueft=&lt;Zeit&gt;“, dann „Schlüssel TAB neueste Version TAB Seite“. Newer wird beim Lesen
	/// neu bestimmt, es hängt an der installierten Version.
	/// </summary>
	private static void Load()
	{
		if (loaded)
		{
			return;
		}
		loaded = true;
		try
		{
			if (!File.Exists(CacheFile))
			{
				return;
			}
			ReadVortex();
			foreach (string line in File.ReadAllLines(CacheFile))
			{
				if (line.StartsWith("geprueft=", StringComparison.Ordinal))
				{
					DateTime.TryParse(line.Substring(9), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out checkedAt);
					continue;
				}
				string[] parts = line.Split('\t');
				if (parts.Length >= 3 && parts[0].Length > 0)
				{
					Results[parts[0]] = new Info { Latest = parts[1], Url = parts[2] };
				}
			}
			foreach (ModSwitch.Mod mod in ModSwitch.Catalog)
			{
				if (Key(mod) is string key && Results.TryGetValue(key, out Info info))
				{
					info.Newer = IsNewer(info.Latest, Installed(mod));
				}
			}
		}
		catch
		{
			Results.Clear();
			checkedAt = DateTime.MinValue;
		}
	}

	private static void Save()
	{
		try
		{
			Directory.CreateDirectory(ModSwitch.Folder);
			List<string> lines = new() { "geprueft=" + (checkedAt > DateTime.MinValue ? checkedAt.ToString("o", CultureInfo.InvariantCulture) : "") };
			lines.AddRange(Results.Select(pair => pair.Key + "\t" + pair.Value.Latest.Replace('\t', ' ') + "\t" + pair.Value.Url.Replace('\t', ' ')));
			File.WriteAllLines(CacheFile, lines, new UTF8Encoding(false));
		}
		catch
		{
			// beim nächsten Mal neu prüfen
		}
	}
}
