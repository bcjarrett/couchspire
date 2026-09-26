using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Platform;

namespace LocalMultiControl.Scripts.Patch;

/// <summary>
/// Local characters are named by seat and character ("P1 · The Ironclad") instead of the platform name. Both lookups are
/// patched: the plain one feeds labels such as the players list on the left; the escaped one feeds rich text.
/// </summary>
[HarmonyPatch(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName))]
internal static class PlatformUtilGetPlayerNamePatch
{
    [HarmonyPrefix]
    private static bool Prefix(PlatformType platformType, ulong playerId, ref string __result)
    {
        if (!LocalSelfCoopContext.TryGetSlotIndex(playerId, out int slotIndex))
        {
            return true;
        }

        __result = (CouchPlayerNames.For(playerId) ?? LocalModText.RoleSlot((slotIndex + 1).ToString())).EscapeBbcodeTags();
        return false;
    }
}

[HarmonyPatch(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerNameRaw))]
internal static class PlatformUtilGetPlayerNameRawPatch
{
    [HarmonyPrefix]
    private static bool PrefixGetPlayerNameRaw(ulong playerId, ref string __result)
    {
        string? name = CouchPlayerNames.For(playerId);
        if (name == null)
        {
            return true;
        }

        __result = name;
        return false;
    }
}
