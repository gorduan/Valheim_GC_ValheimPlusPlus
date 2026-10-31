using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace GC.Shared;

/// <summary>
/// DeepTip (Mod „DeepTip“, GUID gorduan.deeptip), seit 1.9.0 feste Abhängigkeit aller GC-Pakete (Gorduan, 30.10.2026: „DeepTip
/// muss ohne unsere Mods funktionieren. Unsere Mods bauen nur darauf auf und nutzen es als Abhängigkeit.“):
///  * Glossar je Mod: deeptip\de.json und deeptip\en.json im Mod-Ordner, eingebettet (Directory.Build.props). Der Kern meldet
///    es an, sobald die Mod sich anmeldet (GcKern.Register); eine ausgeschaltete Mod meldet sich nicht an, ihr Glossar fehlt.
///  * Live-Werte: Jede Einstellung im Menü GC Option steht als {gc_&lt;mod&gt;_&lt;schlüssel&gt;} in den Texten, mit Einheit (z. B.
///    {gc_kisten_stationchests_distance} → „30 m“). Begriffe in Valheims Tooltips (auch die unserer Items) verlinkt DeepTip
///    selbst über die aliases der Einträge.
///  * Menü GC Option: Der Name einer Funktion öffnet ihr Fenster (gc.&lt;mod&gt;.&lt;schlüssel&gt;, sonst gc.&lt;mod&gt;).
/// </summary>
public static class GcDeepTip
{
	private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

	/// <summary>Glossar einer Mod aus ihren eingebetteten Dateien (Name „&lt;Namensraum&gt;.deeptip.&lt;sprache&gt;.json“).</summary>
	public static void RegisterMod(Assembly assembly, string rootNamespace, string owner)
	{
		string prefix = rootNamespace + ".deeptip.";
		int loaded = 0;
		foreach (string resource in assembly.GetManifestResourceNames())
		{
			if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !Registered.Add(resource))
			{
				continue;
			}
			string locale = resource.Substring(prefix.Length, resource.Length - prefix.Length - ".json".Length);
			using Stream? stream = assembly.GetManifestResourceStream(resource);
			if (stream == null)
			{
				continue;
			}
			using StreamReader reader = new(stream, Encoding.UTF8);
			if (DeepTip.Api.Register(owner, reader.ReadToEnd(), locale))
			{
				loaded++;
			}
		}
		if (loaded > 0)
		{
			KernPlugin.Log.LogInfo($"{owner}: DeepTip-Glossar angemeldet ({loaded} Sprachen).");
		}
	}

	/// <summary>Live-Wert {name} für die Glossare.</summary>
	public static void Value(string name, Func<string?> provider) => DeepTip.Api.SetValue(name, provider);

	public static bool Has(string key) => DeepTip.Api.Has(key);

	/// <summary>Maus auf dem Element öffnet das Fenster des ersten vorhandenen Eintrags. false: keiner vorhanden.</summary>
	public static bool Mark(GameObject element, params string[] keys)
	{
		foreach (string key in keys)
		{
			if (element != null && DeepTip.Api.Has(key))
			{
				DeepTip.Api.Mark(element, key);
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Wert einer Einstellung für {gc_…}: an/aus in der Sprache des Glossars, Zahlen mit Einheit, Auswahlwerte mit ihrem Text
	/// aus dem Menü („enum_&lt;Wert&gt;“ der Mod; ohne Text der Name des Werts, wie im Menü).
	/// </summary>
	internal static string Format(ConfigEntryBase entry, string unit, Func<string, string>? choiceText = null)
	{
		bool german = DeepTip.Api.Locale.StartsWith("de", StringComparison.OrdinalIgnoreCase);
		object value = entry.BoxedValue;
		string text = value switch
		{
			bool b => german ? (b ? "an" : "aus") : (b ? "on" : "off"),
			float f => Number(f, german),
			double d => Number(d, german),
			int i => i.ToString(CultureInfo.InvariantCulture),
			KeyboardShortcut shortcut => Shortcut(shortcut, german),
			KeyCode code => Key(code, german),
			_ => value?.ToString() ?? "",
		};
		if ((value is Enum || value is string) && choiceText != null && text.Length > 0)
		{
			string shown = choiceText(text);
			text = shown.EndsWith("enum_" + text, StringComparison.Ordinal) ? text : shown;
		}
		return unit.Length > 0 && value is not bool ? text + unit : text;
	}

	/// <summary>Tasten lesbar, Zusatztasten vorn: „Alt+L“ statt BepInEx' „L + LeftAlt“.</summary>
	private static string Shortcut(KeyboardShortcut shortcut, bool german)
	{
		if (shortcut.MainKey == KeyCode.None)
		{
			return Key(KeyCode.None, german);
		}
		List<string> parts = new();
		foreach (KeyCode modifier in shortcut.Modifiers)
		{
			parts.Add(Key(modifier, german));
		}
		parts.Add(Key(shortcut.MainKey, german));
		return string.Join("+", parts);
	}

	private static string Key(KeyCode code, bool german)
	{
		switch (code)
		{
			case KeyCode.None: return german ? "keine" : "none";
			case KeyCode.LeftAlt:
			case KeyCode.RightAlt: return "Alt";
			case KeyCode.LeftShift:
			case KeyCode.RightShift: return german ? "Umschalt" : "Shift";
			case KeyCode.LeftControl:
			case KeyCode.RightControl: return german ? "Strg" : "Ctrl";
			case KeyCode.Mouse0: return german ? "linke Maustaste" : "left mouse button";
			case KeyCode.Mouse1: return german ? "rechte Maustaste" : "right mouse button";
			case KeyCode.Mouse2: return german ? "mittlere Maustaste" : "middle mouse button";
			case KeyCode.Delete: return german ? "Entf" : "Delete";
			case KeyCode.Escape: return "Esc";
			case KeyCode.Space: return german ? "Leertaste" : "Space";
			case KeyCode.Return: return german ? "Eingabe" : "Enter";
		}
		if (code >= KeyCode.Alpha0 && code <= KeyCode.Alpha9)
		{
			return ((int)(code - KeyCode.Alpha0)).ToString(CultureInfo.InvariantCulture);
		}
		if (code >= KeyCode.Mouse3 && code <= KeyCode.Mouse6)
		{
			return (german ? "Maustaste " : "mouse button ") + ((int)(code - KeyCode.Mouse0) + 1).ToString(CultureInfo.InvariantCulture);
		}
		return code.ToString();
	}

	private static string Number(double value, bool german)
	{
		string text = value.ToString("0.##", CultureInfo.InvariantCulture);
		return german ? text.Replace('.', ',') : text;
	}
}
