using System;
using System.Reflection;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;

namespace GC.ValheimPlusPlus;

/// <summary>
/// Verbindung der aus ValheimPlusPlus übernommenen Funktion zu ihrer Mod (GC-SDK): Log, Freigabe durch die Stufe des
/// Servers (Allowed) und ob der Server die Mod hat (OnServer). Setzt das Plugin der Mod in Awake.
/// </summary>
internal static class VppPlugin
{
	internal static ManualLogSource Log = null!;
	internal static GcModule? Module;
	internal static bool Allowed => Module == null || Module.Allowed;
	internal static bool OnServer => Module != null && Module.OnServer;
}

/// <summary>
/// Einhängungen in ValheimPlus, das nur per Reflection angesprochen wird (live läuft eine neuere V+-Fassung als die
/// dekompilierte). Review 27.10.2026: Jede Einhängung ist einzeln abgesichert, ein Fehler schaltet nur die betroffene
/// Stelle ab und Awake läuft weiter. Die Handler binden V+-Parameter über ihre Position (__0, __1 …), nicht über Namen,
/// und vor dem Einhängen wird geprüft, ob Position, Typ und Rückgabewert (__result) zur V+-Methode passen.
/// </summary>
internal static class VppPatch
{
	/// <summary>Methode suchen, ohne zu werfen (z. B. wenn V+ eine Überladung ergänzt hat). null = nicht gefunden.</summary>
	internal static MethodInfo? Find(Type? type, string name, Type[]? args = null)
	{
		if (type == null)
		{
			return null;
		}
		try
		{
			return args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args);
		}
		catch (Exception e)
		{
			VppPlugin.Log.LogWarning($"ValheimPlus: {type.Name}.{name} nicht eindeutig ({e.Message}).");
			return null;
		}
	}

	/// <summary>Einhängen. false = Methode fehlt, passt nicht oder Harmony lehnt ab (steht im Log).</summary>
	internal static bool Try(Harmony harmony, MethodBase? target, string what, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
	{
		if (target == null)
		{
			VppPlugin.Log.LogWarning($"ValheimPlus: {what} nicht gefunden, nicht eingehängt.");
			return false;
		}
		HarmonyMethod?[] handlers = { prefix, postfix, finalizer };
		foreach (HarmonyMethod? handler in handlers)
		{
			string? problem = handler?.method != null ? Mismatch(target, handler.method) : null;
			if (problem != null)
			{
				VppPlugin.Log.LogWarning($"ValheimPlus: {what} passt nicht ({problem}), nicht eingehängt.");
				return false;
			}
		}
		try
		{
			harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
			return true;
		}
		catch (Exception e)
		{
			VppPlugin.Log.LogWarning($"ValheimPlus: {what} konnte nicht eingehängt werden: {e.Message}");
			foreach (HarmonyMethod? handler in handlers)
			{
				if (handler?.method == null)
				{
					continue;
				}
				try
				{
					harmony.Unpatch(target, handler.method); // halb eingetragene Einhängung wieder entfernen
				}
				catch
				{
					// war nicht eingetragen
				}
			}
			return false;
		}
	}

	/// <summary>Was an den Positionsparametern (__0, __1 …) oder an __result nicht zur Zielmethode passt; null = passt.</summary>
	private static string? Mismatch(MethodBase target, MethodInfo handler)
	{
		ParameterInfo[] original = target.GetParameters();
		foreach (ParameterInfo parameter in handler.GetParameters())
		{
			string name = parameter.Name ?? "";
			Type type = Plain(parameter.ParameterType);
			if (name == "__result")
			{
				Type returned = target is MethodInfo method ? method.ReturnType : typeof(void);
				if (returned != type)
				{
					return $"Rückgabe {returned.Name} statt {type.Name}";
				}
			}
			else if (name.StartsWith("__", StringComparison.Ordinal) && int.TryParse(name.Substring(2), out int index))
			{
				if (index >= original.Length)
				{
					return $"Parameter {index} fehlt";
				}
				Type have = Plain(original[index].ParameterType);
				if (have != type)
				{
					return $"Parameter {index} ist {have.Name} statt {type.Name}";
				}
			}
		}
		return null;
	}

	private static Type Plain(Type type) => type.IsByRef ? type.GetElementType()! : type;
}
