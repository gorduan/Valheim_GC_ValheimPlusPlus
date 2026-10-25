using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Shared;

/// <summary>
/// Einstellungen aller übrigen Mods (Gorduan, 25.10.2026: „nicht nur unsere Mods-Einstellungen …, sondern alle, jeweils getrennt,
/// client- und serverseitig (falls Admin)“). Je Mod ein zugeklappter Block, darin „Für dich“ und „Server-Regeln“ und
/// darunter die Abschnitte der Mod wie in ihrer .cfg (oder ihre ConfigurationManager-Kategorie).
///
/// Server-Regeln erkennt das Menü an Jötunns IsAdminOnly und an ServerSync (ein SyncedConfigEntry in den Tags). Was ein
/// Admin dort ändert, schickt Jötunn (SyncServer) bzw. ServerSync selbst (eigener SettingChanged-Handler, der Server prüft
/// die Adminliste) an den Server. Mods ohne Abgleich gelten nur auf diesem PC; ihre Werte auf einem Server sieht man hier
/// nicht. Gefunden werden Einstellungen wie in Mod Menu: plugin.Config, ConfigFiles in Feldern des Plugins und statische
/// ConfigFiles der Mod (ohne statische Konstruktoren auszulösen), aber nie die Config eines anderen Plugins. Versteckte
/// Einträge (ConfigurationManagerAttributes.Browsable = false) bleiben weg. Die Zeilen eines Blocks entstehen erst beim
/// Aufklappen oder wenn die Suche darin etwas findet.
/// </summary>
public sealed partial class GcMenu
{
	private enum SyncKind
	{
		Local,
		Jotunn,
		ServerSync,
	}

	private sealed class ForeignEntry
	{
		public ConfigEntryBase Entry = null!;
		public string Name = "";
		public string Section = "";
		public string Description = "";
		public SyncKind Sync;
		public Func<bool>? ReadOnlyCheck;
		public bool HideDefault;
		public bool Advanced;
		public int Order;
		public int Index;
		public string Search = "";
	}

	private sealed class ForeignGroup
	{
		public GameObject Header = null!;
		public readonly List<Row> Rows = new();
	}

	private sealed class ForeignMod
	{
		public string Guid = "";
		public string Name = "";
		public string Version = "";
		/// <summary>Mindestens eine Einstellung kommt vom Server (sonst „nur dieser PC“).</summary>
		public bool HasSync;
		public readonly List<ForeignEntry> Entries = new();
		public string Search = "";
		public GameObject Header = null!;
		public Text HeaderLabel = null!;
		public GameObject Body = null!;
		public bool Built;
		public readonly List<Row> Rows = new();
		public GameObject? ClientHeader;
		public GameObject? ServerHeader;
		public readonly List<ForeignGroup> ClientGroups = new();
		public readonly List<ForeignGroup> ServerGroups = new();
	}

	private static readonly Color SubHeaderColor = new(1f, 0.75f, 0.4f);
	private static readonly Color SectionColor = new(0.9f, 0.82f, 0.66f);

	private readonly List<ForeignMod> foreignMods = new();
	private GameObject? foreignHeader;

	/// <summary>Aufgeklappte Mods (GUID), bis zum Spielende gemerkt.</summary>
	private static readonly HashSet<string> ExpandedMods = new(StringComparer.Ordinal);

	private void BuildForeign(HashSet<ConfigEntryBase> listed)
	{
		List<ForeignMod> mods;
		try
		{
			mods = DiscoverForeign(listed);
		}
		catch (Exception e)
		{
			LogError("Andere Mods", e);
			return;
		}
		if (mods.Count == 0)
		{
			return;
		}
		foreignHeader = Header(content!, ui("other_mods"), 28, GUIManager.Instance.ValheimOrange, true, 46f);
		foreach (ForeignMod mod in mods)
		{
			CreateModBlock(mod);
			foreignMods.Add(mod);
		}
	}

	/// <summary>ValheimPlus steht mit seinen Sperren schon oben (VPlusOptions); die GC-Mods stehen oben.</summary>
	private const string VPlusGuid = "org.bepinex.plugins.valheim_plus";

	private static bool ShownAbove(string guid) =>
		guid.StartsWith("gorduan.gc", StringComparison.OrdinalIgnoreCase) || string.Equals(guid, VPlusGuid, StringComparison.OrdinalIgnoreCase);

	/// <summary>Anzahl aller Einträge der Plugin-Configs: ändert sie sich, legt Show das Fenster neu an (spät angelegte Einstellungen).</summary>
	private static int ForeignCount()
	{
		int count = 0;
		foreach (PluginInfo info in Chainloader.PluginInfos.Values)
		{
			if (info.Instance != null)
			{
				count += info.Instance.Config.Count;
			}
		}
		return count;
	}

	private static List<ForeignMod> DiscoverForeign(HashSet<ConfigEntryBase> listed)
	{
		List<PluginInfo> plugins = Chainloader.PluginInfos.Values.Where(info => info.Instance != null).ToList();
		HashSet<ConfigFile> pluginConfigs = new(plugins.Select(info => info.Instance.Config));
		List<ForeignMod> result = new();
		foreach (PluginInfo info in plugins)
		{
			if (ShownAbove(info.Metadata.GUID))
			{
				continue; // sonst stünde ValheimPlus ein zweites Mal da, ohne „nur im Hauptmenü“ (Code-Prüfung 25.10.2026)
			}
			ForeignMod mod = new() { Guid = info.Metadata.GUID, Name = info.Metadata.Name, Version = info.Metadata.Version.ToString() };
			int index = 0;
			foreach (ConfigFile file in ConfigFiles(info.Instance, pluginConfigs))
			{
				foreach (ConfigEntryBase entry in file.Select(pair => pair.Value).ToList())
				{
					index++;
					if (entry == null || listed.Contains(entry) || Attr(entry, "Browsable") is false)
					{
						continue; // schon bei den GC-Mods oder von der Mod versteckt
					}
					listed.Add(entry);
					mod.Entries.Add(Describe(entry, index, mod.Name));
				}
			}
			if (mod.Entries.Count == 0)
			{
				continue;
			}
			mod.HasSync = mod.Entries.Any(e => e.Sync != SyncKind.Local);
			mod.Search = SearchText(mod.Name, mod.Guid);
			result.Add(mod);
		}
		result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		return result;
	}

	/// <summary>
	/// Config-Dateien einer Mod: ihre Config, ConfigFiles in Feldern des Plugins und statische ConfigFiles in Klassen ohne
	/// statischen Konstruktor (das Lesen löst dann nichts aus). Die Config eines anderen Plugins (mehrere Plugins in einer
	/// DLL) gehört nicht dazu.
	/// </summary>
	private static IEnumerable<ConfigFile> ConfigFiles(BaseUnityPlugin plugin, HashSet<ConfigFile> pluginConfigs)
	{
		List<ConfigFile> files = new() { plugin.Config };
		void Add(object? value)
		{
			if (value is ConfigFile file && !files.Contains(file) && !pluginConfigs.Contains(file))
			{
				files.Add(file);
			}
		}
		const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		try
		{
			for (Type? t = plugin.GetType(); t != null && t != typeof(BaseUnityPlugin); t = t.BaseType)
			{
				foreach (FieldInfo field in t.GetFields(any | BindingFlags.Instance))
				{
					if (typeof(ConfigFile).IsAssignableFrom(field.FieldType))
					{
						Add(field.GetValue(plugin));
					}
				}
			}
			foreach (Type type in TypesOf(plugin.GetType().Assembly))
			{
				if (type.ContainsGenericParameters || type.TypeInitializer != null)
				{
					continue;
				}
				foreach (FieldInfo field in type.GetFields(any | BindingFlags.Static))
				{
					if (!field.IsLiteral && typeof(ConfigFile).IsAssignableFrom(field.FieldType))
					{
						Add(field.GetValue(null));
					}
				}
			}
		}
		catch
		{
			// nur was bis hier gefunden wurde
		}
		return files;
	}

	/// <summary>
	/// Typen einer Mod ohne Lärm im Log: Fehlt eine optionale Abhängigkeit (z. B. PlanBuild ohne AzuCraftyBoxes), liefert
	/// GetTypes die übrigen. AccessTools.GetTypesFromAssembly schrieb dabei jedes Mal eine lange Warnung (Test 25.10.2026).
	/// </summary>
	private static IEnumerable<Type> TypesOf(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException e)
		{
			return e.Types.Where(type => type != null);
		}
		catch
		{
			return Array.Empty<Type>();
		}
	}

	private static ForeignEntry Describe(ConfigEntryBase entry, int index, string modName)
	{
		ForeignEntry fe = new()
		{
			Entry = entry,
			Index = index,
			Name = Attr(entry, "DispName") is string name && name.Trim().Length > 0 ? name.Trim() : entry.Definition.Key,
			Section = Attr(entry, "Category") is string category && category.Trim().Length > 0 ? category.Trim() : entry.Definition.Section,
			Description = Attr(entry, "Description") is string text && text.Trim().Length > 0 ? text.Trim() : (entry.Description?.Description ?? "").Trim(),
			Sync = SyncOf(entry),
			Order = Attr(entry, "Order") is int order ? order : 0,
			HideDefault = Attr(entry, "HideDefaultButton") is true,
			Advanced = Attr(entry, "IsAdvanced") is true,
		};
		// Jötunn und ServerSync setzen ReadOnly selbst je nach Rechten; dafür gilt hier die Admin-Regel des Menüs.
		// Bei den übrigen wird ReadOnly jedes Mal gelesen, Mods ändern es zur Laufzeit.
		fe.ReadOnlyCheck = fe.Sync == SyncKind.Local ? AttrCheck(entry, "ReadOnly") : null;
		fe.Search = SearchText(fe.Name, fe.Description, fe.Section, entry.Definition.Key, modName);
		return fe;
	}

	/// <summary>Wert aus einer ConfigurationManagerAttributes-Klasse in den Tags (jede Mod hat ihre eigene Kopie).</summary>
	private static object? Attr(ConfigEntryBase entry, string name)
	{
		object[]? tags = entry.Description?.Tags;
		if (tags == null)
		{
			return null;
		}
		foreach (object tag in tags)
		{
			if (tag != null && tag.GetType().Name == "ConfigurationManagerAttributes" && Member(tag, name) is object value)
			{
				return value;
			}
		}
		return null;
	}

	/// <summary>Schalter in einer ConfigurationManagerAttributes-Klasse, der sich zur Laufzeit ändern kann (null = gibt es nicht).</summary>
	private static Func<bool>? AttrCheck(ConfigEntryBase entry, string name)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		foreach (object tag in entry.Description?.Tags ?? Array.Empty<object>())
		{
			if (tag == null || tag.GetType().Name != "ConfigurationManagerAttributes")
			{
				continue;
			}
			FieldInfo? field = tag.GetType().GetField(name, flags);
			if (field != null)
			{
				return () => field.GetValue(tag) is true;
			}
			PropertyInfo? property = tag.GetType().GetProperty(name, flags);
			if (property != null)
			{
				return () => property.GetValue(tag) is true;
			}
		}
		return null;
	}

	private static object? Member(object target, string name)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		try
		{
			Type type = target.GetType();
			FieldInfo? field = type.GetField(name, flags);
			return field != null ? field.GetValue(target) : type.GetProperty(name, flags)?.GetValue(target);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>ServerSync hängt an jede abgeglichene Einstellung ein SyncedConfigEntry an die Tags (SynchronizedConfig).</summary>
	private static object? ServerSyncTag(ConfigEntryBase entry)
	{
		object[]? tags = entry.Description?.Tags;
		return tags?.FirstOrDefault(tag => tag != null && tag.GetType().Name.StartsWith("SyncedConfigEntry", StringComparison.Ordinal));
	}

	private static SyncKind SyncOf(ConfigEntryBase entry)
	{
		object? synced = ServerSyncTag(entry);
		if (synced != null)
		{
			return Member(synced, "SynchronizedConfig") is false ? SyncKind.Local : SyncKind.ServerSync;
		}
		return IsServerEntry(entry) ? SyncKind.Jotunn : SyncKind.Local;
	}

	/// <summary>Hat der Server die fremde Mod? Eigene Welt immer; sonst Liste des Servers (GC-Kern), Jötunn oder ein Wert, der von ServerSync kam.</summary>
	private static bool ForeignOnServer(Row row)
	{
		ZNet? net = ZNet.instance;
		if (net == null)
		{
			return false;
		}
		if (net.IsServer())
		{
			return true;
		}
		if (GcKern.ServerHas(row.Guid) || ModOnServer(row.Guid))
		{
			return true;
		}
		return row.SyncTag != null && LocalBaseValue(row.SyncTag) != null; // der Wert kommt gerade vom Server
	}

	private static readonly Dictionary<Type, FieldInfo?> LocalBaseFields = new();

	/// <summary>ServerSync: Wert vor dem Server-Wert (null = der Server hat nichts geschickt). Feld je Typ zwischengespeichert.</summary>
	private static object? LocalBaseValue(object tag)
	{
		Type type = tag.GetType();
		if (!LocalBaseFields.TryGetValue(type, out FieldInfo? field))
		{
			field = type.GetField("LocalBaseValue", BindingFlags.Public | BindingFlags.Instance);
			LocalBaseFields[type] = field;
		}
		try
		{
			return field?.GetValue(tag);
		}
		catch
		{
			return null;
		}
	}

	private void CreateModBlock(ForeignMod mod)
	{
		GameObject header = new("Mod", typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(Button));
		header.transform.SetParent(content!, false);
		LayoutElement layout = header.GetComponent<LayoutElement>();
		layout.minHeight = layout.preferredHeight = 40f;
		Image background = header.GetComponent<Image>();
		background.color = new Color(0f, 0f, 0f, 0.3f);
		Button button = header.GetComponent<Button>();
		button.targetGraphic = background;
		ColorBlock colors = button.colors;
		colors.highlightedColor = new Color(1f, 0.8f, 0.55f, 1f);
		colors.pressedColor = new Color(1f, 0.7f, 0.4f, 1f);
		button.colors = colors;
		button.onClick.AddListener(() => ToggleMod(mod));
		mod.HeaderLabel = GcUi.CreateLabel(header.transform, "", Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-14f, 0f), 20, TextAnchor.MiddleLeft, Color.white);
		mod.HeaderLabel.raycastTarget = false;
		Text note = GcUi.CreateLabel(header.transform, mod.HasSync ? "" : ui("local_only"), Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-14f, 0f), 14, TextAnchor.MiddleRight, MutedColor);
		note.raycastTarget = false;
		mod.Header = header;

		GameObject body = new("ModBody", typeof(RectTransform), typeof(VerticalLayoutGroup));
		body.transform.SetParent(content!, false);
		VerticalLayoutGroup column = body.GetComponent<VerticalLayoutGroup>();
		column.childControlWidth = true;
		column.childControlHeight = true;
		column.childForceExpandWidth = true;
		column.childForceExpandHeight = false;
		column.spacing = 4f;
		column.padding = new RectOffset(18, 0, 2, 10);
		body.SetActive(false);
		mod.Body = body;
	}

	private void ToggleMod(ForeignMod mod)
	{
		if (CancelCapture())
		{
			return;
		}
		if (mod.Body.activeSelf && !ExpandedMods.Contains(mod.Guid))
		{
			searchClosed.Add(mod.Guid); // von der Suche aufgeklappt: für diese Suche zu
		}
		else if (!ExpandedMods.Remove(mod.Guid))
		{
			ExpandedMods.Add(mod.Guid);
		}
		Refresh();
	}

	/// <summary>Blöcke, die die Suche aufgeklappt und der Spieler wieder zugeklappt hat (gilt bis zur nächsten Suche).</summary>
	private readonly HashSet<string> searchClosed = new(StringComparer.Ordinal);

	/// <summary>Höchstens so viele Blöcke klappt die Suche selbst auf; die übrigen Treffer zeigen ihre Anzahl im Kopf.</summary>
	private const int AutoOpenLimit = 6;

	/// <summary>Erst ab so vielen Zeichen sucht das Menü in den Einstellungen fremder Mods (sonst baut „en“ fast jeden Block auf).</summary>
	private const int DeepSearchLength = 3;

	/// <summary>Zeilen eines Blocks: erst „Für dich“, dann „Server-Regeln“, darin die Abschnitte der Mod (alphabetisch).</summary>
	private void BuildModRows(ForeignMod mod)
	{
		mod.Built = true;
		List<ForeignEntry> own = mod.Entries.Where(e => e.Sync == SyncKind.Local).ToList();
		List<ForeignEntry> server = mod.Entries.Where(e => e.Sync != SyncKind.Local).ToList();
		if (own.Count > 0)
		{
			mod.ClientHeader = Header(mod.Body.transform, ui("for_you"), 22, SubHeaderColor, false, 34f);
			AddGroups(mod, own, mod.ClientGroups);
		}
		if (server.Count > 0)
		{
			mod.ServerHeader = Header(mod.Body.transform, ui("server_rules"), 22, SubHeaderColor, false, 34f);
			AddGroups(mod, server, mod.ServerGroups);
		}
	}

	private void AddGroups(ForeignMod mod, List<ForeignEntry> entries, List<ForeignGroup> groups)
	{
		foreach (IGrouping<string, ForeignEntry> section in entries.GroupBy(e => e.Section).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
		{
			ForeignGroup group = new() { Header = Header(mod.Body.transform, section.Key, 17, SectionColor, false, 28f) };
			groups.Add(group);
			foreach (ForeignEntry fe in section.OrderByDescending(e => e.Order).ThenBy(e => e.Index))
			{
				Row? row = CreateRow(ForeignItem(fe), mod.Guid, mod.Body.transform, fe);
				if (row == null)
				{
					continue;
				}
				row.Mod = mod;
				row.Search = fe.Search;
				string description = fe.Description;
				if (fe.Advanced)
				{
					description += (description.Length > 0 ? " " : "") + ui("advanced");
				}
				if (!row.ReadOnly)
				{
					description += (description.Length > 0 ? " " : "") + ui("default_value").Replace("$1", FormatValue(row, fe.Entry.DefaultValue));
				}
				row.BaseDescription = description;
				row.Description.text = description;
				row.Editable = ComputeEditable(row); // gleich richtig, nicht erst beim nächsten Refresh (sonst kurz ausgegraut)
				rows.Add(row);
				mod.Rows.Add(row);
				group.Rows.Add(row);
			}
		}
	}

	private static Dictionary<string, object> ForeignItem(ForeignEntry fe) => new()
	{
		["key"] = fe.Entry.Definition.Key,
		["entry"] = fe.Entry,
		["name"] = (Func<string>)(() => fe.Name),
		["desc"] = (Func<string>)(() => fe.Description),
		["level"] = 0,
		["unit"] = "",
	};

	/// <summary>
	/// Sichtbarkeit der fremden Mods. Ohne Suche: Kopf, wenn die Mod etwas zu zeigen hat (Server-Regeln nur mit Kästchen),
	/// Zeilen nur aufgeklappt. Mit Suche: Treffer im Namen der Mod zeigen ihren Kopf, Treffer in Einstellungen klappen sie
	/// auf und zeigen nur die Treffer.
	/// </summary>
	private bool RefreshForeign(bool server)
	{
		bool any = false;
		bool searching = query.Length > 0;
		bool deep = query.Length >= DeepSearchLength;
		int autoOpened = 0;
		foreach (ForeignMod mod in foreignMods)
		{
			bool modMatch = searching && mod.Search.Contains(query);
			bool anyShown = false;
			int hits = 0;
			foreach (ForeignEntry entry in mod.Entries)
			{
				if (entry.Sync != SyncKind.Local && !server)
				{
					continue;
				}
				anyShown = true;
				if (!deep)
				{
					break;
				}
				if (entry.Search.Contains(query))
				{
					hits++;
				}
			}
			bool entryMatch = hits > 0;
			bool headerShown = anyShown && (!searching || modMatch || entryMatch);
			bool expanded = ExpandedMods.Contains(mod.Guid);
			bool autoOpen = headerShown && entryMatch && !expanded && !searchClosed.Contains(mod.Guid) && autoOpened < AutoOpenLimit;
			if (autoOpen)
			{
				autoOpened++;
			}
			bool open = headerShown && (expanded || autoOpen);
			bool filter = searching && !modMatch;
			if (open && !mod.Built)
			{
				BuildModRows(mod);
			}
			SetActive(mod.Header, headerShown);
			SetActive(mod.Body, open);
			string label = (open ? "− " : "+ ") + mod.Name + "  " + mod.Version + (entryMatch ? "  ·  " + ui("hits").Replace("$1", hits.ToString()) : "");
			if (mod.HeaderLabel.text != label)
			{
				mod.HeaderLabel.text = label;
			}
			if (mod.Built)
			{
				foreach (Row row in mod.Rows)
				{
					row.Visible = open && (!row.Server || server) && (!filter || row.Search.Contains(query));
					SetActive(row.Root, row.Visible);
				}
				UpdateGroups(mod.ClientHeader, mod.ClientGroups);
				UpdateGroups(mod.ServerHeader, mod.ServerGroups);
			}
			any |= headerShown;
		}
		if (foreignHeader != null)
		{
			SetActive(foreignHeader, any);
		}
		return any;
	}

	private static void UpdateGroups(GameObject? header, List<ForeignGroup> groups)
	{
		bool anyVisible = false;
		foreach (ForeignGroup group in groups)
		{
			bool visible = group.Rows.Any(r => r.Visible);
			SetActive(group.Header, visible);
			anyVisible |= visible;
		}
		if (header != null)
		{
			SetActive(header, anyVisible);
		}
	}
}
