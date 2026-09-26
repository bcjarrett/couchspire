using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Modding;

namespace LocalMultiControl.Scripts;

[ModInitializer(nameof(Init))]
public partial class Entry
{
    private const string BuildMarker = "CouchSpire 0.1.0 on Revival v1.33 loaded (game v0.111.0, marker=2026-09-26-couch2)";

    private static Harmony? _harmony;

    public static void Init()
    {
        LocalMultiControlLogger.Info("开始初始化 Harmony 补丁。");
        LocalMultiControlLogger.Info(BuildMarker);
        LocalWakuuRelicLocalization.Initialize();
        _harmony = new Harmony("sts2.couchspire");
        _harmony.PatchAll();
        CouchRuntime.Initialize();
        LocalMultiControlLogger.Info("Mod 初始化完成。");
    }
}
