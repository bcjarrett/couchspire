using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Patch;

/// <summary>Keyboard Tab hands control to the other character (in a run) or the other player's pick (character select).</summary>
[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class NGameInputPatch
{
    [HarmonyPostfix]
    private static void Postfix(InputEvent inputEvent)
    {
        if (!LocalSelfCoopContext.IsEnabled
            || inputEvent is not InputEventKey keyEvent
            || !keyEvent.IsReleased()
            || (keyEvent.Keycode != Key.Tab && keyEvent.PhysicalKeycode != Key.Tab))
        {
            return;
        }

        ModLog.Info("Tab: switching character.");
        if (RunManager.Instance.IsInProgress)
        {
            LocalControlRuntime.SwitchNextControlledPlayer("hotkey:Tab");
        }
        else
        {
            LocalSelfCoopContext.SwitchLobbyEditingPlayer();
        }
    }
}
