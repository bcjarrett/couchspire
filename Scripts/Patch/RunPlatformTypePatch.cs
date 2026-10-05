using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// The game finds "the local player" in a saved run with <c>PlatformUtil.GetLocalPlayerId(run.PlatformType)</c>, and
/// stamps that platform from <c>NetService.Platform</c>. The loopback service reports <see cref="PlatformType.None"/>
/// (id 1), but couch player ids come from <see cref="PlatformUtil.PrimaryPlatform"/>, so with Steam running they are
/// Steam ids and nobody matches. Then run-end progress is skipped ("Local player with net id 1 not found in run"), the
/// driver's character never gets a <c>CharacterStats</c> entry, and the game-over summary throws in
/// <c>SaveBadgesToProgress</c> before enabling Main Menu: a soft-lock after Continue on any character the profile
/// hasn't finished a run with. Stamping the platform the ids actually come from fixes the lookup everywhere the run is
/// read (progress, run history). The service itself keeps reporting None, so lobbies don't offer platform invites.
/// </summary>
[HarmonyPatch]
internal static class RunPlatformTypePatch
{
    private static bool _loggedRestamp;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
    private static void PostfixToSave(SerializableRun __result)
    {
        if (RunManager.Instance.NetService is LocalLoopbackHostGameService && __result.PlatformType != PlatformUtil.PrimaryPlatform)
        {
            if (!_loggedRestamp)
            {
                _loggedRestamp = true;
                ModLog.Info($"Saved runs record platform {PlatformUtil.PrimaryPlatform} instead of {__result.PlatformType}, matching the couch player ids.");
            }

            __result.PlatformType = PlatformUtil.PrimaryPlatform;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunHistoryUtilities), nameof(RunHistoryUtilities.CreateRunHistoryEntry))]
    private static void PrefixCreateRunHistoryEntry(SerializableRun run, ref PlatformType platformType)
    {
        if (RunManager.Instance.NetService is LocalLoopbackHostGameService && platformType != run.PlatformType)
        {
            ModLog.Info($"Run history entry platform {platformType} -> {run.PlatformType} (the couch player ids' platform).");
            platformType = run.PlatformType;
        }
    }
}
