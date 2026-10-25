using HarmonyLib;

namespace GC.Shared;

/// <summary>
/// Hauptmenü: Solange GC Option offen ist (und im Bild, in dem es schließt), bekommt Valheims Startmenü keine Tasten.
/// Jötunns BlockInput wirkt nur in einer Welt; ohne diese Sperre startete Enter im Suchfeld nebenbei „Spiel starten“, und
/// die Pfeiltasten nahmen dem Eingabefeld den Fokus (Code-Prüfung 25.10.2026).
/// </summary>
[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.UpdateKeyboard))]
internal static class GcMenuStartKeyboardPatch
{
	private static bool Prefix() => !GcMenu.HoldsInput;
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.UpdateGamepad))]
internal static class GcMenuStartGamepadPatch
{
	private static bool Prefix() => !GcMenu.HoldsInput;
}
