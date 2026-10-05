using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace CouchSpire.Scripts.Runtime;

/// <summary>
/// The base mod's single-adventure Crystal Sphere: one player plays the event and its cost and rewards are copied to
/// everyone else. Off in couch simultaneous mode, where each player plays their own.
/// </summary>
internal static class CrystalSphereMirrorRuntime
{
    private const string CrystalSphereEventId = "CRYSTAL_SPHERE";

    public static bool IsInCrystalSphereEventContext(Player? player)
    {
        if (player?.RunState == null)
        {
            return false;
        }

        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode || !RunManager.Instance.IsInProgress)
        {
            return false;
        }

        // Couch simultaneous mode: every player plays their own sphere (the teammate in CouchTeammateCrystalSphere), so
        // copying one player's cost and rewards to the others would double them.
        if (CouchConfig.SimultaneousEnabled && CouchTeammate.FindTeammate() != null)
        {
            return false;
        }

        if (player.RunState.CurrentRoom is not EventRoom)
        {
            return false;
        }

        if (RunManager.Instance.EventSynchronizer.IsShared)
        {
            return false;
        }

        try
        {
            MegaCrit.Sts2.Core.Models.EventModel eventForPlayer = RunManager.Instance.EventSynchronizer.GetEventForPlayer(player);
            return eventForPlayer.Id.Entry == CrystalSphereEventId;
        }
        catch
        {
            return false;
        }
    }

    public static List<Player> GetOtherPlayers(Player sourcePlayer)
    {
        return sourcePlayer.RunState.Players
            .Where((candidate) => candidate.NetId != sourcePlayer.NetId)
            .ToList();
    }
}
