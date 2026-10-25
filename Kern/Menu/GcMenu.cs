using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GC.Shared;

/// <summary>Abschnitt einer Mod im Einstellungsmenü. Texte kommen aus der Mod selbst (Schlüssel ohne Präfix, z. B. "title").</summary>
public sealed class GcMenuSection
{
	private readonly List<object> groups;
	private readonly Func<string, string> text;

	internal GcMenuSection(Dictionary<string, object> data, Func<string, string> text)
	{
		groups = (List<object>)data["groups"];
		this.text = text;
	}

	/// <summary>Gruppe mit Überschrift (Text "group_&lt;id&gt;").</summary>
	public GcMenuGroup Group(string id)
	{
		List<object> items = new();
		Func<string, string> t = text;
		groups.Add(new Dictionary<string, object> { ["id"] = id, ["title"] = (Func<string>)(() => t("group_" + id)), ["items"] = items });
		return new GcMenuGroup(items, text);
	}

	/// <summary>
	/// Gruppe einer einzelnen Mod (GC-SDK): eigene Überschrift, eigene Texte, eigene GUID (Prüfung „Server hat die Mod“)
	/// und eigene Freigabe (Stufe des Servers).
	/// </summary>
	public GcMenuGroup Group(string id, Func<string> title, Func<string, string> groupText, string guid, Func<bool>? allowed = null)
	{
		List<object> items = new();
		Dictionary<string, object> group = new() { ["id"] = id, ["title"] = title, ["items"] = items, ["guid"] = guid };
		if (allowed != null)
		{
			group["allowed"] = allowed;
		}
		groups.Add(group);
		return new GcMenuGroup(items, groupText);
	}
}

/// <summary>Einträge einer Gruppe: Funktionen (Main) und eingerückte Unteroptionen (Sub). Texte "t_&lt;key&gt;_name" und "t_&lt;key&gt;_desc".</summary>
public sealed class GcMenuGroup
{
	private readonly List<object> items;
	private readonly Func<string, string> text;

	internal GcMenuGroup(List<object> items, Func<string, string> text)
	{
		this.items = items;
		this.text = text;
	}

	/// <param name="requires">Zeile nur zeigen, wenn das zutrifft (z. B. eine andere Mod ist installiert).</param>
	/// <param name="locked">Gerade nicht änderbar, aber sichtbar (z. B. ValheimPlus in einer Welt).</param>
	public GcMenuGroup Main(string key, ConfigEntryBase entry, Func<bool>? blocked = null, string oldMod = "", Func<string>? note = null, double step = 0, string unit = "", Func<bool>? requires = null, Func<bool>? locked = null)
	{
		return Add(key, entry, 0, blocked, oldMod, note, step, unit, requires, locked);
	}

	public GcMenuGroup Sub(string key, ConfigEntryBase entry, double step = 0, string unit = "", Func<bool>? blocked = null, string oldMod = "", Func<bool>? requires = null, Func<bool>? locked = null)
	{
		return Add(key, entry, 1, blocked, oldMod, null, step, unit, requires, locked);
	}

	private GcMenuGroup Add(string key, ConfigEntryBase entry, int level, Func<bool>? blocked, string oldMod, Func<string>? note, double step, string unit, Func<bool>? requires, Func<bool>? locked)
	{
		Func<string, string> t = text;
		Dictionary<string, object> item = new()
		{
			["key"] = key,
			["entry"] = entry,
			["name"] = (Func<string>)(() => t($"t_{key}_name")),
			["desc"] = (Func<string>)(() => t($"t_{key}_desc")),
			["enumText"] = (Func<string, string>)(value => t("enum_" + value)),
			["level"] = level,
			["step"] = step,
			["unit"] = unit,
			["oldMod"] = oldMod,
		};
		if (blocked != null)
		{
			item["blocked"] = blocked;
		}
		if (note != null)
		{
			item["note"] = note;
		}
		if (requires != null)
		{
			item["requires"] = requires;
		}
		if (locked != null)
		{
			item["locked"] = locked;
		}
		items.Add(item);
		return this;
	}
}

/// <summary>
/// „GC Option": gemeinsames Einstellungsmenü der GC-Mods (GC_Client, ValheimPlusPlus, GC_Server, GC_Guilds,
/// GC_ProximityVoice).
///
/// Jede Mod trägt ihre Abschnitte in eine Liste im AppDomain ein. Weil jede Mod ihre eigene Kopie dieses Codes
/// kompiliert, enthält die Liste nur Typen, die alle kennen (BCL, BepInEx, Unity): Dictionary&lt;string, object&gt;, Func,
/// ConfigEntryBase. Gelesen wird sie nur mit Typprüfung, ein fremder oder alter Eintrag wird übersprungen. Die erste
/// Mod, die ein Pause-Menü sieht und Knöpfe anlegen darf, legt den Knopf „GC Option" an und zeigt alle eingetragenen
/// Abschnitte. ValheimPlusPlus legt keinen Knopf an: allein mit ValheimPlus wird es nur über seine Config-Datei
/// eingestellt, mit einer anderen GC-Mod steht es mit im Menü.
///
/// Wer was sieht und ändern darf (Gorduan, 25.10.2026):
///  * Eigene Einstellungen (ohne Server-Abgleich): immer sichtbar und änderbar.
///  * Server-Regeln (Jötunn IsAdminOnly, bei fremden Mods auch ServerSync): nur mit dem Kästchen „Server-Regeln anzeigen“
///    (für alle; bleibt bis zum Spielende, beim Spielstart aus). Ändern darf sie in einer Welt nur ein Admin des Servers,
///    in einer lokalen Welt der Besitzer und wen er in die adminlist einträgt, und nur, wenn der Server die Mod hat. Im
///    Hauptmenü ist es deine eigene Config: Sie gilt, wenn du selbst eine Welt hostest.
///  * Blockiert (alte Einzelmod noch installiert): nie, auch nicht die Unteroptionen der blockierten Funktion.
/// Eine GC-Funktion, deren Schalter eine Server-Regel ist, bleibt als ausgegraute Überschrift sichtbar, wenn darunter
/// eigene Einstellungen stehen. Unter den GC-Abschnitten stehen alle übrigen Mods mit ihren Einstellungen (zugeklappt,
/// je Mod getrennt in „Für dich“ und „Server-Regeln“). Das Suchfeld filtert nach Name, Beschreibung, Abschnitt und Mod.
/// Das Fenster richtet sich nach der Bildschirmgröße; ist es breit genug, steht die Beschreibung neben dem Namen.
///
/// Bedienung je nach Typ: bool An/Aus, Zahlen − Wert + (Grenzen aus AcceptableValueRange, der Wert ist auch ein
/// Eingabefeld), enum und AcceptableValueList Klick wechselt, KeyboardShortcut und KeyCode Klick und dann Taste drücken
/// (auch Maustasten 3 bis 7; Rechtsklick leert, Esc bricht ab), alles andere, was BepInEx als Text speichern kann
/// (Text, Listen, Farben, Zahlen ohne Grenzen), über ein Eingabefeld: Enter übernimmt, ein ungültiger Wert wird rot und
/// nicht übernommen. Zeilen wachsen mit langen Beschreibungen. Keine Taste zum Öffnen, nur der Knopf im Pause- und im
/// Hauptmenü; Esc oder Schließen führt dorthin zurück. Im Einzelspieler bleibt das Spiel pausiert, solange das Menü
/// offen ist.
/// </summary>
public sealed partial class GcMenu
{
	// v2: alte Zwischenstände (GC_Client 1.0.1) nutzen v1 und lesen weder GUID noch Rechte; sie sollen nicht mitlesen
	public const string SharedSlot = "GC.Options.Registry.v2";
	public const string ButtonName = "GC_OptionButton_v2";

	private sealed class Row
	{
		public Dictionary<string, object> Item = null!;
		public ConfigEntryBase Entry = null!;
		public string Guid = "";
		public Func<bool>? Allowed;
		public int Level;
		public bool Server;
		public GameObject Root = null!;
		public Row? Parent;
		public readonly List<Row> Subs = new();
		public Text Name = null!;
		public Text Description = null!;
		public Text Scope = null!;
		public string BaseDescription = "";
		public EditorKind Kind;
		public Button? Main;
		public Text? MainLabel;
		public Button? Minus;
		public Button? Plus;
		/// <summary>Eingabefeld (Zahlen, Text und alles, was BepInEx als Text speichern kann).</summary>
		public InputField? Input;
		/// <summary>Letzte Eingabe ungültig: rot und nicht übernommen, bis zur nächsten gültigen.</summary>
		public bool InputError;
		/// <summary>AcceptableValueList: ein Klick wechselt durch diese Werte.</summary>
		public object[]? Choices;
		/// <summary>Einstellung einer fremden Mod (nicht aus der Liste der GC-Mods).</summary>
		public bool Foreign;
		public ForeignMod? Mod;
		/// <summary>Wie die Server-Regel zum Server kommt (bei GC-Mods immer Jötunn).</summary>
		public SyncKind Sync;
		/// <summary>Nur lesen (ein Typ ohne Bedienung).</summary>
		public bool ReadOnly;
		/// <summary>ConfigurationManagerAttributes.ReadOnly einer fremden Mod, jedes Mal neu gelesen (Mods ändern es zur Laufzeit).</summary>
		public Func<bool>? ReadOnlyCheck;
		/// <summary>SyncedConfigEntry von ServerSync (einmal gesucht, Refresh fragt oft).</summary>
		public object? SyncTag;
		/// <summary>Suchtext in Kleinbuchstaben: Name, Beschreibung, Abschnitt, Mod.</summary>
		public string Search = "";
		/// <summary>Knopf „Standard“: erscheint, wenn der Wert vom Standard abweicht (aus Mod Menu übernommen).</summary>
		public Button? Reset;
		public bool HideDefault;
		public bool Editable;
		public bool Visible;
	}

	private sealed class Block
	{
		public GameObject Header = null!;
		public readonly List<Row> Rows = new();
		public readonly List<Block> Children = new();
	}

	private static readonly Color OnColor = new(0.45f, 0.95f, 0.45f);
	private static readonly Color OffColor = new(1f, 0.45f, 0.4f);
	private static readonly Color MutedColor = new(0.78f, 0.72f, 0.62f);
	private static readonly Color ValueColor = new(1f, 0.85f, 0.55f);
	private static readonly Color LockedColor = new(0.6f, 0.58f, 0.55f);
	private static readonly KeyCode[] Modifiers = { KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift };
	private static KeyCode[]? allKeys;
	private static MethodInfo? syncMethod;

	private readonly ManualLogSource log;
	private readonly Func<string, string> ui;
	private readonly bool canHost;
	private readonly List<object> registry;
	private readonly List<Dictionary<string, object>> ownSections = new();
	private readonly List<Row> rows = new();
	private readonly List<Block> sections = new();
	private GameObject? window;
	private RectTransform? content;
	private Toggle? showServer;
	/// <summary>Kästchen „Server-Regeln anzeigen“: bleibt bis zum Spielende, beim Spielstart aus (Gorduan, 25.10.2026).</summary>
	private static bool serverRulesShown;
	private InputField? searchField;
	private string query = "";
	private string pendingQuery = "";
	private float queryAt = -1f;
	private bool focusSearch;
	private InputField? typingField;
	private int typingFrame = -10;
	private Vector2 builtSize;
	private int builtForeign;
	private bool wide;
	private int closedFrame = -10;
	private int pendingUnblocks;

	/// <summary>Das Menü ist offen oder hat sich gerade in diesem Bild geschlossen (Valheims Startmenü soll die Taste nicht sehen).</summary>
	public static bool HoldsInput => KernPlugin.Menu != null && (KernPlugin.Menu.IsOpen || Time.frameCount <= KernPlugin.Menu.closedFrame + 1);
	private GameObject? emptyHint;
	private Text? emptyLabel;
	private Text? hintLabel;
	private Button? menuButton;
	private int builtShape = -1;
	private float nextRefresh;
	private Row? capturing;
	private KeyCode capturedModifier = KeyCode.None;
	private bool openedFromPause;
	private int captureFrame = -1;
	private string lastError = "";

	/// <param name="ui">Texte des Menüs selbst ("title", "on", "off", "close" ...).</param>
	/// <param name="canHost">false = nur eintragen, nie selbst den Knopf anlegen (ValheimPlusPlus).</param>
	public GcMenu(ManualLogSource log, Func<string, string> ui, bool canHost = true)
	{
		this.log = log;
		this.ui = ui;
		this.canHost = canHost;
		registry = SharedRegistry();
	}

	public bool IsOpen => window != null && window.activeSelf;

	/// <summary>Das Menü wurde geschlossen (z. B. damit ValheimPlus geänderte Werte neu anwendet).</summary>
	public event Action? Closed;

	/// <summary>Texte des gemeinsamen Menüs (gco_*), mit Vorrang für eigene Texte mit diesem Präfix.</summary>
	public static Func<string, string> UiText(string ownPrefix)
	{
		return name =>
		{
			if (ownPrefix.Length > 0)
			{
				string own = GcLoc.Get(ownPrefix + name);
				if (own != ownPrefix + name)
				{
					return own;
				}
			}
			return GcLoc.Get("gco_" + name);
		};
	}

	/// <summary>Bist du Admin auf dem Server, mit dem du verbunden bist (oder hostest du die Welt selbst)?</summary>
	public static bool CanChangeServer
	{
		get
		{
			if (ZNet.instance == null)
			{
				return false;
			}
			if (ZNet.instance.IsServer())
			{
				return true; // lokale Welt: der Besitzer
			}
			return SynchronizationManager.Instance != null && SynchronizationManager.Instance.PlayerIsAdmin;
		}
	}

	/// <summary>Server-Einstellung dieser Mod änderbar? Der Server muss die Mod haben, und du musst dort Admin sein.</summary>
	public static bool CanChangeServerSetting(string guid) => CanChangeServer && ModOnServer(guid);

	private static bool ModOnServer(string guid)
	{
		try
		{
			return guid.Length == 0 || ModCompatibility.IsModuleOnServer(guid);
		}
		catch
		{
			return false;
		}
	}

	private static List<object> SharedRegistry()
	{
		AppDomain domain = AppDomain.CurrentDomain;
		// Sperre über ein Objekt, das alle Mods teilen (jede Mod hat ihre eigene Kopie dieser Klasse)
		lock (typeof(object))
		{
			if (domain.GetData(SharedSlot) is not List<object> list)
			{
				list = new List<object>();
				domain.SetData(SharedSlot, list);
			}
			return list;
		}
	}

	/// <summary>Abschnitt einer Mod eintragen. order bestimmt die Reihenfolge, guid ist die BepInEx-GUID der Mod (Prüfung „Server hat die Mod").</summary>
	public GcMenuSection Section(string id, int order, Func<string, string> text, string guid)
	{
		Dictionary<string, object> data = new()
		{
			["id"] = id,
			["order"] = order,
			["guid"] = guid,
			["title"] = (Func<string>)(() => text("title")),
			["groups"] = new List<object>(),
		};
		lock (registry)
		{
			registry.RemoveAll(o => o is Dictionary<string, object> d && d.TryGetValue("id", out object v) && v is string s && s == id);
			registry.Add(data);
		}
		ownSections.Add(data);
		return new GcMenuSection(data, text);
	}

	/// <summary>Warnung ins Log für eigene Funktionen, die wegen einer noch installierten alten Einzelmod aus bleiben.</summary>
	public void LogBlocked()
	{
		foreach (Dictionary<string, object> section in ownSections)
		{
			foreach (Dictionary<string, object> group in Children(section, "groups"))
			{
				foreach (Dictionary<string, object> item in Children(group, "items"))
				{
					if (item.TryGetValue("blocked", out object b) && b is Func<bool> blocked && Call(blocked))
					{
						log.LogWarning($"Alte Einzelmod {Str(item, "oldMod")} ist noch installiert. Die Funktion '{Str(item, "key")}' bleibt aus, bis die alte Mod entfernt ist.");
					}
				}
			}
		}
	}

	// ------------------------------------------------------------------ Registry lesen (nur mit Typprüfung)

	private static List<Dictionary<string, object>> Children(Dictionary<string, object> data, string key)
	{
		if (data.TryGetValue(key, out object value) && value is List<object> list)
		{
			lock (list)
			{
				return list.OfType<Dictionary<string, object>>().ToList();
			}
		}
		return new List<Dictionary<string, object>>();
	}

	private static string Str(Dictionary<string, object> data, string key) => data.TryGetValue(key, out object value) && value is string s ? s : "";

	private static int Int(Dictionary<string, object> data, string key, int fallback) => data.TryGetValue(key, out object value) && value is int i ? i : fallback;

	private static string TextOf(Dictionary<string, object> data, string key)
	{
		try
		{
			return data.TryGetValue(key, out object value) && value is Func<string> f ? f() ?? "" : "";
		}
		catch
		{
			return "";
		}
	}

	private static bool Call(Func<bool> f)
	{
		try
		{
			return f();
		}
		catch
		{
			return false;
		}
	}

	private List<Dictionary<string, object>> Sections()
	{
		lock (registry)
		{
			return registry.OfType<Dictionary<string, object>>().OrderBy(s => Int(s, "order", 100)).ToList();
		}
	}

	private void LogError(string where, Exception e)
	{
		string message = where + ": " + e.Message;
		if (message != lastError)
		{
			lastError = message; // nicht jedes Frame dieselbe Meldung
			log.LogError($"Menü GC Option, {message}\n{e}");
		}
	}

	/// <summary>Jedes Frame aus dem Update der Mod aufrufen (Mods, die den Knopf anlegen dürfen; ohne canHost ohne Wirkung).</summary>
	public void Update()
	{
		if (Gc.IsHeadless || !canHost)
		{
			return;
		}
		while (pendingUnblocks > 0 && Time.frameCount > closedFrame)
		{
			pendingUnblocks--;
			GUIManager.BlockInput(false);
		}
		try
		{
			EnsurePauseButton();
			EnsureMainMenuButton();
			FitMainMenu();
			if (!IsOpen)
			{
				return;
			}
			if (capturing != null)
			{
				Capture();
				return;
			}
			if (focusSearch && searchField != null)
			{
				focusSearch = false;
				searchField.Select();
				searchField.ActivateInputField(); // gleich lostippen (Gorduan, 25.10.2026: Einstellungen schneller finden)
			}
			InputField? typing = Typing();
			if (typing != null)
			{
				typingField = typing;
				typingFrame = Time.frameCount;
			}
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				// Esc in einem Wert-Feld bricht nur die Eingabe ab (das Feld hat sie vielleicht schon in diesem Bild beendet);
				// im Suchfeld oder ohne Feld schließt es das Menü
				bool editing = Time.frameCount - typingFrame <= 1 && typingField != null && typingField != searchField && typingField != modsSearch;
				if (!editing)
				{
					Hide();
					return;
				}
			}
			if (queryAt >= 0f && Time.unscaledTime >= queryAt)
			{
				queryAt = -1f; // erst kurz nach dem letzten Tastendruck suchen, nicht bei jedem Buchstaben
				if (pendingQuery != query)
				{
					query = pendingQuery;
					searchClosed.Clear(); // neue Suche: wieder aufklappen
					Refresh();
				}
			}
			if (Time.unscaledTime >= nextRefresh)
			{
				nextRefresh = Time.unscaledTime + 0.5f; // Admin-Status und Server-Werte können sich ändern
				if (Shape() != builtShape)
				{
					RebuildOpen(); // eine Mod hat ihre Einträge geändert (z. B. ValheimPlus-Abschnitt an oder aus)
					return;
				}
				Refresh();
			}
		}
		catch (Exception e)
		{
			LogError("Update", e); // ein Fehler im Menü darf die übrigen Funktionen der Mod nicht anhalten
		}
	}

	/// <summary>Menü öffnen. false, wenn es nicht aufgebaut werden konnte.</summary>
	public bool Show()
	{
		if (IsOpen)
		{
			return true;
		}
		if (Gc.IsHeadless || GUIManager.Instance == null || GUIManager.CustomGUIFront == null)
		{
			return false;
		}
		capturing = null;
		capturedModifier = KeyCode.None;
		try
		{
			if (window != null && (WindowSize() != builtSize || ForeignCount() != builtForeign))
			{
				// Auflösung oder GUI-Größe geändert, oder eine Mod hat seitdem Einstellungen angelegt: neu aufbauen
				UnityEngine.Object.Destroy(window);
				window = null;
				builtShape = -1;
			}
			Build();
			ClearSearch();
			SwitchView(lastView);
			focusSearch = view == View.Settings;
		}
		catch (Exception e)
		{
			LogError("Aufbau", e);
			if (window != null)
			{
				UnityEngine.Object.Destroy(window);
			}
			window = null;
			builtShape = -1;
			return false;
		}
		window!.SetActive(true);
		window.transform.SetAsLastSibling();
		GUIManager.BlockInput(true);
		return true;
	}

	public void Hide()
	{
		capturing = null;
		if (IsOpen)
		{
			window!.SetActive(false);
			// Eingabesperre erst ein Bild später lösen: sonst sieht Valheims Pause-Menü dasselbe Esc noch und öffnet sich
			closedFrame = Time.frameCount;
			pendingUnblocks++;
			try
			{
				Closed?.Invoke();
			}
			catch (Exception e)
			{
				LogError("Schließen", e);
			}
			if (openedFromPause)
			{
				if (Menu.instance != null)
				{
					Menu.instance.Show(); // vom Pause-Menü geöffnet: dorthin zurück (pausiert wieder)
				}
				else
				{
					Game.Unpause(); // ohne Pause-Menü nicht pausiert zurücklassen
				}
			}
			else if (pausedByKey)
			{
				Game.Unpause(); // mit der Taste geöffnet: wieder weiterspielen
			}
		}
		openedFromPause = false;
		pausedByKey = false;
	}

	// ------------------------------------------------------------------ Aufbau

	private int Shape() => Sections().Sum(s => 1000 + Children(s, "groups").Sum(g => 1 + Children(g, "items").Count));

	private void Build()
	{
		int shape = Shape();
		if (window != null && shape == builtShape)
		{
			return;
		}
		if (window != null)
		{
			UnityEngine.Object.Destroy(window); // neue Mod oder neue Einträge: neu aufbauen
			window = null;
		}
		builtShape = -1;
		rows.Clear();
		sections.Clear();
		foreignMods.Clear();
		foreignHeader = null;
		Vector2 size = WindowSize();
		builtSize = size;
		builtForeign = ForeignCount();
		wide = size.x >= WideWidth;
		window = GcUi.CreateWindow("GC_Option_Window", size.x, size.y, out Text header, out RectTransform inner);
		header.text = ui("title");
		innerRect = inner;
		modsArea = modsTools = profilesArea = profilesTools = null;
		modsContent = profilesContent = null;
		modRows.Clear();
		profileRows.Clear();
		BuildTopBar(inner);
		BuildModsTools(inner);
		BuildProfilesTools(inner);

		GameObject areaGo = new("ScrollArea", typeof(RectTransform));
		areaGo.transform.SetParent(inner, false);
		RectTransform area = (RectTransform)areaGo.transform;
		area.anchorMin = Vector2.zero;
		area.anchorMax = Vector2.one;
		area.offsetMin = Vector2.zero;
		area.offsetMax = new Vector2(0f, -TopBarHeight);
		settingsArea = areaGo;
		// Innenfläche des Fensters (Breite − 72, Höhe − 156) minus Leiste oben minus Rand (2 × 8)
		content = GcUi.CreateScroll(area, size.x - 88f, size.y - 172f - TopBarHeight);

		emptyHint = HintRow(content, ui("nothing"), out Text emptyText);
		emptyLabel = emptyText;
		HashSet<ConfigEntryBase> listed = new();
		foreach (Dictionary<string, object> section in Sections())
		{
			string guid = Str(section, "guid");
			string sectionTitle = TextOf(section, "title");
			Block sectionBlock = new() { Header = Header(content, sectionTitle, 28, GUIManager.Instance.ValheimOrange, true, 46f) };
			sections.Add(sectionBlock);
			foreach (Dictionary<string, object> group in Children(section, "groups"))
			{
				List<Dictionary<string, object>> items = Children(group, "items");
				if (items.Count == 0)
				{
					continue;
				}
				string groupTitle = TextOf(group, "title");
				Block groupBlock = new() { Header = Header(content, groupTitle, 22, new Color(1f, 0.75f, 0.4f), false, 34f) };
				sectionBlock.Children.Add(groupBlock);
				string groupGuid = Str(group, "guid");
				Func<bool>? allowed = group.TryGetValue("allowed", out object a) ? a as Func<bool> : null;
				Row? lastMain = null;
				foreach (Dictionary<string, object> item in items)
				{
					Row? row = CreateRow(item, groupGuid.Length > 0 ? groupGuid : guid, content);
					if (row == null)
					{
						continue;
					}
					listed.Add(row.Entry);
					row.Allowed = allowed;
					// mit GUID und Name der Mod: „Einstellungen“ in der Mod-Liste sucht danach
					row.Search = SearchText(TextOf(item, "name"), TextOf(item, "desc"), groupTitle, sectionTitle, row.Entry.Definition.Key, row.Guid, ModuleName(row.Guid));
					if (row.Level == 0)
					{
						lastMain = row;
					}
					else if (lastMain != null)
					{
						row.Parent = lastMain;
						lastMain.Subs.Add(row);
					}
					rows.Add(row);
					groupBlock.Rows.Add(row);
				}
			}
		}
		BuildForeign(listed); // alle übrigen Mods (Gorduan, 25.10.2026), zugeklappt; ihre Zeilen entstehen erst beim Aufklappen

		hintLabel = GcUi.CreateLabel(window.transform, ui("hint_config"), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(44f, 64f), new Vector2(-44f, 86f), 15, TextAnchor.MiddleCenter, MutedColor);
		hintLabel.raycastTarget = false;
		GcUi.CreateButton(window.transform, ui("close"), new Vector2(0.5f, 0f), new Vector2(0f, 38f), 200f, 42f, Hide);
		builtShape = shape; // erst wenn alles steht: ein Fehler beim Aufbau führt beim nächsten Öffnen zu einem neuen Versuch
	}

	private static string ModuleName(string guid) => GcKern.Modules.FirstOrDefault(m => m.Info.Guid == guid)?.Info.Name ?? "";

	private const float WindowMargin = 40f;
	private const float MaxWindowWidth = 1800f;
	/// <summary>Ab dieser Fensterbreite steht die Beschreibung neben dem Namen statt darunter.</summary>
	private const float WideWidth = 1400f;
	/// <summary>Leiste oben: Reiter, darunter die Werkzeuge der Ansicht.</summary>
	private const float TopBarHeight = 92f;
	private const float SearchWidth = 420f;

	/// <summary>
	/// Fenstergröße nach der Bildschirmfläche (Gorduan, 25.10.2026: „an die Bildschirmgröße angepasst, um mehr Informationen auf
	/// einmal zu sehen“): fast bildschirmfüllend mit Rand, auf sehr breiten Bildschirmen höchstens 1800 breit. In Einheiten
	/// der Oberfläche, also nach Valheims GUI-Größe.
	/// </summary>
	private static Vector2 WindowSize()
	{
		Vector2 screen = GUIManager.CustomGUIFront != null && GUIManager.CustomGUIFront.transform is RectTransform canvas && canvas.rect.width > 0f
			? canvas.rect.size
			: new Vector2(1920f, 1080f);
		float width = Mathf.Clamp(screen.x - 2f * WindowMargin, Mathf.Min(900f, screen.x), MaxWindowWidth);
		float height = Mathf.Max(screen.y - 2f * WindowMargin, Mathf.Min(640f, screen.y));
		return new Vector2(Mathf.Round(width), Mathf.Round(height));
	}

	/// <summary>Leiste oben: Reiter, darunter Suchfeld und Kästchen „Server-Regeln anzeigen“ (Gorduan, 25.10.2026).</summary>
	private void BuildTopBar(RectTransform inner)
	{
		BuildTabs(inner);
		settingsTools = ToolRow(inner, "SettingsTools");
		RectTransform tools = (RectTransform)settingsTools.transform;
		GameObject searchGo = GUIManager.Instance.CreateInputField(tools, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, InputField.ContentType.Standard, ui("search"), 18, SearchWidth, 34f);
		searchGo.name = "Search";
		RectTransform searchRect = (RectTransform)searchGo.transform;
		searchRect.pivot = new Vector2(0f, 1f);
		searchRect.anchoredPosition = new Vector2(16f, 0f);
		searchField = searchGo.GetComponent<InputField>();
		searchField.lineType = InputField.LineType.SingleLine;
		searchField.onValueChanged.AddListener(text =>
		{
			pendingQuery = NormalizeSearch(text);
			queryAt = Time.unscaledTime + 0.2f;
		});

		// für alle: ohne Rechte nur zum Lesen, Admins dürfen ändern
		GameObject toggleGo = GUIManager.Instance.CreateToggle(tools, 28f, 28f);
		toggleGo.name = "ShowServer";
		RectTransform toggleRect = (RectTransform)toggleGo.transform;
		toggleRect.SetParent(tools, false);
		toggleRect.localScale = Vector3.one; // CreateToggle hängt mit Weltposition ein, das verzerrt sonst die Größe
		toggleRect.anchorMin = toggleRect.anchorMax = new Vector2(0f, 1f);
		toggleRect.pivot = new Vector2(0f, 1f);
		toggleRect.anchoredPosition = new Vector2(16f + SearchWidth + 32f, -3f);
		if (toggleGo.transform.Find("Background") is RectTransform box)
		{
			box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
			box.anchoredPosition = Vector2.zero;
		}
		Text? toggleLabel = toggleGo.transform.Find("Label")?.GetComponent<Text>();
		if (toggleLabel != null)
		{
			// die Beschriftung gehört zum Kästchen, ein Klick darauf schaltet es ebenfalls
			toggleLabel.text = ui("show_server");
			toggleLabel.font = GUIManager.Instance.AveriaSerifBold;
			toggleLabel.fontSize = 18;
			toggleLabel.color = MutedColor;
			toggleLabel.alignment = TextAnchor.MiddleLeft;
			toggleLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
			RectTransform labelRect = toggleLabel.rectTransform;
			labelRect.anchorMin = new Vector2(1f, 0f);
			labelRect.anchorMax = new Vector2(1f, 1f);
			labelRect.pivot = new Vector2(0f, 0.5f);
			labelRect.offsetMin = new Vector2(12f, 0f);
			labelRect.offsetMax = new Vector2(612f, 0f);
			toggleLabel.gameObject.AddComponent<Outline>().effectColor = Color.black;
		}
		showServer = toggleGo.GetComponent<Toggle>();
		showServer.isOn = serverRulesShown; // DefaultControls legt das Kästchen angehakt an
		showServer.onValueChanged.AddListener(on =>
		{
			serverRulesShown = on;
			Refresh();
		});
	}

	private void ClearSearch()
	{
		query = pendingQuery = "";
		queryAt = -1f;
		searchClosed.Clear();
		if (searchField != null && searchField.text.Length > 0)
		{
			searchField.text = "";
		}
		queryAt = -1f; // das Leeren selbst löst keine Suche aus
	}

	private static string NormalizeSearch(string text) => text.Trim().ToLowerInvariant();

	private static string SearchText(params string[] parts) => string.Join(" ", parts).ToLowerInvariant();

	/// <summary>Das Feld, in das gerade getippt wird (sonst null).</summary>
	private static InputField? Typing()
	{
		GameObject? selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
		return selected != null && selected.TryGetComponent(out InputField field) && field.isFocused ? field : null;
	}

	private GameObject Header(Transform parent, string text, int size, Color color, bool norse, float height)
	{
		GameObject go = new("Header", typeof(RectTransform), typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		LayoutElement layout = go.GetComponent<LayoutElement>();
		layout.minHeight = height;
		layout.preferredHeight = height;
		Text label = GcUi.CreateLabel(go.transform, text, Vector2.zero, Vector2.one, new Vector2(4f, 0f), Vector2.zero, size, TextAnchor.LowerLeft, color, norse);
		label.raycastTarget = false;
		return go;
	}

	/// <summary>Hinweis im Listenbereich, wächst mit dem Text (mehrzeilig).</summary>
	private GameObject HintRow(Transform parent, string text, out Text label)
	{
		GameObject go = new("Hint", typeof(RectTransform), typeof(VerticalLayoutGroup));
		go.transform.SetParent(parent, false);
		VerticalLayoutGroup layout = go.GetComponent<VerticalLayoutGroup>();
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		layout.padding = new RectOffset(8, 8, 16, 16);
		label = FlowText(go.transform, text, 18, MutedColor);
		return go;
	}

	private static Text FlowText(Transform parent, string text, int size, Color color)
	{
		GUIManager gui = GUIManager.Instance;
		GameObject go = gui.CreateText(text, parent, new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero, gui.AveriaSerifBold, size, color, true, Color.black, 100f, 20f, false);
		Text label = go.GetComponent<Text>();
		label.alignment = TextAnchor.UpperLeft;
		label.horizontalOverflow = HorizontalWrapMode.Wrap;
		label.verticalOverflow = VerticalWrapMode.Overflow;
		label.supportRichText = true;
		label.raycastTarget = false;
		return label;
	}

	/// <summary>Eine Zeile: Name und Beschreibung, für wen sie gilt, Bedienelement. foreign: Einstellung einer fremden Mod.</summary>
	private Row? CreateRow(Dictionary<string, object> item, string guid, Transform parent, ForeignEntry? foreign = null)
	{
		if (!item.TryGetValue("entry", out object e) || e is not ConfigEntryBase entry)
		{
			return null;
		}
		object[]? choices = Choices(entry);
		EditorKind kind = KindOf(entry.SettingType, choices);
		int level = Int(item, "level", 0) > 0 ? 1 : 0;

		GameObject rowGo = new("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
		rowGo.transform.SetParent(parent, false);
		HorizontalLayoutGroup layout = rowGo.GetComponent<HorizontalLayoutGroup>();
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.spacing = 12f;
		layout.padding = new RectOffset(10 + level * 36, 4, level == 0 ? 8 : 4, level == 0 ? 8 : 4);
		rowGo.GetComponent<LayoutElement>().minHeight = level == 0 ? 54f : 44f;

		string description = TextOf(item, "desc");
		Text name;
		Text desc;
		if (wide)
		{
			// breites Fenster: Name mit fester Breite, Beschreibung daneben (Gorduan, 25.10.2026: mehr Zeilen im Bild)
			name = FlowText(Cell(rowGo.transform, NameWidth - level * 36f, 0f).transform, TextOf(item, "name"), level == 0 ? 20 : 17, Color.white);
			desc = FlowText(Cell(rowGo.transform, 220f, 1f).transform, description, 15, MutedColor);
		}
		else
		{
			// links: Name und Beschreibung untereinander, wachsen mit dem Text
			GameObject left = Cell(rowGo.transform, 220f, 1f);
			left.GetComponent<VerticalLayoutGroup>().spacing = 2f;
			name = FlowText(left.transform, TextOf(item, "name"), level == 0 ? 20 : 17, Color.white);
			desc = FlowText(left.transform, description, 15, MutedColor);
		}

		// Mitte: für wen die Einstellung gilt
		GameObject scopeGo = new("Scope", typeof(RectTransform), typeof(LayoutElement));
		scopeGo.transform.SetParent(rowGo.transform, false);
		LayoutElement scopeLayout = scopeGo.GetComponent<LayoutElement>();
		scopeLayout.minWidth = scopeLayout.preferredWidth = wide ? 180f : 160f;
		scopeLayout.minHeight = scopeLayout.preferredHeight = 40f;
		Text scope = GcUi.CreateLabel(scopeGo.transform, "", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 14, TextAnchor.MiddleRight, MutedColor);
		scope.raycastTarget = false;

		// rechts: Bedienelement
		float controlWidth = wide ? 260f : 200f;
		GameObject control = new("Control", typeof(RectTransform), typeof(LayoutElement));
		control.transform.SetParent(rowGo.transform, false);
		LayoutElement controlLayout = control.GetComponent<LayoutElement>();
		controlLayout.minWidth = controlLayout.preferredWidth = controlWidth;
		controlLayout.minHeight = controlLayout.preferredHeight = 40f;

		// ganz rechts: „Standard“, nur wenn der Wert abweicht (die Spalte bleibt, damit alles untereinander steht)
		GameObject resetColumn = new("Reset", typeof(RectTransform), typeof(LayoutElement));
		resetColumn.transform.SetParent(rowGo.transform, false);
		LayoutElement resetLayout = resetColumn.GetComponent<LayoutElement>();
		resetLayout.minWidth = resetLayout.preferredWidth = 96f;
		resetLayout.minHeight = resetLayout.preferredHeight = 40f;

		Row row = new()
		{
			Item = item,
			Entry = entry,
			Guid = guid,
			Level = level,
			Root = rowGo,
			Name = name,
			Description = desc,
			Scope = scope,
			BaseDescription = description,
			Kind = kind,
			Choices = choices,
			Foreign = foreign != null,
			Sync = foreign?.Sync ?? (IsServerEntry(entry) ? SyncKind.Jotunn : SyncKind.Local),
			ReadOnly = kind == EditorKind.ReadOnly,
		};
		row.Server = row.Sync != SyncKind.Local;
		row.ReadOnlyCheck = foreign?.ReadOnlyCheck;
		row.SyncTag = row.Sync == SyncKind.ServerSync ? ServerSyncTag(entry) : null;
		Vector2 center = new(0.5f, 0.5f);
		switch (kind)
		{
			case EditorKind.Number when Range(entry, out _, out _) || ItemStep(item) > 0:
				row.Minus = GcUi.CreateButton(control.transform, "−", center, new Vector2(-(controlWidth / 2f - 21f), 0f), 42f, 38f, () => Step(row, -1));
				row.Plus = GcUi.CreateButton(control.transform, "+", center, new Vector2(controlWidth / 2f - 21f, 0f), 42f, 38f, () => Step(row, 1));
				row.Input = CreateInput(control.transform, controlWidth - 96f, row, TextAnchor.MiddleCenter);
				break;
			case EditorKind.Number:
				row.Input = CreateInput(control.transform, controlWidth - 4f, row, TextAnchor.MiddleCenter);
				break;
			case EditorKind.Text:
				row.Input = CreateInput(control.transform, controlWidth - 4f, row, TextAnchor.MiddleLeft);
				break;
			default:
				row.Main = GcUi.CreateButton(control.transform, "", center, Vector2.zero, controlWidth - 4f, 38f, () => Click(row));
				row.MainLabel = row.Main.GetComponentInChildren<Text>(true);
				break;
		}
		if (!row.ReadOnly)
		{
			row.Reset = GcUi.CreateButton(resetColumn.transform, ui("reset"), center, Vector2.zero, 92f, 32f, () => ResetToDefault(row));
			Text? resetLabel = row.Reset.GetComponentInChildren<Text>(true);
			if (resetLabel != null)
			{
				resetLabel.fontSize = 14;
			}
			row.HideDefault = foreign?.HideDefault ?? false;
			row.Reset.gameObject.SetActive(false);
		}
		return row;
	}

	private void ResetToDefault(Row row)
	{
		if (CancelCapture() || !ComputeEditable(row))
		{
			return;
		}
		row.InputError = false;
		Set(row, row.Entry.DefaultValue);
	}

	/// <summary>Breite der Namensspalte im breiten Fenster.</summary>
	private const float NameWidth = 340f;

	/// <summary>Spalte einer Zeile, deren Text mitwächst (Höhe aus dem Text, Breite fest oder flexibel).</summary>
	private static GameObject Cell(Transform parent, float width, float flexible)
	{
		GameObject go = new("Cell", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		VerticalLayoutGroup column = go.GetComponent<VerticalLayoutGroup>();
		column.childControlWidth = true;
		column.childControlHeight = true;
		column.childForceExpandWidth = true;
		column.childForceExpandHeight = false;
		LayoutElement layout = go.GetComponent<LayoutElement>();
		layout.minWidth = width;
		layout.preferredWidth = width;
		layout.flexibleWidth = flexible;
		return go;
	}

	/// <summary>Eingabefeld eines Werts: Enter oder Wegklicken übernimmt, Esc bricht ab.</summary>
	private InputField CreateInput(Transform parent, float width, Row row, TextAnchor align)
	{
		GameObject go = GUIManager.Instance.CreateInputField(parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, InputField.ContentType.Standard, "", 16, width, 36f);
		go.name = "Value";
		InputField field = go.GetComponent<InputField>();
		field.lineType = InputField.LineType.SingleLine;
		field.textComponent.alignment = align;
		field.onEndEdit.AddListener(text => Commit(row, field, text));
		return field;
	}

	private static bool IsServerEntry(ConfigEntryBase entry)
	{
		object[]? tags = entry.Description?.Tags;
		if (tags == null)
		{
			return false;
		}
		foreach (object tag in tags)
		{
			if (tag == null)
			{
				continue;
			}
			Type t = tag.GetType();
			object? value = t.GetProperty("IsAdminOnly")?.GetValue(tag) ?? t.GetField("IsAdminOnly")?.GetValue(tag);
			if (value is bool b && b)
			{
				return true;
			}
		}
		return false;
	}

	// ------------------------------------------------------------------ Bedienung

	/// <summary>Zeile (und ihre Funktion) hat alles, was sie braucht, z. B. ValheimPlus.</summary>
	private static bool Required(Row row)
	{
		bool own = !row.Item.TryGetValue("requires", out object r) || r is not Func<bool> requires || Call(requires);
		return own && (row.Parent == null || Required(row.Parent));
	}

	private static bool OwnBlocked(Row row) => row.Item.TryGetValue("blocked", out object b) && b is Func<bool> blocked && Call(blocked);

	/// <summary>Blockiert ist eine Zeile, wenn sie selbst oder ihre Funktion (die Zeile darüber) blockiert ist.</summary>
	private static bool Blocked(Row row) => OwnBlocked(row) || (row.Parent != null && OwnBlocked(row.Parent));

	private static string OldMod(Row row)
	{
		string own = Str(row.Item, "oldMod");
		return own.Length > 0 || row.Parent == null ? own : Str(row.Parent.Item, "oldMod");
	}

	private static bool Locked(Row row) => row.Item.TryGetValue("locked", out object l) && l is Func<bool> locked && Call(locked);

	/// <summary>
	/// Änderbar? Eigene Einstellungen immer. Server-Regeln im Hauptmenü ja (deine eigene Config, sie gilt, wenn du selbst
	/// hostest), in einer Welt nur als Admin und nur, wenn der Server die Mod hat.
	/// </summary>
	private static bool ComputeEditable(Row row)
	{
		if (IsReadOnly(row) || Blocked(row) || Locked(row))
		{
			return false;
		}
		if (!row.Server || ZNet.instance == null)
		{
			return true;
		}
		return CanChangeServer && OnServer(row);
	}

	private static bool OnServer(Row row) => row.Foreign ? ForeignOnServer(row) : ModOnServer(row.Guid);

	private static bool IsReadOnly(Row row) => row.ReadOnly || (row.ReadOnlyCheck != null && Call(row.ReadOnlyCheck));

	/// <summary>Während eine Taste erfasst wird, bricht jeder Klick auf ein anderes Bedienelement nur die Erfassung ab.</summary>
	private bool CancelCapture()
	{
		if (capturing == null)
		{
			return false;
		}
		capturing = null;
		capturedModifier = KeyCode.None;
		Refresh();
		return true;
	}

	private void Click(Row row)
	{
		if (CancelCapture() || !ComputeEditable(row))
		{
			return;
		}
		Type type = row.Entry.SettingType;
		if (type == typeof(bool))
		{
			Set(row, !(bool)row.Entry.BoxedValue);
		}
		else if (row.Choices != null && row.Choices.Length > 0)
		{
			int index = Array.FindIndex(row.Choices, choice => Equals(choice, row.Entry.BoxedValue));
			Set(row, row.Choices[(index + 1) % row.Choices.Length]); // AcceptableValueList: der nächste erlaubte Wert
		}
		else if (type.IsEnum)
		{
			Array values = Enum.GetValues(type);
			int index = Array.IndexOf(values, row.Entry.BoxedValue);
			Set(row, values.GetValue((index + 1) % values.Length));
		}
		else if (type == typeof(KeyboardShortcut) || type == typeof(KeyCode))
		{
			capturing = row;
			capturedModifier = KeyCode.None;
			captureFrame = Time.frameCount; // die Taste, die den Knopf ausgelöst hat (Enter, Leertaste), nicht gleich erfassen
			EventSystem.current?.SetSelectedGameObject(null); // sonst löst Enter oder Leertaste den Knopf gleich wieder aus
			Refresh();
		}
	}

	private void Step(Row row, int direction)
	{
		if (CancelCapture() || !ComputeEditable(row))
		{
			return;
		}
		Type type = row.Entry.SettingType;
		double step = StepSize(row);
		double current = Convert.ToDouble(row.Entry.BoxedValue, CultureInfo.InvariantCulture);
		double value;
		if (IsIntegral(type))
		{
			value = current + direction * Math.Max(1, Math.Round(step));
		}
		else
		{
			value = Math.Round((current + direction * step) / step) * step; // auf das Raster setzen, damit keine krummen Werte entstehen
			value = Math.Round(value, type == typeof(float) ? 4 : 8);
		}
		if (Range(row.Entry, out double min, out double max))
		{
			value = Math.Min(max, Math.Max(min, value));
		}
		if (TryNumber(value, type, out object? result))
		{
			Set(row, result!);
		}
	}

	private static double StepSize(Row row)
	{
		double step = 0;
		if (row.Item.TryGetValue("step", out object s) && s is IConvertible)
		{
			step = Convert.ToDouble(s, CultureInfo.InvariantCulture);
		}
		if (step > 0)
		{
			return step;
		}
		bool integral = IsIntegral(row.Entry.SettingType);
		double span = Range(row.Entry, out double min, out double max) ? max - min : 0;
		if (span <= 0)
		{
			return integral ? 1 : 0.1;
		}
		double raw = span / 20.0;
		foreach (double nice in new[] { 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 })
		{
			if (raw <= nice)
			{
				return integral ? Math.Max(1, nice) : nice;
			}
		}
		return 1000;
	}

	private void Set(Row row, object value)
	{
		if (!ComputeEditable(row) || Equals(row.Entry.BoxedValue, value))
		{
			Refresh();
			return;
		}
		row.Entry.BoxedValue = value;
		if (!row.Entry.ConfigFile.SaveOnConfigSet)
		{
			row.Entry.ConfigFile.Save(); // manche Mods speichern nicht von selbst (wie Mod Menu)
		}
		if (row.Sync == SyncKind.Jotunn)
		{
			SyncServer(); // ServerSync schickt Änderungen eines Admins selbst an den Server
		}
		string mod = row.Mod != null ? row.Mod.Name + ": " : "";
		log.LogInfo($"Einstellung {mod}{row.Entry.Definition.Section}/{row.Entry.Definition.Key} = {Format(row)}");
		Refresh();
	}

	/// <summary>Eingabe aus einem Wert-Feld übernehmen. Ungültig: rot, der alte Wert bleibt.</summary>
	private void Commit(Row row, InputField field, string text)
	{
		if (field.wasCanceled || !ComputeEditable(row))
		{
			row.InputError = false;
			Refresh(); // Esc oder keine Rechte: wieder den gültigen Wert zeigen
			return;
		}
		if (TryParse(row, text, out object? value))
		{
			row.InputError = false;
			Set(row, value!);
		}
		else
		{
			row.InputError = true;
			Refresh();
		}
	}

	/// <summary>Geänderte Server-Werte über Jötunn verteilen (der Server prüft die Admin-Rechte erneut).</summary>
	private void SyncServer()
	{
		if (ZNet.instance == null)
		{
			return;
		}
		try
		{
			syncMethod ??= AccessTools.Method(typeof(SynchronizationManager), "SynchronizeChangedConfig");
			if (syncMethod == null)
			{
				log.LogWarning("Server-Einstellung wurde nur lokal geändert: Jötunn-Methode SynchronizeChangedConfig nicht gefunden.");
				return;
			}
			syncMethod.Invoke(SynchronizationManager.Instance, null);
		}
		catch (Exception e)
		{
			log.LogWarning($"Server-Einstellung konnte nicht verteilt werden: {e.Message}");
		}
	}

	/// <summary>
	/// Taste erfassen: nächste gedrückte Taste (mit gehaltenen Modifikatoren), auch Entf, Rücktaste und die Maustasten
	/// 3 bis 7. Esc bricht ab, Rechtsklick leert die Belegung. Die linke Maustaste ist ausgenommen (damit wird geklickt).
	/// </summary>
	private void Capture()
	{
		Row row = capturing!;
		if (Time.frameCount == captureFrame)
		{
			return;
		}
		if (Input.GetKeyDown(KeyCode.Escape))
		{
			capturing = null;
			Refresh();
			return;
		}
		if (Input.GetKeyDown(KeyCode.Mouse1))
		{
			capturing = null;
			Set(row, row.Entry.SettingType == typeof(KeyCode) ? KeyCode.None : KeyboardShortcut.Empty);
			return;
		}
		allKeys ??= ((KeyCode[])Enum.GetValues(typeof(KeyCode))).Where(k => k != KeyCode.None && k != KeyCode.Escape && k != KeyCode.Mouse0 && k != KeyCode.Mouse1 && k < KeyCode.JoystickButton0).Distinct().ToArray();
		foreach (KeyCode pressed in allKeys)
		{
			if (!Input.GetKeyDown(pressed))
			{
				continue;
			}
			if (Array.IndexOf(Modifiers, pressed) >= 0 && row.Entry.SettingType == typeof(KeyboardShortcut))
			{
				capturedModifier = pressed; // vielleicht Teil einer Kombination, entschieden wird beim Loslassen
				continue;
			}
			Finish(row, pressed);
			return;
		}
		if (capturedModifier != KeyCode.None && Input.GetKeyUp(capturedModifier))
		{
			Finish(row, capturedModifier); // nur der Modifikator selbst, z. B. Linke Alt-Taste
		}
	}

	private void Finish(Row row, KeyCode main)
	{
		capturing = null;
		capturedModifier = KeyCode.None;
		if (row.Entry.SettingType == typeof(KeyCode))
		{
			Set(row, main);
			return;
		}
		KeyCode[] held = Modifiers.Where(m => m != main && Input.GetKey(m)).ToArray();
		Set(row, new KeyboardShortcut(main, held));
	}

	// ------------------------------------------------------------------ Anzeige

	private string Format(Row row) => FormatValue(row, row.Entry.BoxedValue);

	/// <summary>Wert zum Anzeigen; in Eingabefeldern ist das auch der Text, den man ändert (TryParse liest ihn zurück).</summary>
	private string FormatValue(Row row, object? value)
	{
		string unit = Str(row.Item, "unit");
		switch (value)
		{
			case null:
				return "";
			case bool b:
				return ui(b ? "on" : "off");
			case int i:
				return i.ToString(CultureInfo.CurrentCulture) + unit;
			case float f:
				return f.ToString(FloatFormat(row), CultureInfo.CurrentCulture) + unit;
			case double d:
				return d.ToString("0.########", CultureInfo.CurrentCulture) + unit;
			case KeyboardShortcut shortcut:
				return shortcut.MainKey == KeyCode.None ? ui("key_none") : shortcut.ToString();
			case KeyCode code:
				return code == KeyCode.None ? ui("key_none") : code.ToString();
			case string s:
				return s;
		}
		if (value is Enum && !value.GetType().IsDefined(typeof(FlagsAttribute), false))
		{
			string name = value.ToString();
			string text = row.Item.TryGetValue("enumText", out object f) && f is Func<string, string> enumText ? enumText(name) : name;
			return text.EndsWith("enum_" + name, StringComparison.Ordinal) ? name : text; // kein Text hinterlegt: Name des Werts
		}
		if (IsNumeric(value.GetType()))
		{
			return Convert.ToString(value, CultureInfo.CurrentCulture) + unit;
		}
		try
		{
			return TomlTypeConverter.ConvertToString(value, row.Entry.SettingType);
		}
		catch
		{
			return value.ToString();
		}
	}

	/// <summary>So viele Nachkommastellen, wie die Schrittweite braucht (mindestens zwei), z. B. 0,005 → drei.</summary>
	private static string FloatFormat(Row row)
	{
		double step = StepSize(row);
		int decimals = step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
		return "0." + new string('#', Math.Max(2, Math.Min(6, decimals)));
	}

	private string ScopeText(Row row)
	{
		if (Blocked(row))
		{
			return "";
		}
		if (row.Allowed != null && !Call(row.Allowed))
		{
			return ui("scope_off_here"); // die Stufe des Servers schaltet diese Mod hier ab
		}
		if (Locked(row))
		{
			return ui("scope_mainmenu"); // z. B. ValheimPlus: nur im Hauptmenü änderbar
		}
		if (IsReadOnly(row))
		{
			return ui("scope_readonly");
		}
		if (!row.Server)
		{
			return ui("scope_player");
		}
		if (ZNet.instance == null)
		{
			return ui("scope_server_local"); // Hauptmenü: deine eigene Config, sie gilt, wenn du selbst hostest
		}
		if (!OnServer(row))
		{
			return ui("scope_server_missing");
		}
		return ui(CanChangeServer ? "scope_server" : "scope_server_locked");
	}

	/// <summary>Die offene Ansicht und die Leiste unten auf den neuesten Stand bringen.</summary>
	private void Refresh()
	{
		switch (view)
		{
			case View.Mods:
				RefreshMods();
				break;
			case View.Profiles:
				RefreshProfiles();
				break;
			default:
				RefreshSettings();
				break;
		}
		RefreshBottom();
	}

	private void RefreshSettings()
	{
		// Gorduan (25.10.2026): ohne Kästchen sieht jeder nur seine eigenen Einstellungen; mit Kästchen auch die Server-Regeln,
		// ändern dürfen sie nur Admins (ComputeEditable)
		bool server = serverRulesShown;
		if (showServer != null && showServer.isOn != server)
		{
			showServer.SetIsOnWithoutNotify(server);
		}
		foreach (Row row in rows)
		{
			row.Editable = ComputeEditable(row);
		}
		foreach (Row row in rows)
		{
			if (row.Foreign)
			{
				continue; // fremde Mods: RefreshForeign
			}
			if (!Required(row))
			{
				row.Visible = false; // gehört zu etwas, das hier fehlt, z. B. ValheimPlus
				continue;
			}
			bool shown = !row.Server || server;
			if (row.Level == 0)
			{
				// eine Funktion, deren Schalter eine Server-Regel ist, bleibt ausgegraute Überschrift deiner Unteroptionen
				shown |= row.Subs.Any(s => !s.Server && Required(s));
			}
			row.Visible = shown && MatchesSearch(row, server);
		}
		if (capturing != null && !capturing.Editable)
		{
			capturing = null; // Rechte verloren, während eine Taste erfasst wurde
		}
		bool any = false;
		foreach (Block section in sections)
		{
			bool sectionVisible = false;
			foreach (Block group in section.Children)
			{
				bool groupVisible = group.Rows.Any(r => r.Visible);
				SetActive(group.Header, groupVisible);
				sectionVisible |= groupVisible;
				foreach (Row row in group.Rows)
				{
					SetActive(row.Root, row.Visible);
				}
			}
			SetActive(section.Header, sectionVisible);
			any |= sectionVisible;
		}
		any |= RefreshForeign(server);
		if (emptyHint != null)
		{
			SetActive(emptyHint, !any);
			if (emptyLabel != null)
			{
				string typed = searchField != null ? searchField.text.Trim() : "";
				int hidden = query.Length > 0 && !server ? ServerHits() : 0;
				emptyLabel.text = query.Length == 0 ? ui("nothing")
					: hidden > 0 ? ui("no_hits_server").Replace("$1", typed).Replace("$2", hidden.ToString())
					: ui("no_hits").Replace("$1", typed);
			}
		}

		foreach (Row row in rows)
		{
			if (!row.Visible)
			{
				continue;
			}
			string description = row.BaseDescription;
			if (row.Item.TryGetValue("note", out object n) && n is Func<string> note)
			{
				string extra = "";
				try
				{
					extra = note() ?? "";
				}
				catch
				{
					// Hinweis der Mod nicht verfügbar
				}
				if (extra.Length > 0)
				{
					description += " " + extra;
				}
			}
			bool blocked = Blocked(row);
			if (blocked)
			{
				description += " " + ui("blocked_desc").Replace("$1", OldMod(row));
			}
			if (row.Description.text != description)
			{
				row.Description.text = description;
			}
			row.Name.color = row.Editable ? Color.white : LockedColor;
			row.Scope.text = ScopeText(row);
			UpdateControl(row, blocked);
		}
	}

	/// <summary>
	/// Suche in den GC-Zeilen: ein Treffer zeigt die Zeile, ein Treffer in einer Funktion auch ihre Unteroptionen, ein
	/// Treffer in einer Unteroption auch die Funktion darüber.
	/// </summary>
	private bool MatchesSearch(Row row, bool server)
	{
		if (query.Length == 0 || row.Search.Contains(query))
		{
			return true;
		}
		if (row.Parent != null && row.Parent.Search.Contains(query))
		{
			return true;
		}
		return row.Level == 0 && row.Subs.Any(s => (!s.Server || server) && Required(s) && s.Search.Contains(query));
	}

	/// <summary>Treffer der Suche in Server-Regeln, die ohne Kästchen verborgen sind (Test 25.10.2026: „Keine Treffer“ verwirrte).</summary>
	private int ServerHits()
	{
		int hits = rows.Count(r => !r.Foreign && r.Server && Required(r) && r.Search.Contains(query));
		foreach (ForeignMod mod in foreignMods)
		{
			hits += mod.Entries.Count(e => e.Sync != SyncKind.Local && e.Search.Contains(query));
		}
		return hits;
	}

	private static readonly Color ErrorColor = new(1f, 0.35f, 0.3f);

	private void UpdateControl(Row row, bool blocked)
	{
		if (row.Input != null)
		{
			// beim Tippen und nach einer ungültigen Eingabe nicht überschreiben
			if (!row.Input.isFocused && !row.InputError)
			{
				string text = blocked ? ui("blocked") : Format(row);
				if (row.Input.text != text)
				{
					row.Input.text = text;
				}
			}
			Color color = row.InputError ? ErrorColor : ValueColor;
			row.Input.textComponent.color = row.Editable ? color : Color.Lerp(color, LockedColor, 0.6f);
			row.Input.interactable = row.Editable;
		}
		else if (row.Main != null && row.MainLabel != null)
		{
			if (row == capturing)
			{
				row.MainLabel.text = ui("press_key");
				row.MainLabel.color = ValueColor;
			}
			else if (blocked)
			{
				row.MainLabel.text = ui("blocked");
				row.MainLabel.color = MutedColor;
			}
			else
			{
				row.MainLabel.text = Format(row);
				Color color = row.Entry.BoxedValue is bool on ? (on ? OnColor : OffColor) : ValueColor;
				row.MainLabel.color = row.Editable ? color : Color.Lerp(color, LockedColor, 0.6f);
			}
			row.Main.interactable = row.Editable;
		}
		if (row.Minus != null && row.Plus != null)
		{
			row.Minus.interactable = row.Plus.interactable = row.Editable;
		}
		if (row.Reset != null)
		{
			bool changed = !Equals(row.Entry.BoxedValue, row.Entry.DefaultValue);
			SetActive(row.Reset.gameObject, changed && !row.HideDefault);
			row.Reset.interactable = row.Editable;
		}
	}

	private static void SetActive(GameObject go, bool active)
	{
		if (go.activeSelf != active)
		{
			go.SetActive(active);
		}
	}

	// ------------------------------------------------------------------ Knopf im Pause-Menü

	/// <summary>
	/// Knopf „GC Option" im Pause-Menü direkt unter „Einstellungen". Gibt es den Knopf schon (von einer anderen GC-Mod
	/// angelegt), zeigt deren Menü alles an, diese Mod legt keinen zweiten an. Wird nach jedem Szenenwechsel neu angelegt.
	/// </summary>
	private void EnsurePauseButton()
	{
		if (menuButton != null)
		{
			return;
		}
		Menu? menu = Menu.instance;
		if (menu == null || menu.m_settingsButton == null)
		{
			return;
		}
		Button template = menu.m_settingsButton;
		Transform parent = template.transform.parent;
		Transform? existing = parent.Find(ButtonName);
		if (existing != null)
		{
			// eine ältere GC-Mod (eigene Kopie dieses Menüs) hat den Knopf schon angelegt: der Kern übernimmt den Klick
			menuButton = existing.GetComponent<Button>();
			if (menuButton != null)
			{
				menuButton.onClick = new Button.ButtonClickedEvent();
				menuButton.onClick.AddListener(OpenFromPause);
			}
			return;
		}
		GameObject go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
		go.name = ButtonName;
		go.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
		foreach (Localize localize in go.GetComponentsInChildren<Localize>(true))
		{
			UnityEngine.Object.DestroyImmediate(localize); // sonst setzt Valheim den Text wieder auf „Einstellungen"
		}
		foreach (UIGamePad pad in go.GetComponentsInChildren<UIGamePad>(true))
		{
			if (pad.m_hint != null)
			{
				pad.m_hint.SetActive(false);
			}
			UnityEngine.Object.DestroyImmediate(pad);
		}
		string label = ui("menu_button");
		foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
		{
			text.text = label;
		}
		foreach (Text text in go.GetComponentsInChildren<Text>(true))
		{
			text.text = label;
		}
		menuButton = go.GetComponent<Button>();
		menuButton.onClick = new Button.ButtonClickedEvent();
		menuButton.onClick.AddListener(OpenFromPause);
		menu.m_rebuildLayout = true;
	}

	private Button? mainMenuButton;

	/// <summary>
	/// Knopf „GC Option“ im Hauptmenü direkt unter „Einstellungen“ (GC-Kern): Einstellungen nur für dich und ValheimPlus,
	/// dessen Werte nur hier, außerhalb einer Welt, änderbar sind. Wird nach jedem Szenenwechsel neu angelegt.
	/// </summary>
	private void EnsureMainMenuButton()
	{
		if (mainMenuButton != null || !canHost)
		{
			return;
		}
		FejdStartup? start = FejdStartup.instance;
		if (start == null || start.m_menuList == null)
		{
			return;
		}
		Button? template = null;
		foreach (Button button in start.m_menuList.GetComponentsInChildren<Button>(true))
		{
			for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
			{
				if (button.onClick.GetPersistentMethodName(i) == nameof(FejdStartup.OnButtonSettings))
				{
					template = button;
				}
			}
		}
		if (template == null)
		{
			return;
		}
		Transform parent = template.transform.parent;
		Transform? existing = parent.Find(ButtonName);
		GameObject go = existing != null ? existing.gameObject : UnityEngine.Object.Instantiate(template.gameObject, parent, false);
		go.name = ButtonName;
		go.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
		foreach (Localize localize in go.GetComponentsInChildren<Localize>(true))
		{
			UnityEngine.Object.DestroyImmediate(localize); // sonst setzt Valheim den Text wieder auf „Einstellungen“
		}
		foreach (UIGamePad pad in go.GetComponentsInChildren<UIGamePad>(true))
		{
			if (pad.m_hint != null)
			{
				pad.m_hint.SetActive(false);
			}
			UnityEngine.Object.DestroyImmediate(pad);
		}
		string label = ui("menu_button");
		foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
		{
			text.text = label;
		}
		foreach (Text text in go.GetComponentsInChildren<Text>(true))
		{
			text.text = label;
		}
		mainMenuButton = go.GetComponent<Button>();
		mainMenuButton.onClick = new Button.ButtonClickedEvent();
		mainMenuButton.onClick.AddListener(() =>
		{
			openedFromPause = false;
			Show();
		});
	}

	private static readonly Vector3[] MenuCorners = new Vector3[4];

	/// <summary>
	/// Hauptmenü (Gorduan, 25.10.2026: „mit unseren Mods gibt es im Hauptmenü keine Möglichkeit mehr, das Spiel zu beenden“):
	/// Valheims Liste (MenuList) hängt oben bei 29 % der Bildhöhe und wächst nach unten. Mit „GC Option“ und den Knöpfen
	/// anderer Mods (Serverknopf, Mods) ragte „Beenden“ unten aus dem Bild. Die Liste rückt deshalb so weit nach oben, dass der
	/// letzte Eintrag mit etwas Rand im Bild bleibt; jedes Bild geprüft, weil andere Mods ihre Knöpfe später einfügen.
	/// </summary>
	private void FitMainMenu()
	{
		FejdStartup? start = FejdStartup.instance;
		if (start == null || start.m_menuList == null || !start.m_menuList.activeInHierarchy)
		{
			return;
		}
		RectTransform list = (RectTransform)start.m_menuList.transform;
		Canvas? canvas = list.GetComponentInParent<Canvas>();
		if (canvas == null)
		{
			return;
		}
		Canvas root = canvas.rootCanvas;
		Camera? camera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
		float lowest = float.MaxValue; // Unterkante des untersten sichtbaren Knopfs, in Bildpunkten
		foreach (Button button in list.GetComponentsInChildren<Button>(false))
		{
			((RectTransform)button.transform).GetWorldCorners(MenuCorners);
			lowest = Mathf.Min(lowest, RectTransformUtility.WorldToScreenPoint(camera, MenuCorners[0]).y);
		}
		float scale = Mathf.Max(root.scaleFactor, 0.01f);
		float margin = 24f * scale;
		if (lowest < margin)
		{
			list.anchoredPosition += new Vector2(0f, (margin - lowest) / scale); // genau so weit nach oben, wie unten fehlt
		}
	}

	/// <summary>Offenes Menü neu aufbauen, wenn sich Einträge geändert haben; die Bildlauf-Position bleibt.</summary>
	private void RebuildOpen()
	{
		try
		{
			Vector2 scroll = content != null ? content.anchoredPosition : Vector2.zero;
			capturing = null;
			Build();
			if (searchField != null && query.Length > 0)
			{
				searchField.SetTextWithoutNotify(query); // die Suche bleibt
			}
			SwitchView(view);
			if (window != null)
			{
				window.SetActive(true);
				window.transform.SetAsLastSibling();
			}
			if (content != null)
			{
				content.anchoredPosition = scroll;
			}
		}
		catch (Exception e)
		{
			LogError("Neuaufbau", e);
			ForceClose(); // sonst bliebe die Eingabe gesperrt und das Spiel pausiert
		}
	}

	/// <summary>Nach einem Fehler: Fenster weg, Eingabe frei, Pause aufheben.</summary>
	private void ForceClose()
	{
		capturing = null;
		if (window != null)
		{
			window.SetActive(false);
		}
		GUIManager.BlockInput(false);
		if (openedFromPause || pausedByKey)
		{
			Game.Unpause();
		}
		openedFromPause = false;
		pausedByKey = false;
		builtShape = -1;
	}

	private void OpenFromPause()
	{
		Menu.instance?.Hide(); // Menu.Hide hebt die Pause auf
		if (Show())
		{
			openedFromPause = true;
			Game.Pause(); // im Einzelspieler bleibt das Spiel stehen wie in Valheims Einstellungen
		}
		else
		{
			Menu.instance?.Show();
		}
	}
}
