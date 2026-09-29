using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: keep hover tips above the teammate's HUD (top bar, relic bar, hand), which the couch UI draws with
/// non-zero z-indices of its own (up to 10). Without this, a tooltip for the driver's own potions/relics can end up
/// underneath the teammate's UI. Every tooltip node goes through <c>_Ready</c>, regardless of which factory method
/// (<c>CreateAndShow</c>, <c>CreateAndShowMapPointHistory</c>) created it.
/// </summary>
[HarmonyPatch(typeof(NHoverTipSet), nameof(NHoverTipSet._Ready))]
internal static class HoverTipZIndexPatch
{
    /// <summary>Above the highest z-index the couch HUD uses for its own children (10, for its header/hint labels).</summary>
    private const int TipZIndex = 20;

    [HarmonyPostfix]
    private static void Postfix(NHoverTipSet __instance)
    {
        __instance.ZIndex = TipZIndex;
    }
}
