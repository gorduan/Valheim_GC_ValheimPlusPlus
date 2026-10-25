using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace GC.Shared;

/// <summary>Werte im Menü: Bedienart je Typ, Zahlen jeder Art, Grenzen, Auswahllisten, Text aus Eingabefeldern.</summary>
public sealed partial class GcMenu
{
	private enum EditorKind
	{
		Toggle,
		Cycle,
		Key,
		Number,
		Text,
		ReadOnly,
	}

	/// <summary>
	/// Bedienart: An/Aus, Klick wechselt (enum, AcceptableValueList), Taste erfassen, Zahl (− Feld +, ohne Grenzen nur
	/// das Feld), Eingabefeld für alles, was BepInEx als Text speichern kann, sonst nur lesen.
	/// </summary>
	private static EditorKind KindOf(Type type, object[]? choices)
	{
		if (type == typeof(bool))
		{
			return EditorKind.Toggle;
		}
		if (type == typeof(KeyboardShortcut) || type == typeof(KeyCode))
		{
			return EditorKind.Key;
		}
		if (choices != null && choices.Length > 0)
		{
			return EditorKind.Cycle;
		}
		if (type.IsEnum && !type.IsDefined(typeof(FlagsAttribute), false))
		{
			return EditorKind.Cycle;
		}
		if (IsNumeric(type))
		{
			return EditorKind.Number;
		}
		if (type == typeof(string) || TomlTypeConverter.CanConvert(type))
		{
			return EditorKind.Text; // auch Flags-Enums ("A, B"), Farben, Vektoren, Listen als Text
		}
		return EditorKind.ReadOnly;
	}

	private static bool IsIntegral(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
		|| type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort);

	private static bool IsNumeric(Type type) => IsIntegral(type) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);

	/// <summary>Schrittweite, die eine GC-Mod für − und + vorgibt (0 = aus den Grenzen).</summary>
	private static double ItemStep(Dictionary<string, object> item) => item.TryGetValue("step", out object s) && s is IConvertible ? Convert.ToDouble(s, CultureInfo.InvariantCulture) : 0;

	private static readonly Dictionary<ConfigEntryBase, (bool Has, double Min, double Max)> Ranges = new();

	/// <summary>Grenzen aus AcceptableValueRange, für jeden Zahlentyp (zwischengespeichert, Refresh fragt oft).</summary>
	private static bool Range(ConfigEntryBase entry, out double min, out double max)
	{
		if (!Ranges.TryGetValue(entry, out (bool Has, double Min, double Max) range))
		{
			range = ReadRange(entry.Description?.AcceptableValues);
			Ranges[entry] = range;
		}
		min = range.Min;
		max = range.Max;
		return range.Has;
	}

	private static (bool, double, double) ReadRange(AcceptableValueBase? acceptable)
	{
		for (Type? t = acceptable?.GetType(); t != null; t = t.BaseType)
		{
			if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(AcceptableValueRange<>))
			{
				try
				{
					double min = Convert.ToDouble(t.GetProperty("MinValue")?.GetValue(acceptable), CultureInfo.InvariantCulture);
					double max = Convert.ToDouble(t.GetProperty("MaxValue")?.GetValue(acceptable), CultureInfo.InvariantCulture);
					return (true, min, max);
				}
				catch
				{
					return (false, 0, 0);
				}
			}
		}
		return (false, 0, 0);
	}

	/// <summary>Erlaubte Werte aus AcceptableValueList (jeder Typ), sonst null.</summary>
	private static object[]? Choices(ConfigEntryBase entry)
	{
		AcceptableValueBase? acceptable = entry.Description?.AcceptableValues;
		for (Type? t = acceptable?.GetType(); t != null; t = t.BaseType)
		{
			if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(AcceptableValueList<>))
			{
				try
				{
					return t.GetProperty("AcceptableValues")?.GetValue(acceptable) is Array values ? values.Cast<object>().ToArray() : null;
				}
				catch
				{
					return null;
				}
			}
		}
		return null;
	}

	/// <summary>Zahl in den Typ der Einstellung (ganze Zahlen gerundet); false bei Überlauf.</summary>
	private static bool TryNumber(double number, Type type, out object? value)
	{
		value = null;
		if (double.IsNaN(number) || double.IsInfinity(number))
		{
			return false;
		}
		try
		{
			if (IsIntegral(type))
			{
				value = Convert.ChangeType(Math.Round(number), type, CultureInfo.InvariantCulture);
			}
			else if (type == typeof(decimal))
			{
				value = (decimal)number;
			}
			else
			{
				value = Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
			}
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Text aus einem Eingabefeld: Zahlen mit Komma oder Punkt (die Einheit darf dabeistehen, außerhalb der Grenzen gilt die
	/// Grenze), Text wie eingegeben, alles andere wie BepInEx es liest. false = ungültig (auch gegen AcceptableValueList).
	/// </summary>
	private static bool TryParse(Row row, string text, out object? value)
	{
		value = null;
		Type type = row.Entry.SettingType;
		try
		{
			if (IsNumeric(type))
			{
				string raw = text.Trim();
				string unit = Str(row.Item, "unit").Trim();
				if (unit.Length > 0 && raw.EndsWith(unit, StringComparison.Ordinal))
				{
					raw = raw.Substring(0, raw.Length - unit.Length);
				}
				raw = raw.Replace(" ", "").Replace('−', '-').Replace(',', '.');
				if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !TryNumber(number, type, out value))
				{
					return false;
				}
			}
			else if (type == typeof(string))
			{
				value = text;
			}
			else
			{
				value = TomlTypeConverter.ConvertToValue(text.Trim(), type);
			}
		}
		catch
		{
			return false;
		}
		if (value == null)
		{
			return false;
		}
		AcceptableValueBase? acceptable = row.Entry.Description?.AcceptableValues;
		if (acceptable != null)
		{
			if (Range(row.Entry, out _, out _))
			{
				value = acceptable.Clamp(value);
			}
			else if (!acceptable.IsValid(value))
			{
				return false;
			}
		}
		return true;
	}
}
