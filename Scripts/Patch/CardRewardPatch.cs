using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(CardReward), "OnSelect")]
internal static class CardRewardPatch
{
    private struct SenderState
    {
        internal bool IsPatched;
        internal ulong? PreviousContextNetId;
        internal ulong PreviousSenderId;
    }

    [HarmonyPrefix]
    private static void Prefix(CardReward __instance, ref SenderState __state)
    {
        __state = default;
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode)
        {
            return;
        }

        if (RunManager.Instance.NetService is not LocalLoopbackHostGameService loopback)
        {
            return;
        }

        // Couch simultaneous mode: the teammate picks this card in their own rewards panel, through the game's
        // remote-player path, so don't make them the local player here.
        if (CouchTeammateRewards.OwnsReward(__instance))
        {
            return;
        }

        __state.IsPatched = true;
        __state.PreviousContextNetId = LocalContext.NetId;
        __state.PreviousSenderId = loopback.NetId;

        LocalContext.NetId = __instance.Player.NetId;
        loopback.SetCurrentSenderId(__instance.Player.NetId);
        ModLog.Info($"Card reward switched to the reward's owning player: player={__instance.Player.NetId}");
    }

    [HarmonyPostfix]
    private static void Postfix(SenderState __state)
    {
        if (!__state.IsPatched)
        {
            return;
        }

        if (RunManager.Instance.NetService is LocalLoopbackHostGameService loopback && loopback.NetId != __state.PreviousSenderId)
        {
            loopback.SetCurrentSenderId(__state.PreviousSenderId);
        }

        LocalContext.NetId = __state.PreviousContextNetId;
    }
}
