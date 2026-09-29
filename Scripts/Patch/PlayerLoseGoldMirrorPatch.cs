using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CouchSpire.Scripts.Patch;

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseGold))]
internal static class PlayerLoseGoldMirrorPatch
{
    private static readonly AsyncLocal<bool> IsMirroring = new();

    [HarmonyPostfix]
    private static void Postfix(decimal amount, Player player, GoldLossType goldLossType, ref Task __result)
    {
        if (!CrystalSphereMirrorRuntime.IsInCrystalSphereEventContext(player))
        {
            return;
        }

        if (amount <= 0m || IsMirroring.Value)
        {
            return;
        }

        __result = MirrorGoldLossToOtherPlayersAsync(amount, player, goldLossType, __result);
    }

    private static async Task MirrorGoldLossToOtherPlayersAsync(
        decimal amount,
        Player sourcePlayer,
        GoldLossType goldLossType,
        Task originalTask)
    {
        await originalTask;

        IsMirroring.Value = true;
        try
        {
            System.Collections.Generic.List<Player> otherPlayers = CrystalSphereMirrorRuntime.GetOtherPlayers(sourcePlayer);
            foreach (Player otherPlayer in otherPlayers)
            {
                await PlayerCmd.LoseGold(amount, otherPlayer, goldLossType);
            }

            ModLog.Info(
                $"Crystal Sphere event gold expenditure mirrored to the other players: amount={amount}, owner={sourcePlayer.NetId}, mirrored={string.Join(",", otherPlayers.Select((player) => player.NetId))}");
        }
        finally
        {
            IsMirroring.Value = false;
        }
    }
}
