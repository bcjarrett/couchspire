using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch]
internal static class SynchronizationOwnershipLogPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedCard))]
    private static void PostfixRewardCard(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "奖励-拿牌");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedRelic))]
    private static void PostfixRewardRelic(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "奖励-拿遗物");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedPotion))]
    private static void PostfixRewardPotion(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "奖励-拿药水");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedGold))]
    private static void PostfixRewardGold(RewardSynchronizer __instance)
    {
        LogOwnership(__instance, "奖励-拿金币");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalMerchantCardRemoval))]
    private static void PostfixMerchantRemoval(OneOffSynchronizer __instance)
    {
        LogOwnership(__instance, "商店-删牌");
    }

    private static void LogOwnership(object synchronizer, string operation)
    {
        ulong? contextId = LocalContext.NetId;
        object? localId = AccessTools.Field(synchronizer.GetType(), "_localPlayerId")?.GetValue(synchronizer);
        LocalMultiControl.Scripts.Runtime.LocalMultiControlLogger.Info($"{operation}归属玩家: context={contextId?.ToString() ?? "null"}, syncLocal={localId?.ToString() ?? "null"}");
    }
}
