using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.ValheimPlusPlus.ModBasisEssen;

/// <summary>
/// GC_BasisEssen (Gorduan), Stufe 4 „GC leicht“: Essen läuft an der Werkbank und im Bereich eines Wächtersteins langsamer ab.
/// Früher Teil von ValheimPlusPlus. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		GC.ValheimPlusPlus.VppPlugin.Log = Logger;
		GC.ValheimPlusPlus.VppPlugin.Module = Module;
		Harmony harmony = new(GcMod.Guid);
		BaseFood.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		Module.IsOn = () => BaseFood.Plugin.On; // Boss-Fortschritt: nur, wenn eingeschaltet
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("vpp_" + key))
			.Main("basefood", BaseFood.Plugin.Enabled)
			.Sub("basefood_workbench", BaseFood.Plugin.WorkbenchPercent, 10, " %")
			.Sub("basefood_ward", BaseFood.Plugin.WardPercent, 10, " %")
			.Sub("basefood_access", BaseFood.Plugin.WardNeedsAccess);
	}
}
