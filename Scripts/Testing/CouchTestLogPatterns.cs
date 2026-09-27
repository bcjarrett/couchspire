#if COUCHSPIRE_TESTS
namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// Mod log lines that signal a real problem even when logged below Error level (docs/design/testing-plan.md §6.5).
/// <see cref="CouchTestLogWatch"/> fails a scenario on the first line matching one of these, or on any Error-level
/// line. Substrings, not whole interpolated lines: values and ids inside them change per run.
///
/// Each pattern below was checked against the current source (paths given); update this list whenever a cited line
/// changes wording, and re-check line numbers if this file is touched during a game-patch adaptation (AGENTS.md §5).
/// </summary>
internal static class CouchTestLogPatterns
{
    private static readonly (string Substring, string Meaning)[] FailurePatterns =
    {
        // Scripts/Patch/UsePotionActionWatchdogPatch.cs:31
        ("waited for selection over", "potion-use watchdog fired: a potion action waited too long for its selection"),

        // Scripts/Runtime/LocalMultiControlRuntime.cs (RecordFlowBlockSignal)
        ("Flow-block watchdog:", "flow-block watchdog fired: repeated signals that a player's turn/flow is stuck"),

        // Scripts/Runtime/LocalMultiControlRuntime.cs (ApplyControlContext)
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

        // Scripts/Runtime/LocalMultiControlRuntime.cs (AlignContextForActionOwner)
        ("Detected manual card-play context drift", "the mod detected and corrected a wrong action-owner context (a real wrong-player symptom upstream)"),
    };

    /// <summary>
    /// Known-benign lines that would otherwise match a pattern above (here: any Error-level line, since this list
    /// isn't limited to <see cref="FailurePatterns"/>). To allow one, add an entry with a comment explaining why
    /// it's expected, e.g.: <c>("substring unique to the benign line", "benign because ..."),</c>
    /// </summary>
    private static readonly (string Substring, string Reason)[] Allowlist =
    {
        // GodotFileIo.cs:167-173 (game core): DeleteFile logs Error only when DirAccess.RemoveAbsolute returns
        // neither Ok nor FileNotFound. On a profile reset down to just settings.save (docs/design/testing-plan.md
        // §6.6 rule 1), modded/profile1/saves/ doesn't exist yet the first time a couch run is entered — nothing has
        // written a save there yet — so SaveManager.DeleteCurrentMultiplayerRun() (called unconditionally by
        // NMultiplayerHostSubmenuPatch.OnLocalSelfCoopPressed to clear stale multiplayer saves) hits a missing
        // parent directory, which Godot reports as Error.Failed rather than Error.FileNotFound. Confirmed benign by
        // running "start" on a freshly reset profile (2026-09-27): the assertions all passed and
        // modded/profile1/saves/current_run_mp.save legitimately doesn't exist afterward either way. In real play
        // this profile directory already exists by the time anyone reaches Couch Co-op, so this is a test-harness
        // fresh-profile artifact, not a reachable player-facing bug.
        ("Error deleting path modded/profile1/saves/current_run_mp.save", "GodotFileIo.DeleteFile logs Error.Failed for a missing parent directory on a freshly reset profile; the delete's goal (no such file) is already true either way")
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
