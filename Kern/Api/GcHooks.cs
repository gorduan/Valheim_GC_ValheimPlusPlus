using System;

namespace GC.Shared;

/// <summary>Wie ein wildes Tier gezähmt wird (GC_Tierarten).</summary>
public enum GcTaming
{
	/// <summary>Wie in Valheim (Monster-KI).</summary>
	Normal,

	/// <summary>Gar nicht (Unbjørn, abgeschaltete Art).</summary>
	Blocked,

	/// <summary>Mit der einfachen Tier-KI (Hirsch, Hase, Robbe), zähmt GC_Tierarten selbst.</summary>
	SimpleAI,
}

/// <summary>
/// Verbindungsstellen zwischen GC-Mods (GC-SDK). Eine Mod, die etwas anbietet, setzt hier beim Start ihren Teil;
/// eine Mod, die es nutzt, fragt nur hier nach. Fehlt die anbietende Mod, bleibt der Eintrag leer und die nutzende
/// Mod läuft ohne diesen Teil weiter. So kennt keine Mod die Typen einer anderen.
/// </summary>
public static class GcHooks
{
	// ------------------------------------------------------------------ Dungeon-Karte → Kompass

	/// <summary>GC_DungeonKarte: Ist die große Dungeon-Karte gerade offen? (Der Kompass blendet sich dann aus.)</summary>
	public static Func<bool>? DungeonMapLargeOpen;

	public static bool IsDungeonMapLargeOpen()
	{
		try
		{
			return DungeonMapLargeOpen != null && DungeonMapLargeOpen();
		}
		catch
		{
			return false;
		}
	}

	// ------------------------------------------------------------------ Portalschutz → Portalnamen

	/// <summary>GC_Portalschutz: läuft er gerade?</summary>
	public static Func<bool>? PortalGuardActive;

	/// <summary>GC_Portalschutz: Portal umbenennen und den vorherigen Namen merken (ohne zweite Rückfrage).</summary>
	public static Action<TeleportWorld, string>? PortalRename;

	public static bool IsPortalGuardActive()
	{
		try
		{
			return PortalGuardActive != null && PortalGuardActive();
		}
		catch
		{
			return false;
		}
	}

	// ------------------------------------------------------------------ Tierarten → Tiere

	/// <summary>GC_Tierarten: Höchststufe einer Tierart (Prefab-Hash). Ohne GC_Tierarten gilt 150 für alle.</summary>
	public static Func<int, float>? SpeciesMaxLevel;

	/// <summary>GC_Tierarten: Grenze der wirksamen Stufe für die Boni (Grenze A). Ohne GC_Tierarten 150.</summary>
	public static Func<float>? SpeciesEffectCap;

	/// <summary>GC_Tierarten: Ist das Ziel für einen Angriffsbefehl ein Feind (ohne den Bärenfrieden)? Ohne: Valheims Feindschaft.</summary>
	public static Func<Character, Character, bool>? SpeciesEnemyForOrder;

	/// <summary>GC_Tierarten: Wie wird dieses wilde Tier gezähmt? Ohne: wie in Valheim.</summary>
	public static Func<Character, GcTaming>? SpeciesTaming;

	// ------------------------------------------------------------------ Walküren-Transport → Tiere (Walkürenpfeife)

	/// <summary>GC_WalkuerenTransport: ist der Transport an? (Ohne die Mod gibt es die Walkürenpfeife nicht.)</summary>
	public static Func<bool>? CourierOn;

	/// <summary>GC_WalkuerenTransport: Pfiff mit der Walkürenpfeife; true = die Walküre übernimmt, die Tiere bekommen keinen Befehl.</summary>
	public static Func<Player, ItemDrop.ItemData, bool>? CourierWhistle;

	/// <summary>GC_WalkuerenTransport: Zeile im Tooltip der Walkürenpfeife (wo dein Karren ist).</summary>
	public static Func<string>? CourierTooltip;

	public static bool IsCourierOn()
	{
		try
		{
			return CourierOn != null && CourierOn();
		}
		catch
		{
			return false;
		}
	}

	// ------------------------------------------------------------------ Futterfolge → Tiere (Zucht)

	/// <summary>
	/// GC_Futterfolge: Stufe des Partners für das Junge, das dieses Muttertier gerade bekommt (der beste satte Partner beim
	/// Trächtigwerden). Null = unbekannt, dann nimmt GC_Tiere wie bisher das nächste zahme Tier seiner Art.
	/// </summary>
	public static Func<Procreation, float?>? MateLevel;

	public static float? GetMateLevel(Procreation parent)
	{
		try
		{
			return MateLevel?.Invoke(parent);
		}
		catch
		{
			return null;
		}
	}

	// ------------------------------------------------------------------ Schlösser → Kisten (Gebäude-Menü)

	/// <summary>
	/// Rotes Schloss: Solange dieser Zähler über 0 liegt, zählen, suchen und entnehmen Inventare nach Namen ohne rot
	/// fixierte Items (GC_Schloesser). Andere Mods erhöhen ihn für eigene Vorgänge, z. B. das Gebäude-Menü.
	/// </summary>
	public static int RedCraft;

	public static void Leave(ref int depth) => depth = Math.Max(0, depth - 1);

	/// <summary>Menge ohne rot fixierte Items (ohne GC_Schloesser einfach die Menge).</summary>
	public static int CountUsable(Inventory inventory, string name)
	{
		RedCraft++;
		try
		{
			return inventory.CountItems(name);
		}
		finally
		{
			Leave(ref RedCraft);
		}
	}
}
