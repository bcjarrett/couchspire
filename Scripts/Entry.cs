using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Testing;
#endif
using MegaCrit.Sts2.Core.Modding;

namespace LocalMultiControl.Scripts;

[ModInitializer(nameof(Init))]
public partial class Entry
{
    private const string BuildMarker = "CouchSpire 0.1.0 on Revival v1.33 loaded (game v0.111.0, marker=2026-09-26-couch4)";

    private static Harmony? _harmony;

    public static void Init()
    {
        LocalMultiControlLogger.Info("Applying Harmony patches.");
        LocalMultiControlLogger.Info(BuildMarker);
        _harmony = new Harmony("sts2.couchspire");
        _harmony.PatchAll();
        CouchRuntime.Initialize();
        LocalMultiControlLogger.Info("Mod initialized.");
#if COUCHSPIRE_TESTS
        CouchTestRunner.StartIfRequested();
#endif
    }
}
