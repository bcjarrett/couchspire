using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Testing;
#endif
using MegaCrit.Sts2.Core.Modding;

namespace CouchSpire.Scripts;

[ModInitializer(nameof(Init))]
public partial class Entry
{
    private const string BuildMarker = "CouchSpire 0.2.0 loaded (game v0.111.0)";

    private static Harmony? _harmony;

    public static void Init()
    {
        ModLog.Info("Applying Harmony patches.");
        ModLog.Info(BuildMarker);
        _harmony = new Harmony("sts2.couchspire");
        _harmony.PatchAll();
        CouchRuntime.Initialize();
        ModLog.Info("Mod initialized.");
#if COUCHSPIRE_TESTS
        CouchTestRunner.StartIfRequested();
#endif
    }
}
