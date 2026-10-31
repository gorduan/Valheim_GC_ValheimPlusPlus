using System.Collections.Generic;
using System.Linq;
using System.Text;
using GC.Shared;
using GC.ValheimPlusPlus;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace GC.ValheimPlusPlus.StationStorage;

/// <summary>Markiert Kisten farbig (Vanilla-Technik wie die Bau-Markierung) und setzt die Markierung sauber zurück.</summary>
internal static class Highlighter
{
	private static readonly Dictionary<GameObject, Color> Active = new();
	private static float nextRefresh;

	public static readonly Color Hover = new(1f, 0.9f, 0.2f);
	public static readonly Color Input = new(0.3f, 1f, 0.35f);
	public static readonly Color Output = new(1f, 0.3f, 0.25f);
	public static readonly Color Both = new(0.35f, 0.6f, 1f);
	public static readonly Color Station = new(1f, 0.55f, 0.15f);

	/// <summary>Eingang grün, Ausgang rot, beides blau.</summary>
	public static Color For(bool input, bool output) => input && output ? Both : output ? Output : Input;

	public static void Apply(Dictionary<GameObject, Color> wanted)
	{
		bool refresh = Time.time >= nextRefresh;
		if (refresh)
		{
			nextRefresh = Time.time + 0.5f;
		}
		foreach (GameObject go in Active.Keys.ToList())
		{
			if (go == null)
			{
				Active.Remove(go!);
				continue;
			}
			if (!wanted.ContainsKey(go))
			{
				Gc.ResetHighlight(go);
				Active.Remove(go);
			}
		}
		foreach (KeyValuePair<GameObject, Color> pair in wanted)
		{
			if (pair.Key == null)
			{
				continue;
			}
			if (refresh || !Active.TryGetValue(pair.Key, out Color color) || color != pair.Value)
			{
				Gc.Highlight(pair.Key, pair.Value);
				Active[pair.Key] = pair.Value;
			}
		}
	}

	public static void Clear() => Apply(new Dictionary<GameObject, Color>());

	private static Station? cachedFor;
	private static float cachedUntil;
	private static Dictionary<GameObject, Color> cached = new();

	/// <summary>
	/// Wie <see cref="Assigned"/>, aber höchstens zweimal je Sekunde neu berechnet (Review 27.10.2026: Menü und Kistenwahl
	/// lösten sonst jedes Bild alle Gruppen auf). Das Ergebnis nicht verändern, sondern kopieren.
	/// </summary>
	public static Dictionary<GameObject, Color> AssignedCached(Station station)
	{
		if (!station.Equals(cachedFor) || Time.unscaledTime >= cachedUntil)
		{
			cachedFor = station;
			cachedUntil = Time.unscaledTime + 0.5f;
			cached = Assigned(station);
		}
		return cached;
	}

	public static void Invalidate() => cachedUntil = -1f;

	/// <summary>Alle zugewiesenen Kisten eines Gebäudes mit ihrer Farbe (Eingang, Ausgang, beides).</summary>
	public static Dictionary<GameObject, Color> Assigned(Station station)
	{
		HashSet<Container> inputs = new();
		foreach (string key in Plugin.InputKeys(station))
		{
			inputs.UnionWith(Plugin.Resolve(station, Plugin.Assigned(station, key)));
		}
		HashSet<Container> outputs = new(Plugin.Resolve(station, Plugin.Assigned(station, Plugin.OutputKey)));
		Dictionary<GameObject, Color> result = new();
		foreach (Container chest in inputs.Union(outputs))
		{
			if (chest != null)
			{
				result[chest.gameObject] = For(inputs.Contains(chest), outputs.Contains(chest));
			}
		}
		return result;
	}
}

/// <summary>
/// Kistenwahl per Mausklick in der Welt: eine Kiste anklicken wählt sie (oder hebt die Wahl auf), danach erscheint
/// sofort wieder das Menü. Rechtsklick bricht ab und führt ebenfalls zurück ins Menü.
/// </summary>
internal static class ChestPicker
{
	private static Station? station;
	private static string key = "";
	private static float startedAt;

	/// <summary>
	/// Review 27.10.2026: Die Kistenwahl blockiert Angriffe (AttackBlockPatch). Sie endet deshalb nach 30 s oder wenn der
	/// Spieler mehr als 10 m außerhalb der Kistenreichweite (MaxChestDistance um das Gebäude) steht. Gemessen ab dem
	/// Gebäude plus Reichweite, weil zugewiesene Kisten bis MaxChestDistance entfernt stehen dürfen und man zum Anklicken
	/// bei der Kiste stehen muss.
	/// </summary>
	private const float MaxSeconds = 30f;
	private const float Leeway = 10f;

	public static bool Active { get; private set; }

	public static void Begin(Station target, string assignKey)
	{
		station = target;
		key = assignKey;
		Active = true;
		startedAt = Time.time;
		Gc.Center(GcLoc.Get("gcss_pick_help"));
	}

	public static void Update()
	{
		if (!Active)
		{
			return;
		}
		Player player = Player.m_localPlayer;
		if (player == null || station == null || !station.Valid)
		{
			Cancel(false);
			return;
		}
		if (Time.time - startedAt > MaxSeconds || Vector3.Distance(player.transform.position, station.Position) > Plugin.MaxChestDistance.Value + Leeway)
		{
			Cancel(false);
			Gc.Center(GcLoc.Get("gcss_pick_ended", MaxSeconds.ToString("0")));
			return;
		}
		Container? hovered = Gc.Hovered<Container>();
		if (hovered != null && !GcWard.IsPlayerChest(hovered))
		{
			hovered = null;
		}

		Dictionary<GameObject, Color> wanted = new(Highlighter.AssignedCached(station)); // Kopie: unten kommen Gebäude und Hover dazu
		wanted[station.GameObject] = Highlighter.Station;
		if (hovered != null)
		{
			wanted[hovered.gameObject] = Highlighter.Hover;
		}
		Highlighter.Apply(wanted);

		if (Gc.TextInputActive() || InventoryGui.IsVisible() || Minimap.IsOpen() || global::Menu.IsVisible() || StoreGui.IsVisible() || (TextViewer.instance != null && TextViewer.instance.IsVisible()))
		{
			return; // Klicks in offenen Fenstern gehören dem Fenster, nicht der Kistenwahl
		}
		if (UnityEngine.Input.GetMouseButtonDown(0) && hovered != null)
		{
			if (Toggle(hovered))
			{
				Cancel(true); // gewählt: zurück ins Menü
			}
		}
		else if (UnityEngine.Input.GetMouseButtonDown(1))
		{
			Cancel(true); // Esc öffnet in Valheim das Pause-Menü, deshalb bricht hier der Rechtsklick ab
		}
	}

	/// <summary>Kiste hinzufügen oder entfernen. true = Zuweisung geändert.</summary>
	private static bool Toggle(Container chest)
	{
		List<string> selected = Plugin.Assigned(station!, key);
		string existing = GcIds.Read(chest.m_nview);
		if (existing.Length > 0 && selected.Remove(existing))
		{
			Save(selected);
			Gc.TopLeft(GcLoc.Get("gcss_pick_removed", selected.Count));
			return true;
		}
		if (Vector3.Distance(chest.transform.position, station!.Position) > Plugin.MaxChestDistance.Value)
		{
			Gc.Center(GcLoc.Get("gcss_pick_too_far", Plugin.MaxChestDistance.Value.ToString("0")));
			return false;
		}
		if (!PrivateArea.CheckAccess(chest.transform.position))
		{
			return false;
		}
		if (chest.m_privacy != Container.PrivacySetting.Public)
		{
			Gc.Center(GcLoc.Get("gcss_pick_not_public"));
			return false;
		}
		if (!GcWard.SameWards(chest.transform.position, station.Position))
		{
			Gc.Center(GcLoc.Get("gcss_pick_wards"));
			return false;
		}
		string? id = GcIds.Ensure(chest, chest.m_nview);
		if (id == null)
		{
			Gc.Center(GcLoc.Get("gcss_pick_busy"));
			return false;
		}
		selected.Add(id);
		Save(selected);
		int group = LinkedChests.Links.Group(chest).Count;
		Gc.TopLeft(GcLoc.Get(group > 1 ? "gcss_pick_added_group" : "gcss_pick_added", selected.Count, group));
		return true;
	}

	private static void Save(List<string> selected)
	{
		station!.View.InvokeRPC(Plugin.RpcAssign, key, string.Join(";", selected.Distinct()));
		Highlighter.Invalidate();
	}

	public static void Cancel(bool backToMenu)
	{
		Station? target = station;
		Active = false;
		station = null;
		Highlighter.Clear();
		if (backToMenu && target != null && target.Valid)
		{
			StationWindow.Open(target);
		}
	}
}

/// <summary>Das Gebäude-Menü im Kompendium-Stil.</summary>
internal static class StationWindow
{
	private static GameObject? window;
	private static Text? header;
	private static RectTransform? content;
	private static Station? station;
	private static string signature = "";
	private static float nextCheck;
	private static int openedFrame = -1;

	public static bool IsOpen => window != null && window.activeSelf;

	public static void Open(Station target)
	{
		if (ChestPicker.Active)
		{
			return;
		}
		Build();
		station = target;
		signature = "";
		openedFrame = Time.frameCount; // die Esc- oder Rechtsklick-Taste, die die Kistenwahl beendet hat, schließt das Menü nicht gleich wieder
		window!.SetActive(true);
		GUIManager.BlockInput(true);
		Refresh();
	}

	public static void Close()
	{
		if (window != null && window.activeSelf)
		{
			window.SetActive(false);
			GUIManager.BlockInput(false);
		}
		station = null;
		if (!ChestPicker.Active)
		{
			Highlighter.Clear();
		}
	}

	public static void Update()
	{
		if (!IsOpen)
		{
			return;
		}
		Player player = Player.m_localPlayer;
		bool escape = UnityEngine.Input.GetKeyDown(KeyCode.Escape) && Time.frameCount != openedFrame;
		if (player == null || station == null || !station.Valid || Vector3.Distance(player.transform.position, station.Position) > 10f || escape)
		{
			Close();
			return;
		}
		if (Plugin.HighlightWhileOpen.Value)
		{
			Highlighter.Apply(Highlighter.AssignedCached(station)); // höchstens zweimal je Sekunde neu berechnet (Review 27.10.2026)
		}
		if (Time.time >= nextCheck)
		{
			nextCheck = Time.time + 0.5f;
			Refresh();
		}
	}

	public static void Refresh()
	{
		if (!IsOpen || station == null || !station.Valid)
		{
			return;
		}
		ZDO zdo = station.View.GetZDO();
		Inventory inventory = Player.m_localPlayer.GetInventory();
		StringBuilder sig = new();
		foreach (string key in Plugin.InputKeys(station).Append(Plugin.OutputKey))
		{
			sig.Append('|').Append(zdo.GetString(Plugin.KeyPrefix + key));
		}
		Smelter? smelter = station.Smelter;
		if (smelter != null)
		{
			sig.Append(string.Join(",", Plugin.Queue(zdo))).Append('|').Append(Mathf.FloorToInt(zdo.GetFloat(ZDOVars.s_fuel)))
				.Append('|').Append(zdo.GetInt(ZDOVars.s_spawnAmount)).Append(zdo.GetString(ZDOVars.s_spawnOre));
			foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
			{
				if (conversion.m_from != null)
				{
					sig.Append('|').Append(GcHooks.CountUsable(inventory, conversion.m_from.m_itemData.m_shared.m_name));
				}
			}
			if (smelter.m_fuelItem != null)
			{
				sig.Append('|').Append(GcHooks.CountUsable(inventory, smelter.m_fuelItem.m_itemData.m_shared.m_name));
			}
		}
		else
		{
			sig.Append('|').Append(station.Level.Level);
		}
		if (sig.ToString() == signature)
		{
			return;
		}
		signature = sig.ToString();
		GcUi.ClearChildren(content!);
		header!.text = Localization.instance.Localize(station.Name);
		if (smelter != null)
		{
			BuildSmelter(smelter, zdo, inventory);
		}
		else if (station.IsTable)
		{
			Section(GcLoc.Get("gcss_section_table"));
			Row(EnchantingChests.Describe(station));
		}
		else
		{
			BuildProducer(station);
		}
		BuildChests(station);
	}

	private static void BuildSmelter(Smelter smelter, ZDO zdo, Inventory inventory)
	{
		List<string> queue = Plugin.Queue(zdo);
		float fuel = zdo.GetFloat(ZDOVars.s_fuel);
		int ready = zdo.GetInt(ZDOVars.s_spawnAmount);
		string readyOre = zdo.GetString(ZDOVars.s_spawnOre);

		Section(GcLoc.Get("gcss_section_queue", queue.Count, smelter.m_maxOre));
		if (queue.Count == 0)
		{
			Row(GcLoc.Get("gcss_empty"));
		}
		foreach (IGrouping<string, string> group in queue.GroupBy(name => name))
		{
			string item = group.Key;
			int count = group.Count();
			Row($"{Plugin.ItemName(item)}  x{count}", GcLoc.Get("gcss_take"), () => smelter.m_nview.InvokeRPC(Plugin.RpcTake, item, count));
		}
		if (smelter.m_fuelItem != null)
		{
			int whole = Mathf.FloorToInt(fuel);
			Row(GcLoc.Get("gcss_fuel", Localization.instance.Localize(smelter.m_fuelItem.m_itemData.m_shared.m_name), Mathf.CeilToInt(fuel), smelter.m_maxFuel),
				whole > 0 ? GcLoc.Get("gcss_take") : null, () => smelter.m_nview.InvokeRPC(Plugin.RpcTakeFuel, whole));
		}
		if (ready > 0)
		{
			Smelter.ItemConversion? conversion = smelter.GetItemConversion(readyOre);
			string product = conversion?.m_to != null ? Plugin.ItemName(conversion.m_to.gameObject.name) : readyOre;
			Row(GcLoc.Get("gcss_ready", product, ready), GcLoc.Get("gcss_take"), () => smelter.m_nview.InvokeRPC(Plugin.RpcTakeOutput));
		}

		Section(GcLoc.Get("gcss_section_add"));
		bool any = false;
		foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
		{
			if (conversion.m_from == null)
			{
				continue;
			}
			ItemDrop.ItemData from = conversion.m_from.m_itemData;
			int have = GcHooks.CountUsable(inventory, from.m_shared.m_name);
			if (have <= 0)
			{
				continue;
			}
			any = true;
			string prefab = conversion.m_from.gameObject.name;
			Row(GcLoc.Get("gcss_have", Localization.instance.Localize(from.m_shared.m_name), have), GcLoc.Get("gcss_add_all"), () => AddOre(smelter, prefab, from.m_shared.m_name));
		}
		if (smelter.m_fuelItem != null)
		{
			string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;
			int have = GcHooks.CountUsable(inventory, fuelName);
			if (have > 0)
			{
				any = true;
				Row(GcLoc.Get("gcss_have", Localization.instance.Localize(fuelName), have), GcLoc.Get("gcss_add_all"), () => AddFuel(smelter, fuelName));
			}
		}
		if (!any)
		{
			Row(GcLoc.Get("gcss_nothing_to_add"));
		}
	}

	private static void BuildProducer(Station producer)
	{
		(int level, int max) = producer.Level;
		Section(GcLoc.Get("gcss_section_yield"));
		Row(GcLoc.Get("gcss_level", producer.ProductName, level, max), level > 0 ? GcLoc.Get("gcss_take") : null, () => producer.Extract());
	}

	private static void BuildChests(Station target)
	{
		// Review 27.10.2026: nur Zeilen, deren V+-Einhängungen aktiv sind; sonst wirkte eine Zuweisung nicht
		bool inputs = (VPlusBridge.InputReady && target.Smelter != null) || (target.IsTable && EnchantingChests.Ready && Plugin.TableChests.Value);
		bool output = !target.IsTable && VPlusBridge.OutputFor(target);
		if (!Gc.VPlusLoaded || !Plugin.ChestsOn || (!inputs && !output))
		{
			return;
		}
		Section(GcLoc.Get("gcss_section_chests"));
		Smelter? smelter = target.Smelter;
		if (inputs)
		{
			foreach (string key in Plugin.InputKeys(target))
			{
				string label = key == EnchantingChests.TableKey ? GcLoc.Get("gcss_table_material")
					: key == Plugin.FuelKey ? Localization.instance.Localize(smelter!.m_fuelItem.m_itemData.m_shared.m_name) : Plugin.ItemName(key);
				ChestRow(target, key, GcLoc.Get("gcss_input_chests", label, Plugin.Assigned(target, key).Count));
			}
		}
		if (output)
		{
			ChestRow(target, Plugin.OutputKey, GcLoc.Get("gcss_output_chests", Plugin.Assigned(target, Plugin.OutputKey).Count));
		}
		Row($"<i>{GcLoc.Get(target.IsTable ? "gcss_chests_note_table" : target.IsProducer ? "gcss_chests_note_producer" : "gcss_chests_note")}</i>");
	}

	private static void ChestRow(Station target, string key, string text)
	{
		List<string> ids = Plugin.Assigned(target, key);
		int found = ids.Count(id => LinkedChests.Links.Find(id) != null);
		Row(text + (found < ids.Count ? " " + GcLoc.Get("gcss_found", found) : ""), GcLoc.Get("gcss_pick"), () => Pick(target, key),
			ids.Count > 0 ? GcLoc.Get("gcss_clear") : null, () => target.View.InvokeRPC(Plugin.RpcAssign, key, ""));
	}

	private static void Pick(Station target, string key)
	{
		Close();
		ChestPicker.Begin(target, key);
	}

	/// <summary>Einfüllen aus dem Inventar. Rot fixierte Items bleiben (wie beim Craften), weiß fixierte dürfen benutzt werden.</summary>
	private static void AddOre(Smelter smelter, string prefab, string sharedName)
	{
		GcHooks.RedCraft++;
		try
		{
			AddOreInner(smelter, prefab, sharedName);
		}
		finally
		{
			GcHooks.Leave(ref GcHooks.RedCraft);
		}
	}

	private static void AddOreInner(Smelter smelter, string prefab, string sharedName)
	{
		Player player = Player.m_localPlayer;
		Inventory inventory = player.GetInventory();
		int space = smelter.m_maxOre - smelter.GetQueueSize();
		int have = inventory.CountItems(sharedName);
		int amount = Mathf.Min(space, have);
		if (amount <= 0)
		{
			Gc.Center("$msg_itsfull");
			return;
		}
		// Review 27.10.2026: einmal entnehmen und ein Sammel-RPC an den Besitzer, der nicht Angenommenes über GC_SS_Give
		// zurückgibt. Vorher ging je Item ein RPC_AddOre los; wechselte dazwischen der Besitzer, waren die Items weg.
		bool cheated = inventory.m_inventory.Exists(i => i.m_shared.m_name == sharedName && i.m_cheated); // wie V+ beim Nachfüllen aus Kisten
		inventory.RemoveItem(sharedName, amount);
		int removed = have - inventory.CountItems(sharedName);
		if (removed <= 0)
		{
			return;
		}
		Rpcs.ToOwner(smelter.m_nview, Plugin.RpcAddOre, prefab, removed, cheated);
		Gc.TopLeft(GcLoc.Get("gcss_added", removed, Plugin.ItemName(prefab)));
	}

	private static void AddFuel(Smelter smelter, string sharedName)
	{
		GcHooks.RedCraft++;
		try
		{
			AddFuelInner(smelter, sharedName);
		}
		finally
		{
			GcHooks.Leave(ref GcHooks.RedCraft);
		}
	}

	private static void AddFuelInner(Smelter smelter, string sharedName)
	{
		Inventory inventory = Player.m_localPlayer.GetInventory();
		int space = Mathf.FloorToInt(smelter.m_maxFuel - smelter.GetFuel());
		int have = inventory.CountItems(sharedName);
		int amount = Mathf.Min(space, have);
		if (amount <= 0)
		{
			Gc.Center("$msg_itsfull");
			return;
		}
		inventory.RemoveItem(sharedName, amount);
		int removed = have - inventory.CountItems(sharedName);
		if (removed <= 0)
		{
			return;
		}
		Rpcs.ToOwner(smelter.m_nview, Plugin.RpcAddFuel, removed); // ein Sammel-RPC, der Besitzer gibt den Überschuss zurück (Review 27.10.2026)
		Gc.TopLeft(GcLoc.Get("gcss_added", removed, Localization.instance.Localize(sharedName)));
	}

	private static void Section(string text)
	{
		RectTransform row = GcUi.CreateRow(content!, 34f);
		GcUi.CreateLabel(row, text, Vector2.zero, Vector2.one, new Vector2(4f, 0f), Vector2.zero, 22, TextAnchor.LowerLeft, GUIManager.Instance.ValheimOrange, true);
	}

	private static void Row(string text, string? button1 = null, System.Action? action1 = null, string? button2 = null, System.Action? action2 = null)
	{
		RectTransform row = GcUi.CreateRow(content!, 40f);
		float right = button1 == null ? 0f : button2 == null ? 150f : 300f;
		GcUi.CreateLabel(row, text, Vector2.zero, Vector2.one, new Vector2(12f, 0f), new Vector2(-right, 0f), 19, TextAnchor.MiddleLeft);
		if (button1 != null && action1 != null)
		{
			GcUi.CreateButton(row, button1, new Vector2(1f, 0.5f), new Vector2(button2 == null ? -72f : -222f, 0f), 140f, 34f, action1);
		}
		if (button2 != null && action2 != null)
		{
			GcUi.CreateButton(row, button2, new Vector2(1f, 0.5f), new Vector2(-72f, 0f), 140f, 34f, action2);
		}
	}

	private static void Build()
	{
		if (window != null)
		{
			return;
		}
		window = GcUi.CreateWindow("GC_StationStorage_Window", 860f, 640f, out header, out RectTransform inner);
		content = GcUi.CreateScroll(inner, 780f, 470f);
		GcUi.CreateButton(window.transform, GcLoc.Get("gcss_close"), new Vector2(0.5f, 0f), new Vector2(0f, 44f), 200f, 44f, Close);
	}
}
