using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace GC.Shared;

/// <summary>
/// Texte aller GC-Mods (Englisch als Standard, Deutsch eingebettet), gemeinsam im Kern.
/// Jede Mod bringt ihre Texte als eingebettete translations/*.&lt;Sprache&gt;.yml mit; der Kern liest sie beim Anmelden
/// (GcKern.Register). Eine Datei BepInEx/config/&lt;Modname&gt;.&lt;Sprache&gt;.yml überschreibt einzelne Texte.
/// Platzhalter $1, $2 … werden durch die Argumente ersetzt.
/// </summary>
public static class GcLoc
{
	private static readonly Dictionary<string, Dictionary<string, string>> Texts = new(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<Assembly> loadedAssemblies = new();
	private static ManualLogSource? log;

	/// <summary>Kern: eigene Texte und die Überschreib-Dateien der alten Sammelpakete (GC_Server, GC_Client, ValheimPlusPlus).</summary>
	public static void Init(string name, ManualLogSource logger)
	{
		log = logger;
		AddAssembly(typeof(GcLoc).Assembly, name, logger);
		foreach (string old in new[] { "GC_Server", "GC_Client", "ValheimPlusPlus" })
		{
			LoadExternal(old);
		}
	}

	/// <summary>Eingebettete Texte einer Mod und ihre Überschreib-Datei im config-Ordner.</summary>
	public static void AddAssembly(Assembly asm, string modName, ManualLogSource? logger = null)
	{
		log ??= logger;
		lock (loadedAssemblies)
		{
			if (!loadedAssemblies.Add(asm))
			{
				return;
			}
		}
		foreach (string res in asm.GetManifestResourceNames().Where(r => r.Contains(".translations.") && r.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
		{
			string withoutExt = res.Substring(0, res.Length - 4);
			string language = withoutExt.Substring(withoutExt.LastIndexOf('.') + 1);
			using Stream? stream = asm.GetManifestResourceStream(res);
			if (stream == null)
			{
				continue;
			}
			using StreamReader reader = new(stream, Encoding.UTF8);
			Merge(language, Parse(reader.ReadToEnd()));
		}
		LoadExternal(modName);
	}

	private static void LoadExternal(string modName)
	{
		if (modName.Length == 0)
		{
			return;
		}
		try
		{
			foreach (string file in Directory.GetFiles(Paths.ConfigPath, $"{modName}.*.yml", SearchOption.TopDirectoryOnly))
			{
				string[] parts = Path.GetFileNameWithoutExtension(file).Split('.');
				if (parts.Length < 2)
				{
					continue;
				}
				Merge(parts[parts.Length - 1], Parse(File.ReadAllText(file, Encoding.UTF8)));
				log?.LogInfo($"Übersetzung geladen: {file}");
			}
		}
		catch (Exception e)
		{
			log?.LogWarning($"Externe Übersetzungen konnten nicht gelesen werden: {e.Message}");
		}
	}

	private static void Merge(string language, Dictionary<string, string> entries)
	{
		lock (Texts)
		{
			if (!Texts.TryGetValue(language, out Dictionary<string, string> dict))
			{
				Texts[language] = dict = new Dictionary<string, string>();
			}
			foreach (KeyValuePair<string, string> kv in entries)
			{
				dict[kv.Key] = kv.Value;
			}
		}
	}

	private static Dictionary<string, string> Parse(string content)
	{
		Dictionary<string, string> result = new();
		foreach (string raw in content.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
			{
				continue;
			}
			int colon = line.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}
			string key = line.Substring(0, colon).Trim();
			string value = line.Substring(colon + 1).Trim();
			if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
			{
				value = value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n");
			}
			result[key] = value;
		}
		return result;
	}

	private static string CurrentLanguage()
	{
		try
		{
			return Localization.instance?.GetSelectedLanguage() ?? "English";
		}
		catch
		{
			return "English";
		}
	}

	/// <summary>Alle Sprachen, in denen der Schlüssel übersetzt ist (für Texte, die Valheim selbst übersetzen soll, z. B. Item-Namen).</summary>
	public static IEnumerable<KeyValuePair<string, string>> AllLanguages(string key)
	{
		List<KeyValuePair<string, string>> found = new();
		lock (Texts)
		{
			foreach (KeyValuePair<string, Dictionary<string, string>> language in Texts)
			{
				if (language.Value.TryGetValue(key, out string text))
				{
					found.Add(new KeyValuePair<string, string>(language.Key, text));
				}
			}
		}
		return found;
	}

	public static string Get(string key, params object?[] args)
	{
		string? text = null;
		lock (Texts)
		{
			if (Texts.TryGetValue(CurrentLanguage(), out Dictionary<string, string> dict))
			{
				dict.TryGetValue(key, out text);
			}
			if (text == null && Texts.TryGetValue("English", out Dictionary<string, string> english))
			{
				english.TryGetValue(key, out text);
			}
		}
		text ??= key;

		for (int i = args.Length - 1; i >= 0; i--)
		{
			text = text.Replace("$" + (i + 1), args[i]?.ToString() ?? "");
		}
		return text;
	}
}
