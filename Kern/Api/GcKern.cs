using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace GC.Shared;

/// <summary>Wo eine GC-Mod laufen muss (GcSeite in der .csproj).</summary>
public enum GcSeite
{
	/// <summary>Nur beim Spieler. Der Server braucht sie nicht, kann sie aber über seine Stufe abschalten.</summary>
	Spieler,

	/// <summary>Beim Spieler, mit Teilen, die den Server brauchen. Hat der Server die Mod, brauchen alle sie; sonst bleiben diese Teile aus.</summary>
	ServerOptional,

	/// <summary>Server und alle Spieler brauchen genau diese Mod (Jötunn lehnt die Verbindung sonst ab).</summary>
	Alle,
}

/// <summary>Steckbrief einer GC-Mod. Wird aus den Gc*-Eigenschaften der .csproj erzeugt (GcMod.Info).</summary>
public sealed class GcModInfo
{
	public string Id = "";
	public string Guid = "";
	public string Name = "";
	public string Version = "";

	/// <summary>1 Sehr Vanilla, 2 Vanilla, 3 Vanilla+, 4 GC leicht, 5 GC (0 = Kern).</summary>
	public int Stufe = 1;

	public GcSeite Seite = GcSeite.Spieler;

	/// <summary>IDs anderer GC-Mods, ohne die diese Mod nicht startet.</summary>
	public string[] Braucht = Array.Empty<string>();

	/// <summary>IDs anderer GC-Mods, mit denen diese Mod mehr kann.</summary>
	public string[] Partner = Array.Empty<string>();

	/// <summary>GUIDs fremder Mods, mit denen diese Mod mehr kann.</summary>
	public string[] Fremd = Array.Empty<string>();

	/// <summary>cfg-Datei(en) der alten Sammelpakete (GC_Client, GC_Server, ValheimPlusPlus), mit ; getrennt; der erste Start übernimmt daraus die Werte.</summary>
	public string AlteConfig = "";

	/// <summary>
	/// Schwierigkeitsausgleich (Gorduan, 20.10.2026): so viel Prozent je besiegtem Boss werden Gegner stärker, solange diese Mod
	/// läuft. Der Boss-Fortschritt nimmt automatisch den höchsten Wert aller aktiven Mods.
	/// </summary>
	public float Ausgleich;
}

/// <summary>Was der Server meldet, mit dem du verbunden bist (nur wenn er den Kern hat).</summary>
public sealed class GcServerInfo
{
	/// <summary>Höchste Stufe der GC-Mods auf dem Server (automatische Stufe).</summary>
	public int Stufe;

	/// <summary>GUIDs aller Mods auf dem Server.</summary>
	public HashSet<string> Guids = new(StringComparer.Ordinal);
}

/// <summary>Eine angemeldete GC-Mod. Jede Mod meldet sich als Erstes in Awake mit GcKern.Register an.</summary>
public sealed class GcModule
{
	private readonly HashSet<string> reported = new();

	internal GcModule(BaseUnityPlugin plugin, ManualLogSource log, GcModInfo info, bool freshConfig)
	{
		Plugin = plugin;
		Log = log;
		Info = info;
		FreshConfig = freshConfig;
	}

	public BaseUnityPlugin Plugin { get; }
	public ManualLogSource Log { get; }
	public GcModInfo Info { get; }
	public ConfigFile Config => Plugin.Config;

	/// <summary>Die cfg-Datei der Mod gab es beim Anmelden noch nicht (erster Start).</summary>
	public bool FreshConfig { get; }

	/// <summary>
	/// Darf die Mod hier wirken? Nein nur, wenn der Server, mit dem du verbunden bist, eine niedrigere Stufe vorgibt.
	/// Mods der Seite „Alle“ hat der Server immer (Jötunn), sie dürfen immer.
	/// </summary>
	public bool Allowed => GcKern.Allowed(Info);

	/// <summary>Hat der Server diese Mod? Im Hauptmenü nein, in der eigenen Welt und auf dem Server selbst ja.</summary>
	public bool OnServer => GcKern.ServerHas(Info.Guid);

	/// <summary>Hauptschalter der Mod (setzt die Mod, z. B. Leinen oder Tierpflege an). Ohne: an.</summary>
	public Func<bool>? IsOn;

	/// <summary>Läuft die Mod gerade (freigegeben und eingeschaltet)? Der Boss-Fortschritt zählt nur laufende Mods.</summary>
	public bool On
	{
		get
		{
			try
			{
				return Allowed && (IsOn == null || IsOn());
			}
			catch
			{
				return false;
			}
		}
	}

	/// <summary>
	/// Gruppe dieser Mod im Menü GC Option, im Abschnitt ihrer Stufe. text: Text zu einem Schlüssel (z. B. "t_x_name").
	/// titleKey: eigene Überschrift (Standard gck_mod_&lt;id&gt;), z. B. für eine zweite Gruppe derselben Mod.
	/// </summary>
	public GcMenuGroup MenuGroup(Func<string, string> text, string? titleKey = null) => GcKern.MenuGroup(this, text, titleKey);

	/// <summary>
	/// Nach allen Config.Bind aufrufen: Beim ersten Start übernimmt die Mod die Werte aus der cfg des alten Sammelpakets
	/// (gleicher Abschnitt, gleicher Schlüssel). Die alte Datei bleibt unverändert liegen.
	/// </summary>
	public void TakeOverOldSettings()
	{
		if (!FreshConfig || Info.AlteConfig.Length == 0)
		{
			return;
		}
		foreach (string file in Info.AlteConfig.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string name = file.Trim();
			string path = Path.Combine(Paths.ConfigPath, name);
			if (!File.Exists(path))
			{
				continue;
			}
			bool save = Config.SaveOnConfigSet;
			try
			{
				Dictionary<string, string> old = GcCfgFile.Read(path);
				int taken = 0;
				Config.SaveOnConfigSet = false;
				foreach (ConfigDefinition definition in Config.Keys.ToList())
				{
					if (old.TryGetValue(GcCfgFile.Key(definition.Section, definition.Key), out string value))
					{
						ConfigEntryBase entry = Config[definition];
						string before = entry.GetSerializedValue();
						entry.SetSerializedValue(value);
						if (entry.GetSerializedValue() != before)
						{
							taken++;
						}
					}
				}
				Config.SaveOnConfigSet = save;
				Config.Save();
				Log.LogInfo($"Einstellungen aus {name} übernommen: {taken} Werte weichen vom Standard ab.");
			}
			catch (Exception e)
			{
				Config.SaveOnConfigSet = save;
				Log.LogWarning($"Einstellungen aus {name} konnten nicht übernommen werden: {e.Message}");
			}
		}
	}

	/// <summary>Ein Teil der Mod für sich: Ein Fehler darin hält den Rest nicht an und steht je Sitzung nur einmal im Log.</summary>
	public void Guard(string part, Action action)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			if (reported.Add(part))
			{
				Log.LogWarning($"{part}: {e}");
			}
		}
	}

	/// <summary>Ist die GC-Mod mit dieser ID geladen (z. B. "tiere")?</summary>
	public bool Has(string id) => GcKern.ModLoaded(id);
}

/// <summary>
/// Kern des GC-SDK (Gorduan). Jede GC-Mod meldet sich in Awake an, bevor sie Einstellungen anlegt:
/// <code>
/// Module = GcKern.Register(this, Logger, GcMod.Info);
/// … Config.Bind / Gc.BindSynced …
/// Module.TakeOverOldSettings();
/// Module.MenuGroup(key => GcLoc.Get("prefix_" + key)).Main(…).Sub(…);
/// </code>
/// </summary>
public static class GcKern
{
	public const string Guid = "gorduan.gc.kern";
	public const string Name = "GC_Kern";
	public const string Version = "1.1.1"; // gleich <Version> in GC_Kern.csproj, build.ps1 prüft das

	/// <summary>Anfang der GUID jeder GC-Mod; dahinter steht ihre ID.</summary>
	public const string GuidPrefix = "gorduan.gc.";

	/// <summary>Die alten Sammelpakete. Laufen sie noch, startet der Kern nicht (BepInIncompatibility), damit nichts doppelt wirkt.</summary>
	public static readonly string[] OldPackages = { "gorduan.gc_server", "gorduan.gc_client", "gorduan.valheimplusplus" };

	private static readonly List<GcModule> modules = new();
	private static readonly Dictionary<int, GcMenuSection> sections = new();

	/// <summary>Alle angemeldeten GC-Mods in Ladereihenfolge (der Kern zuerst).</summary>
	public static IReadOnlyList<GcModule> Modules => modules;

	/// <summary>Meldung des Servers, mit dem du verbunden bist. null: nicht verbunden oder Server ohne Kern.</summary>
	public static GcServerInfo? Server { get; internal set; }

	/// <summary>Vom Admin eingestellte Stufe für Spieler-Mods (0 = automatisch: höchste Stufe der Server-Mods).</summary>
	internal static ConfigEntry<int>? StufeSetting;

	public static GcModule Register(BaseUnityPlugin plugin, ManualLogSource log, GcModInfo info)
	{
		bool fresh = !File.Exists(plugin.Config.ConfigFilePath);
		GcModule module = new(plugin, log, info, fresh);
		modules.Add(module);
		if (plugin.GetType().Assembly != typeof(GcKern).Assembly)
		{
			GcLoc.AddAssembly(plugin.GetType().Assembly, info.Name, log);
		}
		log.LogInfo($"{info.Name} {info.Version} angemeldet: Stufe {info.Stufe}, {SeiteText(info.Seite)}.");
		return module;
	}

	public static bool IsLoaded(string guid) => Chainloader.PluginInfos.ContainsKey(guid);

	/// <summary>Ist die GC-Mod mit dieser ID geladen (z. B. "tiere")?</summary>
	public static bool ModLoaded(string id) => IsLoaded(GuidPrefix + id);

	/// <summary>Höchste Stufe der hier geladenen GC-Mods.</summary>
	public static int HighestLocalStufe => modules.Count == 0 ? 0 : modules.Max(m => m.Info.Stufe);

	/// <summary>Stufe, die der verbundene Server für Spieler-Mods vorgibt (6 = keine Grenze).</summary>
	public static int ServerStufe
	{
		get
		{
			ZNet? net = ZNet.instance;
			if (net == null || net.IsServer() || Server == null)
			{
				return 6;
			}
			int set = StufeSetting?.Value ?? 0;
			return set > 0 ? set : Math.Max(1, Server.Stufe); // Stufe 1 (nur Anzeige) gilt immer
		}
	}

	internal static bool Allowed(GcModInfo info)
	{
		if (info.Seite == GcSeite.Alle || info.Stufe <= 0)
		{
			return true;
		}
		if (info.Seite == GcSeite.ServerOptional && ServerHas(info.Guid))
		{
			return true; // hat der Server die Mod, gilt sie, egal welche Stufe er vorgibt
		}
		return info.Stufe <= ServerStufe;
	}

	internal static bool ServerHas(string guid)
	{
		ZNet? net = ZNet.instance;
		if (net == null)
		{
			return false;
		}
		if (net.IsServer())
		{
			return IsLoaded(guid);
		}
		return Server != null && Server.Guids.Contains(guid);
	}

	public static string StufeName(int stufe) => GcLoc.Get("gck_stufe" + stufe);

	public static string SeiteText(GcSeite seite) => seite switch
	{
		GcSeite.Spieler => "nur Spieler",
		GcSeite.ServerOptional => "Spieler, Server optional",
		_ => "Server und alle Spieler",
	};

	/// <summary>Abschnitt der Stufe im Menü (0 = Kern).</summary>
	internal static GcMenuSection Section(int stufe)
	{
		if (!sections.TryGetValue(stufe, out GcMenuSection section))
		{
			string id = stufe == 0 ? "gc_kern" : "gc_stufe" + stufe;
			int order = stufe == 0 ? 90 : stufe * 10;
			section = KernPlugin.Menu.Section(id, order, key => GcLoc.Get(stufe == 0 ? "gck_kern_" + key : "gck_stufe" + stufe + "_" + key), "");
			sections[stufe] = section;
		}
		return section;
	}

	internal static GcMenuGroup MenuGroup(GcModule module, Func<string, string> text, string? ownTitleKey = null)
	{
		GcModInfo info = module.Info;
		string titleKey = ownTitleKey ?? "gck_mod_" + info.Id;
		Func<string> title = () =>
		{
			string t = GcLoc.Get(titleKey);
			return t == titleKey ? info.Name : t;
		};
		return Section(info.Stufe).Group(titleKey, title, text, info.Guid, () => module.Allowed);
	}
}

/// <summary>Liest eine BepInEx-cfg (INI) ohne sie zu laden: [Abschnitt] und Schlüssel = Wert.</summary>
internal static class GcCfgFile
{
	public static string Key(string section, string key) => section.Trim() + "\u0001" + key.Trim();

	public static Dictionary<string, string> Read(string path)
	{
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		string section = "";
		foreach (string raw in File.ReadAllLines(path))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
			{
				continue;
			}
			if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
			{
				section = line.Substring(1, line.Length - 2).Trim();
				continue;
			}
			int equals = line.IndexOf('=');
			if (equals <= 0)
			{
				continue;
			}
			values[Key(section, line.Substring(0, equals))] = line.Substring(equals + 1).Trim();
		}
		return values;
	}
}
