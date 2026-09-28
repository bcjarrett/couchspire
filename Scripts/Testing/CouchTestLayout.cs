#if COUCHSPIRE_TESTS
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Godot;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>One aspect ratio pass a scenario can run at (docs/design/testing-plan.md §6.5.3, §8.1 spike facts).</summary>
internal readonly record struct CouchTestAspectPass(string Label, AspectRatioSetting Setting, Vector2I WindowSize, Vector2I ExpectedViewport);

/// <summary>Everything a scenario/aspect pass's <see cref="CouchTestContext"/> needs to run layout checks at a checkpoint.</summary>
internal sealed record CouchTestCheckpointOptions(
    string ScenarioName,
    string Aspect,
    string OutDir,
    string BaselinesDir,
    bool Bless,
    bool Review,
    bool StrictLayout);

/// <summary>A rect rounded to whole pixels (docs/design/testing-plan.md §6.5.2), as written to a snapshot/baseline file.</summary>
internal readonly record struct CouchTestRect(int X, int Y, int W, int H)
{
    public static CouchTestRect FromGodot(Rect2 rect)
    {
        return new CouchTestRect(
            Mathf.RoundToInt(rect.Position.X), Mathf.RoundToInt(rect.Position.Y),
            Mathf.RoundToInt(rect.Size.X), Mathf.RoundToInt(rect.Size.Y));
    }

    /// <summary>The largest single-axis move or resize between this rect and <paramref name="other"/>, in pixels.</summary>
    public int MaxDelta(CouchTestRect other)
    {
        int delta = Math.Abs(X - other.X);
        delta = Math.Max(delta, Math.Abs(Y - other.Y));
        delta = Math.Max(delta, Math.Abs(W - other.W));
        delta = Math.Max(delta, Math.Abs(H - other.H));
        return delta;
    }

    public override string ToString() => $"({X}, {Y}, {W}x{H})";
}

/// <summary>One checkpoint's rounded rects, kept in memory (not just on disk) so <c>--repeat</c> can compare runs
/// byte-for-byte without racing its own baseline/snapshot files on disk (docs/design/testing-plan.md §6.2, §6.6 rule 8).</summary>
internal sealed record CouchTestLayoutSnapshot(
    string ScenarioName,
    string Aspect,
    string Checkpoint,
    IReadOnlyDictionary<string, CouchTestRect> Nodes);

/// <summary>
/// Layout checks run at every <see cref="CouchTestContext.Checkpoint"/> (docs/design/testing-plan.md §6.5.1-3, §8 WP4).
///
/// <para>
/// <b>Plan deviation, found while implementing this (report this back — docs/design/testing-plan.md §6.5.1 says to use
/// "the GetGlobalRect() of each visible mod UI root" directly):</b> none of the mod's UI roots carry a meaningful
/// <c>Size</c> on themselves. <see cref="CouchTeammateHud"/> is anchored <c>FullRect</c> to its parent, so its own rect
/// is the whole screen; <see cref="CouchTeammateRelicBar"/>, <see cref="CouchTeammateTopBar"/> and every
/// <see cref="CouchPanel"/> subclass (rest site, shop, treasure, info, choice panel, event, rewards) only ever
/// position their *children* (icons, labels, the <c>CouchFrame</c> background) — their own <c>Size</c> stays the Godot
/// default of zero. So "the root's rect" is computed here as the bounding box of everything visibly drawn under that
/// root (<see cref="ContentBounds"/>), which is what a human actually sees as "the panel". This needed no change to
/// <c>Scripts/Runtime/Couch/</c>.
/// </para>
///
/// <para>
/// <b>Determinism finding (§6.6):</b> <see cref="CouchPanel"/> slides and fades in over ~0.3s
/// (<c>_appearTween</c>, a <c>Back</c>-eased tween) whenever it becomes visible, and that isn't tracked by
/// <see cref="CouchTestContext.Settle"/> (panel visibility never touches <c>NOverlayStack</c>, which is all Settle
/// watches). Measuring rects right after a panel opens would be flaky. <see cref="WaitForStableRootsAsync"/> polls
/// every visible mod root's content bounds frame-by-frame and waits for them to stop moving (3 consecutive frames,
/// capped at 2s) before anything below measures a single rect.
/// </para>
/// </summary>
internal static class CouchTestLayout
{
    /// <summary>Stable UI root names that are Controls with a rect (docs/design/testing-plan.md §4). Excluded, and
    /// why: <c>CouchTeammateDeckChanges</c> is a plain <c>Node</c> (a transient VFX host, not a layout element);
    /// <c>CouchDebugOverlay</c> is a <c>CanvasLayer</c> (no rect) and forced off in tests (§6.6 rule 7,
    /// COUCHSPIRE_OVERLAY=0); <c>CouchInputGate</c> is a plain <c>Node</c> coordinator with no visual footprint;
    /// <c>CouchTeammatePanels</c> is the FullRect container that hosts several of these roots, not itself one.</summary>
    private static readonly string[] ModRootNames =
    {
        "CouchTeammateHud",
        "CouchTeammateRelicBar",
        "CouchTeammateTopBar",
        "CouchTeammateInfo",
        "CouchTeammateRewards",
        "CouchTeammateRestSite",
        "CouchTeammateShop",
        "CouchTeammateTreasure",
        "CouchTeammateEvent",
        "CouchTeammateChoicePanel"
    };

    /// <summary>Overlaps that are allowed by design, each with why (docs/design/testing-plan.md §6.5.1: "some
    /// overlaps may be by design... record any you allowlist, with a reason, in one place"). This is that one place.
    /// Checked in both name orders.</summary>
    private static readonly (string A, string B, string Reason)[] AllowedOverlaps =
    {
        ("CouchTeammateTopBar", "P1TopBar",
            "By design (CouchTeammateTopBar.Layout()): the teammate's top-bar strip is placed inside the empty " +
            "middle of the driver's own top bar (after the floor/boss icons, before the timer/map/deck/pause " +
            "buttons), vertically centered on the driver's HP text. It is meant to sit inside NTopBar's rect, not " +
            "beside it."),
        ("CouchTeammateHud", "CouchTeammateRelicBar",
            "Not a real collision, confirmed on a run (2026-09-28): CouchTeammateRelicBar.Layout() places the combat " +
            "relic row at `CouchTeammateHud.HeaderRight + 30` — always to the right of the header — but " +
            "CouchTeammateHud's *measured* bounding box also spans its separately-laid-out hand of cards, which can " +
            "sit at the same X range further down the screen. The bounding box therefore looks like it overlaps the " +
            "relic row even though the header (the only row the relic bar is actually placed next to) never reaches " +
            "that far right.")
    };

    private const float ViewportTolerancePx = 0.5f;
    private const float AnchorTolerancePx = 4f;
    private const int SnapshotTolerancePx = 4;
    private const int StableFrameCount = 3;
    private static readonly TimeSpan StabilizeTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex SnapshotEntryPattern = new(
        "\"(?<name>[^\"]+)\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(?<x>-?\\d+)\\s*,\\s*\"y\"\\s*:\\s*(?<y>-?\\d+)\\s*,\\s*\"w\"\\s*:\\s*(?<w>-?\\d+)\\s*,\\s*\"h\"\\s*:\\s*(?<h>-?\\d+)\\s*\\}",
        RegexOptions.Compiled);

    private static readonly List<(string Label, string Path)> ReviewShots = new();

    /// <summary>Snapshot mismatches this run that didn't fail it (no <c>--strict-layout</c>); listed in summary.txt.</summary>
    public static readonly List<string> DriftNotes = new();

    public static readonly IReadOnlyList<string> KnownAspects = new[] { "16:9", "16:10" };

    // ---------------------------------------------------------------------------------------------------------
    // Aspect passes (docs/design/testing-plan.md §6.5.3, §8.1)
    // ---------------------------------------------------------------------------------------------------------

    public static CouchTestAspectPass ResolveAspect(string label)
    {
        return label switch
        {
            "16:9" => new CouchTestAspectPass("16:9", AspectRatioSetting.SixteenByNine, new Vector2I(1600, 900), new Vector2I(1920, 1080)),
            "16:10" => new CouchTestAspectPass("16:10", AspectRatioSetting.SixteenByTen, new Vector2I(1440, 900), new Vector2I(1920, 1200)),
            _ => throw new ArgumentException($"Unknown aspect label '{label}'. Known: {string.Join(", ", KnownAspects)}.")
        };
    }

    /// <summary>Pins the window/aspect the game's own way (§8.1 spike facts) and waits for the viewport to actually
    /// settle to the aspect's content-scale size before returning.</summary>
    public static async Task PinAsync(SceneTree tree, string aspectLabel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CouchTestAspectPass pass = ResolveAspect(aspectLabel);
        SettingsSave settings = SaveManager.Instance.SettingsSave;
        settings.AspectRatioSetting = pass.Setting;
        settings.WindowSize = pass.WindowSize;
        NGame.Instance!.ApplyDisplaySettings();

        Vector2 expected = new(pass.ExpectedViewport.X, pass.ExpectedViewport.Y);
        try
        {
            await WaitHelper.Until(
                () => tree.Root.GetVisibleRect().Size == expected,
                token,
                TimeSpan.FromSeconds(10),
                () => $"Viewport did not settle to {pass.ExpectedViewport} for aspect {pass.Label} after ApplyDisplaySettings (got {tree.Root.GetVisibleRect().Size}).");
        }
        catch (AutoSlayTimeoutException ex)
        {
            throw new CouchTestExpectationFailedException(ex.Message);
        }

        CouchTestLog.Info($"Display pinned: aspect={pass.Label}, window={pass.WindowSize}, viewport={tree.Root.GetVisibleRect().Size}.");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Checkpoint entry point
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Runs every layout rule and the snapshot compare/bless for one checkpoint (docs/design/testing-plan.md
    /// §6.5.1-3). Throws <see cref="CouchTestExpectationFailedException"/> naming every failing node and its numbers
    /// on the first violation found; on success, returns this checkpoint's snapshot for <c>--repeat</c> to compare.</summary>
    public static async Task<CouchTestLayoutSnapshot> RunCheckpointAsync(SceneTree tree, CouchTestCheckpointOptions options, string checkpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        Dictionary<string, Control> roots = await WaitForStableRootsAsync(tree, token);
        token.ThrowIfCancellationRequested();

        Vector2 viewport = tree.Root.GetVisibleRect().Size;
        Dictionary<string, Rect2> modRects = new();
        foreach ((string name, Control control) in roots)
        {
            if (ContentBounds(control) is Rect2 bounds)
            {
                modRects[name] = bounds;
            }
        }

        Dictionary<string, Rect2> anchors = CollectAnchors();

        List<string> failures = new();
        List<string> allowedNotes = new();
        CheckContainment(modRects, viewport, failures);
        CheckOverlaps(modRects, anchors, failures, allowedNotes);
        CheckAnchoring(modRects, failures);

        if (allowedNotes.Count > 0)
        {
            CouchTestLog.Info($"Checkpoint '{checkpoint}' ({options.Aspect}): allow-listed overlaps: {string.Join("; ", allowedNotes)}");
        }

        Dictionary<string, CouchTestRect> snapshotNodes = new();
        foreach ((string name, Rect2 rect) in modRects)
        {
            snapshotNodes[$"mod.{name}"] = CouchTestRect.FromGodot(rect);
        }

        foreach ((string name, Rect2 rect) in anchors)
        {
            snapshotNodes[$"anchor.{name}"] = CouchTestRect.FromGodot(rect);
        }

        string? snapshotFailure = RunSnapshot(options, checkpoint, snapshotNodes);
        if (snapshotFailure != null && options.StrictLayout)
        {
            failures.Add(snapshotFailure);
        }
        else if (snapshotFailure != null)
        {
            // Snapshots are a drift report, not a gate (docs/testing.md): the relational rules above decide pass/fail.
            string note = $"{options.ScenarioName}/{checkpoint}@{options.Aspect}: {snapshotFailure}";
            DriftNotes.Add(note);
            CouchTestLog.Warn($"Layout drift (non-blocking; --strict-layout makes it fail): {note}");
        }

        if (options.Review)
        {
            await TakeReviewScreenshotAsync(options, checkpoint);
        }

        CouchTestLayoutSnapshot snapshot = new(options.ScenarioName, options.Aspect, checkpoint, snapshotNodes);
        CouchTestLog.Info($"Layout checkpoint '{checkpoint}' ({options.Aspect}): {modRects.Count} mod root(s), {anchors.Count} anchor(s), {failures.Count} failure(s).");

        if (failures.Count > 0)
        {
            throw new CouchTestExpectationFailedException(
                $"Layout checkpoint '{checkpoint}' ({options.Aspect}) failed:\n  - " + string.Join("\n  - ", failures));
        }

        return snapshot;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rect gathering
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Waits until every currently-visible mod root's <see cref="ContentBounds"/> stops moving across
    /// <see cref="StableFrameCount"/> consecutive frames (see the class doc's determinism finding), or gives up after
    /// <see cref="StabilizeTimeout"/> and measures whatever is there (logged, never silently swallowed).</summary>
    private static async Task<Dictionary<string, Control>> WaitForStableRootsAsync(SceneTree tree, CancellationToken token)
    {
        Dictionary<string, Control> current = FindVisibleRoots(tree.Root);
        List<Rect2> previous = ContentRectsOf(current);
        int stableFrames = 0;
        DateTime deadline = DateTime.UtcNow + StabilizeTimeout;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await NextFrameAsync(tree, token);
            token.ThrowIfCancellationRequested();

            current = FindVisibleRoots(tree.Root);
            List<Rect2> rects = ContentRectsOf(current);
            bool stable = RectsApproximatelyEqual(previous, rects);
            stableFrames = stable ? stableFrames + 1 : 0;
            previous = rects;

            if (stableFrames >= StableFrameCount)
            {
                return current;
            }

            if (DateTime.UtcNow > deadline)
            {
                CouchTestLog.Warn($"Layout rects did not stabilize within {StabilizeTimeout.TotalSeconds:0}s; measuring anyway.");
                return current;
            }
        }
    }

    private static List<Rect2> ContentRectsOf(Dictionary<string, Control> roots)
    {
        return roots.OrderBy((kv) => kv.Key, StringComparer.Ordinal)
            .Select((kv) => ContentBounds(kv.Value) ?? new Rect2())
            .ToList();
    }

    private static bool RectsApproximatelyEqual(List<Rect2> a, List<Rect2> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Position.DistanceTo(b[i].Position) > 0.1f || a[i].Size.DistanceTo(b[i].Size) > 0.1f)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, Control> FindVisibleRoots(Node searchRoot)
    {
        Dictionary<string, Control> found = new();
        Visit(searchRoot);
        return found;

        void Visit(Node node)
        {
            string name = node.Name.ToString();
            if (node is Control control && !found.ContainsKey(name) && Array.IndexOf(ModRootNames, name) >= 0 && control.IsVisibleInTree())
            {
                found[name] = control;
            }

            foreach (Node child in node.GetChildren())
            {
                Visit(child);
            }
        }
    }

    /// <summary>The visual bounding box of everything drawn under <paramref name="root"/>, excluding the root
    /// Control's own rect (see the class doc's plan-deviation note). Null when nothing visible is drawn.</summary>
    private static Rect2? ContentBounds(Node root)
    {
        Rect2? bounds = null;
        foreach (Node child in root.GetChildren())
        {
            if (VisualBounds(child) is Rect2 childBounds)
            {
                bounds = bounds is Rect2 merged ? merged.Merge(childBounds) : childBounds;
            }
        }

        return bounds;
    }

    private static Rect2? VisualBounds(Node node)
    {
        if (node is CanvasItem { Visible: false })
        {
            return null;
        }

        Rect2? bounds = null;
        if (node is Control { Visible: true } control && control.Size.X > 0.5f && control.Size.Y > 0.5f)
        {
            bounds = control.GetGlobalRect();
        }

        foreach (Node child in node.GetChildren())
        {
            if (VisualBounds(child) is Rect2 childBounds)
            {
                bounds = bounds is Rect2 merged ? merged.Merge(childBounds) : childBounds;
            }
        }

        return bounds;
    }

    /// <summary>P1's named elements (docs/design/testing-plan.md §6.5.1): the relic row, the top bar, the end-turn
    /// button (combat only), plus the players list, which is not one of the named elements but is needed for the
    /// HUD-band anchoring rule and is worth recording in the snapshot too.</summary>
    private static Dictionary<string, Rect2> CollectAnchors()
    {
        Dictionary<string, Rect2> anchors = new();

        // Observed on a real run: NRelicInventory/NTopBar don't shrink-wrap their content — NTopBar's raw
        // GetGlobalRect() came back full-screen. A generic recursive union (as used for the mod's own roots, see the
        // class doc's plan-deviation note) isn't safe here either: NTopBar.TrailContainer parks played-card VFX
        // nodes off-screen while idle, and a naive union picked up a rect at (-328, -430) sized 2586x541 from it. Use
        // the exact sub-controls the mod's own CouchTeammateTopBar.Layout() already treats as "the row" instead.
        NRelicInventory? relicRow = NRun.Instance?.GlobalUi?.RelicInventory;
        if (relicRow != null && GodotObject.IsInstanceValid(relicRow) && relicRow.IsVisibleInTree()
            && UnionOf(relicRow.RelicNodes) is Rect2 relicBounds)
        {
            anchors["P1RelicRow"] = relicBounds;
        }

        NTopBar? topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar != null && GodotObject.IsInstanceValid(topBar) && topBar.IsVisibleInTree()
            && UnionOf(new Control[] { topBar.RoomIcon, topBar.FloorIcon, topBar.BossIcon, topBar.Portrait, topBar.Hp, topBar.Gold, topBar.PotionContainer, topBar.Timer, topBar.Map, topBar.Deck, topBar.Pause }) is Rect2 topBarBounds)
        {
            anchors["P1TopBar"] = topBarBounds;
        }

        // NEndTurnButton is a scene-authored, real interactive Control (unlike the mod's own bare Controls), so its
        // own Size is meaningful.
        Control? endTurnButton = NCombatRoom.Instance?.Ui?.EndTurnButton;
        if (endTurnButton != null && GodotObject.IsInstanceValid(endTurnButton) && endTurnButton.IsVisibleInTree())
        {
            anchors["P1EndTurnButton"] = endTurnButton.GetGlobalRect();
        }

        if (CouchLayout.PlayersList() is Rect2 playersList)
        {
            anchors["PlayersList"] = playersList;
        }

        return anchors;
    }

    private static Rect2? UnionOf(IEnumerable<Control> controls)
    {
        Rect2? bounds = null;
        foreach (Control control in controls)
        {
            if (!GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree() || control.Size.X <= 0.5f || control.Size.Y <= 0.5f)
            {
                continue;
            }

            Rect2 rect = control.GetGlobalRect();
            bounds = bounds is Rect2 merged ? merged.Merge(rect) : rect;
        }

        return bounds;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rules (docs/design/testing-plan.md §6.5.1)
    // ---------------------------------------------------------------------------------------------------------

    private static void CheckContainment(Dictionary<string, Rect2> modRects, Vector2 viewport, List<string> failures)
    {
        foreach ((string name, Rect2 rect) in modRects)
        {
            if (rect.Size.X <= 0.5f || rect.Size.Y <= 0.5f)
            {
                failures.Add($"{name}: zero-size rect {FormatRect(rect)}.");
                continue;
            }

            if (rect.Position.X < -ViewportTolerancePx || rect.Position.Y < -ViewportTolerancePx
                || rect.End.X > viewport.X + ViewportTolerancePx || rect.End.Y > viewport.Y + ViewportTolerancePx)
            {
                failures.Add($"{name}: outside the viewport, rect {FormatRect(rect)}, viewport {viewport.X:0}x{viewport.Y:0}.");
            }
        }
    }

    private static void CheckOverlaps(Dictionary<string, Rect2> modRects, Dictionary<string, Rect2> anchors, List<string> failures, List<string> allowedNotes)
    {
        foreach ((string modName, Rect2 modRect) in modRects)
        {
            foreach ((string anchorName, Rect2 anchorRect) in anchors)
            {
                // PlayersList is recorded for the anchoring rule and the snapshot, but it isn't one of the "named P1
                // elements" (relic row/top bar/end-turn button) the overlap rule calls out.
                if (anchorName == "PlayersList" || !modRect.Intersects(anchorRect))
                {
                    continue;
                }

                if (IsAllowedOverlap(modName, anchorName, allowedNotes))
                {
                    continue;
                }

                failures.Add($"{modName} overlaps {anchorName}: {FormatRect(modRect)} x {FormatRect(anchorRect)}.");
            }
        }

        List<string> names = modRects.Keys.ToList();
        for (int i = 0; i < names.Count; i++)
        {
            for (int j = i + 1; j < names.Count; j++)
            {
                Rect2 a = modRects[names[i]];
                Rect2 b = modRects[names[j]];
                if (!a.Intersects(b))
                {
                    continue;
                }

                if (IsAllowedOverlap(names[i], names[j], allowedNotes))
                {
                    continue;
                }

                failures.Add($"{names[i]} overlaps {names[j]}: {FormatRect(a)} x {FormatRect(b)}.");
            }
        }
    }

    private static bool IsAllowedOverlap(string a, string b, List<string> allowedNotes)
    {
        foreach ((string x, string y, string reason) in AllowedOverlaps)
        {
            if ((x == a && y == b) || (x == b && y == a))
            {
                allowedNotes.Add($"{a} x {b} ({reason})");
                return true;
            }
        }

        return false;
    }

    private static void CheckAnchoring(Dictionary<string, Rect2> modRects, List<string> failures)
    {
        // P2's combat relic row is vertically centered on the teammate HUD's status line (regression for commit
        // 4c19bc4; see CouchTeammateRelicBar.Layout()'s "Centered on the HUD's status line" branch).
        if (modRects.TryGetValue("CouchTeammateRelicBar", out Rect2 relicBar) && CouchTeammateHud.HeaderMiddle is float headerMiddle)
        {
            float relicCenterY = relicBar.Position.Y + relicBar.Size.Y * 0.5f;
            float delta = Mathf.Abs(relicCenterY - headerMiddle);
            if (delta > AnchorTolerancePx)
            {
                failures.Add(
                    $"CouchTeammateRelicBar is not centered on the HUD status line: relic bar center y={relicCenterY:0.#}, " +
                    $"HeaderMiddle={headerMiddle:0.#}, delta={delta:0.#}px (limit {AnchorTolerancePx:0}px).");
            }
        }

        // The HUD band (its header line) must not start above the players list (CouchTeammateHud.BandTop() levels
        // the HUD's top with the top of the players list — docs/design/testing-plan.md §6.5.1 "the HUD band starts
        // below the players list"). This uses CouchTeammateHud.HeaderTop, not the HUD root's measured bounding box:
        // that box also spans the separately-laid-out hand of cards, which can extend above the header line when a
        // card is focused/enlarged, and would make this a false positive on every combat checkpoint (observed on a
        // real run). Only CouchTeammateHud: CouchTeammateTopBar is deliberately placed inside the driver's own top
        // bar, above the players list, and would otherwise be a guaranteed false positive here too.
        if (modRects.ContainsKey("CouchTeammateHud") && CouchTeammateHud.HeaderTop is float headerTop && CouchLayout.PlayersList() is Rect2 playersList)
        {
            float delta = playersList.Position.Y - headerTop;
            if (delta > AnchorTolerancePx)
            {
                failures.Add(
                    $"CouchTeammateHud's header starts above the players list: header top y={headerTop:0.#}, " +
                    $"players list top y={playersList.Position.Y:0.#}, delta={delta:0.#}px (limit {AnchorTolerancePx:0}px).");
            }
        }
    }

    private static string FormatRect(Rect2 rect) => $"({rect.Position.X:0}, {rect.Position.Y:0}, {rect.Size.X:0}x{rect.Size.Y:0})";

    // ---------------------------------------------------------------------------------------------------------
    // Snapshots: compare against the committed baseline, or write it under --bless (docs/design/testing-plan.md §6.5.2)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Always writes this run's snapshot to <c>&lt;out&gt;/layout/</c>. Under <c>--bless</c>, also (over)writes
    /// the committed baseline and returns null. Otherwise compares against the committed baseline and returns a
    /// failure message (also written to a diff file) when they differ, or when no baseline exists yet.</summary>
    private static string? RunSnapshot(CouchTestCheckpointOptions options, string checkpoint, Dictionary<string, CouchTestRect> current)
    {
        string runPath = SnapshotPath(Path.Combine(options.OutDir, "layout"), options.ScenarioName, checkpoint, options.Aspect);
        WriteSnapshot(runPath, current);

        string baselinePath = SnapshotPath(options.BaselinesDir, options.ScenarioName, checkpoint, options.Aspect);
        if (options.Bless)
        {
            WriteSnapshot(baselinePath, current);
            CouchTestLog.Info($"Blessed layout baseline: {baselinePath}");
            return null;
        }

        if (!File.Exists(baselinePath))
        {
            return $"no baseline; run with --bless ({baselinePath}).";
        }

        Dictionary<string, CouchTestRect> baseline = ReadSnapshot(baselinePath);
        List<string> diffs = new();
        foreach (string key in baseline.Keys.Where((k) => !current.ContainsKey(k)).OrderBy((k) => k, StringComparer.Ordinal))
        {
            diffs.Add($"{key}: disappeared (baseline had {baseline[key]}).");
        }

        foreach (string key in current.Keys.Where((k) => !baseline.ContainsKey(k)).OrderBy((k) => k, StringComparer.Ordinal))
        {
            diffs.Add($"{key}: appeared (now {current[key]}, no baseline entry).");
        }

        foreach (string key in current.Keys.Where(baseline.ContainsKey).OrderBy((k) => k, StringComparer.Ordinal))
        {
            CouchTestRect baselineRect = baseline[key];
            CouchTestRect currentRect = current[key];
            int delta = baselineRect.MaxDelta(currentRect);
            if (delta > SnapshotTolerancePx)
            {
                diffs.Add($"{key}: moved/resized by {delta}px (baseline {baselineRect}, now {currentRect}).");
            }
        }

        if (diffs.Count == 0)
        {
            return null;
        }

        string diffPath = SnapshotPath(Path.Combine(options.OutDir, "layout"), options.ScenarioName, checkpoint, options.Aspect, extension: "diff.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
        File.WriteAllText(diffPath, string.Join("\n", diffs) + "\n");
        return $"layout snapshot differs from baseline ({diffPath}):\n    " + string.Join("\n    ", diffs);
    }

    private static string SnapshotPath(string baseDir, string scenario, string checkpoint, string aspect, string extension = "json")
    {
        // "16:9" -> "16x9": ':' is not allowed in Windows file names, and the baselines are committed.
        return Path.Combine(baseDir, scenario, $"{checkpoint}@{aspect.Replace(':', 'x')}.{extension}");
    }

    private static void WriteSnapshot(string path, Dictionary<string, CouchTestRect> nodes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        List<string> keys = nodes.Keys.OrderBy((k) => k, StringComparer.Ordinal).ToList();
        StringBuilder json = new();
        json.Append("{\n  \"nodes\": {\n");
        for (int i = 0; i < keys.Count; i++)
        {
            CouchTestRect rect = nodes[keys[i]];
            json.Append($"    \"{JsonEscape(keys[i])}\": {{ \"x\": {rect.X}, \"y\": {rect.Y}, \"w\": {rect.W}, \"h\": {rect.H} }}");
            json.Append(i == keys.Count - 1 ? "\n" : ",\n");
        }

        json.Append("  }\n}\n");
        File.WriteAllText(path, json.ToString());
    }

    /// <summary>A minimal hand-rolled reader for the fixed shape <see cref="WriteSnapshot"/> writes — this file
    /// controls both sides of the format, so a full JSON parser isn't needed (docs/design/testing-plan.md §5's Layer A
    /// project makes the same "hand-rolled, no reflection" call for the same reason).</summary>
    private static Dictionary<string, CouchTestRect> ReadSnapshot(string path)
    {
        string text = File.ReadAllText(path);
        Dictionary<string, CouchTestRect> nodes = new();
        foreach (Match match in SnapshotEntryPattern.Matches(text))
        {
            nodes[match.Groups["name"].Value] = new CouchTestRect(
                int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture));
        }

        return nodes;
    }

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------------------------------------------------------------------------------------------------------
    // --repeat: cross-run comparison (docs/design/testing-plan.md §6.2, §6.6 rule 8)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Resets the in-process snapshot list a fresh <c>--couch-test</c> invocation starts with. Not currently
    /// needed (the runner process exits after one invocation) but keeps repeated calls from a single process safe.</summary>
    public static void ResetReview() => ReviewShots.Clear();

    /// <summary>Compares one repeat pass's layout snapshots against the first pass's, in checkpoint order. Returns the
    /// first mismatch found (scenario, aspect, checkpoint, and node), or null when they match exactly.</summary>
    public static string? CompareLayoutForRepeat(IReadOnlyList<CouchTestLayoutSnapshot> first, IReadOnlyList<CouchTestLayoutSnapshot> repeatPass, int repeatNumber)
    {
        if (first.Count != repeatPass.Count)
        {
            return $"repeat {repeatNumber}: checkpoint count differs (first run={first.Count}, this run={repeatPass.Count}).";
        }

        for (int i = 0; i < first.Count; i++)
        {
            CouchTestLayoutSnapshot a = first[i];
            CouchTestLayoutSnapshot b = repeatPass[i];
            if (a.ScenarioName != b.ScenarioName || a.Aspect != b.Aspect || a.Checkpoint != b.Checkpoint)
            {
                return $"repeat {repeatNumber}: checkpoint order differs at index {i} (first run={a.ScenarioName}/{a.Checkpoint}@{a.Aspect}, this run={b.ScenarioName}/{b.Checkpoint}@{b.Aspect}).";
            }

            foreach (string key in a.Nodes.Keys.Union(b.Nodes.Keys).OrderBy((k) => k, StringComparer.Ordinal))
            {
                bool inA = a.Nodes.TryGetValue(key, out CouchTestRect rectA);
                bool inB = b.Nodes.TryGetValue(key, out CouchTestRect rectB);
                if (inA != inB)
                {
                    return $"repeat {repeatNumber}: {a.ScenarioName}/{a.Checkpoint}@{a.Aspect}: {key} {(inB ? "appeared" : "disappeared")} vs. the first run.";
                }

                if (inA && !rectA.Equals(rectB))
                {
                    return $"repeat {repeatNumber}: {a.ScenarioName}/{a.Checkpoint}@{a.Aspect}: {key} differs (first run={rectA}, this run={rectB}).";
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // --review: checkpoint screenshots and the contact sheet (docs/design/testing-plan.md §6.5, §6.7). Never affects
    // the verdict: failures here are logged and swallowed, not thrown.
    // ---------------------------------------------------------------------------------------------------------

    private static async Task TakeReviewScreenshotAsync(CouchTestCheckpointOptions options, string checkpoint)
    {
        string path = SnapshotPath(Path.Combine(options.OutDir, "layout"), options.ScenarioName, checkpoint, options.Aspect, extension: "png");
        try
        {
            await CouchScreenshots.TakeToAsync(path);
            ReviewShots.Add(($"{options.ScenarioName}/{checkpoint}@{options.Aspect}", path));
        }
        catch (Exception ex)
        {
            CouchTestLog.Warn($"Could not save a review screenshot for {options.ScenarioName}/{checkpoint}@{options.Aspect}: {ex.Message}");
        }
    }

    /// <summary>Builds <c>&lt;out&gt;/contact-sheet.png</c> from every checkpoint screenshot taken this run (all
    /// scenarios, aspects and repeat passes), via an offscreen <see cref="SubViewport"/> so the labels are real
    /// rendered text rather than pixels baked in by hand.</summary>
    public static async Task BuildContactSheetAsync(SceneTree tree, string outDir)
    {
        if (ReviewShots.Count == 0)
        {
            return;
        }

        const int thumbWidth = 320;
        const int thumbHeight = 180;
        const int padding = 10;
        const int labelHeight = 22;
        int columns = Math.Max(1, Mathf.CeilToInt(Mathf.Sqrt(ReviewShots.Count)));
        int rows = Mathf.CeilToInt((float)ReviewShots.Count / columns);
        int cellWidth = thumbWidth + padding;
        int cellHeight = thumbHeight + labelHeight + padding;
        int sheetWidth = columns * cellWidth + padding;
        int sheetHeight = rows * cellHeight + padding;

        SubViewport viewport = new()
        {
            Size = new Vector2I(sheetWidth, sheetHeight),
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        viewport.AddChild(new ColorRect { Size = new Vector2(sheetWidth, sheetHeight), Color = new Color(0.05f, 0.06f, 0.08f) });

        for (int i = 0; i < ReviewShots.Count; i++)
        {
            (string label, string path) = ReviewShots[i];
            int column = i % columns;
            int row = i / columns;
            float x = padding + column * cellWidth;
            float y = padding + row * cellHeight;

            Image? image = Image.LoadFromFile(path);
            if (image != null)
            {
                image.Resize(thumbWidth, thumbHeight, Image.Interpolation.Bilinear);
                ImageTexture texture = ImageTexture.CreateFromImage(image);
                viewport.AddChild(new TextureRect { Texture = texture, Position = new Vector2(x, y), Size = new Vector2(thumbWidth, thumbHeight) });
            }

            Label caption = new()
            {
                Text = label,
                Position = new Vector2(x, y + thumbHeight + 2f),
                Size = new Vector2(thumbWidth, labelHeight),
                ClipText = true
            };
            caption.AddThemeColorOverride("font_color", Colors.White);
            caption.AddThemeFontSizeOverride("font_size", 13);
            viewport.AddChild(caption);
        }

        tree.Root.AddChild(viewport);
        try
        {
            // A couple of frames so the SubViewport actually renders before its texture is read back.
            for (int i = 0; i < 3; i++)
            {
                await NextFrameAsync(tree, CancellationToken.None);
            }

            Image sheet = viewport.GetTexture().GetImage();
            string sheetPath = Path.Combine(outDir, "contact-sheet.png");
            Error error = sheet.SavePng(sheetPath);
            CouchTestLog.Info(error == Error.Ok
                ? $"Contact sheet saved: {sheetPath} ({ReviewShots.Count} checkpoint shot(s))."
                : $"Contact sheet failed ({error}): {sheetPath}");
        }
        catch (Exception ex)
        {
            CouchTestLog.Warn($"Could not build the contact sheet: {ex.Message}");
        }
        finally
        {
            viewport.QueueFree();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    private static Task NextFrameAsync(SceneTree tree, CancellationToken token)
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        void OnFrame()
        {
            tree.ProcessFrame -= OnFrame;
            registration.Dispose();
            tcs.TrySetResult(true);
        }

        tree.ProcessFrame += OnFrame;
        registration = token.Register(() =>
        {
            tree.ProcessFrame -= OnFrame;
            tcs.TrySetCanceled(token);
        });

        return tcs.Task;
    }
}
#endif
