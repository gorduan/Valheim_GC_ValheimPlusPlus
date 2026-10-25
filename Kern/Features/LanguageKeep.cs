using System.Collections.Generic;
using GC.Shared;
using HarmonyLib;

namespace GC.Kern;

/// <summary>
/// Sprachwechsel im Spiel (Gorduan, 18.10.2026): Valheims Localization.SetLanguage leert bei einem echten Wechsel alle Wörter
/// und lädt nur die Sprachdateien des Spiels neu. Mods, die ihre Texte nur einmal per AddWord eintragen, verlieren sie
/// bis zum Neustart; PlantEverything zeigte danach „[peGrowthTimeHoverPrefix]“ an wachsenden Pflanzen (Log Profil Default:
/// Sprache 17.10.2026 22:40 auf Englisch, 22:41 zurück auf Deutsch). Was vorher eingetragen war und danach fehlt, kommt
/// zurück, in der alten Sprache. Mods, die beim Sprachwechsel selbst neu eintragen (Jötunn, EarthWright), bleiben unberührt.
/// </summary>
[HarmonyPatch(typeof(Localization), nameof(Localization.SetLanguage))]
internal static class LanguageKeepPatch
{
	private static void Prefix(Localization __instance, out Dictionary<string, string>? __state)
	{
		__state = __instance.m_translations != null ? new Dictionary<string, string>(__instance.m_translations) : null;
	}

	private static void Postfix(Localization __instance, Dictionary<string, string>? __state)
	{
		if (__state == null || __instance.m_translations == null)
		{
			return;
		}
		int restored = 0;
		foreach (KeyValuePair<string, string> word in __state)
		{
			if (!__instance.m_translations.ContainsKey(word.Key))
			{
				__instance.m_translations[word.Key] = word.Value;
				restored++;
			}
		}
		if (restored > 0)
		{
			__instance.m_cache.EvictAll(); // schon übersetzte Texte mit „[Schlüssel]“ verwerfen
			KernPlugin.Log.LogInfo($"Sprachwechsel: {restored} Texte anderer Mods wiederhergestellt.");
		}
	}
}
