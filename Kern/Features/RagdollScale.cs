using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Kern;

/// <summary>
/// Leichen mit eigener Größe liegen bei allen Spielern gleich groß (Valheim gleicht die Größe nicht ab): Wer eine Leiche
/// erzeugt, schreibt ihre Größe nach GcKeys.RagdollScale (Wächter des Walküren-Transports, Bärenjunge der Tierarten).
/// </summary>
[HarmonyPatch(typeof(Ragdoll), "Awake")]
internal static class RagdollScalePatch
{
	private static void Postfix(Ragdoll __instance)
	{
		ZNetView? nview = __instance.GetComponent<ZNetView>();
		if (nview == null || !nview.IsValid())
		{
			return;
		}
		float scale = nview.GetZDO().GetFloat(GcKeys.RagdollScaleHash, 0f);
		if (scale > 0f)
		{
			__instance.transform.localScale = Vector3.one * scale;
		}
	}
}
