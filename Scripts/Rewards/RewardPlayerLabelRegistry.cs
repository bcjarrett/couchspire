using System.Runtime.CompilerServices;
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Rewards;

namespace CouchSpire.Scripts.Rewards;

/// <summary>
/// Records the player label (e.g. "Player 1") corresponding to each Reward instance.
/// Written during combat reward merging, and read when NRewardButton displays it.
/// </summary>
internal static class RewardPlayerLabelRegistry
{
    private static readonly ConditionalWeakTable<Reward, string> Labels = new();

    internal static void Register(Reward reward, ulong playerNetId)
    {
        string slotLabel = LocalSelfCoopContext.GetSlotLabel(playerNetId);
        string label = LocalModText.RoleSlot(slotLabel);
        Labels.AddOrUpdate(reward, label);
    }

    internal static bool TryGetLabel(Reward reward, out string? label)
    {
        return Labels.TryGetValue(reward, out label);
    }
}
