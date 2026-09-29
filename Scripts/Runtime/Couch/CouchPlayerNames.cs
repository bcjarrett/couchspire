using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Names for the local characters, e.g. "P1 · The Ironclad": the seat and the character. The platform name would be the
/// same Steam account for every local character, or the made-up id the mod gives P2.
/// </summary>
internal static class CouchPlayerNames
{
    /// <summary>Names already worked out, per player and character (the game can ask for names often).</summary>
    private static readonly Dictionary<(ulong PlayerId, int Slot, string? Character), string> Cache = new();

    /// <summary>The couch name for a local character, or null for anyone else.</summary>
    public static string? For(ulong playerId)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.TryGetSlotIndex(playerId, out int slot))
        {
            return null;
        }

        Player? player = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(playerId);
        (ulong, int, string?) key = (playerId, slot, player?.Character.Id.Entry);
        if (!Cache.TryGetValue(key, out string? name))
        {
            name = Build(slot, player);
            Cache[key] = name;
        }

        return name;
    }

    private static string Build(int slot, Player? player)
    {
        string seat = $"P{slot + 1}";
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
