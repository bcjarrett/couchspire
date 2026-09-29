#if COUCHSPIRE_TESTS
namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Mod log lines that signal a real problem even when logged below Error level (docs/testing.md).
/// <see cref="CouchTestLogWatch"/> fails a scenario on the first line matching one of these, or on any Error-level
/// line. Substrings, not whole interpolated lines: values and ids inside them change per run.
///
/// Each pattern below was checked against the current source (paths given); update this list whenever a cited line
/// changes wording, and re-check line numbers if this file is touched during a game-patch adaptation (see
/// AGENTS.md §5).
/// </summary>
internal static class CouchTestLogPatterns
{
    private static readonly (string Substring, string Meaning)[] FailurePatterns =
    {
        // Scripts/Patch/UsePotionActionWatchdogPatch.cs:31
        ("waited for selection over", "potion-use watchdog fired: a potion action waited too long for its selection"),

        // Scripts/Runtime/LocalControlRuntime.cs (RecordFlowBlockSignal)
        ("Flow-block watchdog:", "flow-block watchdog fired: repeated signals that a player's turn/flow is stuck"),

        // Scripts/Runtime/LocalControlRuntime.cs (ApplyControlContext)
        ("Control context switch rolled back:", "a control-context switch had to be rolled back after a combat UI refresh failure"),

        // Scripts/Runtime/Couch/CouchInputRouter.cs:210
        ("claim did not take effect (switch was rolled back)", "a seat's driver claim was rolled back"),

        // Scripts/Patch/ActionQueueFailSafePatch.cs (both finalizers)
        ("triggered a null reference, intercepted to avoid blocking", "a null reference was swallowed in the action-queue UI enqueue path"),
        ("null reference intercepted, avoiding a block", "a null reference was swallowed in RequestEnqueue"),

        // Scripts/Patch/CombatManagerReadyEnemyTurnPatch.cs (Postfix) — logged at Info, but it means the game's own
        // ready-signal path stalled and the mod's local fallback had to advance the enemy turn itself.
        ("triggering a local fallback to advance it", "the enemy turn was not progressing on its own; the mod's local fallback had to advance it"),

        // Scripts/Runtime/Couch/CouchTeammateChoices.cs:141
        ("kind of choice is unknown", "a teammate choice was requested whose kind the mod doesn't recognize, so it can't be answered"),

        // Scripts/Patch/CombatManagerReadyEnemyTurnPatch.cs (GetTurnState) — game-patch drift: CombatManager's
        // internals were renamed/removed.
        ("CombatTurnState members not found", "patch drift: CombatManager's internal turn-state members were not found by reflection"),

        // Scripts/Runtime/LocalControlRuntime.cs (AlignContextForActionOwner)
        ("Detected manual card-play context drift", "the mod detected and corrected a wrong action-owner context (a real wrong-player symptom upstream)"),
    };

    /// <summary>
    /// Known-benign lines that would otherwise match a pattern above (here: any Error-level line, since this list
    /// isn't limited to <see cref="FailurePatterns"/>). To allow one, add an entry with a comment explaining why
    /// it's expected, e.g.: <c>("substring unique to the benign line", "benign because ..."),</c>
    /// </summary>
    private static readonly (string Substring, string Reason)[] Allowlist =
    {
    };

    /// <summary>The first pattern this line matches, or null. Checked after <see cref="IsAllowlisted"/>.</summary>
    public static string? FirstMatch(string line)
    {
        foreach ((string substring, string _) in FailurePatterns)
        {
            if (line.Contains(substring, System.StringComparison.Ordinal))
            {
                return substring;
            }
        }

        return null;
    }

    public static bool IsAllowlisted(string line)
    {
        foreach ((string substring, string _) in Allowlist)
        {
            if (line.Contains(substring, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
#endif
