using System;
using System.Collections.Generic;
using System.Linq;
using GC.Kern;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Shared;

/// <summary>
/// Ansichten „Mods“ und „Profile“ (Gorduan, 25.10.2026: „Übernehme alle Features von Mod Menu und verbessere sie, dann können
/// wir die Mod auch rauslassen“). Mods: jede Mod mit Schalter (wirkt nach einem Neustart, über den Vorlader
/// GC_ModSchalter), Zustand (läuft, aus nach dem Neustart, lädt nicht und warum …), Paket und Sprung zu ihren Einstellungen.
/// Profile: welche Mods aus sind, speichern, laden, kopieren, aus der Zwischenablage anlegen. Brauchen geänderte Mods einen
/// Neustart, sagt es die Zeile unten; neu gestartet wird über r2modman.
/// Entfernt (Gorduan, 25.10.2026, nach der Ablehnung auf Thunderstore: „ist das für uns überhaupt notwendig?“): Knopf „Neu
/// starten“ (verstecktes PowerShell, Steam-Pfad aus der Registry), Update-Prüfung bei Thunderstore und Nexus und Knopf „Seite“.
/// r2modman zeigt Updates selbst an.
/// </summary>
public sealed partial class GcMenu
{
	private enum View
	{
		Settings,
		Mods,
		Profiles,
	}

	private sealed class ModRow
	{
		public ModSwitch.Mod Mod = null!;
		public GameObject Root = null!;
		public Toggle Toggle = null!;
		public Text Title = null!;
		public Text Sub = null!;
		public Text Status = null!;
	}

	private sealed class ProfileRow
	{
		public ModSwitch.Profile Profile = null!;
		public GameObject Root = null!;
		public Text Info = null!;
		public Button Load = null!;
		public Button Overwrite = null!;
		public Button Delete = null!;
		public string Pending = "";
		public float PendingUntil;
	}

	private const float TabWidth = 190f;
	private static readonly Color GoodColor = new(0.45f, 0.95f, 0.45f);
	private static readonly Color WarnColor = new(1f, 0.7f, 0.3f);

	/// <summary>Zuletzt offene Ansicht, bis zum Spielende gemerkt.</summary>
	private static View lastView = View.Settings;

	private View view = View.Settings;
	private RectTransform? innerRect;
	private readonly List<(View View, Text? Label)> tabs = new();
	private GameObject? settingsArea;
	private GameObject? settingsTools;
	private GameObject? modsArea;
	private GameObject? modsTools;
	private RectTransform? modsContent;
	private GameObject? modsNoPatcher;
	private InputField? modsSearch;
	private string modsQuery = "";
	private Text? modsStatus;
	private string modsMessage = "";
	private readonly List<ModRow> modRows = new();
	private GameObject? profilesArea;
	private GameObject? profilesTools;
	private RectTransform? profilesContent;
	private GameObject? profilesEmpty;
	private InputField? profileName;
	private Text? profilesStatus;
	private Text? profileSaveLabel;
	private string profilesMessage = "";
	private string savePending = "";
	private float savePendingUntil;
	private readonly List<ProfileRow> profileRows = new();
	private bool pausedByKey;

	// ------------------------------------------------------------------ Reiter und Leisten

	private void BuildTabs(RectTransform inner)
	{
		tabs.Clear();
		string[] keys = { "tab_settings", "tab_mods", "tab_profiles" };
		for (int i = 0; i < keys.Length; i++)
		{
			View target = (View)i;
			Button button = GcUi.CreateButton(inner, ui(keys[i]), new Vector2(0f, 1f), Vector2.zero, TabWidth, 36f, () => SwitchView(target));
			button.name = "Tab_" + target;
			RectTransform rect = (RectTransform)button.transform;
			rect.pivot = new Vector2(0f, 1f);
			rect.anchoredPosition = new Vector2(16f + i * (TabWidth + 8f), -6f);
			tabs.Add((target, button.GetComponentInChildren<Text>(true)));
		}
	}

	/// <summary>Zweite Zeile der Leiste oben (Werkzeuge der Ansicht), Kinder von links oben aus gesetzt.</summary>
	private static GameObject ToolRow(RectTransform inner, string name)
	{
		GameObject go = new(name, typeof(RectTransform));
		go.transform.SetParent(inner, false);
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(1f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(0f, -50f);
		rect.sizeDelta = new Vector2(0f, 40f);
		return go;
	}

	private static RectTransform TopLeft(Component component, float x, float y = 0f)
	{
		RectTransform rect = (RectTransform)component.transform;
		rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(x, y);
		return rect;
	}

	private InputField ToolInput(Transform parent, string placeholder, float x, float width)
	{
		GameObject go = GUIManager.Instance.CreateInputField(parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, InputField.ContentType.Standard, placeholder, 18, width, 34f);
		InputField field = go.GetComponent<InputField>();
		field.lineType = InputField.LineType.SingleLine;
		TopLeft(field, x);
		return field;
	}

	private Button ToolButton(Transform parent, string text, float x, float width, Action onClick)
	{
		Button button = GcUi.CreateButton(parent, text, new Vector2(0f, 1f), Vector2.zero, width, 34f, onClick);
		TopLeft(button, x);
		return button;
	}

	private Text ToolStatus(Transform parent, float x)
	{
		Text label = GcUi.CreateLabel(parent, "", new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(x, 0f), new Vector2(-16f, -6f), 16, TextAnchor.MiddleLeft, MutedColor);
		label.raycastTarget = false;
		label.horizontalOverflow = HorizontalWrapMode.Wrap;
		return label;
	}

	/// <summary>Scrollfläche einer Ansicht unter der Leiste (wie die der Einstellungen).</summary>
	private GameObject ViewArea(RectTransform inner, string name, out RectTransform viewContent)
	{
		GameObject areaGo = new(name, typeof(RectTransform));
		areaGo.transform.SetParent(inner, false);
		RectTransform area = (RectTransform)areaGo.transform;
		area.anchorMin = Vector2.zero;
		area.anchorMax = Vector2.one;
		area.offsetMin = Vector2.zero;
		area.offsetMax = new Vector2(0f, -TopBarHeight);
		viewContent = GcUi.CreateScroll(area, builtSize.x - 88f, builtSize.y - 172f - TopBarHeight);
		return areaGo;
	}

	private void SwitchView(View target)
	{
		if (CancelCapture())
		{
			return;
		}
		try
		{
			if (target == View.Mods && modsArea == null && innerRect != null)
			{
				BuildModsView(innerRect);
			}
			if (target == View.Profiles && profilesArea == null && innerRect != null)
			{
				BuildProfilesView(innerRect);
			}
		}
		catch (Exception e)
		{
			LogError("Ansicht " + target, e);
			target = View.Settings; // die Einstellungen gehen immer; sonst scheiterte jedes weitere Öffnen
		}
		view = target;
		lastView = target;
		SetActiveIf(settingsArea, target == View.Settings);
		SetActiveIf(settingsTools, target == View.Settings);
		SetActiveIf(modsArea, target == View.Mods);
		SetActiveIf(modsTools, target == View.Mods);
		SetActiveIf(profilesArea, target == View.Profiles);
		SetActiveIf(profilesTools, target == View.Profiles);
		foreach ((View tab, Text? label) in tabs)
		{
			if (label != null)
			{
				label.color = tab == target ? GUIManager.Instance.ValheimOrange : Color.white;
			}
		}
		Refresh();
	}

	private static void SetActiveIf(GameObject? go, bool active)
	{
		if (go != null)
		{
			SetActive(go, active);
		}
	}

	/// <summary>Hinweiszeile unten je Ansicht; brauchen geänderte Mods einen Neustart, steht dort, wie (über r2modman).</summary>
	private void RefreshBottom()
	{
		bool restart = ModSwitch.RestartNeeded;
		if (hintLabel != null)
		{
			string hint = capturing != null ? ui("capture_hint") : view switch
			{
				View.Mods => ui("hint_mods"),
				View.Profiles => ui("hint_profiles"),
				_ => ui("hint_config"),
			};
			if (restart && capturing == null)
			{
				hint = ui("restart_needed");
			}
			hintLabel.text = hint;
		}
	}

	// ------------------------------------------------------------------ Ansicht Mods

	private void BuildModsView(RectTransform inner)
	{
		modsArea = ViewArea(inner, "ModsArea", out RectTransform list);
		modsContent = list;
		modsNoPatcher = HintRow(list, ui("mods_no_patcher"), out Text warning);
		warning.color = OffColor;
		modRows.Clear();
		foreach (ModSwitch.Mod mod in ModSwitch.Catalog)
		{
			modRows.Add(CreateModRow(list, mod));
		}
	}

	private void BuildModsTools(RectTransform inner)
	{
		modsTools = ToolRow(inner, "ModsTools");
		modsSearch = ToolInput(modsTools.transform, ui("mods_search"), 16f, 360f);
		modsSearch.SetTextWithoutNotify(modsQuery); // nach einem Neuaufbau steht der Filter sichtbar im Feld
		modsSearch.onValueChanged.AddListener(text =>
		{
			modsQuery = NormalizeSearch(text);
			RefreshMods();
		});
		ToolButton(modsTools.transform, ui("mods_all_on"), 16f + 360f + 16f, 220f, () =>
		{
			if (ModSwitch.PatcherActive)
			{
				ModSwitch.AllOn();
				modsMessage = SaveError();
				Refresh();
			}
		});
		modsStatus = ToolStatus(modsTools.transform, 16f + 360f + 16f + 220f + 16f);
		modsTools.SetActive(false);
	}

	private ModRow CreateModRow(Transform parent, ModSwitch.Mod mod)
	{
		GameObject rowGo = new("ModRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
		rowGo.transform.SetParent(parent, false);
		HorizontalLayoutGroup layout = rowGo.GetComponent<HorizontalLayoutGroup>();
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.spacing = 12f;
		layout.padding = new RectOffset(10, 4, 6, 6);
		rowGo.GetComponent<LayoutElement>().minHeight = 58f;
		ModRow row = new() { Mod = mod, Root = rowGo };

		GameObject switchCell = FixedCell(rowGo.transform, "Switch", 40f);
		GameObject toggleGo = GUIManager.Instance.CreateToggle(switchCell.transform, 30f, 30f);
		toggleGo.transform.SetParent(switchCell.transform, false);
		toggleGo.transform.localScale = Vector3.one;
		RectTransform toggleRect = (RectTransform)toggleGo.transform;
		toggleRect.anchorMin = toggleRect.anchorMax = toggleRect.pivot = new Vector2(0.5f, 0.5f);
		toggleRect.anchoredPosition = Vector2.zero;
		if (toggleGo.transform.Find("Background") is RectTransform box)
		{
			box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
			box.anchoredPosition = Vector2.zero;
		}
		toggleGo.transform.Find("Label")?.gameObject.SetActive(false);
		row.Toggle = toggleGo.GetComponent<Toggle>();
		row.Toggle.onValueChanged.AddListener(on => OnModToggle(row, on));

		GameObject texts = Cell(rowGo.transform, 300f, 1f);
		texts.GetComponent<VerticalLayoutGroup>().spacing = 1f;
		row.Title = FlowText(texts.transform, "", 19, Color.white);
		row.Sub = FlowText(texts.transform, "", 13, MutedColor);
		row.Status = FlowText(texts.transform, "", 14, MutedColor);

		GameObject buttons = FixedCell(rowGo.transform, "Buttons", 176f);
		if (HasSettings(mod))
		{
			GcUi.CreateButton(buttons.transform, ui("mod_settings"), new Vector2(0.5f, 0.5f), Vector2.zero, 160f, 34f, () => JumpToSettings(mod));
		}
		return row;
	}

	private static GameObject FixedCell(Transform parent, string name, float width)
	{
		GameObject go = new(name, typeof(RectTransform), typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		LayoutElement layout = go.GetComponent<LayoutElement>();
		layout.minWidth = layout.preferredWidth = width;
		layout.minHeight = layout.preferredHeight = 40f;
		return go;
	}

	private bool HasSettings(ModSwitch.Mod mod) =>
		foreignMods.Any(f => string.Equals(f.Guid, mod.Guid, StringComparison.OrdinalIgnoreCase)) ||
		rows.Any(r => !r.Foreign && string.Equals(r.Guid, mod.Guid, StringComparison.OrdinalIgnoreCase));

	/// <summary>Zu den Einstellungen der Mod: Ansicht Einstellungen, Block aufgeklappt, Suche nach ihrem Namen.</summary>
	private void JumpToSettings(ModSwitch.Mod mod)
	{
		ExpandedMods.Add(mod.Guid);
		SwitchView(View.Settings);
		if (searchField != null)
		{
			searchField.text = mod.Name;
		}
		query = pendingQuery = NormalizeSearch(mod.Name);
		queryAt = -1f;
		searchClosed.Clear();
		Refresh();
	}

	private void OnModToggle(ModRow row, bool on)
	{
		if (!ModSwitch.PatcherActive || row.Mod.Protected)
		{
			Refresh();
			return;
		}
		List<ModSwitch.Mod> changed = ModSwitch.SetOff(row.Mod, !on);
		modsMessage = SaveError();
		if (modsMessage.Length == 0 && changed.Count > 1)
		{
			modsMessage = ui(on ? "mods_also_on" : "mods_also_off").Replace("$1", string.Join(", ", changed.Skip(1).Select(m => m.Name)));
		}
		log.LogInfo($"Mod {(on ? "an" : "aus")} nach dem Neustart: {string.Join(", ", changed.Select(m => m.Name))}");
		Refresh();
	}

	/// <summary>Meldung, wenn die Liste oder ein Profil nicht gespeichert werden konnte (sonst leer).</summary>
	private string SaveError() => ModSwitch.LastError.Length > 0 ? ui("save_failed").Replace("$1", ModSwitch.LastError) : "";

	private void RefreshMods()
	{
		if (modsContent == null)
		{
			return;
		}
		bool patcher = ModSwitch.PatcherActive;
		if (modsNoPatcher != null)
		{
			SetActive(modsNoPatcher, !patcher);
		}
		foreach (ModRow row in modRows)
		{
			ModSwitch.Mod mod = row.Mod;
			bool visible = modsQuery.Length == 0 || mod.Search.Contains(modsQuery);
			SetActive(row.Root, visible);
			if (!visible)
			{
				continue;
			}
			bool off = patcher && ModSwitch.IsWantedOff(mod); // ohne Vorlader wirkt die Liste nicht
			row.Toggle.SetIsOnWithoutNotify(!off);
			row.Toggle.interactable = patcher && !mod.Protected;
			row.Title.text = mod.Name + "  <size=15><color=#c8b89a>" + mod.Version + "</color></size>";
			row.Title.color = off || mod.State != ModSwitch.State.Loaded ? LockedColor : Color.white;
			string package = mod.Package.Length > 0 ? mod.Package + (mod.PackageVersion.Length > 0 ? " " + mod.PackageVersion : "") + "  ·  " : "";
			row.Sub.text = package + mod.Guid;
			row.Status.text = StatusText(mod, off, out Color color);
			row.Status.color = color;
		}
		if (modsStatus != null)
		{
			modsStatus.text = modsMessage;
		}
	}

	private string StatusText(ModSwitch.Mod mod, bool off, out Color color)
	{
		string text;
		color = MutedColor;
		if (mod.Protected)
		{
			text = ui("mod_protected");
		}
		else
		{
			switch (mod.State)
			{
				case ModSwitch.State.Loaded when off:
					text = ui("mod_off_after");
					color = WarnColor;
					break;
				case ModSwitch.State.Loaded:
					text = ui("mod_running");
					color = GoodColor;
					break;
				case ModSwitch.State.Off when !off:
					List<string> missing = ModSwitch.MissingDeps(mod);
					text = ui("mod_on_after") + (missing.Count > 0 ? " · " + ui("mod_needs").Replace("$1", string.Join(", ", missing)) : "");
					color = WarnColor;
					break;
				case ModSwitch.State.Off:
					text = ui("mod_off");
					break;
				case ModSwitch.State.Failed when off:
					text = ui("mod_off");
					break;
				case ModSwitch.State.Failed:
					text = ui("mod_failed").Replace("$1", mod.Reason.Length > 0 ? mod.Reason : ui("mod_failed_log"));
					color = OffColor;
					break;
				case ModSwitch.State.OtherProcess:
					text = ui("mod_other_process").Replace("$1", mod.Reason);
					break;
				default:
					text = off ? ui("mod_off") : ui("mod_not_loaded");
					break;
			}
		}
		// verbunden: Hat der Server die Mod, warnt das Ausschalten (sonst lehnt er womöglich die Verbindung ab)
		ZNet? net = ZNet.instance;
		if (net != null && !net.IsServer() && GcKern.ServerHas(mod.Guid))
		{
			if (off)
			{
				text += " · " + ui("mod_server_needs");
				color = OffColor;
			}
			else
			{
				text += " · " + ui("mod_on_server");
			}
		}
		return text;
	}

	// ------------------------------------------------------------------ Ansicht Profile

	private void BuildProfilesTools(RectTransform inner)
	{
		profilesTools = ToolRow(inner, "ProfilesTools");
		profileName = ToolInput(profilesTools.transform, ui("profile_name"), 16f, 320f);
		Button save = ToolButton(profilesTools.transform, ui("profile_save"), 16f + 320f + 16f, 180f, SaveProfile);
		profileSaveLabel = save.GetComponentInChildren<Text>(true);
		ToolButton(profilesTools.transform, ui("profile_paste"), 16f + 320f + 16f + 180f + 12f, 240f, PasteProfile);
		profilesStatus = ToolStatus(profilesTools.transform, 16f + 320f + 16f + 180f + 12f + 240f + 16f);
		profilesTools.SetActive(false);
	}

	private void BuildProfilesView(RectTransform inner)
	{
		profilesArea = ViewArea(inner, "ProfilesArea", out RectTransform list);
		profilesContent = list;
		profilesEmpty = HintRow(list, ui("profile_none"), out _);
		RebuildProfiles();
	}

	private void RebuildProfiles()
	{
		if (profilesContent == null)
		{
			return;
		}
		foreach (ProfileRow row in profileRows)
		{
			UnityEngine.Object.Destroy(row.Root);
		}
		profileRows.Clear();
		foreach (ModSwitch.Profile profile in ModSwitch.Profiles())
		{
			profileRows.Add(CreateProfileRow(profilesContent, profile));
		}
	}

	private ProfileRow CreateProfileRow(Transform parent, ModSwitch.Profile profile)
	{
		GameObject rowGo = new("ProfileRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
		rowGo.transform.SetParent(parent, false);
		HorizontalLayoutGroup layout = rowGo.GetComponent<HorizontalLayoutGroup>();
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.spacing = 12f;
		layout.padding = new RectOffset(10, 4, 6, 6);
		rowGo.GetComponent<LayoutElement>().minHeight = 54f;
		ProfileRow row = new() { Profile = profile, Root = rowGo };

		GameObject texts = Cell(rowGo.transform, 260f, 1f);
		FlowText(texts.transform, profile.Name, 19, Color.white);
		row.Info = FlowText(texts.transform, "", 14, MutedColor);

		GameObject buttons = FixedCell(rowGo.transform, "Buttons", 4f * 150f + 3f * 8f);
		Vector2 left = new(0f, 0.5f);
		row.Load = GcUi.CreateButton(buttons.transform, ui("profile_load"), left, new Vector2(75f, 0f), 150f, 34f, () => LoadProfile(row));
		row.Overwrite = GcUi.CreateButton(buttons.transform, ui("profile_overwrite"), left, new Vector2(75f + 158f, 0f), 150f, 34f, () => Confirm(row, "overwrite"));
		GcUi.CreateButton(buttons.transform, ui("profile_copy"), left, new Vector2(75f + 2f * 158f, 0f), 150f, 34f, () => CopyProfile(row));
		row.Delete = GcUi.CreateButton(buttons.transform, ui("profile_delete"), left, new Vector2(75f + 3f * 158f, 0f), 150f, 34f, () => Confirm(row, "delete"));
		return row;
	}

	/// <summary>Überschreiben und Löschen brauchen einen zweiten Klick binnen 4 s (Mod Menu fragte nicht nach).</summary>
	private void Confirm(ProfileRow row, string action)
	{
		if (row.Pending == action && Time.unscaledTime < row.PendingUntil)
		{
			row.Pending = "";
			bool done = action == "overwrite" ? ModSwitch.SaveProfile(row.Profile.Name) : ModSwitch.DeleteProfile(row.Profile);
			profilesMessage = !done ? SaveError()
				: ui(action == "overwrite" ? "profile_saved" : "profile_deleted").Replace("$1", row.Profile.Name);
			RebuildProfiles();
		}
		else
		{
			row.Pending = action;
			row.PendingUntil = Time.unscaledTime + 4f;
		}
		Refresh();
	}

	private void LoadProfile(ProfileRow row)
	{
		if (!ModSwitch.PatcherActive)
		{
			return;
		}
		int missing = ModSwitch.ApplyProfile(row.Profile);
		profilesMessage = missing < 0 ? SaveError()
			: missing > 0 ? ui("profile_loaded_missing").Replace("$1", row.Profile.Name).Replace("$2", missing.ToString())
			: ui("profile_loaded").Replace("$1", row.Profile.Name);
		modsMessage = "";
		Refresh();
	}

	private void CopyProfile(ProfileRow row)
	{
		GUIUtility.systemCopyBuffer = ModSwitch.ProfileText(row.Profile);
		profilesMessage = ui("profile_copied").Replace("$1", row.Profile.Name);
		Refresh();
	}

	private void SaveProfile()
	{
		string name = ModSwitch.CleanName(profileName?.text ?? "");
		if (name.Length == 0)
		{
			profilesMessage = ui("profile_bad_name");
			Refresh();
			return;
		}
		bool exists = ModSwitch.Profiles().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
		if (exists && !(savePending == name && Time.unscaledTime < savePendingUntil))
		{
			savePending = name; // gibt es schon: zweiter Klick überschreibt
			savePendingUntil = Time.unscaledTime + 4f;
			Refresh();
			return;
		}
		savePending = "";
		profilesMessage = ModSwitch.SaveProfile(name) ? ui("profile_saved").Replace("$1", name) : SaveError();
		RebuildProfiles();
		Refresh();
	}

	private void PasteProfile()
	{
		HashSet<string> guids = ModSwitch.ParseGuids(GUIUtility.systemCopyBuffer ?? "");
		if (guids.Count == 0)
		{
			profilesMessage = ui("profile_paste_empty");
			Refresh();
			return;
		}
		string name = ModSwitch.CleanName(profileName?.text ?? "");
		if (name.Length == 0)
		{
			name = ui("profile_pasted_name").Replace("$1", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
		}
		name = ModSwitch.FreeName(name); // Einfügen überschreibt nie ein vorhandenes Profil
		profilesMessage = ModSwitch.WriteProfile(name, guids) ? ui("profile_pasted").Replace("$1", name) : SaveError();
		RebuildProfiles();
		Refresh();
	}

	private void RefreshProfiles()
	{
		if (profilesContent == null)
		{
			return;
		}
		if (profilesEmpty != null)
		{
			SetActive(profilesEmpty, profileRows.Count == 0);
		}
		bool patcher = ModSwitch.PatcherActive;
		foreach (ProfileRow row in profileRows)
		{
			bool current = ModSwitch.IsCurrent(row.Profile);
			int missing = ModSwitch.MissingIn(row.Profile);
			string info = (current ? ui("profile_current") + "  " : "") + ui("profile_off").Replace("$1", row.Profile.Off.Count.ToString());
			if (missing > 0)
			{
				info += "  ·  " + ui("profile_missing").Replace("$1", missing.ToString());
			}
			row.Info.text = info;
			row.Info.color = current ? GoodColor : MutedColor;
			row.Load.interactable = patcher && !current;
			if (row.Pending.Length > 0 && Time.unscaledTime >= row.PendingUntil)
			{
				row.Pending = "";
			}
			SetLabel(row.Overwrite, ui(row.Pending == "overwrite" ? "profile_sure" : "profile_overwrite"));
			SetLabel(row.Delete, ui(row.Pending == "delete" ? "profile_sure" : "profile_delete"));
		}
		if (savePending.Length > 0 && Time.unscaledTime >= savePendingUntil)
		{
			savePending = "";
		}
		if (profileSaveLabel != null)
		{
			profileSaveLabel.text = ui(savePending.Length > 0 ? "profile_sure" : "profile_save");
		}
		if (profilesStatus != null)
		{
			profilesStatus.text = profilesMessage;
		}
	}

	private static void SetLabel(Button button, string text)
	{
		Text? label = button.GetComponentInChildren<Text>(true);
		if (label != null && label.text != text)
		{
			label.text = text;
		}
	}

	// ------------------------------------------------------------------ Taste zum Öffnen (aus Mod Menu)

	/// <summary>
	/// Taste aus der Kern-Config (Standard: keine): öffnet und schließt das Menü im Hauptmenü und im Spiel, nicht während
	/// Chat, Konsole oder eine Texteingabe offen ist. Im Einzelspieler hält das Spiel an, solange das Menü offen ist.
	/// </summary>
	public void CheckHotkey()
	{
		if (Gc.IsHeadless || !canHost || KernPlugin.MenuKey == null)
		{
			return;
		}
		BepInEx.Configuration.KeyboardShortcut key = KernPlugin.MenuKey.Value;
		if (key.MainKey == KeyCode.None || !key.IsDown())
		{
			return;
		}
		if (IsOpen)
		{
			// beim Tippen schließt nur eine Taste, die nichts schreibt (F-Taste, Strg/Alt-Kombination); im Suchfeld steht
			// der Cursor beim Öffnen immer, das darf das Schließen nicht verhindern (Code-Prüfung 25.10.2026)
			InputField? typing = Typing();
			bool search = typing == searchField || typing == modsSearch;
			if (capturing == null && (typing == null || (search && !Writes(key))))
			{
				Hide();
			}
			return;
		}
		if (Gc.TextInputActive())
		{
			return; // Chat, Konsole, Kartenmarke, Händler, Pause-Menü …
		}
		if (Show() && Game.instance != null && FejdStartup.instance == null)
		{
			Game.Pause();
			pausedByKey = true;
		}
	}

	/// <summary>Schreibt die Taste in ein Textfeld ein Zeichen (dann darf sie beim Tippen nicht schließen)?</summary>
	private static bool Writes(BepInEx.Configuration.KeyboardShortcut key)
	{
		if (key.Modifiers.Any(m => m is KeyCode.LeftControl or KeyCode.RightControl or KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.AltGr))
		{
			return false;
		}
		KeyCode main = key.MainKey;
		bool function = main >= KeyCode.F1 && main <= KeyCode.F15;
		bool navigation = main is KeyCode.Insert or KeyCode.Home or KeyCode.End or KeyCode.PageUp or KeyCode.PageDown or KeyCode.Pause or KeyCode.ScrollLock or KeyCode.Print;
		return !function && !navigation;
	}
}
