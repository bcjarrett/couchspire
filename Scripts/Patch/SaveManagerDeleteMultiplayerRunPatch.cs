using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Saves;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// <c>SaveManager.DeleteCurrentMultiplayerRun</c> always deletes both
/// <c>current_run_mp.save</c> and its <c>.backup</c>, even when neither exists. On a profile
/// whose <c>saves/</c> folder hasn't been created yet (a first modded session, or a freshly
/// reset test profile), Godot's <c>DirAccess.RemoveAbsolute</c> reports a missing parent
/// directory as <see cref="Godot.Error.Failed"/> rather than <see cref="Godot.Error.FileNotFound"/>,
/// so <c>GodotFileIo.DeleteFile</c> logs an <c>[ERROR]</c> for a delete whose goal (no such
/// file) was already true. The game calls this unconditionally in several places (couch entry,
/// run-end cleanup in <c>RunManager.OnEnded</c>), so guard it once here instead of at each
/// call site: skip the delete entirely when <see cref="SaveManager.HasMultiplayerRunSave"/> is
/// false, since then there is nothing to delete either way.
/// </summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentMultiplayerRun))]
internal static class SaveManagerDeleteMultiplayerRunPatch
{
    [HarmonyPrefix]
    private static bool Prefix(SaveManager __instance)
    {
        if (!__instance.HasMultiplayerRunSave)
        {
            ModLog.Info("Skipped deleting the multiplayer run save: none exists (avoids a spurious [ERROR] on a profile with no saves/ folder yet).");
            return false;
        }

        return true;
    }
}
