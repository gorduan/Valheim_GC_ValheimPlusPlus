using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Kern;

/// <summary>
/// ValheimPlus im Menü GC Option (Gorduan, 20.10.2026: „auch ValheimPlus-Optionen sollten in unseren Einstellungen live
/// geändert werden und angezeigt, vorausgesetzt es ist vorgesehen“).
///
/// ValheimPlus 0.10.3 sieht Änderungen nur im Hauptmenü vor: In einer Welt sind seine Einstellungen schreibgeschützt,
/// weil die meisten erst beim Spielstart wirken (README von ValheimPlus). Danach richtet sich dieser Abschnitt:
///  * Im Spiel zeigt er nur die eingeschalteten Abschnitte mit den Werten, die gerade gelten (auf einem Server mit
///    serverSyncsConfig die des Servers), zum Lesen.
///  * Im Hauptmenü (Knopf „GC Option“) zeigt er alle Abschnitte und lässt sie ändern, solange ValheimPlus selbst Änderungen
///    zulässt (ConfigurationManagerWatcher.ReadyForEdit; mit alter valheim_plus.cfg ist es schreibgeschützt). Beim
///    Schließen wendet ValheimPlus die Änderungen selbst neu an, wie nach dem Configuration Manager.
/// Einstellungen eines Abschnitts stehen nur im Menü, solange er an ist; wird einer ein- oder ausgeschaltet, baut sich der
/// Abschnitt neu auf. Texte so, wie ValheimPlus sie mitbringt (Englisch). Texte, Listen und Flags-Aufzählungen bleiben in
/// der cfg (eine Kombination würde mit einem Klick auf einen Wert gesetzt).
/// </summary>
internal static class VPlusOptions
{
	private static ConfigFile? config;
	private static GcMenu? menu;
	private static bool dirty;
	private static MethodInfo? reapply;
	private static PropertyInfo? readyForEdit;

	internal static void Register(GcMenu gcMenu)
	{
		if (!Chainloader.PluginInfos.TryGetValue(GcKeys.VPlusGuid, out PluginInfo info) || info.Instance == null)
		{
			return;
		}
		menu = gcMenu;
		config = info.Instance.Config;
		reapply = AccessTools.Method(AccessTools.TypeByName("ValheimPlus.Configurations.BepInExConfig"), "ReapplyPatches", new[] { typeof(string) });
		readyForEdit = AccessTools.Property(AccessTools.TypeByName("ValheimPlus.Configurations.ConfigurationManagerWatcher"), "ReadyForEdit");
		if (reapply == null)
		{
			KernPlugin.Log.LogWarning("ValheimPlus: BepInExConfig.ReapplyPatches nicht gefunden. Änderungen im Menü wirken erst nach dem nächsten Start.");
		}
		config.SettingChanged += (_, e) =>
		{
			if (ZNet.instance == null)
			{
				dirty = true; // im Hauptmenü geändert: beim Schließen neu anwenden
			}
			if (e?.ChangedSetting?.Definition.Key == "enabled")
			{
				Build(); // Abschnitt an oder aus: seine Einstellungen kommen dazu oder fallen weg
			}
		};
		Build();
		gcMenu.Closed += OnClosed;
	}

	/// <summary>Abschnitt im Menü (neu) anlegen: je Abschnitt von ValheimPlus eine Gruppe, Einstellungen nur, wenn er an ist.</summary>
	private static void Build()
	{
		if (config == null || menu == null)
		{
			return;
		}
		GcMenuSection section = menu.Section("gc_vplus", 60, key => GcLoc.Get("gck_vplus_" + key), "");
		int groups = 0;
		int rows = 0;
		foreach (IGrouping<string, ConfigDefinition> part in config.Keys.GroupBy(d => d.Section).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
		{
			List<ConfigEntryBase> entries = part.Select(d => config[d]).Where(Supported).ToList();
			if (entries.Count == 0)
			{
				continue;
			}
			Dictionary<string, string> descriptions = entries.ToDictionary(e => e.Definition.Key, e => e.Description?.Description ?? "", StringComparer.Ordinal);
			string title = part.Key;
			GcMenuGroup group = section.Group("vplus_" + part.Key, () => title, key => Text(key, descriptions), "");
			ConfigEntryBase? enabled = entries.FirstOrDefault(e => e.Definition.Key == "enabled" && e.SettingType == typeof(bool));
			if (enabled != null)
			{
				// im Spiel nur eingeschaltete Abschnitte, im Hauptmenü alle
				group.Main("enabled", enabled, locked: Locked, requires: () => ZNet.instance == null || IsOn(enabled));
				rows++;
				if (!IsOn(enabled))
				{
					groups++;
					continue;
				}
			}
			foreach (ConfigEntryBase entry in entries)
			{
				if (entry == enabled)
				{
					continue;
				}
				if (enabled != null)
				{
					group.Sub(entry.Definition.Key, entry, locked: Locked);
				}
				else
				{
					group.Main(entry.Definition.Key, entry, locked: Locked);
				}
				rows++;
			}
			groups++;
		}
		KernPlugin.Log.LogDebug($"ValheimPlus im Menü GC Option: {groups} Abschnitte, {rows} Zeilen.");
	}

	private static bool IsOn(ConfigEntryBase entry) => entry.BoxedValue is bool on && on;

	/// <summary>Gesperrt in einer Welt, und immer dann, wenn ValheimPlus selbst gerade keine Änderungen zulässt.</summary>
	private static bool Locked()
	{
		if (ZNet.instance != null)
		{
			return true;
		}
		try
		{
			return readyForEdit != null && readyForEdit.GetValue(null, null) is bool ready && !ready;
		}
		catch
		{
			return false;
		}
	}

	private static bool Supported(ConfigEntryBase entry)
	{
		Type type = entry.SettingType;
		if (type.IsEnum)
		{
			return !type.IsDefined(typeof(FlagsAttribute), false);
		}
		return type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(KeyboardShortcut) || type == typeof(KeyCode);
	}

	/// <summary>Texte der Menüzeilen: Name des Schlüssels, Beschreibung von ValheimPlus, Werte von Aufzählungen wie sie heißen.</summary>
	private static string Text(string key, Dictionary<string, string> descriptions)
	{
		if (key.StartsWith("t_", StringComparison.Ordinal) && key.EndsWith("_name", StringComparison.Ordinal))
		{
			return key.Substring(2, key.Length - 7);
		}
		if (key.StartsWith("t_", StringComparison.Ordinal) && key.EndsWith("_desc", StringComparison.Ordinal))
		{
			string name = key.Substring(2, key.Length - 7);
			return descriptions.TryGetValue(name, out string text) ? FirstSentences(text) : "";
		}
		if (key.StartsWith("enum_", StringComparison.Ordinal))
		{
			return key.Substring(5);
		}
		return key;
	}

	/// <summary>Beschreibungen von ValheimPlus sind teils lang: die ersten zwei Sätze reichen im Menü.</summary>
	private static string FirstSentences(string text)
	{
		string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
		int end = -1;
		for (int i = 0, found = 0; i < flat.Length; i++)
		{
			if (flat[i] == '.' && (i + 1 == flat.Length || flat[i + 1] == ' ') && ++found == 2)
			{
				end = i + 1;
				break;
			}
		}
		return end > 0 ? flat.Substring(0, end) : flat;
	}

	private static void OnClosed()
	{
		if (!dirty || ZNet.instance != null)
		{
			return;
		}
		dirty = false;
		try
		{
			reapply?.Invoke(null, new object[] { "Einstellungen in GC Option geändert" });
		}
		catch (Exception e)
		{
			KernPlugin.Log.LogWarning($"ValheimPlus konnte die Änderungen nicht neu anwenden: {e.Message}");
		}
	}
}
