using System;
using System.Reflection;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NCombatCardPile), nameof(NCombatCardPile.Initialize))]
internal static class NCombatCardPilePatch
{
    [HarmonyPrefix]
    private static void Prefix(NCombatCardPile __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        CardPile? previousPile = AccessTools.Field(typeof(NCombatCardPile), "_pile")?.GetValue(__instance) as CardPile;
        if (previousPile == null)
        {
            return;
        }

        try
        {
            MethodInfo? addCardMethod = AccessTools.Method(__instance.GetType(), "AddCard");
            MethodInfo? removeCardMethod = AccessTools.Method(typeof(NCombatCardPile), "RemoveCard");
            if (addCardMethod == null || removeCardMethod == null)
            {
                return;
            }

            Action addCardHandler = (Action)Delegate.CreateDelegate(typeof(Action), __instance, addCardMethod);
            Action removeCardHandler = (Action)Delegate.CreateDelegate(typeof(Action), __instance, removeCardMethod);
            previousPile.CardAddFinished -= addCardHandler;
            previousPile.CardRemoveFinished -= removeCardHandler;
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"切换角色时清理牌堆监听失败: {exception.Message}");
        }
    }
}
