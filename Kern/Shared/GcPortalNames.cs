using System;
using System.Collections.Generic;
using System.Linq;

namespace GC.Shared;

/// <summary>
/// Portalnamen-Listen über Mod-Grenzen hinweg. GC_Client zeigt beim Umbenennen eines Portals die eigene Liste und
/// alle Listen, die andere GC-Mods anmelden (GC_Server: Server-Liste, GC_Guilds: Gilden-Liste). Keine Mod setzt eine
/// andere voraus: Anbieter tragen sich mit einem Dictionary aus BCL-Typen in eine Liste im AppDomain ein.
///
/// Schlüssel eines Anbieters:
///  id (string), order (int), title (Func&lt;string&gt;), available (Func&lt;bool&gt;), names (Func&lt;List&lt;string&gt;&gt;),
///  canEdit (Func&lt;bool&gt;), add (Action&lt;string&gt;), remove (Action&lt;string&gt;), refresh (Action), version (Func&lt;int&gt;).
/// </summary>
public static class GcPortalNames
{
	public const string Slot = "GC.PortalNames.Providers.v1";

	/// <summary>Höchstlänge wie im Vanilla-Dialog (TeleportWorld.Interact: RequestText(..., 10)).</summary>
	public const int MaxLength = 10;

	/// <summary>Höchstzahl Namen pro Server- oder Gilden-Liste (der Server lehnt mehr ab).</summary>
	public const int MaxNames = 200;

	public static List<object> Providers()
	{
		AppDomain domain = AppDomain.CurrentDomain;
		lock (typeof(object))
		{
			if (domain.GetData(Slot) is not List<object> list)
			{
				list = new List<object>();
				domain.SetData(Slot, list);
			}
			return list;
		}
	}

	public static void Register(string id, int order, Func<string> title, Func<bool> available, Func<List<string>> names, Func<bool> canEdit, Action<string> add, Action<string> remove, Action refresh, Func<int> version)
	{
		List<object> providers = Providers();
		lock (providers)
		{
			providers.RemoveAll(o => o is Dictionary<string, object> d && d.TryGetValue("id", out object v) && (string)v == id);
			providers.Add(new Dictionary<string, object>
			{
				["id"] = id,
				["order"] = order,
				["title"] = title,
				["available"] = available,
				["names"] = names,
				["canEdit"] = canEdit,
				["add"] = add,
				["remove"] = remove,
				["refresh"] = refresh,
				["version"] = version,
			});
		}
	}

	/// <summary>Name für eine Liste vereinheitlichen: Leerzeichen am Rand weg, höchstens <see cref="MaxLength"/> Zeichen.</summary>
	public static string Clean(string? name)
	{
		string text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim(); // keine Zeilenumbrüche: die Listen sind zeilenweise gespeichert
		return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
	}

	/// <summary>Sortierte Liste ohne Doppelte (Groß-/Kleinschreibung zählt, wie bei Portalnamen).</summary>
	public static List<string> Normalize(IEnumerable<string> names)
	{
		return names.Select(Clean).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
	}
}
