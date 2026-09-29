using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: the driver's hand is a little smaller (<c>p1_hand_scale</c>), since the teammate's band shares the
/// screen, and shifted sideways (<c>p1_hand_offset</c>, left by default) so the driver's raised cards stay off the
/// enemies, whose health and intents the teammate may be reading. Resting cards are shrunk, drawn in toward the middle
/// and lowered so their bottom edge stays where it was; the focused or picked-up card grows as usual but to the same
/// smaller size. Applied after the hand lays itself out, so it never compounds.
/// </summary>
[HarmonyPatch]
internal static class CouchDriverHandPatch
{
    private static readonly AccessTools.FieldRef<NHandCardHolder, Vector2> TargetPositionRef =
        AccessTools.FieldRefAccess<NHandCardHolder, Vector2>("_targetPosition");

    private static readonly AccessTools.FieldRef<NHandCardHolder, Vector2> TargetScaleRef =
        AccessTools.FieldRefAccess<NHandCardHolder, Vector2>("_targetScale");

    private static bool Active =>
        CouchConfig.SimultaneousEnabled
        && LocalSelfCoopContext.IsEnabled
        && (Mathf.Abs(CouchConfig.DriverHandScale - 1f) > 0.01f || Mathf.Abs(CouchConfig.DriverHandOffset) > 0.5f);

    [HarmonyPatch(typeof(NPlayerHand), "RefreshLayout")]
    [HarmonyPostfix]
    private static void PostfixRefreshLayout(NPlayerHand __instance)
    {
        if (!Active)
        {
            return;
        }

        foreach (NHandCardHolder holder in __instance.ActiveHolders)
        {
            if (holder == __instance.FocusedHolder)
            {
                ShrinkFocused(holder);
            }
            else
            {
                Shrink(holder);
            }
        }
    }

    [HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.BeginDrag))]
    [HarmonyPostfix]
    private static void PostfixBeginDrag(NHandCardHolder __instance)
    {
        if (Active)
        {
            __instance.SetScaleInstantly(__instance.Scale * CouchConfig.DriverHandScale);
        }
    }

    [HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.SetDefaultTargets))]
    [HarmonyPostfix]
    private static void PostfixSetDefaultTargets(NHandCardHolder __instance)
    {
        if (Active)
        {
            Shrink(__instance);
        }
    }

    /// <summary>
    /// The focused card: <c>NPlayerHand.RefreshLayout</c> snaps it to full size with its bottom edge on the hand's
    /// baseline; keep it on the baseline at the smaller size.
    /// </summary>
    private static void ShrinkFocused(NHandCardHolder holder)
    {
        float scale = CouchConfig.DriverHandScale;
        Vector2 target = TargetPositionRef(holder);
        float drop = holder.Hitbox.Size.Y * 0.5f * (1f - scale);
        Vector2 shrunk = new(target.X * scale + CouchConfig.DriverHandOffset, target.Y + drop);
        holder.SetScaleInstantly(Vector2.One * scale);
        holder.Position = new Vector2(holder.Position.X, shrunk.Y);
        holder.SetTargetPosition(shrunk);
    }

    private static void Shrink(NHandCardHolder holder)
    {
        float scale = CouchConfig.DriverHandScale;
        Vector2 position = TargetPositionRef(holder);
        Vector2 targetScale = TargetScaleRef(holder);
        float drop = NCard.defaultSize.Y * targetScale.Y * (1f - scale) * 0.5f;
        holder.SetTargetPosition(new Vector2(position.X * scale + CouchConfig.DriverHandOffset, position.Y + drop));
        holder.SetTargetScale(targetScale * scale);
    }
}
