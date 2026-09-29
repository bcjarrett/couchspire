using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: give every combat room the teammate HUD. It hides itself unless simultaneous combat has a teammate.
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom._Ready))]
internal static class CouchTeammateHudPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCombatRoom __instance)
    {
        if (LocalSelfCoopContext.IsEnabled)
        {
            CouchTeammateHud.Attach(__instance);
        }
    }
}
