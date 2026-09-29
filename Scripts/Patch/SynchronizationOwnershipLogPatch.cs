using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch]
internal static class SynchronizationOwnershipLogPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedCard))]
    private static void PostfixRewardCard(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "Reward-ObtainCard");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedRelic))]
    private static void PostfixRewardRelic(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "Reward-ObtainRelic");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedPotion))]
    private static void PostfixRewardPotion(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "Reward-ObtainPotion");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedGold))]
    private static void PostfixRewardGold(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "Reward-ObtainGold");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalMerchantCardRemoval))]
    private static void PostfixMerchantRemoval(OneOffSynchronizer __instance)
    {
        LogOwnership(__instance, "Shop-RemoveCard");
    }

    private static void LogOwnership(object synchronizer, string operation)
    {
        ulong? contextId = LocalContext.NetId;
        object? localId = AccessTools.Field(synchronizer.GetType(), "_localPlayerId")?.GetValue(synchronizer);
        CouchSpire.Scripts.Runtime.ModLog.Info($"{operation} owning player: context={contextId?.ToString() ?? "null"}, syncLocal={localId?.ToString() ?? "null"}");
    }
}
