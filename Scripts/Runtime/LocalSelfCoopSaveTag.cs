using MegaCrit.Sts2.Core.Saves;

namespace LocalMultiControl.Scripts.Runtime;

/// <summary>
/// A marker file next to the multiplayer save that says the run belongs to local co-op, and which two player ids it
/// uses. Format: <c>v3:players=id1,id2</c>. Older tags may carry extra sections or more players; those are ignored or
/// rejected.
/// </summary>
internal static class LocalSelfCoopSaveTag
{
    private const string SaveTagFileName = "local_self_coop_mp.tag";
    private const string V3Prefix = "v3:";
    private const string V2Prefix = "v2:";

    public static void MarkCurrentProfile(IReadOnlyList<ulong> playerIds)
    {
        try
        {
            List<ulong> ids = playerIds.Where((id) => id != 0).Distinct().ToList();
            if (ids.Count != LocalSelfCoopContext.PlayerCount)
            {
                LocalMultiControlLogger.Warn($"Save tag not written: expected {LocalSelfCoopContext.PlayerCount} player ids, got [{string.Join(",", ids)}].");
                return;
            }

            string serialized = $"{V3Prefix}players={string.Join(",", ids)}";
            CreateFileIo().WriteFile(SaveTagFileName, serialized);
            LocalMultiControlLogger.Info($"Save tag written: {serialized}");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Save tag write failed: {exception.Message}");
        }
    }

    public static void ClearCurrentProfile()
    {
        try
        {
            GodotFileIo fileIo = CreateFileIo();
            if (fileIo.FileExists(SaveTagFileName))
            {
                fileIo.DeleteFile(SaveTagFileName);
                LocalMultiControlLogger.Info("Save tag cleared.");
            }
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Save tag clear failed: {exception.Message}");
        }
    }

    /// <summary>Reads the tag's player ids. False when there is no tag, or it isn't a two-player local co-op run.</summary>
    public static bool TryReadCurrentProfile(out List<ulong> playerIds)
    {
        playerIds = new List<ulong>();
        try
        {
            GodotFileIo fileIo = CreateFileIo();
            if (!fileIo.FileExists(SaveTagFileName))
            {
                return false;
            }

            string content = fileIo.ReadFile(SaveTagFileName)?.Trim() ?? string.Empty;
            playerIds = ParseIds(ExtractPlayersSection(content));
            if (playerIds.Count != LocalSelfCoopContext.PlayerCount)
            {
                LocalMultiControlLogger.Warn($"Save tag ignored: CouchSpire runs have {LocalSelfCoopContext.PlayerCount} players (tag: {content}).");
                playerIds.Clear();
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"Save tag read failed: {exception.Message}");
            return false;
        }
    }

    private static string ExtractPlayersSection(string content)
    {
        if (content.StartsWith(V3Prefix, StringComparison.OrdinalIgnoreCase))
        {
            // "players=1,2" plus optional sections from older versions (e.g. ";wakuu=...").
            return content.Substring(V3Prefix.Length)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select((segment) => segment.Split('=', 2, StringSplitOptions.TrimEntries))
                .FirstOrDefault((parts) => parts.Length == 2 && parts[0].Equals("players", StringComparison.OrdinalIgnoreCase))
                ?[1] ?? string.Empty;
        }

        return content.StartsWith(V2Prefix, StringComparison.OrdinalIgnoreCase)
            ? content.Substring(V2Prefix.Length)
            : content;
    }

    private static List<ulong> ParseIds(string csv)
    {
        return csv.Split(',')
            .Select((part) => ulong.TryParse(part.Trim(), out ulong id) ? id : 0UL)
            .Where((id) => id != 0)
            .Distinct()
            .ToList();
    }

    private static GodotFileIo CreateFileIo()
    {
        return new GodotFileIo(
            UserDataPathProvider.GetProfileScopedPath(SaveManager.Instance.CurrentProfileId, UserDataPathProvider.SavesDir));
    }
}
