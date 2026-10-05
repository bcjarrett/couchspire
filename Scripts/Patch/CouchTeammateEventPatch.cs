using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: give every event room the teammate's event panel (it hides itself unless the teammate has an event
/// to play), and keep the driver in the room until the teammate is done.
/// </summary>
[HarmonyPatch]
internal static class CouchTeammateEventPatch
{
    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom._Ready))]
    [HarmonyPostfix]
    private static void PostfixReady(NEventRoom __instance)
    {
        if (LocalSelfCoopContext.IsEnabled && CouchConfig.SimultaneousEnabled)
        {
            CouchTeammateEvent.Attach(__instance);
            CouchTeammateCrystalSphere.Attach(__instance);
        }
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.Proceed))]
    [HarmonyPrefix]
    private static bool PrefixProceed(ref Task __result)
    {
        if (!CouchTeammateEvent.BlocksProceed)
        {
            return true;
        }

        CouchTeammateEvent.NotifyProceedBlocked();
        __result = Task.CompletedTask;
        return false;
    }
}
