using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace GC.Kern;

/// <summary>
/// Mods ein- und ausschalten, Mod-Profile, Neustart (Gorduan, 25.10.2026: „Übernehme alle Features von Mod Menu und verbessere
/// sie, dann können wir die Mod auch rauslassen“). Der Vorlader GC_ModSchalter (BepInEx\patchers) nimmt beim Start die
/// Plugins aus BepInEx/config/GC_Kern/aus.txt aus der Liste des Chainloaders; hier entstehen Liste, Zustände und die Datei.
///
/// Besser als Mod Menu:
///  * Abhängigkeiten: Wer eine Mod ausschaltet, schaltet auch die aus, die sie hart brauchen (sie würden sonst ohnehin
///    scheitern); wer eine einschaltet, schaltet ihre harten Abhängigkeiten mit ein. GUIDs ohne Rücksicht auf Groß/klein.
///  * Kein zweiter Scan des Plugin-Ordners: Der Vorlader hat alle gefundenen Plugins schon (BepInEx' Cache bleibt heil).
///  * Profile: Laden übergeht Mods, die nicht mehr installiert sind, und sagt es; Löschen legt die Datei in profile\geloescht.
///  * Neustart behält das r2modman-Profil: dieselbe valheim.exe mit genau denselben Argumenten (Doorstop), sauber
///    gequotet; nur ohne steam_appid.txt über steam.exe -applaunch. Nur im Hauptmenü (in einer Welt erst abmelden).
///  * Liste und Profile von Mod Menu (config/ModMenu) werden beim ersten Mal übernommen.
/// </summary>
internal static class ModSwitch
{
	// dieselben Schlüssel wie im Vorlader (Patcher\ModSchalter.cs)
	private const string DataAll = "GC.ModSchalter.All";
	private const string DataSkipped = "GC.ModSchalter.Skipped";
	private const string DataProtected = "GC.ModSchalter.Protected";
	private const string DataFile = "GC.ModSchalter.File";
	private const string KernGuid = "gorduan.gc.kern";

	internal enum State
	{
		Loaded,
		Failed,
		Off,
		OtherProcess,
		NotLoaded,
	}

	internal sealed class Mod
	{
		public string Guid = "";
		public string Name = "";
		public string Version = "";
		public string Location = "";
		/// <summary>r2modman-Paketordner (Namespace-Name), leer bei einer losen DLL.</summary>
		public string Package = "";
		public string PackageVersion = "";
		public string Namespace = "";
		public string PackageName = "";
		public State State;
		public string Reason = "";
		public string[] HardDeps = Array.Empty<string>();
		public bool Protected;
		public string Search = "";
	}

	internal sealed class Profile
	{
		public string Name = "";
		public string File = "";
		public HashSet<string> Off = new(StringComparer.OrdinalIgnoreCase);
	}

	private static List<Mod>? catalog;
	private static HashSet<string>? wanted;
	private static HashSet<string> skippedAtStart = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Der Vorlader hat sich eingehängt (sonst lassen sich Mods hier nicht schalten).</summary>
	internal static bool PatcherActive => AppDomain.CurrentDomain.GetData(DataFile) is string;

	internal static string Folder => Path.Combine(Paths.ConfigPath, "GC_Kern");

	private static string ListFile => AppDomain.CurrentDomain.GetData(DataFile) as string ?? Path.Combine(Folder, "aus.txt");

	private static string ProfileFolder => Path.Combine(Folder, "profile");

	internal static IReadOnlyList<Mod> Catalog => catalog ??= BuildCatalog();

	/// <summary>Gewünscht aus (aus.txt); wirkt beim nächsten Start.</summary>
	private static HashSet<string> Wanted => wanted ??= ReadList(ListFile);

	internal static bool IsWantedOff(Mod mod) => !mod.Protected && Wanted.Contains(mod.Guid);

	internal static Mod? Find(string guid) => Catalog.FirstOrDefault(m => string.Equals(m.Guid, guid, StringComparison.OrdinalIgnoreCase));

	/// <summary>Die Wunschliste weicht vom Stand beim Start ab: erst ein Neustart setzt sie um (ohne Vorlader nie).</summary>
	internal static bool RestartNeeded
	{
		get
		{
			if (!PatcherActive)
			{
				return false;
			}
			HashSet<string> known = new(Catalog.Where(m => !m.Protected).Select(m => m.Guid), StringComparer.OrdinalIgnoreCase);
			known.IntersectWith(Wanted);
			return !known.SetEquals(skippedAtStart);
		}
	}

	/// <summary>Letzter Fehler beim Schreiben (Liste, Profil), für die Statuszeile; leer = alles gut.</summary>
	internal static string LastError { get; private set; } = "";

	private static List<Mod> BuildCatalog()
	{
		ImportModMenu();
		AppDomain domain = AppDomain.CurrentDomain;
		List<PluginInfo> all = domain.GetData(DataAll) as List<PluginInfo> ?? new List<PluginInfo>();
		List<PluginInfo> skipped = domain.GetData(DataSkipped) as List<PluginInfo> ?? new List<PluginInfo>();
		skippedAtStart = new HashSet<string>(skipped.Select(info => info.Metadata.GUID), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, Mod> byGuid = new(StringComparer.OrdinalIgnoreCase);
		void Add(PluginInfo info, State state, string reason)
		{
			string guid = info.Metadata.GUID;
			if (byGuid.ContainsKey(guid))
			{
				return; // GC_Kern liegt in jedem Paket: eine Zeile
			}
			Mod mod = new()
			{
				Guid = guid,
				Name = info.Metadata.Name,
				Version = info.Metadata.Version.ToString(),
				Location = info.Location ?? "",
				State = state,
				Reason = reason,
				HardDeps = info.Dependencies.Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0).Select(d => d.DependencyGUID).ToArray(),
			};
			FindPackage(mod);
			mod.Search = string.Join(" ", mod.Name, mod.Guid, mod.Package).ToLowerInvariant();
			byGuid[guid] = mod;
		}
		foreach (PluginInfo info in Chainloader.PluginInfos.Values)
		{
			Add(info, info.Instance != null ? State.Loaded : State.Failed, "");
		}
		foreach (PluginInfo info in skipped)
		{
			Add(info, State.Off, "");
		}
		foreach (PluginInfo info in all)
		{
			if (!byGuid.ContainsKey(info.Metadata.GUID))
			{
				(State state, string reason) = NotLoaded(info);
				Add(info, state, reason);
			}
		}
		HashSet<string> keep = new((domain.GetData(DataProtected) as string[]) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		if (keep.Count == 0)
		{
			keep = Protected(byGuid); // Vorlader ohne Liste: selbst ausrechnen
		}
		foreach (Mod mod in byGuid.Values)
		{
			mod.Protected = keep.Contains(mod.Guid);
		}
		return byGuid.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>Warum eine gefundene Mod nicht geladen ist: anderes Programm (BepInProcess) oder ein Fehler des Chainloaders.</summary>
	private static (State, string) NotLoaded(PluginInfo info)
	{
		string[] processes = info.Processes.Select(p => p.ProcessName).ToArray();
		if (processes.Length > 0 && !processes.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), Paths.ProcessName, StringComparison.OrdinalIgnoreCase)))
		{
			return (State.OtherProcess, string.Join(", ", processes));
		}
		string? error = Chainloader.DependencyErrors.FirstOrDefault(line => line.Contains(info.Metadata.GUID) || line.Contains("[" + info.Metadata.Name));
		return error != null ? (State.Failed, error) : (State.NotLoaded, "");
	}

	private static HashSet<string> Protected(Dictionary<string, Mod> byGuid)
	{
		HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase) { KernGuid };
		Queue<string> todo = new(keep);
		while (todo.Count > 0)
		{
			if (byGuid.TryGetValue(todo.Dequeue(), out Mod mod))
			{
				foreach (string dependency in mod.HardDeps)
				{
					if (keep.Add(dependency))
					{
						todo.Enqueue(dependency);
					}
				}
			}
		}
		return keep;
	}

	[Serializable]
	private sealed class ManifestJson
	{
		public string name = "";
		public string version_number = "";
	}

	/// <summary>r2modman-Paket der DLL: der nächste Ordner mit manifest.json unterhalb von plugins (Namespace aus dem Ordnernamen).</summary>
	private static void FindPackage(Mod mod)
	{
		try
		{
			string root = Path.GetFullPath(Paths.PluginPath).TrimEnd('\\', '/');
			string? dir = mod.Location.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(mod.Location)) : null;
			while (dir != null && dir.Length > root.Length && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			{
				string manifest = Path.Combine(dir, "manifest.json");
				if (File.Exists(manifest))
				{
					ManifestJson json = JsonUtility.FromJson<ManifestJson>(File.ReadAllText(manifest));
					mod.Package = Path.GetFileName(dir);
					mod.PackageName = json?.name ?? "";
					mod.PackageVersion = json?.version_number ?? "";
					int dash = mod.Package.IndexOf('-');
					if (dash > 0 && string.Equals(mod.Package.Substring(dash + 1), mod.PackageName, StringComparison.OrdinalIgnoreCase))
					{
						mod.Namespace = mod.Package.Substring(0, dash);
					}
					return;
				}
				dir = Path.GetDirectoryName(dir);
			}
		}
		catch
		{
			// ohne Paket weiter
		}
	}

	/// <summary>
	/// Ein- oder ausschalten, mit Abhängigkeiten: aus nimmt alle mit, die sie hart brauchen, an holt ihre harten
	/// Abhängigkeiten dazu. Gibt die geänderten Mods zurück (die gewählte zuerst).
	/// </summary>
	internal static List<Mod> SetOff(Mod start, bool off)
	{
		List<Mod> changed = new();
		if (!PatcherActive || start.Protected)
		{
			return changed;
		}
		Queue<Mod> todo = new();
		todo.Enqueue(start);
		while (todo.Count > 0)
		{
			Mod mod = todo.Dequeue();
			if (mod.Protected || Wanted.Contains(mod.Guid) == off)
			{
				continue;
			}
			if (off)
			{
				Wanted.Add(mod.Guid);
			}
			else
			{
				Wanted.Remove(mod.Guid);
			}
			changed.Add(mod);
			IEnumerable<Mod> next = off
				? Catalog.Where(other => other.HardDeps.Contains(mod.Guid, StringComparer.OrdinalIgnoreCase))
				: mod.HardDeps.Select(Find).Where(other => other != null).Cast<Mod>();
			foreach (Mod other in next)
			{
				todo.Enqueue(other);
			}
		}
		if (!Save())
		{
			changed.Clear(); // nicht gespeichert: die Liste gilt wie in der Datei
		}
		return changed;
	}

	/// <summary>Alle wieder an (wie „Switch all on“ in Mod Menu).</summary>
	internal static void AllOn()
	{
		Wanted.Clear();
		Save();
	}

	/// <summary>Wer eine ausgeschaltete Mod hart braucht, kann nicht laden: auch aus (über mehrere Stufen).</summary>
	private static HashSet<string> WithDependents(IEnumerable<string> off)
	{
		HashSet<string> result = new(off, StringComparer.OrdinalIgnoreCase);
		bool added = true;
		while (added)
		{
			added = false;
			foreach (Mod mod in Catalog)
			{
				if (!mod.Protected && !result.Contains(mod.Guid) && mod.HardDeps.Any(result.Contains))
				{
					result.Add(mod.Guid);
					added = true;
				}
			}
		}
		return result;
	}

	/// <summary>Wer die Mod hart braucht und gerade an sein soll (für den Hinweis „schaltet auch aus“).</summary>
	internal static List<Mod> Dependents(Mod mod) => Catalog.Where(other => !IsWantedOff(other) && other.HardDeps.Contains(mod.Guid, StringComparer.OrdinalIgnoreCase)).ToList();

	/// <summary>Harte Abhängigkeiten, die fehlen oder aus sein sollen.</summary>
	internal static List<string> MissingDeps(Mod mod) => mod.HardDeps.Where(guid => Find(guid) is not Mod dep || IsWantedOff(dep)).Select(guid => Find(guid)?.Name ?? guid).ToList();

	/// <summary>Liste schreiben. Geht das nicht (Datei gesperrt …), gilt wieder der Stand der Datei und LastError sagt warum.</summary>
	private static bool Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(ListFile)!);
			List<string> lines = new() { "# Im Menü GC Option ausgeschaltete Mods, eine GUID je Zeile. Datei löschen = alle wieder an." };
			lines.AddRange(Wanted.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
			File.WriteAllLines(ListFile, lines, new UTF8Encoding(false));
			LastError = "";
			return true;
		}
		catch (Exception e)
		{
			LastError = e.Message;
			wanted = ReadList(ListFile);
			return false;
		}
	}

	private static HashSet<string> ReadList(string file)
	{
		HashSet<string> list = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (File.Exists(file))
			{
				foreach (string line in File.ReadAllLines(file))
				{
					string text = line.Trim();
					if (text.Length > 0 && !text.StartsWith("#", StringComparison.Ordinal))
					{
						list.Add(text);
					}
				}
			}
		}
		catch (Exception e)
		{
			UnityEngine.Debug.LogWarning($"GC_Kern: {file} nicht lesbar: {e.Message}");
		}
		return list;
	}

	// ------------------------------------------------------------------ Profile

	internal static List<Profile> Profiles()
	{
		List<Profile> list = new();
		if (!Directory.Exists(ProfileFolder))
		{
			return list;
		}
		foreach (string file in Directory.GetFiles(ProfileFolder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			list.Add(new Profile { Name = Path.GetFileNameWithoutExtension(file), File = file, Off = ReadList(file) });
		}
		return list;
	}

	/// <summary>Installierte, schaltbare GUIDs eines Profils (Profile von früher können Mods nennen, die es nicht mehr gibt).</summary>
	private static HashSet<string> Known(IEnumerable<string> guids)
	{
		HashSet<string> known = new(Catalog.Where(m => !m.Protected).Select(m => m.Guid), StringComparer.OrdinalIgnoreCase);
		known.IntersectWith(guids);
		return known;
	}

	internal static bool IsCurrent(Profile profile) => Known(profile.Off).SetEquals(Known(Wanted));

	internal static int MissingIn(Profile profile) => profile.Off.Count(guid => Find(guid) == null);

	/// <summary>Name als Dateiname: ungültige Zeichen fallen weg. Leer = ungültig.</summary>
	internal static string CleanName(string name)
	{
		string clean = new(name.Trim().Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray());
		return clean.Trim().TrimEnd('.');
	}

	internal static bool SaveProfile(string name) => WriteProfile(name, Wanted);

	internal static bool WriteProfile(string name, IEnumerable<string> off)
	{
		string clean = CleanName(name);
		if (clean.Length == 0)
		{
			return false;
		}
		try
		{
			Directory.CreateDirectory(ProfileFolder);
			List<string> lines = new() { "# GC Option, Mod-Profil „" + clean + "“: diese Mods aus, alle anderen an." };
			lines.AddRange(off.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
			File.WriteAllLines(Path.Combine(ProfileFolder, clean + ".txt"), lines, new UTF8Encoding(false));
			LastError = "";
			return true;
		}
		catch (Exception e)
		{
			LastError = e.Message; // z. B. reservierter Name wie CON oder gesperrte Datei
			return false;
		}
	}

	/// <summary>Freier Profilname: „Name“, sonst „Name (2)“, „Name (3)“ …</summary>
	internal static string FreeName(string name)
	{
		HashSet<string> taken = new(Profiles().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
		string clean = CleanName(name);
		string candidate = clean;
		for (int i = 2; taken.Contains(candidate); i++)
		{
			candidate = $"{clean} ({i})";
		}
		return candidate;
	}

	/// <summary>
	/// Profil übernehmen: Diese Mods sollen aus sein, alle anderen an; dazu aus, wer eine davon hart braucht (sonst scheitert
	/// sie beim Start). Gibt zurück, wie viele Mods des Profils nicht installiert sind; -1, wenn die Liste nicht schreibbar war.
	/// </summary>
	internal static int ApplyProfile(Profile profile)
	{
		HashSet<string> before = new(Wanted, StringComparer.OrdinalIgnoreCase);
		Wanted.Clear();
		Wanted.UnionWith(WithDependents(Known(profile.Off)));
		if (!Save())
		{
			wanted = before;
			return -1;
		}
		return MissingIn(profile);
	}

	/// <summary>Löschen heißt hier: in profile\geloescht legen (Gorduan: nichts endgültig löschen).</summary>
	internal static bool DeleteProfile(Profile profile)
	{
		try
		{
			string bin = Path.Combine(ProfileFolder, "geloescht");
			Directory.CreateDirectory(bin);
			string target = Path.Combine(bin, Path.GetFileNameWithoutExtension(profile.File) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
			File.Move(profile.File, target);
			LastError = "";
			return true;
		}
		catch (Exception e)
		{
			LastError = e.Message;
			return false;
		}
	}

	/// <summary>Profil als Text (für die Zwischenablage, z. B. Discord).</summary>
	internal static string ProfileText(Profile profile) =>
		"# GC Option, Mod-Profil „" + profile.Name + "“\n" + string.Join("\n", profile.Off.OrderBy(g => g, StringComparer.OrdinalIgnoreCase)) + "\n";

	/// <summary>GUIDs aus einem Text (Zwischenablage); leer, wenn nichts darin wie eine GUID aussieht.</summary>
	internal static HashSet<string> ParseGuids(string text)
	{
		HashSet<string> list = new(StringComparer.OrdinalIgnoreCase);
		foreach (string line in text.Replace("\r", "").Split('\n'))
		{
			string guid = line.Trim();
			if (guid.Length > 0 && !guid.StartsWith("#", StringComparison.Ordinal) && guid.IndexOf(' ') < 0)
			{
				list.Add(guid);
			}
		}
		return list;
	}

	/// <summary>Einmalig: Liste und Profile von Mod Menu (config/ModMenu) übernehmen, damit man umsteigen kann.</summary>
	private static void ImportModMenu()
	{
		try
		{
			string old = Path.Combine(Paths.ConfigPath, "ModMenu");
			if (!Directory.Exists(old))
			{
				return;
			}
			string oldList = Path.Combine(old, "disabled.txt");
			if (File.Exists(oldList) && !File.Exists(ListFile))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(ListFile)!);
				File.Copy(oldList, ListFile);
				UnityEngine.Debug.Log("GC_Kern: ausgeschaltete Mods aus Mod Menu übernommen.");
			}
			string oldProfiles = Path.Combine(old, "profiles");
			if (Directory.Exists(oldProfiles) && !Directory.Exists(ProfileFolder))
			{
				Directory.CreateDirectory(ProfileFolder);
				foreach (string file in Directory.GetFiles(oldProfiles, "*.txt"))
				{
					File.Copy(file, Path.Combine(ProfileFolder, Path.GetFileName(file)));
				}
				UnityEngine.Debug.Log("GC_Kern: Profile aus Mod Menu übernommen.");
			}
		}
		catch (Exception e)
		{
			UnityEngine.Debug.LogWarning($"GC_Kern: Mod Menu nicht übernommen: {e.Message}");
		}
	}

	// ------------------------------------------------------------------ Neustart

	/// <summary>Neustart geht nur im Hauptmenü (in einer Welt erst abmelden) und unter Windows.</summary>
	internal static bool CanRestart => ZNet.instance == null && Application.platform == RuntimePlatform.WindowsPlayer;

	private static bool restarting;

	/// <summary>
	/// Spiel neu starten und das r2modman-Profil behalten: Ein verstecktes PowerShell wartet, bis dieses Spiel beendet ist, und
	/// startet dieselbe valheim.exe mit denselben Argumenten (Doorstop zeigt auf das Profil). Ohne steam_appid.txt würde
	/// Steam das Spiel ohne Argumente neu starten, dann über steam.exe -applaunch 892970 mit den Argumenten. Das Skript geht
	/// über die Standardeingabe (-Command -), nicht verschlüsselt: Virenscanner melden versteckte -EncodedCommand-Aufrufe
	/// eines Spiels gern (Code-Prüfung 25.10.2026). Ein zweiter Klick tut nichts.
	/// </summary>
	internal static bool Restart(out string error)
	{
		error = "";
		if (restarting)
		{
			return true;
		}
		try
		{
			Process self = Process.GetCurrentProcess();
			string exe = self.MainModule.FileName;
			string dir = Path.GetDirectoryName(exe)!;
			string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument));
			string file = exe;
			if (!File.Exists(Path.Combine(dir, "steam_appid.txt")) && SteamExe() is string steam)
			{
				file = steam;
				args = ("-applaunch 892970 " + args).Trim();
			}
			// Doorstop setzt im laufenden Spiel DOORSTOP_*-Variablen; erbt das neue Spiel sie, hält Doorstop es für einen
			// Unterprozess und lädt BepInEx nicht (Test 25.10.2026: Neustart kam ohne Mods). Deshalb ohne diese Variablen.
			string script = "Get-ChildItem Env: | Where-Object { $_.Name -like 'DOORSTOP*' } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }; "
				+ $"Wait-Process -Id {self.Id} -ErrorAction SilentlyContinue; Start-Process -FilePath {Ps(file)} -WorkingDirectory {Ps(dir)}"
				+ (args.Length > 0 ? $" -ArgumentList {Ps(args)}" : "");
			ProcessStartInfo start = new("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command -")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
			};
			foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToList())
			{
				if (key.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
				{
					start.EnvironmentVariables.Remove(key);
				}
			}
			Process shell = Process.Start(start);
			shell.StandardInput.WriteLine(script);
			shell.StandardInput.Close();
			restarting = true;
			Application.Quit();
			return true;
		}
		catch (Exception e)
		{
			error = e.Message;
			return false;
		}
	}

	/// <summary>
	/// Text als PowerShell-Ausdruck aus reinen ASCII-Zeichen (die Standardeingabe hat keine sichere Kodierung): Umlaute und
	/// andere Zeichen als [char]0x…, der Rest in einfachen Anführungszeichen.
	/// </summary>
	private static string Ps(string text)
	{
		List<string> parts = new();
		StringBuilder run = new();
		foreach (char c in text)
		{
			if (c >= 0x20 && c < 0x7f)
			{
				run.Append(c == '\'' ? "''" : c.ToString());
				continue;
			}
			if (run.Length > 0)
			{
				parts.Add("'" + run + "'");
				run.Clear();
			}
			parts.Add($"[char]0x{(int)c:X4}");
		}
		if (run.Length > 0 || parts.Count == 0)
		{
			parts.Add("'" + run + "'");
		}
		// mit '' vorne, damit PowerShell Text verkettet und nicht mit [char] rechnet
		return parts.Count == 1 && parts[0].StartsWith("'", StringComparison.Ordinal) ? parts[0] : "('' + " + string.Join(" + ", parts) + ")";
	}

	/// <summary>Ein Argument so quoten, wie Windows (CommandLineToArgvW) es wieder zerlegt.</summary>
	private static string QuoteArgument(string arg)
	{
		if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
		{
			return arg;
		}
		StringBuilder sb = new("\"");
		int backslashes = 0;
		foreach (char c in arg)
		{
			if (c == '\\')
			{
				backslashes++;
				continue;
			}
			sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
			backslashes = 0;
			sb.Append(c);
		}
		sb.Append('\\', backslashes * 2).Append('"');
		return sb.ToString();
	}

	private static string? SteamExe()
	{
		try
		{
			using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
			string? path = key?.GetValue("SteamExe") as string;
			return path != null && File.Exists(path) ? path : null;
		}
		catch
		{
			return null;
		}
	}
}
