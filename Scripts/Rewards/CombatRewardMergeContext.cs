using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Rewards;

/// <summary>
/// Marks whether we are currently in the combat reward merge flow.
/// While in the merge flow, mirror copying of relics/potions/gold should be suppressed,
/// because each player has already independently generated their own reward.
/// Uses a static counter instead of AsyncLocal to ensure it can also be read correctly from UI callbacks.
/// </summary>
internal static class CombatRewardMergeContext
{
    private sealed class MergeMarker
    {
    }

    private static int _depth;
    private static readonly ConditionalWeakTable<AbstractRoom, MergeMarker> MergedRooms = new();

    internal static bool IsActive => _depth > 0;

    internal static bool TryMarkRoomMerged(AbstractRoom room)
    {
        lock (MergedRooms)
        {
            if (MergedRooms.TryGetValue(room, out _))
            {
                return false;
            }

            MergedRooms.Add(room, new MergeMarker());
            return true;
        }
    }

    /// <summary>
    /// Completes when the rewards screen finishes normally, or when it leaves the tree without finishing (the run is
    /// abandoned, or the room changes, with rewards still open). Waiting for <c>Completed</c> alone never returned in
    /// that case, so the merge counter stayed up and gold/relic/potion sharing stayed off until the game restarted.
    /// </summary>
    internal static Task WaitForRewardsScreenDoneAsync(NRewardsScreen screen)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        uint oneShot = (uint)GodotObject.ConnectFlags.OneShot;
        screen.Connect(NRewardsScreen.SignalName.Completed, Callable.From(() => done.TrySetResult()), oneShot);
        screen.Connect(Node.SignalName.TreeExiting, Callable.From(() => done.TrySetResult()), oneShot);
        return done.Task;
    }

    internal static void Enter()
    {
        _depth++;
    }

    internal static void Exit()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }
}
