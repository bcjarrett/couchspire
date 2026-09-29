using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Null;
using MegaCrit.Sts2.Core.Platform.Steam;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Local characters are named by seat and character ("P1 · The Ironclad") instead of the platform name. Both lookups are
/// patched: the plain one feeds labels such as the players list on the left; the escaped one feeds rich text. They are
/// one-liners the JIT inlines into their callers (so their patches don't always run); the platform strategies below
/// them are called through an interface and can't be inlined, so they're patched too.
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

[HarmonyPatch]
internal static class PlatformStrategyPlayerNamePatch
{
    [HarmonyPatch(typeof(SteamPlatformUtilStrategy), nameof(SteamPlatformUtilStrategy.GetPlayerName))]
    [HarmonyPrefix]
    private static bool PrefixSteamGetPlayerName(ulong playerId, ref string __result)
    {
        return UseCouchName(playerId, ref __result);
    }

    [HarmonyPatch(typeof(NullPlatformUtilStrategy), nameof(NullPlatformUtilStrategy.GetPlayerName))]
    [HarmonyPrefix]
    private static bool PrefixNullGetPlayerName(ulong playerId, ref string __result)
    {
        return UseCouchName(playerId, ref __result);
    }

    private static bool UseCouchName(ulong playerId, ref string result)
    {
        string? name = CouchPlayerNames.For(playerId);
        if (name == null)
        {
            return true;
        }

        result = name;
        return false;
    }
}
