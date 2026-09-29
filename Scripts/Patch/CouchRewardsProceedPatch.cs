using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: the driver can't leave the reward screen while the teammate is still taking rewards in their panel.
/// </summary>
[HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
internal static class CouchRewardsProceedPatch
{
    [HarmonyPrefix]
    private static bool PrefixOnProceedButtonPressed(NRewardsScreen __instance)
    {
        if (!CouchTeammateRewards.BlocksProceed(__instance))
        {
            return true;
        }

        CouchTeammateRewards.NotifyProceedBlocked();
        return false;
    }
}
