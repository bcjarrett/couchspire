namespace CouchSpire.Tests;

/// <summary>
/// Intentional legacy-name fallbacks: call sites where the mod tries a newer member name first and only falls
/// back to one of these old names if the new one is missing (see AGENTS.md §5 step 5). A current sts2.dll
/// legitimately not having the old name is not a bug, so these are allow-listed here — one entry per fallback,
/// each pointing at the source line that does the "new name, then old name" dance. Anything that fails to
/// resolve and isn't listed here is a real finding, not something to add here to make the test pass.
/// </summary>
internal static class AccessToolsAllowList
{
    private static readonly (string TypeName, string MemberName, string Comment)[] Entries =
    {
        (
            "LoadRunLobby",
            "BeginRunIfAllPlayersReady",
            "New name tried first (BeginRunForAllPlayersIfAllReady); this is the fallback. " +
            "Scripts/Compat/Beta/LoadRunLobbyPatch.cs (InvokeBeginRunIfAllPlayersReady)."
        ),
        (
            "StartRunLobby",
            "BeginRunIfAllPlayersReady",
            "New name tried first (BeginRunForAllPlayersIfAllReady); this is the fallback. " +
            "Scripts/Patch/StartRunLobbySetReadyPatch.cs:43."
        ),
    };

    public static bool TryGetReason(string? declaringTypeFullName, string memberName, out string reason)
    {
        if (declaringTypeFullName != null)
        {
            foreach ((string typeName, string member, string comment) in Entries)
            {
                if (member == memberName && (declaringTypeFullName == typeName || declaringTypeFullName.EndsWith("." + typeName, StringComparison.Ordinal)))
                {
                    reason = comment;
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }
}
