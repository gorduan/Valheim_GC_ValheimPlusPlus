using BepInEx;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;

namespace GC.Mods.Beispiel;

/// <summary>
/// GC_Beispiel (Vorlage des GC-SDK): zeigt beim Betreten der Welt eine Begrüßung.
/// Die Plugin-Attribute ([BepInPlugin], [BepInDependency], [NetworkCompatibility]) erzeugt das SDK aus der .csproj
/// (obj\GcModInfo.g.cs), deshalb ist die Klasse partial und trägt hier keine Attribute.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<string> Greeting = null!;

	private void Awake()
	{
		// 1. Als Erstes anmelden (vor jedem Config.Bind): Der Kern merkt sich dann, ob die cfg neu ist, und liest die Texte.
		Module = GcKern.Register(this, Logger, GcMod.Info);

		// 2. Einstellungen. Gc.Bind = nur für den Spieler, Gc.BindSynced = vom Server vorgegeben (nur Admins ändern sie).
		Enabled = Gc.Bind(Config, "1 - Beispiel", "Enabled", true, "Begrüßung beim Betreten der Welt ein- oder ausschalten.");
		Greeting = Gc.Bind(Config, "1 - Beispiel", "Greeting", "", "Eigener Text; leer = Text aus den Übersetzungen.");

		// 3. Werte aus einer alten cfg übernehmen (GcAlteConfig in der .csproj), danach Hauptschalter und Patches.
		Module.TakeOverOldSettings();
		Module.IsOn = () => Enabled.Value; // für den Boss-Fortschritt: läuft die Mod gerade?
		GcMod.PatchAll(new Harmony(GcMod.Guid)); // nur die Patches dieser Mod (alle Mods des Pakets liegen in einer DLL)

		// 4. Eintrag im Menü GC Option, im Abschnitt der Stufe. Texte: translations\texte.<Sprache>.yml dieser Mod.
		Module.MenuGroup(key => GcLoc.Get("gcbeispiel_" + key))
			.Main("enabled", Enabled);
	}
}

/// <summary>Beim Betreten der Welt: Begrüßung oben links, nur wenn die Mod hier wirken darf (Stufe des Servers).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
internal static class GreetingPatch
{
	private static void Postfix(Player __instance)
	{
		if (__instance != Player.m_localPlayer || !Plugin.Enabled.Value || !Plugin.Module.Allowed)
		{
			return;
		}
		string text = Plugin.Greeting.Value.Length > 0 ? Plugin.Greeting.Value : GcLoc.Get("gcbeispiel_hello", __instance.GetPlayerName());
		Gc.TopLeft(text);
	}
}
