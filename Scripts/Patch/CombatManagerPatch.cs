using System.Threading.Tasks;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
internal static class CombatManagerPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, ref bool canBackOut, ref Func<Task>? actionDuringEnemyTurn)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        if (canBackOut)
        {
            canBackOut = false;
            LocalMultiControlLogger.Info($"本地双人模式禁用回合回退: player={player.NetId}");
        }
    }
}
