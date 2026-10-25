using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GC.Shared;

/// <summary>
/// Dauerhafte IDs für Kisten.
///
/// Wichtig: ZDOIDs sind NICHT dauerhaft. Beim Laden der Welt vergibt das Spiel allen gespeicherten Objekten neue IDs
/// (ZDO.Load: m_uid.SetID(++ZDOID.m_loadID)). Verweise auf andere Objekte brauchen deshalb einen eigenen Schlüssel,
/// der im ZDO des Objekts gespeichert wird. Jede Mod führt ihr eigenes Verzeichnis ID → geladene Kiste und muss dazu
/// in Container.Awake <see cref="Track"/> und beim Zerstören <see cref="Untrack"/> aufrufen.
/// </summary>
public static class GcIds
{
	public const string Key = "gc_uid";

	private static readonly Dictionary<string, Container> Map = new();

	public static string Read(ZNetView? view)
	{
		return view != null && view.IsValid() ? view.GetZDO().GetString(Key) : "";
	}

	public static bool InUse(ZNetView? view)
	{
		return view != null && view.IsValid() && view.GetZDO().GetInt(ZDOVars.s_inUse) == 1;
	}

	public static void Track(Container container, ZNetView? ownView)
	{
		string id = Read(ownView);
		if (id.Length > 0)
		{
			Map[id] = container;
		}
	}

	public static void Untrack(Container container)
	{
		foreach (string id in Map.Where(pair => pair.Value == container || pair.Value == null).Select(pair => pair.Key).ToList())
		{
			Map.Remove(id);
		}
	}

	public static Container? Find(string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}
		if (Map.TryGetValue(id, out Container container))
		{
			if (container != null && container.m_nview != null && container.m_nview.IsValid())
			{
				return container;
			}
			Map.Remove(id);
		}
		return null;
	}

	/// <summary>ID lesen oder neu vergeben. Vergeben braucht den ZDO-Besitz und wird abgelehnt, solange jemand die Kiste offen hat.</summary>
	public static string? Ensure(Container container, ZNetView ownView)
	{
		string id = Read(ownView);
		if (id.Length > 0)
		{
			Map[id] = container;
			return id;
		}
		if (InUse(ownView) || !Gc.Own(ownView))
		{
			return null;
		}
		id = Guid.NewGuid().ToString("N");
		ownView.GetZDO().Set(Key, id);
		Map[id] = container;
		return id;
	}
}

/// <summary>Merkt das Zerstören einer Kiste für <see cref="GcIds.Untrack"/>.</summary>
public class GcIdTracker : MonoBehaviour
{
	public Container? Container;

	private void OnDestroy()
	{
		if (Container != null)
		{
			GcIds.Untrack(Container);
		}
	}
}

public static class GcWard
{
	/// <summary>True, wenn beide Punkte in genau denselben aktiven Wards liegen (wie V+ SharesWards).</summary>
	public static bool SameWards(Vector3 a, Vector3 b)
	{
		foreach (PrivateArea area in PrivateArea.m_allAreas)
		{
			if (area != null && area.IsEnabled() && area.IsInside(a, 0f) != area.IsInside(b, 0f))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>Echte, vom Spieler gebaute Kiste (keine Fahrzeug-Ladefläche, kein Obliterator, kein Grabstein, keine Dungeon-Kiste).</summary>
	public static bool IsPlayerChest(Container? container)
	{
		if (container == null)
		{
			return false;
		}
		Piece? piece = container.GetComponent<Piece>();
		return piece != null && piece.IsPlacedByPlayer()
			&& container.GetComponentInParent<Player>() == null
			&& container.GetComponentInParent<Vagon>() == null
			&& container.GetComponentInParent<Ship>() == null
			&& container.GetComponentInParent<Incinerator>() == null
			&& container.GetComponent<TombStone>() == null;
	}
}
