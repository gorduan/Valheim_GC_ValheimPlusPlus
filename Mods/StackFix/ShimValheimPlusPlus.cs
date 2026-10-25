using BepInEx.Logging;
using GC.Shared;

namespace GC.ValheimPlusPlus;

/// <summary>
/// Verbindung der aus ValheimPlusPlus übernommenen Funktion zu ihrer Mod (GC-SDK): Log, Freigabe durch die Stufe des
/// Servers (Allowed) und ob der Server die Mod hat (OnServer). Setzt das Plugin der Mod in Awake.
/// </summary>
internal static class VppPlugin
{
	internal static ManualLogSource Log = null!;
	internal static GcModule? Module;
	internal static bool Allowed => Module == null || Module.Allowed;
	internal static bool OnServer => Module != null && Module.OnServer;
}
