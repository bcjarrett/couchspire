using CouchSpire.Scripts.Runtime.Couch;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Compat;

/// <summary>
/// Game API differences between the Steam main and beta branches, beta-branch side. Scripts/Compat/Main/GameCompat.cs
/// has the same members for main; the csproj compiles one or the other (AGENTS.md §5).
/// </summary>
internal static class GameCompat
{
    public const string GameBranch = "beta";

    public const string SteamStickPositionField = "_lStickPosition";

    private static readonly AccessTools.FieldRef<GodotControllerInputStrategy, Dictionary<StringName, StringName[]>> AnalogToDigitalRef =
        AccessTools.FieldRefAccess<GodotControllerInputStrategy, Dictionary<StringName, StringName[]>>("_analogToDigitalInput");

    public static bool IsUsingController()
    {
        return NControllerManager.Instance?.InputType == InputType.Controller;
    }

    public static bool CanUsePotions(Player player)
    {
        return player.CanUseOrRemovePotions;
    }

    public static VoteToMoveToNextActAction NewNextActVote(Player player, RunState runState)
    {
        return new VoteToMoveToNextActAction(player, runState.CurrentActIndex);
    }

    public static void GenerateEventCombatState(EventSynchronizer synchronizer, EventModel targetEvent, RunState runState)
    {
        synchronizer.GenerateInternalCombatStateIfNecessary(targetEvent);
    }

    public static IEnumerable<ulong> LoadLobbyPlayerIds(LoadRunLobby lobby)
    {
        return lobby.PlayerIds;
    }

    /// <summary>Mirror vanilla: the before-offered hook runs on the reward sets that will actually be shown.</summary>
    public static Task BeforeCombatRewardOffered(RewardsSet rewardsSet, IRunState runState, CombatRoom combatRoom)
    {
        return Hook.BeforeCombatRewardOffered(rewardsSet, runState, combatRoom);
    }

    public static void NotifyRestSiteSkipped(Player teammate)
    {
        CouchRemotePlay.DispatchAs(teammate, new RestSiteSkippedMessage { Location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation });
    }

    /// <summary>The game's analog-to-digital action map (stick directions and triggers, one to many).</summary>
    public static Dictionary<StringName, StringName[]> AnalogToDigital(GodotControllerInputStrategy strategy)
    {
        return AnalogToDigitalRef(strategy);
    }
}
