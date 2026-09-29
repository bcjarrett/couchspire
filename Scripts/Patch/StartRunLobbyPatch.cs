using System.Reflection;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Compatibility for 0.103.2: the run-start entry point has changed to BeginRunForAllPlayers; the actual local start flow is handled by BeginRunLocally.
/// </summary>
[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayers")]
internal static class StartRunLobbyPatch
{
    [HarmonyPrefix]
    private static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers)
    {
        if (__instance.NetService is not LocalLoopbackHostGameService)
        {
            return true;
        }

        ModLog.Info("Detected the local loopback lobby starting a run; taking over the BeginRunForAllPlayers logic.");

        MethodInfo? updatePreferredAscensionMethod = AccessTools.Method(typeof(StartRunLobby), "UpdatePreferredAscension");
        updatePreferredAscensionMethod?.Invoke(__instance, Array.Empty<object>());

        LobbyBeginRunMessage beginRunMessage = new()
        {
            playersInLobby = __instance.Players,
            seed = seed,
            modifiers = modifiers.Select((modifier) => modifier.ToSerializable()).ToList(),
            act1 = __instance.Act1
        };
        __instance.NetService.SendMessage(beginRunMessage);

        MethodInfo? beginRunLocallyMethod = AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally");
        if (beginRunLocallyMethod == null)
        {
            throw new MissingMethodException(typeof(StartRunLobby).FullName, "BeginRunLocally");
        }

        beginRunLocallyMethod.Invoke(__instance, new object[] { seed, modifiers });
        ModLog.Info($"Local loopback lobby run-start flow complete, player count={__instance.Players.Count}, seed={seed}");
        return false;
    }
}
