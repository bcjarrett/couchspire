using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Names for the local characters, e.g. "P1 · The Ironclad": the seat and the character. The platform name would be the
/// same Steam account for every local character, or the made-up id the fork gives the extra ones.
/// </summary>
internal static class CouchPlayerNames
{
    /// <summary>The couch name for a local character, or null for anyone else.</summary>
    public static string? For(ulong playerId)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.TryGetSlotIndex(playerId, out int slot))
        {
            return null;
        }

        string seat = $"P{slot + 1}";
        Player? player = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(playerId);
        string character;
        try
        {
            character = player?.Character.Title.GetFormattedText() ?? "";
        }
        catch (System.Exception)
        {
            character = "";
        }

        return character.Length > 0 ? $"{seat} · {character}" : seat;
    }
}
