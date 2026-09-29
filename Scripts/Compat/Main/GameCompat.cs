using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Compat;

/// <summary>
/// Game API differences between the Steam main and beta branches, main-branch side. Scripts/Compat/Beta/GameCompat.cs
/// has the same members for the beta; the csproj compiles one or the other (AGENTS.md §5).
/// </summary>
internal static class GameCompat
{
    public const string GameBranch = "main";

    public const string SteamStickPositionField = "_joystickPosition";

    private static readonly AccessTools.FieldRef<GodotControllerInputStrategy, Dictionary<StringName, StringName>> MegaInputMapRef =
        AccessTools.FieldRefAccess<GodotControllerInputStrategy, Dictionary<StringName, StringName>>("_megaInputMap");

    private static readonly Dictionary<StringName, StringName[]> _analogToDigital = new();

    public static bool IsUsingController()
    {
        return NControllerManager.Instance?.IsUsingController == true;
    }

    public static bool CanUsePotions(Player player)
    {
        return player.CanRemovePotions;
    }

    public static VoteToMoveToNextActAction NewNextActVote(Player player, RunState runState)
    {
        return new VoteToMoveToNextActAction(player);
    }

    public static void GenerateEventCombatState(EventSynchronizer synchronizer, EventModel targetEvent, RunState runState)
    {
        targetEvent.GenerateInternalCombatState(runState);
    }

    public static IEnumerable<ulong> LoadLobbyPlayerIds(LoadRunLobby lobby)
    {
        return lobby.ConnectedPlayerIds;
    }

    /// <summary>Main has no before-offered hook; the reward modify hooks already ran inside GenerateForRoomEnd.</summary>
    public static Task BeforeCombatRewardOffered(RewardsSet rewardsSet, IRunState runState, CombatRoom combatRoom)
    {
        return Task.CompletedTask;
    }

    /// <summary>Main has no skip message: leaving a rest site is local UI even for online players.</summary>
    public static void NotifyRestSiteSkipped(Player teammate)
    {
    }

    /// <summary>
    /// The game's analog-to-digital action map. Main maps each stick direction to one action; this presents it in the
    /// beta's one-to-many shape, cached so the per-frame poll doesn't allocate.
    /// </summary>
    public static Dictionary<StringName, StringName[]> AnalogToDigital(GodotControllerInputStrategy strategy)
    {
        Dictionary<StringName, StringName> source = MegaInputMapRef(strategy);
        bool stale = source.Count != _analogToDigital.Count;
        if (!stale)
        {
            foreach (KeyValuePair<StringName, StringName> mapping in source)
            {
                if (!_analogToDigital.TryGetValue(mapping.Key, out StringName[]? targets) || targets[0] != mapping.Value)
                {
                    stale = true;
                    break;
                }
            }
        }

        if (stale)
        {
            _analogToDigital.Clear();
            foreach (KeyValuePair<StringName, StringName> mapping in source)
            {
                _analogToDigital[mapping.Key] = new[] { mapping.Value };
            }
        }

        return _analogToDigital;
    }
}
