using System.Runtime.CompilerServices;
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
