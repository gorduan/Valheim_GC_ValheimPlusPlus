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
			Gc.Center(GcLoc.Get("gclc_linked", Links.Group(master).Count));
		}
	}
}

/// <summary>Verbindungen: welche geladenen Kisten zu einer Gruppe gehören.</summary>
internal static class Links
{
	private static readonly HashSet<Container> Known = new();

	public static void Register(Container c)
	{
		GcIds.Track(c, c.m_nview);
		Known.Add(c);
	}

	public static void Forget(Container c)
	{
		Known.Remove(c);
		GcIds.Untrack(c);
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

	/// <summary>Geladene Kisten der Gruppe: Hauptkiste zuerst, dann nach ihrer ID (feste Reihenfolge). Ohne Gruppe nur die Kiste selbst.</summary>
	public static List<Container> Group(Container c)
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
		Known.RemoveWhere(k => k == null);
		List<Container> members = Known
			.Where(k => k.m_nview != null && k.m_nview.IsValid() && MasterId(k) == root)
			.OrderBy(OwnId, StringComparer.Ordinal)
			.ToList();
		Container? master = GcIds.Find(root);
		if (master != null && !members.Contains(master))
		{
			members.Insert(0, master);
		}
		return members.Count > 1 && members.Contains(c) ? members : single;
	}

	public static bool Link(Container proxy, Container master)
	{
		string? masterId = GcIds.Ensure(master, master.m_nview);
		ZNetView own = proxy.m_nview;
		if (masterId == null || GcIds.InUse(own) || !Gc.Own(own) || GcIds.Ensure(proxy, own) == null)
		{
			Gc.Center(GcLoc.Get("gclc_busy"));
			return false;
		}
		own.GetZDO().Set(GcKeys.LinkMaster, masterId);
		return true;
	}

	public static bool Unlink(Container proxy)
	{
		ZNetView own = proxy.m_nview;
		if (GcIds.InUse(own) || proxy.m_inUse || !Gc.Own(own))
		{
			Gc.Center(GcLoc.Get("gclc_busy"));
			return false;
		}
		own.GetZDO().Set(GcKeys.LinkMaster, "");
		return true;
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
		List<Container> group = Links.Group(shown);
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
		foreach (Member m in Members)
		{
			if (m.Chest != Shown)
			{
				m.Chest.SetInUse(true);
			}
			if (built && m.InView && Signature(m.Real.m_inventory) != m.Synced)
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

	/// <summary>Genau derselbe Inhalt? (Vergleich Feld für Feld nach Platz, nicht über Fingerabdrücke.)</summary>
	private static bool SameContent(List<ItemDrop.ItemData> a, List<ItemDrop.ItemData> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		Dictionary<long, ItemDrop.ItemData> byPos = new(a.Count);
		foreach (ItemDrop.ItemData item in a)
		{
			long key = ((long)item.m_gridPos.y << 32) | (uint)item.m_gridPos.x;
			if (byPos.ContainsKey(key))
			{
				return false; // doppelt belegt: lieber schreiben
			}
			byPos[key] = item;
		}
		foreach (ItemDrop.ItemData item in b)
		{
			long key = ((long)item.m_gridPos.y << 32) | (uint)item.m_gridPos.x;
			if (!byPos.TryGetValue(key, out ItemDrop.ItemData other) || !SameItem(item, other))
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

	/// <summary>Jede Änderung der Ansicht sofort auf die echten Kisten verteilen und dort speichern.</summary>
	private static void Distribute()
	{
		Inventory view = Virtual!;
		applying = true;
		try
		{
			List<List<ItemDrop.ItemData>> buckets = Members.Select(_ => new List<ItemDrop.ItemData>()).ToList();
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
				Place(buckets[i], Members[i], index - Members[i].Offset, item);
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
				if (Signature(m.Real.m_inventory) != m.Synced)
				{
					Plugin.Log.LogWarning($"Verbundene Kisten: {m.Chest.m_name} wurde an der Ansicht vorbei verändert, die Ansicht gilt.");
				}
				if (SameContent(m.Real.m_inventory, buckets[i]))
				{
					m.Synced = Signature(m.Real.m_inventory);
					continue; // diese Kiste hat sich nicht geändert: nicht neu speichern
				}
				m.Real.m_inventory.Clear();
				m.Real.m_inventory.AddRange(buckets[i]);
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

	private static void Place(List<ItemDrop.ItemData> bucket, Member m, int local, ItemDrop.ItemData item)
	{
		ItemDrop.ItemData copy = item.Clone();
		copy.m_gridPos = new Vector2i(local % m.Width, local / m.Width);
		bucket.Add(copy);
	}

	private static bool PlaceAnywhere(List<List<ItemDrop.ItemData>> buckets, List<HashSet<int>> used, ItemDrop.ItemData item)
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
					Place(buckets[i], m, local, item);
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
		if (__instance.m_nview == null || __instance.m_nview.GetZDO() == null)
		{
			return;
		}
		__instance.gameObject.AddComponent<LinkTracker>().Container = __instance;
		Links.Register(__instance);
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

[HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
internal static class HoverPatch
{
	private static void Postfix(Container __instance, ref string __result)
	{
		if (!Plugin.On || string.IsNullOrEmpty(__result) || !GcWard.IsPlayerChest(__instance))
		{
			return;
		}
		int count = Links.Group(__instance).Count;
		if (count > 1)
		{
			__result += "\n" + GcLoc.Get("gclc_hover_group", count);
		}
		__result += "\n" + GcLoc.Get("gclc_hover_key", Plugin.LinkKey.Value.ToString());
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
	internal static void Apply(Harmony harmony)
	{
		if (!Gc.VPlusLoaded)
		{
			return;
		}
		Type? assistant = AccessTools.TypeByName("ValheimPlus.InventoryAssistant");
		if (assistant == null)
		{
			Plugin.Log.LogWarning("ValheimPlus gefunden, aber InventoryAssistant nicht. Offene verbundene Kisten könnten dort doppelt zählen.");
			return;
		}
		foreach (string name in new[] { "GetNearbyChests", "GetNearbyChestsForMachine" })
		{
			MethodInfo? method = AccessTools.Method(assistant, name);
			if (method != null)
			{
				harmony.Patch(method, postfix: new HarmonyMethod(typeof(VPlusDedupe), nameof(Canonicalize)) { priority = Priority.Low });
			}
		}
		// V+ merkt sich die Kistenliste einige Sekunden (Craften aus Kisten). Wurde sie vor dem Öffnen der Ansicht gefüllt,
		// stünde die Gruppe mehrfach darin und ihre Items zählten mehrfach: beim Zählen dieselbe Bereinigung anwenden.
		MethodInfo? items = AccessTools.Method(assistant, "GetNearbyChestItemsByContainerList");
		if (items != null)
		{
			harmony.Patch(items, prefix: new HarmonyMethod(typeof(VPlusDedupe), nameof(CanonicalizeArgument)) { priority = Priority.First });
		}
		Plugin.Log.LogInfo("ValheimPlus erkannt: offene verbundene Kisten zählen dort als eine Kiste, fremd geöffnete und nicht geladene Kisten werden ausgelassen.");
	}

	private static void Canonicalize(ref List<Container> __result) => LinkedView.CanonicalizeList(ref __result);

	private static void CanonicalizeArgument(ref List<Container> nearbyChests) => LinkedView.CanonicalizeList(ref nearbyChests);
}
