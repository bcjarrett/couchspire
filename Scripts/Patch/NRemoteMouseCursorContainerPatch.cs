using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NRemoteMouseCursorContainer), nameof(NRemoteMouseCursorContainer.GetCursorPosition))]
internal static class NRemoteMouseCursorContainerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NRemoteMouseCursorContainer), nameof(NRemoteMouseCursorContainer._Input))]
    private static bool PrefixInput()
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        // Risk: when local co-op reuses the same process, remote mouse input sync continuously drives PeerInput state changes,
        // which easily triggers access to an already-freed NRemoteTargetingIndicator object during the player-switch/node-destruction window.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NRemoteMouseCursorContainer), "OnGuiFocusChanged")]
    private static bool PrefixOnGuiFocusChanged()
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        // Risk: focus changes trigger SyncLocalIsUsingController, further driving the remote intent refresh chain.
        // This chain has no business value in local co-op, and it amplifies lifecycle races during combat player switches.
        return false;
    }

    [HarmonyPrefix]
    private static bool Prefix(NRemoteMouseCursorContainer __instance, ulong playerId, ref Vector2 __result)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return true;
        }

        try
        {
            object? cursor = AccessTools.Method(typeof(NRemoteMouseCursorContainer), "GetCursor")?.Invoke(__instance, new object[] { playerId });
            if (cursor == null)
            {
                __result = Vector2.Zero;
                return false;
            }
        }
        catch
        {
            __result = Vector2.Zero;
            return false;
        }

        return true;
    }
}
