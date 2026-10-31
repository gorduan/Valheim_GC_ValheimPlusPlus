using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.ValheimPlusPlus;
using HarmonyLib;
using UnityEngine;

namespace GC.ValheimPlusPlus.LinkedChests;

/// <summary>
/// GC_LinkedChests: Verbundene Kisten erweitern den Lagerplatz.
///
/// Jede Kiste behält ihre Items in ihren eigenen Daten. Fehlt die Mod einmal, sind es wieder ganz normale Kisten,
/// nichts geht verloren. Öffnet man eine Kiste einer Gruppe, zeigt das Fenster eine gemeinsame Ansicht: alle Plätze
/// aller geladenen Kisten der Gruppe hintereinander (Hauptkiste zuerst, das Fenster scrollt). Herausnehmen,
/// Hineinlegen, Sortieren und Fixieren wirken auf die Ansicht, jede Änderung wird sofort auf die echten Kisten verteilt.
/// Platz n der Ansicht gehört immer zu derselben Kiste und demselben Platz dort: Liegt Gerste in Kiste 1, bleibt sie
/// dort, bis sie jemand verschiebt oder die Sortierung sie woanders hinlegt.
///
/// Mehrspieler: Beim Öffnen werden alle Kisten der Gruppe wie beim normalen Öffnen beim Besitzer angefragt
/// (RPC_RequestOpen). Danach gehören sie dem Spieler und gelten als benutzt, niemand sonst kann sie öffnen. Eine Kiste,
/// die gerade jemand anderes offen hat, fehlt in der Ansicht. Verliert der Spieler den Besitz einer Kiste, schließt
/// das Fenster.
///
/// Technik: Solange die Ansicht offen ist, zeigt das Feld m_inventory jeder Kiste der Gruppe auf das gemeinsame
/// Inventar der Ansicht. So landet alles, was Vanilla, ValheimPlus oder andere Mods mit diesen Kisten tun, in der
/// Ansicht und wird von dort verteilt. Gespeichert wird immer das echte Inventar der Kiste (Container.Save ersetzt).
///
/// Verbindung: Die Zweigkiste speichert die dauerhafte ID der Hauptkiste (GcIds, nicht die ZDOID, die sich bei jedem
/// Weltstart ändert). Regeln: vom Spieler gebaute Kisten, gleiche Privatsphäre, Zugriff auf beide, dieselben Wards,
/// höchstens MaxDistance auseinander.
///
/// ValheimPlus: Die Kisten bleiben einzeln. Nur solange die Ansicht offen ist, sieht V+ die Gruppe als eine Kiste (die
/// angeklickte), damit nichts doppelt gezählt wird.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_linkedchests";
	public const string ModName = "GC_LinkedChests";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && VppPlugin.Allowed && VppPlugin.OnServer;
	internal static ConfigEntry<KeyboardShortcut> LinkKey = null!;
	internal static ConfigEntry<float> MaxDistance = null!;

	private static Container? selectedMaster;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = VppPlugin.Log;
		Enabled = Gc.BindSynced(Config, "3 - Linked Chests", "Enabled", true, "Verbundene Kisten: eine Kiste öffnen zeigt den Inhalt aller verbundenen Kisten (wird vom Server vorgegeben).");
		LinkKey = Gc.Bind(Config, "3 - Linked Chests", "LinkKey", new KeyboardShortcut(KeyCode.K, KeyCode.LeftAlt), "Auf eine Kiste schauen und diese Tasten drücken: Hauptkiste wählen, Kiste verbinden oder lösen.");
		MaxDistance = Gc.BindSynced(Config, "3 - Linked Chests", "MaxDistance", 50f, "Größte Entfernung zwischen Hauptkiste und verbundener Kiste in Metern.", new AcceptableValueRange<float>(2f, 200f));
		VPlusDedupe.Apply(harmony);
		AppDomain.CurrentDomain.SetData(GcKeys.LinkedViewHealthySlot, new Func<bool>(LinkedView.EnsureHealthy)); // GC_Client: vor Sortieren und Vernichten
	}

	internal static void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		LinkedView.Tick();
		if (Player.m_localPlayer == null)
		{
			return;
		}
		if (!On)
		{
			selectedMaster = null;
			GroupHighlight.Update(null, null); // Markierungen entfernen, falls die Funktion gerade abgeschaltet wurde
			return;
		}
		if (StationStorage.StationWindow.IsOpen || StationStorage.ChestPicker.Active)
		{
			GroupHighlight.Update(null, null); // das Gebäude-Menü färbt Kisten selbst, nicht dazwischenfunken
			return;
		}
		Container? hovered = Gc.Hovered<Container>();
		if (!GcWard.IsPlayerChest(hovered))
		{
			hovered = null;
		}
		GroupHighlight.Update(hovered, selectedMaster);
		if (hovered == null || !Gc.Down(LinkKey) || InventoryGui.IsVisible())
		{
			return;
		}
		if (!PrivateArea.CheckAccess(hovered.transform.position))
		{
			return;
		}
		if (Links.HasLink(hovered))
		{
			if (Links.Unlink(hovered))
			{
				Gc.Center(GcLoc.Get("gclc_unlinked"));
			}
			return;
		}
		long playerId = Player.m_localPlayer.GetPlayerID();
		if (selectedMaster == hovered)
		{
			selectedMaster = null;
			Gc.Center(GcLoc.Get("gclc_master_cleared"));
			return;
		}
		if (selectedMaster == null || !selectedMaster.m_nview.IsValid())
		{
			if (!hovered.CheckAccess(playerId))
			{
				Gc.Center("$msg_cantopen");
				return;
			}
			selectedMaster = hovered;
			Gc.Center(GcLoc.Get("gclc_master_selected", Localization.instance.Localize(hovered.m_name), LinkKey.Value.ToString()));
			return;
		}
		TryLink(hovered, selectedMaster, playerId);
	}

	private static void TryLink(Container proxy, Container master, long playerId)
	{
		if (Vector3.Distance(master.transform.position, proxy.transform.position) > MaxDistance.Value)
		{
			Gc.Center(GcLoc.Get("gclc_too_far", MaxDistance.Value.ToString("0")));
			return;
		}
		if (Links.IsMaster(proxy))
		{
			Gc.Center(GcLoc.Get("gclc_is_master"));
			return;
		}
		if (Links.HasLink(master))
		{
			selectedMaster = null; // inzwischen selbst verbunden: keine Gruppen in Gruppen
			Gc.Center(GcLoc.Get("gclc_master_linked"));
			return;
		}
		if (proxy.m_privacy != master.m_privacy || !proxy.CheckAccess(playerId) || !master.CheckAccess(playerId))
		{
			Gc.Center(GcLoc.Get("gclc_privacy"));
			return;
		}
		if (!PrivateArea.CheckAccess(master.transform.position) || !GcWard.SameWards(master.transform.position, proxy.transform.position))
		{
			Gc.Center(GcLoc.Get("gclc_wards"));
			return;
		}
		if (Links.Link(proxy, master))
		{
			// der Besitzer setzt die Verbindung, bei einem anderen Spieler kommt sie erst gleich an: dann mitzählen
			List<Container> group = Links.Group(master, true);
			Gc.Center(GcLoc.Get("gclc_linked", group.Count + (group.Contains(proxy) ? 0 : 1)));
		}
	}
}

/// <summary>Verbindungen: welche geladenen Kisten zu einer Gruppe gehören.</summary>
internal static class Links
{
	/// <summary>RPC an den Besitzer einer Kiste: Hauptkisten-ID setzen oder lösen (Review 27.10.2026, statt ClaimOwnership).</summary>
	internal const string RpcSetMaster = "GC_LinkMaster";

	private static readonly HashSet<Container> Known = new();

	/// <summary>
	/// Verzeichnis Hauptkisten-ID → damit verbundene geladene Kisten, höchstens alle 0,5 s neu aufgebaut (Review 27.10.2026:
	/// Group lief bei jedem Aufruf über alle geladenen Kisten, in der V+-Automatik und im Hover-Text sehr oft je Sekunde).
	/// Eigene Änderungen und neue oder entfernte Kisten bauen es sofort neu, Änderungen anderer Spieler gelten nach
	/// höchstens 0,5 s.
	/// </summary>
	private static readonly Dictionary<string, List<Container>> ByRoot = new();
	private static float indexUntil = -1f;
	private const float IndexLifetime = 0.5f;

	public static void Register(Container c)
	{
		GcIds.Track(c, c.m_nview);
		Known.Add(c);
		Invalidate();
	}

	public static void Forget(Container c)
	{
		Known.Remove(c);
		GcIds.Untrack(c);
		Invalidate();
	}

	public static void Invalidate() => indexUntil = -1f;

	private static float retrackUntil = -1f;

	/// <summary>
	/// Kiste zur dauerhaften ID. GcIds kennt eine ID nur, wenn die Kiste hier lud (Register) oder die ID hier vergeben wurde;
	/// hat ein anderer Spieler sie später vergeben (erstes Verbinden, erste Zuweisung), fände GcIds.Find sie bis zum
	/// nächsten Laden nicht, und Gruppe oder Zuweisung fehlten. Fehlt eine ID, werden deshalb höchstens alle 0,5 s alle
	/// geladenen Kisten neu eingetragen (Review 27.10.2026, beim Prüfen des Datenabgleichs gefunden).
	/// </summary>
	public static Container? Find(string id)
	{
		Container? found = GcIds.Find(id);
		if (found != null || string.IsNullOrEmpty(id) || Time.unscaledTime < retrackUntil)
		{
			return found;
		}
		retrackUntil = Time.unscaledTime + IndexLifetime;
		Known.RemoveWhere(k => k == null);
		foreach (Container k in Known)
		{
			GcIds.Track(k, k.m_nview);
		}
		return GcIds.Find(id);
	}

	private static Dictionary<string, List<Container>> Index()
	{
		if (Time.unscaledTime < indexUntil)
		{
			return ByRoot;
		}
		indexUntil = Time.unscaledTime + IndexLifetime;
		ByRoot.Clear();
		Known.RemoveWhere(k => k == null);
		foreach (IGrouping<string, Container> linked in Known.Where(k => k.m_nview != null && k.m_nview.IsValid()).GroupBy(MasterId))
		{
			if (linked.Key.Length > 0)
			{
				ByRoot[linked.Key] = linked.OrderBy(OwnId, StringComparer.Ordinal).ToList();
			}
		}
		return ByRoot;
	}

	public static string OwnId(Container c) => GcIds.Read(c.m_nview);

	public static string MasterId(Container c)
	{
		return c != null && c.m_nview != null && c.m_nview.IsValid() ? c.m_nview.GetZDO().GetString(GcKeys.LinkMaster) : "";
	}

	public static bool HasLink(Container c) => MasterId(c).Length > 0;

	private static string RootId(Container c)
	{
		string master = MasterId(c);
		return master.Length > 0 ? master : OwnId(c);
	}

	/// <summary>Ist die Kiste Hauptkiste anderer geladener Kisten?</summary>
	public static bool IsMaster(Container c)
	{
		string id = OwnId(c);
		return id.Length > 0 && Known.Any(k => k != null && k != c && MasterId(k) == id);
	}

	/// <summary>
	/// Geladene Kisten der Gruppe: Hauptkiste zuerst, dann nach ihrer ID (feste Reihenfolge). Ohne Gruppe nur die Kiste
	/// selbst. fresh = Verzeichnis jetzt neu aufbauen (beim Öffnen der Ansicht, nach eigenem Verbinden).
	/// </summary>
	public static List<Container> Group(Container c, bool fresh = false)
	{
		List<Container> single = new() { c };
		if (!Plugin.On || c == null || !GcWard.IsPlayerChest(c))
		{
			return single;
		}
		string root = RootId(c);
		if (root.Length == 0)
		{
			return single;
		}
		if (fresh)
		{
			Invalidate();
		}
		List<Container> members = new();
		if (Index().TryGetValue(root, out List<Container> linked))
		{
			foreach (Container k in linked)
			{
				if (k != null && k.m_nview != null && k.m_nview.IsValid())
				{
					members.Add(k); // Kopie: Aufrufer dürfen die Liste behalten, das Verzeichnis bleibt unberührt
				}
			}
		}
		Container? master = Find(root);
		if (master != null && !members.Contains(master))
		{
			members.Insert(0, master);
		}
		return members.Count > 1 && members.Contains(c) ? members : single;
	}

	public static bool Link(Container proxy, Container master)
	{
		// Die IDs vergibt der Kern (GcIds.Ensure, beim ersten Mal mit Besitzübernahme), die Verbindung setzt der Besitzer
		string? masterId = GcIds.Ensure(master, master.m_nview);
		ZNetView own = proxy.m_nview;
		if (masterId == null || GcIds.InUse(own) || GcIds.Ensure(proxy, own) == null)
		{
			Gc.Center(GcLoc.Get("gclc_busy"));
			return false;
		}
		SetMaster(own, masterId);
		return true;
	}

	public static bool Unlink(Container proxy)
	{
		ZNetView own = proxy.m_nview;
		if (own == null || !own.IsValid() || GcIds.InUse(own) || proxy.m_inUse)
		{
			Gc.Center(GcLoc.Get("gclc_busy"));
			return false;
		}
		SetMaster(own, "");
		return true;
	}

	/// <summary>
	/// Verbindung beim Besitzer der Kiste setzen, wie Rpcs.Assign im Gebäude-Menü (Review 27.10.2026). Vorher übernahm der
	/// Spieler den Besitz und schrieb seinen womöglich veralteten Stand; hatte der bisherige Besitzer schon dieselbe
	/// DataRevision verschickt, verwarf der Server die Änderung und die Stände liefen auseinander. Nur ohne Besitzer
	/// übernimmt der Spieler die Kiste selbst, dann gibt es keinen fremden Stand.
	/// </summary>
	private static void SetMaster(ZNetView view, string masterId)
	{
		if (!view.GetZDO().HasOwner())
		{
			view.ClaimOwnership();
		}
		view.InvokeRPC(RpcSetMaster, masterId); // an den Besitzer; sind wir das selbst, läuft es sofort
		Invalidate();
	}

	/// <summary>Beim Besitzer der Kiste: Verbindung setzen oder lösen.</summary>
	internal static void OnSetMaster(ZNetView view, string masterId)
	{
		if (view != null && view.IsValid() && view.IsOwner())
		{
			view.GetZDO().Set(GcKeys.LinkMaster, masterId);
			Invalidate();
		}
	}
}

/// <summary>
/// Die gemeinsame Ansicht einer Gruppe. Platz n der Ansicht (Zeile mal Breite plus Spalte) gehört zur Kiste, in deren
/// Bereich n fällt: Kiste 1 belegt die ersten Breite·Höhe Plätze, Kiste 2 die nächsten und so weiter. Plätze hinter
/// dem letzten Bereich gibt es nicht (gesperrt und im Fenster ausgeblendet).
/// </summary>
internal static class LinkedView
{
	private sealed class Member
	{
		public Container Chest = null!;
		public Inventory Real = null!;
		public int Order;
		public int Offset;
		public int Width = 1;
		public int Capacity;
		public int Synced;
		public bool InView;
	}

	internal static Container? Shown;
	internal static Inventory? Virtual;

	private static readonly List<Container> GroupOrder = new();
	private static readonly List<Member> Members = new();
	private static readonly HashSet<Container> Pending = new();
	private static readonly HashSet<Container> Granted = new();
	private static readonly List<ItemDrop.ItemData> Homeless = new();

	/// <summary>Eigene Öffnen-Anfragen für weitere Kisten (Zeitpunkt). Kommt die Antwort zu spät, öffnet sie kein eigenes Fenster.</summary>
	private static readonly Dictionary<Container, float> Recent = new();
	private const float LateAnswerWindow = 30f;

	private static int width = 1;
	private static int total;
	private static float startedAt;
	private static bool applying;
	private static bool rebuildWanted;
	private static bool built;
	private static bool reportedBusy;
	private static int batchDepth;
	private static bool batchDirty;

	/// <summary>Nächste Prüfung, ob eine echte Kiste an der Ansicht vorbei verändert wurde (Review 27.10.2026: nicht mehr jedes Bild).</summary>
	private static float nextOutsideCheck;
	private const float OutsideCheckInterval = 0.5f;

	public static bool Active => Shown != null && Virtual != null && built;

	public static bool IsVirtual(Inventory? inventory) => inventory != null && Virtual != null && ReferenceEquals(inventory, Virtual);

	/// <summary>Echtes Inventar einer Kiste, solange ihr Feld auf die Ansicht zeigt (sonst null).</summary>
	public static Inventory? RealOf(Container c)
	{
		foreach (Member m in Members)
		{
			if (m.InView && m.Chest == c)
			{
				return m.Real;
			}
		}
		return null;
	}

	/// <summary>Während die Ansicht offen ist, steht die angeklickte Kiste für die ganze Gruppe (für Kistenlisten).</summary>
	public static Container Canonical(Container c) => Active && RealOf(c) != null ? Shown! : c;

	/// <summary>
	/// Kistenliste für ValheimPlus oder die Gebäude-Kisten: immer eine neue Liste (Zwischenspeicher von V+ bleiben
	/// unverändert). Ohne nicht geladene Kisten und ohne Kisten, die gerade jemand anderes offen hat (dessen Stand gilt,
	/// eine Änderung hier ginge verloren oder verdoppelte Items). Solange die Ansicht offen ist, steht die angeklickte
	/// Kiste für die ganze Gruppe; ist eine Kiste der Gruppe für die Automatik gesperrt, fehlt die ganze Ansicht.
	/// </summary>
	public static void CanonicalizeList(ref List<Container> list)
	{
		if (list == null || list.Count == 0)
		{
			return;
		}
		EnsureHealthy();
		bool locked = ViewLocked();
		List<Container> result = new(list.Count);
		HashSet<Container> seen = new();
		HashSet<Inventory> inventories = new();
		foreach (Container chest in list)
		{
			if (chest == null || !Usable(chest))
			{
				continue;
			}
			Container target = Canonical(chest);
			if (locked && target == Shown)
			{
				continue;
			}
			Inventory? inventory = target.GetInventory();
			if (inventory != null && seen.Add(target) && inventories.Add(inventory))
			{
				result.Add(target);
			}
		}
		list = result;
	}

	/// <summary>Darf eine Kistenliste diese Kiste benutzen? Geladen (sonst würde ein Speichern die Kiste leeren) und nicht bei jemand anderem offen.</summary>
	public static bool Usable(Container c)
	{
		ZNetView? view = c.m_nview;
		if (view == null || !view.IsValid() || c.m_lastRevision == uint.MaxValue)
		{
			return false;
		}
		return view.IsOwner() || view.GetZDO().GetInt(ZDOVars.s_inUse) != 1;
	}

	/// <summary>Ist eine Kiste der offenen Ansicht für die Automatik gesperrt (Alt+L)?</summary>
	public static bool ViewLocked()
	{
		if (!Active)
		{
			return false;
		}
		foreach (Member m in Members)
		{
			if (m.InView && Gc.IsAutoLocked(m.Chest))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Die Kiste steht gerade für die ganze offene Ansicht und eine Kiste davon ist gesperrt.</summary>
	public static bool LockedView(Container c) => Active && c == Shown && ViewLocked();

	/// <summary>
	/// Gehören alle Kisten der Ansicht noch uns? Sonst sofort schließen, bevor sich etwas ändert (eine Änderung an einer
	/// fremden Kiste ginge verloren oder verdoppelte Items). false = gerade geschlossen.
	/// </summary>
	public static bool EnsureHealthy()
	{
		if (Shown == null)
		{
			return true;
		}
		foreach (Member m in Members)
		{
			if (m.Chest == null || m.Chest.m_nview == null || !m.Chest.m_nview.IsValid() || !m.Chest.m_nview.IsOwner())
			{
				Gc.Center(GcLoc.Get("gclc_view_lost"));
				Close();
				return false;
			}
		}
		return true;
	}

	/// <summary>Fenster schließen (über Vanilla, falls es diese Ansicht zeigt) und die Ansicht beenden.</summary>
	private static void Close()
	{
		InventoryGui gui = InventoryGui.instance;
		if (gui != null && Shown != null && gui.m_currentContainer == Shown)
		{
			gui.CloseContainer(); // CloseContainerPatch beendet die Ansicht
		}
		End();
	}

	/// <summary>Eine Kiste der offenen Gruppe wird zerstört: erst die Ansicht schließen, damit nur ihre eigenen Items fallen.</summary>
	public static void Release(Container c)
	{
		if (Shown != null && (c == Shown || Pending.Contains(c) || Granted.Contains(c) || Members.Any(m => m.Chest == c)))
		{
			Close();
		}
	}

	/// <summary>Der Spieler benutzt eine Kiste selbst: deren nächste Antwort gehört ihm, nicht einer früheren Anfrage der Ansicht.</summary>
	public static void UserRequested(Container c) => Recent.Remove(c);

	/// <summary>Viele Änderungen nacheinander (alles nehmen, Stapel ablegen): erst am Ende einmal verteilen und speichern.</summary>
	public static void BeginBatch() => batchDepth++;

	public static void EndBatch()
	{
		if (batchDepth == 0 || --batchDepth > 0 || !batchDirty)
		{
			return;
		}
		batchDirty = false;
		if (Active)
		{
			Distribute();
		}
	}

	// ------------------------------------------------------------------ Ablauf

	public static void Begin(Container shown)
	{
		End();
		List<Container> group = Links.Group(shown, true); // beim Öffnen immer den aktuellen Stand
		if (group.Count < 2 || Game.instance == null)
		{
			return;
		}
		Shown = shown;
		GroupOrder.AddRange(group);
		Inventory view = new(shown.m_name, shown.m_bkg, 1, 1);
		view.m_onChanged = (Action)Delegate.Combine(view.m_onChanged, new Action(() => OnVirtualChanged(view))); // nur diese Ansicht, keine abgelöste
		Virtual = view;
		long playerId = Game.instance.GetPlayerProfile().GetPlayerID();
		bool warded = false;
		foreach (KeyValuePair<Container, float> old in Recent.Where(r => r.Key == null || Time.time - r.Value > LateAnswerWindow).ToList())
		{
			Recent.Remove(old.Key);
		}
		foreach (Container other in group)
		{
			if (other == shown || other.m_nview == null || !other.m_nview.IsValid())
			{
				continue;
			}
			if (!PrivateArea.CheckAccess(other.transform.position, 0f, false))
			{
				warded = true; // ein später gesetzter fremder Ward schützt auch eine verbundene Kiste
				continue;
			}
			Pending.Add(other);
			Recent[other] = Time.time;
			other.m_nview.InvokeRPC("RPC_RequestOpen", playerId); // wie normales Öffnen: Besitzer prüft „benutzt" und gibt die Kiste ab
		}
		if (warded)
		{
			Gc.Center(GcLoc.Get("gclc_view_ward"));
		}
		startedAt = Time.time;
		rebuildWanted = true;
	}

	/// <summary>
	/// Antwort auf eine Öffnen-Anfrage. true = gehört zur Ansicht oder ist eine verspätete Antwort auf eine eigene
	/// Anfrage: nicht als eigenes Fenster öffnen (das würde die Ansicht ersetzen).
	/// </summary>
	public static bool HandleOpenResponse(Container c, bool granted)
	{
		if (Pending.Remove(c))
		{
			Recent.Remove(c);
			if (granted)
			{
				Granted.Add(c); // gehört uns erst, wenn die Daten mit dem neuen Besitzer angekommen sind
			}
			else
			{
				ReportBusy();
			}
			return true;
		}
		if (Shown != null && c != Shown && (Granted.Contains(c) || Members.Any(m => m.Chest == c)))
		{
			return true; // schon Teil der offenen Ansicht
		}
		if (Recent.TryGetValue(c, out float requested))
		{
			Recent.Remove(c);
			return Time.time - requested < LateAnswerWindow;
		}
		return false;
	}

	private static void ReportBusy()
	{
		if (!reportedBusy)
		{
			reportedBusy = true;
			Gc.Center(GcLoc.Get("gclc_view_busy"));
		}
	}

	public static void Tick()
	{
		if (Shown == null)
		{
			return;
		}
		InventoryGui gui = InventoryGui.instance;
		if (gui == null || gui.m_currentContainer != Shown || Shown.m_nview == null || !Shown.m_nview.IsValid())
		{
			End();
			return;
		}
		// die angeklickte Kiste: Vanilla übernimmt sie, sobald sie uns gehört (kann nach eigenen Kisten der Gruppe kommen)
		if (Shown.m_nview.IsOwner())
		{
			Acquire(Shown, false);
		}
		foreach (Container c in Granted.Count > 0 ? Granted.ToList() : NoContainers)
		{
			if (c == null || c.m_nview == null || !c.m_nview.IsValid())
			{
				Granted.Remove(c!);
				continue;
			}
			if (c.m_nview.IsOwner())
			{
				Granted.Remove(c);
				Acquire(c, true);
			}
		}
		if (Pending.Count > 0 && Time.time - startedAt > 4f)
		{
			Pending.Clear(); // keine Antwort: diese Kisten fehlen in der Ansicht
			ReportBusy();
		}
		if (Granted.Count > 0 && Time.time - startedAt > 10f)
		{
			Granted.Clear(); // zugesagt, aber die Daten kamen nicht an
			ReportBusy();
		}
		if (!EnsureHealthy())
		{
			return; // ohne Besitz könnten Änderungen nicht gespeichert werden
		}
		// Fingerabdrücke aller Mitglieder höchstens alle 0,5 s bilden (Review 27.10.2026: vorher jedes Bild über alle Items)
		bool checkOutside = built && Time.unscaledTime >= nextOutsideCheck;
		if (checkOutside)
		{
			nextOutsideCheck = Time.unscaledTime + OutsideCheckInterval;
		}
		foreach (Member m in Members)
		{
			if (m.Chest != Shown)
			{
				m.Chest.SetInUse(true);
			}
			if (checkOutside && m.InView && Signature(m.Real.m_inventory) != m.Synced)
			{
				rebuildWanted = true; // eine echte Kiste wurde an der Ansicht vorbei verändert
			}
		}
		if (rebuildWanted && HasMember(Shown) && CanRebuild(gui))
		{
			rebuildWanted = false;
			Rebuild();
		}
	}

	private static readonly List<Container> NoContainers = new();

	private static bool HasMember(Container c)
	{
		foreach (Member m in Members)
		{
			if (m.Chest == c)
			{
				return true;
			}
		}
		return false;
	}

	private static void Acquire(Container c, bool load)
	{
		if (HasMember(c) || IsVirtual(c.m_inventory))
		{
			return;
		}
		if (load)
		{
			c.Load(); // neueste Daten vom bisherigen Besitzer (noch nicht benutzt, also wird geladen)
			c.SetInUse(true);
		}
		Members.Add(new Member { Chest = c, Real = c.m_inventory, Order = Math.Max(0, GroupOrder.IndexOf(c)) });
		rebuildWanted = true;
	}

	/// <summary>Nicht neu aufbauen, solange ein Item am Mauszeiger hängt oder der Teilen-Dialog offen ist (die Items würden ersetzt).</summary>
	private static bool CanRebuild(InventoryGui gui) => gui.m_dragItem == null && gui.m_dragGo == null && (gui.m_splitDialog == null || !gui.m_splitDialog.IsActive);

	public static void End()
	{
		if (Shown == null && Members.Count == 0)
		{
			return;
		}
		Inventory? old = Virtual;
		InventoryGui gui = InventoryGui.instance;
		if (gui != null && old != null)
		{
			// ein Item am Mauszeiger oder im Teilen-Dialog aus der Ansicht darf nicht in einer verwaisten Ansicht landen
			if (gui.m_dragInventory == old)
			{
				gui.SetupDragItem(null, null, 1);
			}
			if (gui.m_splitInventory == old)
			{
				gui.OnSplitCancel();
			}
		}
		bool shownStillOpen = gui != null && gui.m_currentContainer == Shown;
		foreach (Member m in Members)
		{
			if (m.Chest == null)
			{
				continue;
			}
			if (IsVirtual(m.Chest.m_inventory))
			{
				m.Chest.m_inventory = m.Real; // vor dem Freigeben zurück, damit späteres Laden ins echte Inventar geht
			}
			if (m.Chest == Shown && shownStillOpen)
			{
				continue; // die angeklickte Kiste gibt Vanilla beim Schließen frei
			}
			if (m.Chest.m_nview != null && m.Chest.m_nview.IsValid() && m.Chest.m_nview.IsOwner())
			{
				m.Chest.SetInUse(false);
			}
			else
			{
				m.Chest.m_inUse = false; // Besitz verloren: wenigstens das lokale Feld, sonst blockiert es späteres Laden
			}
		}
		old?.m_inventory.Clear(); // eine verwaiste Ansicht ist leer: aus ihr lässt sich nichts mehr verdoppeln
		DropHomeless();
		Members.Clear();
		GroupOrder.Clear();
		Pending.Clear();
		Granted.Clear();
		Shown = null;
		Virtual = null;
		built = false;
		rebuildWanted = false;
		reportedBusy = false;
		batchDepth = 0;
		batchDirty = false;
		total = 0;
		width = 1;
	}

	/// <summary>Sicherheitsnetz: Items ohne gültigen Platz (sollte nie vorkommen) nicht verlieren, sondern vor den Spieler legen.</summary>
	private static void DropHomeless()
	{
		Player player = Player.m_localPlayer;
		foreach (ItemDrop.ItemData item in Homeless)
		{
			if (player != null)
			{
				Vector3 at = player.transform.position + player.transform.forward + Vector3.up;
				ItemDrop.DropItem(item, item.m_stack, at, Quaternion.identity);
				Plugin.Log.LogWarning($"Verbundene Kisten: {item.m_shared.m_name} x{item.m_stack} hatte keinen Platz und liegt jetzt am Boden.");
			}
		}
		Homeless.Clear();
	}

	// ------------------------------------------------------------------ Ansicht aufbauen und verteilen

	/// <summary>Fingerabdruck eines Kisteninhalts (unabhängig von der Reihenfolge der Liste), nur zum Erkennen von Änderungen.</summary>
	private static int Signature(List<ItemDrop.ItemData> items)
	{
		unchecked
		{
			int hash = items.Count * 7919;
			foreach (ItemDrop.ItemData item in items)
			{
				int h = Mix(item.m_shared.m_name?.GetHashCode() ?? 0, item.m_gridPos.x);
				h = Mix(h, item.m_gridPos.y);
				h = Mix(h, item.m_stack);
				h = Mix(h, item.m_quality);
				h = Mix(h, item.m_variant);
				h = Mix(h, item.m_durability.GetHashCode());
				if (item.m_customData != null)
				{
					foreach (KeyValuePair<string, string> pair in item.m_customData)
					{
						h += Mix(pair.Key?.GetHashCode() ?? 0, pair.Value?.GetHashCode() ?? 0);
					}
				}
				hash += Mix(h, 0x2545F491);
			}
			return hash;
		}
	}

	private static int Mix(int a, int b)
	{
		unchecked
		{
			uint h = (uint)a * 0x9E3779B1u ^ (uint)b;
			h ^= h >> 15;
			h *= 0x85EBCA77u;
			h ^= h >> 13;
			return (int)h;
		}
	}

	/// <summary>Ein Platz einer echten Kiste (Zeile mal Breite plus Spalte) mit dem Item der Ansicht, das dorthin gehört (noch keine Kopie).</summary>
	private readonly struct Slot
	{
		public readonly int Local;
		public readonly ItemDrop.ItemData Item;

		public Slot(int local, ItemDrop.ItemData item)
		{
			Local = local;
			Item = item;
		}
	}

	/// <summary>Echte Items einer Kiste nach Platz. null = ein Item liegt außerhalb oder ein Platz ist doppelt belegt: dann ganz neu schreiben.</summary>
	private static Dictionary<int, ItemDrop.ItemData>? ByLocal(Member m)
	{
		Dictionary<int, ItemDrop.ItemData> result = new(m.Real.m_inventory.Count);
		foreach (ItemDrop.ItemData item in m.Real.m_inventory)
		{
			Vector2i pos = item.m_gridPos;
			int local = pos.y * m.Width + pos.x;
			if (pos.x < 0 || pos.y < 0 || pos.x >= m.Width || local >= m.Capacity || result.ContainsKey(local))
			{
				return null;
			}
			result[local] = item;
		}
		return result;
	}

	/// <summary>Genau derselbe Inhalt? (Vergleich Feld für Feld nach Platz, nicht über Fingerabdrücke, und ohne Kopien.)</summary>
	private static bool SameContent(Dictionary<int, ItemDrop.ItemData> real, List<Slot> wanted)
	{
		if (real.Count != wanted.Count)
		{
			return false;
		}
		foreach (Slot slot in wanted)
		{
			if (!real.TryGetValue(slot.Local, out ItemDrop.ItemData other) || !SameItem(slot.Item, other))
			{
				return false;
			}
		}
		return true;
	}

	private static bool SameItem(ItemDrop.ItemData a, ItemDrop.ItemData b)
	{
		if (a.m_shared.m_name != b.m_shared.m_name || a.m_stack != b.m_stack || a.m_quality != b.m_quality || a.m_variant != b.m_variant
			|| !Mathf.Approximately(a.m_durability, b.m_durability) || a.m_worldLevel != b.m_worldLevel || a.m_crafterID != b.m_crafterID
			|| a.m_crafterName != b.m_crafterName || a.m_pickedUp != b.m_pickedUp || a.m_dropPrefab != b.m_dropPrefab)
		{
			return false;
		}
		int countA = a.m_customData?.Count ?? 0;
		int countB = b.m_customData?.Count ?? 0;
		if (countA != countB)
		{
			return false;
		}
		if (countA > 0)
		{
			foreach (KeyValuePair<string, string> pair in a.m_customData!)
			{
				if (!b.m_customData!.TryGetValue(pair.Key, out string value) || value != pair.Value)
				{
					return false;
				}
			}
		}
		return true;
	}

	private static void Rebuild()
	{
		Inventory view = Virtual!;
		Members.Sort((a, b) => a.Order.CompareTo(b.Order));
		width = 1;
		total = 0;
		foreach (Member m in Members)
		{
			int w = Math.Max(1, m.Real.GetWidth());
			int h = Math.Max(1, m.Real.GetHeight());
			m.Width = w;
			m.Offset = total;
			m.Capacity = w * h;
			total += m.Capacity;
			width = Math.Max(width, w);
		}
		view.m_width = width;
		view.m_height = Math.Max(1, (total + width - 1) / width);
		view.m_name = GcLoc.Get("gclc_view_title", Shown!.m_name, Members.Count);
		view.m_inventory.Clear();
		foreach (Member m in Members)
		{
			foreach (ItemDrop.ItemData item in m.Real.m_inventory)
			{
				int local = item.m_gridPos.y * m.Width + item.m_gridPos.x;
				if (local < 0 || local >= m.Capacity || item.m_gridPos.x >= m.Width)
				{
					Plugin.Log.LogWarning($"Verbundene Kisten: {item.m_shared.m_name} liegt außerhalb der Kiste ({item.m_gridPos.x},{item.m_gridPos.y}).");
					local = Math.Min(Math.Max(local, 0), m.Capacity - 1);
				}
				int index = m.Offset + local;
				ItemDrop.ItemData copy = item.Clone();
				copy.m_gridPos = new Vector2i(index % width, index / width);
				view.m_inventory.Add(copy);
			}
			if (!m.InView)
			{
				m.InView = true;
				m.Chest.m_inventory = view; // alles, was jetzt mit dieser Kiste passiert, landet in der Ansicht
			}
			m.Synced = Signature(m.Real.m_inventory);
		}
		applying = true;
		try
		{
			view.Changed(); // Gewicht und Anzeige, ohne zu verteilen
		}
		finally
		{
			applying = false;
		}
		built = true;
	}

	private static void OnVirtualChanged(Inventory source)
	{
		if (applying || !Active || !ReferenceEquals(source, Virtual))
		{
			return; // eigene Schreibvorgänge, oder eine abgelöste Ansicht
		}
		if (batchDepth > 0)
		{
			batchDirty = true; // am Ende des Stapels einmal verteilen
			return;
		}
		Distribute();
	}

	private static int MemberAt(int index)
	{
		for (int i = 0; i < Members.Count; i++)
		{
			Member m = Members[i];
			if (m.InView && index >= m.Offset && index < m.Offset + m.Capacity)
			{
				return i;
			}
		}
		return -1;
	}

	public static bool IsDead(int x, int y) => x < 0 || y < 0 || x >= width || y * width + x >= total;

	/// <summary>
	/// Jede Änderung der Ansicht sofort auf die echten Kisten verteilen und dort speichern. Review 27.10.2026: Erst wird nur
	/// zugeordnet und verglichen; geklont wird beim Schreiben und nur für Kisten, die sich geändert haben, und dort behalten
	/// unveränderte Plätze ihr Item-Objekt. Vorher klonte jede Änderung alle Items der Ansicht (EpicLoot serialisiert dabei).
	/// </summary>
	private static void Distribute()
	{
		Inventory view = Virtual!;
		applying = true;
		try
		{
			List<List<Slot>> buckets = Members.Select(_ => new List<Slot>()).ToList();
			List<HashSet<int>> used = Members.Select(_ => new HashSet<int>()).ToList();
			List<ItemDrop.ItemData> lost = new();
			foreach (ItemDrop.ItemData item in view.m_inventory)
			{
				int index = item.m_gridPos.y * width + item.m_gridPos.x;
				int i = item.m_gridPos.x < width ? MemberAt(index) : -1;
				if (i < 0 || !used[i].Add(index - Members[i].Offset))
				{
					lost.Add(item);
					continue;
				}
				buckets[i].Add(new Slot(index - Members[i].Offset, item));
			}
			foreach (ItemDrop.ItemData item in lost)
			{
				if (!PlaceAnywhere(buckets, used, item))
				{
					view.m_inventory.Remove(item);
					Homeless.Add(item);
				}
			}
			for (int i = 0; i < Members.Count; i++)
			{
				Member m = Members[i];
				if (!m.InView || m.Chest == null || m.Chest.m_nview == null || !m.Chest.m_nview.IsValid() || !m.Chest.m_nview.IsOwner())
				{
					continue;
				}
				int current = Signature(m.Real.m_inventory);
				if (current != m.Synced)
				{
					Plugin.Log.LogWarning($"Verbundene Kisten: {m.Chest.m_name} wurde an der Ansicht vorbei verändert, die Ansicht gilt.");
				}
				Dictionary<int, ItemDrop.ItemData>? before = ByLocal(m);
				if (before != null && SameContent(before, buckets[i]))
				{
					m.Synced = current;
					continue; // diese Kiste hat sich nicht geändert: nicht klonen, nicht neu speichern
				}
				List<ItemDrop.ItemData> fresh = new(buckets[i].Count);
				foreach (Slot slot in buckets[i])
				{
					if (before != null && before.TryGetValue(slot.Local, out ItemDrop.ItemData keep) && SameItem(slot.Item, keep))
					{
						fresh.Add(keep); // Platz unverändert: dasselbe Objekt, kein Klon
						continue;
					}
					ItemDrop.ItemData copy = slot.Item.Clone();
					copy.m_gridPos = new Vector2i(slot.Local % m.Width, slot.Local / m.Width);
					fresh.Add(copy);
				}
				m.Real.m_inventory.Clear();
				m.Real.m_inventory.AddRange(fresh);
				m.Real.Changed(); // Container.OnContainerChanged speichert (Container.Save nimmt das echte Inventar)
				m.Synced = Signature(m.Real.m_inventory);
			}
		}
		finally
		{
			applying = false;
		}
		if (Homeless.Count > 0)
		{
			DropHomeless();
		}
	}

	private static bool PlaceAnywhere(List<List<Slot>> buckets, List<HashSet<int>> used, ItemDrop.ItemData item)
	{
		for (int i = 0; i < Members.Count; i++)
		{
			Member m = Members[i];
			if (!m.InView)
			{
				continue;
			}
			for (int local = 0; local < m.Capacity; local++)
			{
				if (used[i].Add(local))
				{
					int index = m.Offset + local;
					item.m_gridPos = new Vector2i(index % width, index / width); // auch in der Ansicht an den neuen Platz
					buckets[i].Add(new Slot(local, item));
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>Erster freier, gültiger Platz der Ansicht (oben zuerst oder unten zuerst wie Vanilla).</summary>
	public static Vector2i FirstFree(Inventory view, bool topFirst)
	{
		HashSet<int> taken = new(view.m_inventory.Select(item => item.m_gridPos.y * width + item.m_gridPos.x));
		if (topFirst)
		{
			for (int index = 0; index < total; index++)
			{
				if (!taken.Contains(index))
				{
					return new Vector2i(index % width, index / width);
				}
			}
		}
		else
		{
			int rows = (total + width - 1) / width;
			for (int y = rows - 1; y >= 0; y--)
			{
				for (int x = 0; x < width; x++)
				{
					int index = y * width + x;
					if (index < total && !taken.Contains(index))
					{
						return new Vector2i(x, y);
					}
				}
			}
		}
		return new Vector2i(-1, -1);
	}

	public static int Capacity => total;
}

// ------------------------------------------------------------------ Kisten-Lebenszyklus

[HarmonyPatch(typeof(Container), nameof(Container.Awake))]
internal static class AwakePatch
{
	[HarmonyPriority(Priority.First)]
	private static void Postfix(Container __instance)
	{
		ZNetView nview = __instance.m_nview;
		if (nview == null || nview.GetZDO() == null)
		{
			return;
		}
		__instance.gameObject.AddComponent<LinkTracker>().Container = __instance;
		Links.Register(__instance);
		// Verbindung beim Besitzer setzen (Review 27.10.2026). Mehrere Behälter an einem ZNetView (m_rootObjectOverride):
		// nur einmal registrieren, doppelt würde werfen und die übrigen Postfixe von Container.Awake abbrechen.
		if (!nview.m_functions.ContainsKey(Links.RpcSetMaster.GetStableHashCode()))
		{
			nview.Register<string>(Links.RpcSetMaster, (sender, masterId) => Links.OnSetMaster(nview, masterId));
		}
	}
}

public class LinkTracker : MonoBehaviour
{
	internal Container? Container;

	private void OnDestroy()
	{
		if (Container != null)
		{
			Links.Forget(Container);
		}
	}
}

/// <summary>Kiste einer Gruppe geöffnet: gemeinsame Ansicht starten.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
internal static class ShowPatch
{
	private static void Postfix(InventoryGui __instance, Container container)
	{
		if (!Plugin.On || container == null || __instance.m_currentContainer != container || LinkedView.Shown == container)
		{
			return;
		}
		if (!VPlusDedupe.Ready)
		{
			return; // V+ würde die Ansicht mehrfach zählen (Einhängung fehlt): Kisten öffnen einzeln, Review 27.10.2026
		}
		LinkedView.Begin(container);
	}
}

/// <summary>Fenster geschlossen: Kisten freigeben, Felder zurücksetzen.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.CloseContainer))]
internal static class CloseContainerPatch
{
	private static void Postfix() => LinkedView.End();
}

/// <summary>Antwort für eine weitere Kiste der Gruppe: kein eigenes Fenster.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.RPC_OpenResponse))]
internal static class OpenResponsePatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Container __instance, bool granted) => !LinkedView.HandleOpenResponse(__instance, granted);
}

/// <summary>Gespeichert wird immer das echte Inventar der Kiste, nie die gemeinsame Ansicht.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.Save))]
internal static class SavePatch
{
	private static bool Prefix(Container __instance)
	{
		Inventory? real = LinkedView.RealOf(__instance);
		if (real == null)
		{
			return true;
		}
		ZPackage package = new();
		real.Save(package);
		__instance.m_nview.GetZDO().Set(ZDOVars.s_items, package.GetArray());
		__instance.m_lastRevision = __instance.m_nview.GetZDO().DataRevision;
		return false;
	}
}

/// <summary>Nie in die Ansicht laden (Vanilla lädt ohnehin nicht, solange die Kiste benutzt wird).</summary>
[HarmonyPatch(typeof(Container), nameof(Container.Load))]
internal static class LoadPatch
{
	private static bool Prefix(Container __instance, ref bool __result)
	{
		if (LinkedView.RealOf(__instance) == null)
		{
			return true;
		}
		__result = false;
		return false;
	}
}

/// <summary>Eine Kiste der offenen Gruppe wird abgerissen oder zerstört: erst die Ansicht schließen, dann fallen nur ihre eigenen Items.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.OnDestroyed))]
internal static class DestroyedPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(Container __instance) => LinkedView.Release(__instance);
}

/// <summary>Der Spieler benutzt eine Kiste selbst (auch Umschalt+E): Die Antwort darauf ist seine, keine verspätete der Ansicht.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.Interact))]
internal static class InteractPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Prefix(Container __instance, Humanoid character, bool hold, bool __runOriginal)
	{
		if (__runOriginal && !hold && character == Player.m_localPlayer)
		{
			LinkedView.UserRequested(__instance); // vor dem Original: die Antwort kann sofort kommen (eigene Kiste)
		}
	}
}

/// <summary>Vor jeder Änderung im Fenster prüfen, ob alle Kisten der Ansicht noch uns gehören (sonst schließen statt ändern).</summary>
[HarmonyPatch]
internal static class GuardPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem));
		yield return AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnRightClickItem));
		yield return AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnTakeAll));
		yield return AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnStackAll));
		yield return AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnDropOutside));
	}

	[HarmonyPriority(Priority.First)]
	private static bool Prefix() => LinkedView.EnsureHealthy();
}

/// <summary>E halten legt Stapel ab (Vanilla-RPC): vorher prüfen, ob alle Kisten der Ansicht noch uns gehören.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.RPC_StackResponse))]
internal static class StackGuardPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix() => LinkedView.EnsureHealthy();
}

/// <summary>Alles nehmen oder Stapel ablegen bewegt viele Items: erst am Ende einmal auf die Kisten verteilen und speichern.</summary>
[HarmonyPatch]
internal static class BatchPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Inventory), nameof(Inventory.MoveAll));
		yield return AccessTools.Method(typeof(Inventory), nameof(Inventory.StackAll));
	}

	[HarmonyPriority(Priority.First + 100)]
	private static void Prefix(Inventory __instance, Inventory fromInventory, out bool __state)
	{
		__state = LinkedView.IsVirtual(__instance) || LinkedView.IsVirtual(fromInventory);
		if (__state)
		{
			LinkedView.BeginBatch();
		}
	}

	[HarmonyPriority(Priority.Last - 1)]
	private static void Finalizer(bool __state)
	{
		if (__state)
		{
			LinkedView.EndBatch();
		}
	}
}

// ------------------------------------------------------------------ Plätze hinter der letzten Kiste sperren

[HarmonyPatch(typeof(Inventory), nameof(Inventory.FindEmptySlot))]
internal static class FindEmptySlotPatch
{
	private static void Postfix(Inventory __instance, bool topFirst, ref Vector2i __result)
	{
		if (LinkedView.IsVirtual(__instance))
		{
			__result = LinkedView.FirstFree(__instance, topFirst);
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveEmptySlot))]
internal static class HaveEmptySlotPatch
{
	private static void Postfix(Inventory __instance, ref bool __result)
	{
		if (LinkedView.IsVirtual(__instance))
		{
			__result = __instance.m_inventory.Count < LinkedView.Capacity;
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.GetEmptySlots))]
internal static class GetEmptySlotsPatch
{
	private static void Postfix(Inventory __instance, ref int __result)
	{
		if (LinkedView.IsVirtual(__instance))
		{
			__result = Math.Max(0, LinkedView.Capacity - __instance.m_inventory.Count);
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.CanAddItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
internal static class CanAddItemPatch
{
	private static void Postfix(Inventory __instance, ItemDrop.ItemData item, int stack, ref bool __result)
	{
		if (!LinkedView.IsVirtual(__instance) || item == null)
		{
			return;
		}
		if (stack <= 0)
		{
			stack = item.m_stack;
		}
		int room = __instance.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) + Math.Max(0, LinkedView.Capacity - __instance.m_inventory.Count) * item.m_shared.m_maxStackSize;
		__result = room >= stack;
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })]
internal static class AddAtPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Inventory __instance, int x, int y, ref bool __result)
	{
		if (LinkedView.IsVirtual(__instance) && LinkedView.IsDead(x, y))
		{
			__result = false;
			return false;
		}
		return true;
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(Vector2i) })]
internal static class AddAtPosPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Inventory __instance, Vector2i pos, ref bool __result)
	{
		if (LinkedView.IsVirtual(__instance) && LinkedView.IsDead(pos.x, pos.y))
		{
			__result = false;
			return false;
		}
		return true;
	}
}

/// <summary>Gesperrte Plätze im Fenster ausblenden; bei normalen Kisten alle Plätze wieder zeigen.</summary>
[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
internal static class GridPatch
{
	private static readonly HashSet<InventoryGrid> Hidden = new();

	private static void Postfix(InventoryGrid __instance)
	{
		bool view = LinkedView.IsVirtual(__instance.m_inventory);
		if (!view && !Hidden.Contains(__instance))
		{
			return;
		}
		List<InventoryElement> elements = __instance.m_elements;
		for (int i = 0; i < elements.Count; i++)
		{
			if (elements[i] == null)
			{
				continue;
			}
			bool show = !view || i < LinkedView.Capacity;
			if (elements[i].gameObject.activeSelf != show)
			{
				elements[i].gameObject.SetActive(show);
			}
		}
		if (view)
		{
			Hidden.Add(__instance);
		}
		else
		{
			Hidden.Remove(__instance);
		}
	}
}

// ------------------------------------------------------------------ Anzeige in der Welt

/// <summary>Hover-Text: Gruppe und Taste. Höchstens zweimal je Sekunde neu gebaut (Review 27.10.2026: vorher jedes Bild über alle Kisten).</summary>
[HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
internal static class HoverPatch
{
	private static Container? last;
	private static float until;
	private static string suffix = "";

	private static void Postfix(Container __instance, ref string __result)
	{
		if (!Plugin.On || string.IsNullOrEmpty(__result))
		{
			return;
		}
		if (__instance != last || Time.unscaledTime >= until)
		{
			last = __instance;
			until = Time.unscaledTime + 0.5f;
			suffix = "";
			if (GcWard.IsPlayerChest(__instance))
			{
				int count = VPlusDedupe.Ready ? Links.Group(__instance).Count : 1; // ohne Ansicht kein „Öffnen zeigt alle"
				if (count > 1)
				{
					suffix += "\n" + GcLoc.Get("gclc_hover_group", count);
				}
				suffix += "\n" + GcLoc.Get("gclc_hover_key", Plugin.LinkKey.Value.ToString());
			}
		}
		__result += suffix;
	}
}

/// <summary>Gruppe farbig zeigen, solange Alt gehalten wird (Hauptkiste orange, weitere Kisten grün).</summary>
internal static class GroupHighlight
{
	private static readonly List<(GameObject, Color)> Wanted = new();
	private static readonly List<GameObject> Marked = new();

	public static void Update(Container? hovered, Container? selected)
	{
		List<(GameObject, Color)> wanted = Wanted;
		wanted.Clear();
		bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
		if (selected != null)
		{
			wanted.Add((selected.gameObject, new Color(1f, 0.55f, 0.15f)));
		}
		if (alt && hovered != null)
		{
			List<Container> group = Links.Group(hovered);
			if (group.Count > 1)
			{
				for (int i = 0; i < group.Count; i++)
				{
					bool master = i == 0 && !Links.HasLink(group[i]);
					wanted.Add((group[i].gameObject, master ? new Color(1f, 0.55f, 0.15f) : new Color(0.3f, 1f, 0.35f)));
				}
			}
		}
		foreach (GameObject go in Marked)
		{
			if (go != null && wanted.All(w => w.Item1 != go))
			{
				Gc.ResetHighlight(go);
			}
		}
		Marked.Clear();
		foreach ((GameObject go, Color color) in wanted)
		{
			Gc.Highlight(go, color);
			Marked.Add(go);
		}
	}
}

/// <summary>
/// ValheimPlus-Kistenlisten: Die Kisten bleiben einzeln. Nur solange die gemeinsame Ansicht offen ist, stehen alle
/// Kisten der Gruppe für die angeklickte Kiste (deren Inventar ist dann die Ansicht), damit V+ nichts doppelt zählt.
/// </summary>
internal static class VPlusDedupe
{
	/// <summary>
	/// Darf die gemeinsame Ansicht öffnen? Mit ValheimPlus nur, wenn alle drei Einhängungen aktiv sind, sonst zählte V+
	/// (Craften aus Kisten, Maschinen) die Ansicht je Kiste der Gruppe einmal. Ohne V+ immer (Review 27.10.2026).
	/// </summary>
	internal static bool Ready { get; private set; } = true;

	/// <summary>Review 27.10.2026: jede Einhängung einzeln abgesichert (VppPatch), Parameter per Position.</summary>
	internal static void Apply(Harmony harmony)
	{
		if (!Gc.VPlusLoaded)
		{
			return;
		}
		Ready = false;
		int active = 0;
		const int total = 3;
		try
		{
			Type? assistant = AccessTools.TypeByName("ValheimPlus.InventoryAssistant");
			if (assistant == null)
			{
				Plugin.Log.LogWarning("ValheimPlus: ValheimPlus.InventoryAssistant nicht gefunden.");
			}
			else
			{
				foreach (string name in new[] { "GetNearbyChests", "GetNearbyChestsForMachine" })
				{
					if (VppPatch.Try(harmony, VppPatch.Find(assistant, name), "InventoryAssistant." + name, postfix: new HarmonyMethod(typeof(VPlusDedupe), nameof(Canonicalize)) { priority = Priority.Low }))
					{
						active++;
					}
				}
				// V+ merkt sich die Kistenliste einige Sekunden (Craften aus Kisten). Wurde sie vor dem Öffnen der Ansicht gefüllt,
				// stünde die Gruppe mehrfach darin und ihre Items zählten mehrfach: beim Zählen dieselbe Bereinigung anwenden.
				if (VppPatch.Try(harmony, VppPatch.Find(assistant, "GetNearbyChestItemsByContainerList"), "InventoryAssistant.GetNearbyChestItemsByContainerList", prefix: new HarmonyMethod(typeof(VPlusDedupe), nameof(CanonicalizeArgument)) { priority = Priority.First }))
				{
					active++;
				}
			}
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"ValheimPlus: Einhängen abgebrochen: {e.Message}");
		}
		Ready = active == total;
		if (Ready)
		{
			Plugin.Log.LogInfo($"ValheimPlus erkannt: {active} von {total} V+-Einhängungen aktiv. Offene verbundene Kisten zählen dort als eine Kiste, fremd geöffnete und nicht geladene Kisten werden ausgelassen.");
		}
		else
		{
			Plugin.Log.LogWarning($"ValheimPlus: nur {active} von {total} V+-Einhängungen aktiv. Damit V+ nichts doppelt zählt, öffnen verbundene Kisten einzeln (keine gemeinsame Ansicht).");
		}
	}

	private static void Canonicalize(ref List<Container> __result) => LinkedView.CanonicalizeList(ref __result);

	/// <summary>V+ GetNearbyChestItemsByContainerList(List&lt;Container&gt; nearbyChests): Parameter per Position.</summary>
	private static void CanonicalizeArgument(ref List<Container> __0) => LinkedView.CanonicalizeList(ref __0);
}
