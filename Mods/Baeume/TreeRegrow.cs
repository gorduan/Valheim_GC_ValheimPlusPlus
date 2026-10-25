using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.ValheimPlusPlus;
using HarmonyLib;
using UnityEngine;

namespace GC.ValheimPlusPlus.TreeRegrow;

/// <summary>
/// Bäume wachsen nach (früher GC_TreeRegrow): Baumstümpfe werden nach einiger Zeit wieder zum selben Baum.
///
/// Vanilla (TreeBase.SpawnLog) setzt beim Fällen den Stumpf (m_stubPrefab) an die Stelle des Baums. Hier merkt sich
/// der Stumpf, welcher Baum dort stand, und wird nach Ablauf der Zeit durch denselben Baum mit Wachstumsanimation
/// (TreeBase.Grow) ersetzt. Rodet jemand den Stumpf, wächst nichts nach. Gebaute Teile direkt auf dem Stumpf
/// verhindern das Nachwachsen.
///
/// Nur Stümpfe, deren Baum beim Fällen gemerkt wurde, wachsen nach. Stümpfe aus der Weltgenerierung bleiben Stümpfe.
/// Wartezeit: dieselbe wie beim Setzling dieses Baums im Spiel (Plant.m_growTime bis m_growTimeMax des Setzlings,
/// der zu diesem Baum heranwächst). Ändert eine andere Mod die Wachstumszeit der Setzlinge, gilt deren Wert.
/// Bäume ohne Setzling (z. B. Sumpf, Ashlands) nutzen FallbackMinutes. OverrideMinutes größer 0 ersetzt beides.
/// Die Zeit läuft in Spielwelt-Zeit (ZNet.GetTime) und wird im Stumpf gespeichert, auch über Neustarts hinweg.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_treeregrow";
	public const string ModName = "GC_TreeRegrow";

	/// <summary>ZDO-Schlüssel wie in GC_TreeRegrow, damit vorhandene Stümpfe weiterlaufen.</summary>
	internal const string TreeKey = "gc_regrow_tree";
	internal const string TimeKey = "gc_regrow_at";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> OverrideMinutes = null!;
	internal static ConfigEntry<float> FallbackMinutes = null!;
	internal static ConfigEntry<float> Spread = null!;
	internal static ConfigEntry<bool> ShowHover = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked;
	internal static bool HoverOn => ShowHover.Value && !Blocked;

	/// <summary>Stumpf-Prefab → Bäume mit diesem Stumpf (nur für das Log).</summary>
	internal static readonly Dictionary<string, List<string>> StubToTrees = new();

	/// <summary>Baum-Prefab → Setzling, der zu diesem Baum heranwächst.</summary>
	internal static readonly Dictionary<string, Plant> TreeToSapling = new();

	internal static string? PendingTree;
	internal static Vector3 PendingPosition;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = VppPlugin.Log;
		Enabled = Gc.BindSynced(Config, "5 - Tree Regrow", "Enabled", true, "Baumstümpfe wachsen wieder zum Baum nach (wird vom Server vorgegeben).");
		OverrideMinutes = Gc.BindSynced(Config, "5 - Tree Regrow", "OverrideMinutes", 0f, "0 = Wachstumszeit des passenden Setzlings aus dem Spiel. Größer 0 = feste Minuten Spielzeit für alle Bäume (ein Valheim-Tag dauert 30 Minuten).", new AcceptableValueRange<float>(0f, 10080f));
		FallbackMinutes = Gc.BindSynced(Config, "5 - Tree Regrow", "FallbackMinutes", 60f, "Minuten für Bäume, die keinen Setzling haben.", new AcceptableValueRange<float>(1f, 10080f));
		Spread = Gc.BindSynced(Config, "5 - Tree Regrow", "RandomSpread", 0.25f, "Zufällige Abweichung für feste Zeiten (Override und Fallback), 0.25 = plus/minus 25 %. Setzling-Zeiten haben schon eine eigene Spanne.", new AcceptableValueRange<float>(0f, 0.9f));
		ShowHover = Gc.Bind(Config, "5 - Tree Regrow - Interface", "ShowHover", true, "Restzeit anzeigen, wenn man auf einen Stumpf schaut.");
	}

	/// <summary>Wartezeit in Sekunden Spielzeit für diesen Baum.</summary>
	internal static double RegrowSeconds(string? tree)
	{
		if (OverrideMinutes.Value > 0f)
		{
			return OverrideMinutes.Value * 60.0 * (1.0 + UnityEngine.Random.Range(-Spread.Value, Spread.Value));
		}
		if (tree != null && TreeToSapling.TryGetValue(tree, out Plant sapling) && sapling != null)
		{
			float min = Mathf.Min(sapling.m_growTime, sapling.m_growTimeMax);
			float max = Mathf.Max(sapling.m_growTime, sapling.m_growTimeMax);
			return Mathf.Lerp(min, max, UnityEngine.Random.value);
		}
		return FallbackMinutes.Value * 60.0 * (1.0 + UnityEngine.Random.Range(-Spread.Value, Spread.Value));
	}
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZNetSceneAwakePatch
{
	private static void Postfix(ZNetScene __instance)
	{
		if (Plugin.Blocked)
		{
			return; // die alte Einzelmod hängt ihre eigene Komponente an die Stümpfe
		}
		Plugin.StubToTrees.Clear();
		Plugin.TreeToSapling.Clear();
		foreach (GameObject prefab in __instance.m_prefabs)
		{
			if (prefab == null)
			{
				continue;
			}
			Plant? plant = prefab.GetComponent<Plant>();
			if (plant != null && plant.m_grownPrefabs != null)
			{
				foreach (GameObject grown in plant.m_grownPrefabs)
				{
					if (grown != null && grown.GetComponent<TreeBase>() != null && !Plugin.TreeToSapling.ContainsKey(grown.name))
					{
						Plugin.TreeToSapling[grown.name] = plant;
					}
				}
			}
			TreeBase? tree = prefab.GetComponent<TreeBase>();
			if (tree == null || tree.m_stubPrefab == null)
			{
				continue;
			}
			string stubName = tree.m_stubPrefab.name;
			if (!Plugin.StubToTrees.TryGetValue(stubName, out List<string> list))
			{
				Plugin.StubToTrees[stubName] = list = new List<string>();
			}
			list.Add(prefab.name);
			if (tree.m_stubPrefab.GetComponent<RegrowStub>() == null)
			{
				tree.m_stubPrefab.AddComponent<RegrowStub>();
				foreach (MonoBehaviour other in tree.m_stubPrefab.GetComponentsInChildren<MonoBehaviour>(true))
				{
					if (other is Hoverable && other is not RegrowStub)
					{
						// Valheim nimmt die erste Hover-Komponente: Dort zeigt HoverTextPatch den Status (Gorduan, 16.10.2026)
						Plugin.Log.LogInfo($"Stumpf {stubName} hat schon {other.GetType().Name} an {other.name}: Status kommt dort hin.");
					}
				}
			}
		}
		string saplings = string.Join(", ", Plugin.TreeToSapling
			.OrderBy(pair => pair.Key)
			.Select(pair => $"{pair.Key} {pair.Value.m_growTime / 60f:0}-{pair.Value.m_growTimeMax / 60f:0} Min."));
		Plugin.Log.LogInfo($"Bäume wachsen nach: {Plugin.StubToTrees.Count} Stumpf-Arten, Setzling-Zeiten für {Plugin.TreeToSapling.Count} Bäume: {saplings}");
	}
}

/// <summary>Merkt sich beim Fällen, welcher Baum den Stumpf erzeugt.</summary>
[HarmonyPatch(typeof(TreeBase), nameof(TreeBase.SpawnLog))]
internal static class SpawnLogPatch
{
	private static void Prefix(TreeBase __instance)
	{
		Plugin.PendingTree = Utils.GetPrefabName(__instance.gameObject);
		Plugin.PendingPosition = __instance.transform.position;
	}

	private static void Finalizer()
	{
		Plugin.PendingTree = null;
	}
}

public class RegrowStub : MonoBehaviour, Hoverable
{
	private ZNetView? nview;
	private string? fellenTree;
	private float hoverCheckUntil;
	private bool hoverBlocked;

	private void Awake()
	{
		// Nur merken: die ZNetView ist in Awake eventuell noch nicht bereit. Gespeichert wird in Start.
		if (Plugin.PendingTree != null && Vector3.Distance(Plugin.PendingPosition, transform.position) < 1f)
		{
			fellenTree = Plugin.PendingTree;
		}
	}

	private void Start()
	{
		nview = GetComponent<ZNetView>();
		if (nview == null || nview.GetZDO() == null)
		{
			return;
		}
		if (fellenTree != null && nview.IsOwner())
		{
			nview.GetZDO().Set(Plugin.TreeKey, fellenTree);
		}
		InvokeRepeating(nameof(Check), UnityEngine.Random.Range(3f, 10f), 15f);
	}

	private long Now => ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;

	private void Check()
	{
		if (!Plugin.On || nview == null || !nview.IsValid() || !nview.IsOwner() || ZNet.instance == null)
		{
			return;
		}
		ZDO zdo = nview.GetZDO();
		string tree = zdo.GetString(Plugin.TreeKey);
		if (string.IsNullOrEmpty(tree))
		{
			return; // nicht von einem Spieler gefällt (z. B. Stumpf aus der Weltgenerierung): wächst nicht nach
		}
		long due = zdo.GetLong(Plugin.TimeKey);
		if (due == 0L)
		{
			zdo.Set(Plugin.TimeKey, Now + (long)(Plugin.RegrowSeconds(tree) * TimeSpan.TicksPerSecond));
			return;
		}
		if (Now < due)
		{
			return;
		}
		if (Blocked())
		{
			zdo.Set(Plugin.TimeKey, Now + 10 * TimeSpan.TicksPerMinute);
			return;
		}
		Regrow(zdo);
	}

	private static int pieceMask;

	/// <summary>
	/// Steht ein gebautes Teil auf dem Stumpf? Nur feste Kollider: Wirkbereiche von Werkbank, Feuer oder Portal sind
	/// Auslöser und zählten sonst mit (Stümpfe in der Basis wuchsen nie nach, gefunden beim Review 16.10.2026).
	/// </summary>
	private bool Blocked()
	{
		if (pieceMask == 0)
		{
			pieceMask = LayerMask.GetMask("piece", "piece_nonsolid", "Default", "static_solid", "Default_small", "vehicle");
		}
		foreach (Collider collider in Physics.OverlapSphere(transform.position + Vector3.up, 1.2f, pieceMask, QueryTriggerInteraction.Ignore))
		{
			Piece? piece = collider.GetComponentInParent<Piece>();
			if (piece != null && piece.IsPlacedByPlayer())
			{
				return true;
			}
		}
		return false;
	}

	private void Regrow(ZDO zdo)
	{
		string name = zdo.GetString(Plugin.TreeKey);
		GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
		if (prefab == null)
		{
			Plugin.Log.LogWarning($"Kein Baum für Stumpf {Utils.GetPrefabName(gameObject)} gefunden, Nachwachsen übersprungen.");
			zdo.Set(Plugin.TimeKey, Now + 60 * TimeSpan.TicksPerMinute);
			return;
		}
		Vector3 position = transform.position;
		Quaternion rotation = transform.rotation;
		Vector3 scale = transform.localScale;
		ZNetScene.instance.Destroy(gameObject);
		GameObject tree = Instantiate(prefab, position, rotation);
		ZNetView treeView = tree.GetComponent<ZNetView>();
		if (treeView != null)
		{
			treeView.SetLocalScale(scale);
		}
		TreeBase grown = tree.GetComponent<TreeBase>();
		if (grown != null)
		{
			grown.Grow();
		}
	}

	/// <summary>
	/// Wachstumsstand wie bei Valheims Pflanzen „Name ( Status )“ (Gorduan, 16.10.2026): Restzeit als Countdown, oder warum er
	/// gerade nicht nachwächst. Leer bei Stümpfen, die nicht nachwachsen (Weltgenerierung).
	/// </summary>
	public string GetHoverText()
	{
		if (!Plugin.On || !Plugin.HoverOn || nview == null || !nview.IsValid() || ZNet.instance == null)
		{
			return "";
		}
		if (string.IsNullOrEmpty(nview.GetZDO().GetString(Plugin.TreeKey)))
		{
			return "";
		}
		long due = nview.GetZDO().GetLong(Plugin.TimeKey);
		if (due == 0L)
		{
			return GcLoc.Get("gctr_hover_pending");
		}
		if (Time.time >= hoverCheckUntil)
		{
			hoverCheckUntil = Time.time + 1f; // die Anzeige fragt jedes Bild
			hoverBlocked = Blocked();
		}
		if (hoverBlocked)
		{
			return GcLoc.Get("gctr_hover_blocked");
		}
		long left = due - Now;
		if (left <= 0L)
		{
			return GcLoc.Get("gctr_hover_soon"); // der Besitzer prüft alle 15 s
		}
		long total = (long)Math.Ceiling(left / (double)TimeSpan.TicksPerSecond);
		string clock = (total / 3600).ToString("00") + ":" + (total / 60 % 60).ToString("00") + ":" + (total % 60).ToString("00");
		return GcLoc.Get("gctr_hover_time", clock);
	}

	public string GetHoverName() => GcLoc.Get("gctr_name");

	public float GetHoverOffset() => 0f;
}

/// <summary>
/// Hat ein Stumpf schon Valheims HoverText, nimmt Valheim diesen (erste Hover-Komponente): Dann steht dort der
/// Wachstumsstand des Stumpfs.
/// </summary>
[HarmonyPatch(typeof(HoverText), nameof(HoverText.GetHoverText))]
internal static class HoverTextPatch
{
	private static void Postfix(HoverText __instance, ref string __result)
	{
		RegrowStub? stub = Plugin.On ? __instance.GetComponentInParent<RegrowStub>() : null;
		if (stub != null)
		{
			string own = stub.GetHoverText();
			if (own.Length > 0)
			{
				__result = own;
			}
		}
	}
}
