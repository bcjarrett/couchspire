#if COUCHSPIRE_TESTS
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CouchSpire.Scripts.Testing;

/// <summary>One aspect ratio pass a scenario can run at (see docs/testing.md).</summary>
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

/// <summary>A rect rounded to whole pixels (docs/testing.md), as written to a snapshot/baseline file.</summary>
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
/// byte-for-byte without racing its own baseline/snapshot files on disk (docs/testing.md).</summary>
internal sealed record CouchTestLayoutSnapshot(
    string ScenarioName,
    string Aspect,
    string Checkpoint,
    IReadOnlyDictionary<string, CouchTestRect> Nodes);

/// <summary>
/// Layout checks run at every <see cref="CouchTestContext.Checkpoint"/> (see docs/testing.md).
///
/// <para>
/// <b>Plan deviation, found while implementing this (report this back — docs/testing.md says to use
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
/// <b>Determinism finding:</b> <see cref="CouchPanel"/> slides and fades in over ~0.3s
/// (<c>_appearTween</c>, a <c>Back</c>-eased tween) whenever it becomes visible, and that isn't tracked by
/// <see cref="CouchTestContext.Settle"/> (panel visibility never touches <c>NOverlayStack</c>, which is all Settle
/// watches). Measuring rects right after a panel opens would be flaky. <see cref="WaitForStableRootsAsync"/> polls
/// every visible mod root's content bounds frame-by-frame and waits for them to stop moving (3 consecutive frames,
/// capped at 2s) before anything below measures a single rect.
/// </para>
/// </summary>
internal static class CouchTestLayout
{
    /// <summary>Stable UI root names that are Controls with a rect (docs/testing.md). Excluded, and
    /// why: <c>CouchTeammateDeckChanges</c> is a plain <c>Node</c> (a transient VFX host, not a layout element);
    /// <c>CouchDebugOverlay</c> is a <c>CanvasLayer</c> (no rect) and forced off in tests (COUCHSPIRE_OVERLAY=0);
    /// <c>CouchInputGate</c> is a plain <c>Node</c> coordinator with no visual footprint;
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

    /// <summary>Overlaps that are allowed by design, each with why (docs/testing.md: "some
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

    /// <summary>The shared right-hand P2 column panels (docs/testing.md): checked by
    /// <see cref="CheckColumnPanelCursorAndHint"/> for the cursor-row/hint-in-view rule. Not
    /// <c>CouchTeammateHud</c>/<c>RelicBar</c>/<c>TopBar</c>/<c>Info</c>/<c>Event</c>, which don't use the shared
    /// column or its row-cursor/hint layout.</summary>
    private static readonly string[] ColumnPanelRootNames =
    {
        "CouchTeammateRewards", "CouchTeammateRestSite", "CouchTeammateShop", "CouchTeammateTreasure", "CouchTeammateChoicePanel"
    };

    private static readonly PropertyInfo? PanelHintProperty = AccessTools.Property(typeof(CouchPanel), "Hint");
    private static readonly PropertyInfo? PanelBackgroundProperty = AccessTools.Property(typeof(CouchPanel), "Background");

    // CouchTeammateChoicePanel's preview card(s) - checked against the panel frame too (senior review round 2: the
    // side-by-side Smith preview overflowed the panel's own left edge without tripping any existing rule, since it
    // never left the viewport, just the panel it's drawn inside of).
    private static readonly FieldInfo? ChoicePanelBeforeField = AccessTools.Field(typeof(CouchTeammateChoicePanel), "_before");
    private static readonly FieldInfo? ChoicePanelAfterField = AccessTools.Field(typeof(CouchTeammateChoicePanel), "_after");
    private static readonly FieldInfo? ChoicePanelFocusedField = AccessTools.Field(typeof(CouchTeammateChoicePanel), "_focusedPreview");

    /// <summary>
    /// Bound for <see cref="WaitForStableRootsAsync"/> (used by <see cref="RunCheckpointAsync"/>) and for the
    /// screen-stability check <see cref="CouchTestContext.Settle"/> folds in. Generous on purpose: the combat entry
    /// banners chain <see cref="NCombatStartBanner"/> (~2.3s reveal/hold + a 2.5s delayed fade-out tail) into
    /// <see cref="NPlayerTurnBanner"/> (~2.2s), and neither is shortened by the test harness's FastMode.Instant
    /// (docs/testing.md; confirmed against the decompiled source — <c>NCombatStartBanner.AnimateVfx</c> and
    /// <c>NPlayerTurnBanner.Display</c> use fixed tween durations regardless of FastMode). A signal that's still
    /// stuck after this must fail loudly, not be silently measured through (see <see cref="FirstUnsettledScreenSignal"/>).
    /// </summary>
    private static readonly TimeSpan ScreenStableTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex SnapshotEntryPattern = new(
        "\"(?<name>[^\"]+)\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(?<x>-?\\d+)\\s*,\\s*\"y\"\\s*:\\s*(?<y>-?\\d+)\\s*,\\s*\"w\"\\s*:\\s*(?<w>-?\\d+)\\s*,\\s*\"h\"\\s*:\\s*(?<h>-?\\d+)\\s*\\}",
        RegexOptions.Compiled);

    private static readonly List<(string Label, string Path)> ReviewShots = new();

    /// <summary>Snapshot mismatches this run that didn't fail it (no <c>--strict-layout</c>); listed in summary.txt.</summary>
    public static readonly List<string> DriftNotes = new();

    public static readonly IReadOnlyList<string> KnownAspects = new[] { "16:9", "16:10" };

    // ---------------------------------------------------------------------------------------------------------
    // Aspect passes (docs/testing.md)
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

    /// <summary>Pins the window/aspect the game's own way (see docs/testing.md) and waits for the viewport to
    /// actually settle to the aspect's content-scale size before returning.</summary>
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

    /// <summary>Runs every layout rule and the snapshot compare/bless for one checkpoint (see docs/testing.md).
    /// Throws <see cref="CouchTestExpectationFailedException"/> naming every failing node and its numbers
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
        CheckColumnPanelCursorAndHint(roots, viewport, failures);

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

    /// <summary>
    /// Waits until the screen is actually done transitioning before anything measures or screenshots it: no
    /// screen fade in progress, no combat turn banner or the mod's own "Controlled Character" notice still on
    /// screen (<see cref="FirstUnsettledScreenSignal"/>), and every currently-visible mod root's
    /// <see cref="ContentBounds"/> plus every named anchor from <see cref="CollectAnchors"/> (e.g. a combat room's
    /// own P1EndTurnButton, which can still be sliding into place from its own entry animation independent of any
    /// mod panel) stopped moving across <see cref="StableFrameCount"/> consecutive frames (see the class doc's
    /// determinism finding — this is how a running tween on a mod panel or a game anchor is detected, without
    /// enumerating <c>Tween</c> objects). Bounded by <see cref="ScreenStableTimeout"/>; on timeout this throws
    /// naming whichever signal is still stuck, rather than silently measuring a mid-transition screen (regression
    /// for the 2026-09-28 --review contact sheet: banners, the mod's own notice and screen fades were caught
    /// mid-transition in checkpoint screenshots, and that also explains the layout jitter between runs — the old
    /// 2s stabilize timeout gave up and measured anyway while a tween's Back/Expo-eased tail was still moving a few
    /// px). Confirmed on a run: without also tracking anchors here, a --repeat pass could measure P1EndTurnButton
    /// mid-slide and report a spurious snapshot mismatch against a pass that measured it after settling.
    /// </summary>
    private static async Task<Dictionary<string, Control>> WaitForStableRootsAsync(SceneTree tree, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + ScreenStableTimeout;
        Dictionary<string, Control> current = FindVisibleRoots(tree.Root);
        List<Rect2> previous = ContentRectsOf(current);
        List<Rect2> previousAnchors = CollectAnchors().Values.ToList();
        int stableFrames = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await NextFrameAsync(tree, token);
            token.ThrowIfCancellationRequested();

            string? signal = FirstUnsettledScreenSignal(tree);
            if (signal != null)
            {
                stableFrames = 0;
                if (DateTime.UtcNow > deadline)
                {
                    throw new CouchTestExpectationFailedException(
                        $"Screen did not stabilize within {ScreenStableTimeout.TotalSeconds:0}s before a layout checkpoint: stuck on {signal}.");
                }

                // Keep the rect baselines current while we wait, so a banner/fade clearing doesn't itself get
                // mistaken for "rects moved" on the very next frame.
                previous = ContentRectsOf(FindVisibleRoots(tree.Root));
                previousAnchors = CollectAnchors().Values.ToList();
                continue;
            }

            current = FindVisibleRoots(tree.Root);
            List<Rect2> rects = ContentRectsOf(current);
            List<Rect2> anchors = CollectAnchors().Values.ToList();
            bool stable = RectsApproximatelyEqual(previous, rects) && RectsApproximatelyEqual(previousAnchors, anchors);
            stableFrames = stable ? stableFrames + 1 : 0;
            previous = rects;
            previousAnchors = anchors;

            if (stableFrames >= StableFrameCount)
            {
                return current;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new CouchTestExpectationFailedException(
                    $"Layout rects did not stabilize within {ScreenStableTimeout.TotalSeconds:0}s before a layout checkpoint " +
                    "(a mod panel or game anchor tween never settled).");
            }
        }
    }

    /// <summary>
    /// Returns the name of the first screen-transition signal that hasn't cleared yet, or null once the screen is
    /// genuinely settled. All of these are self-clearing within a bounded time on their own (a fixed-duration
    /// tween or Task.Delay that frees the node when done) — none of them wait on a player action — so this is safe
    /// for <see cref="CouchTestContext.Settle"/> to gate scenario steps on too, not just checkpoints: unlike a
    /// pending teammate choice, waiting here can never deadlock on something that's legitimately supposed to stay
    /// up. Checked:
    /// <list type="bullet">
    /// <item>the screen fade/transition (<see cref="NTransition.InTransition"/>, set by <c>NTransition</c>'s
    /// <c>FadeIn</c>/<c>FadeOut</c>/<c>RoomFadeIn</c>/<c>RoomFadeOut</c> — confirmed against the decompiled source
    /// that <c>RoomFadeIn</c> always runs its ~0.8s tween before clearing <c>InTransition</c>, even under the test
    /// harness's FastMode.Instant);</item>
    /// <item>the combat turn banners (<see cref="NCombatStartBanner"/> "Battle Start", <see cref="NPlayerTurnBanner"/>
    /// "Player Turn", <see cref="NEnemyTurnBanner"/> "Enemy Turn") and the other full-screen story banners
    /// (<see cref="NActBanner"/> — its own darkening overlay is why a map screen briefly looks half-faded when a
    /// new act starts; <see cref="NAncientNameBanner"/>) — each is a transient scene instance that QueueFrees
    /// itself once its own animation finishes (confirmed in the decompiled source: none of their durations are
    /// shortened by FastMode.Instant either), so its mere presence in the tree means it's still showing;</item>
    /// <item>the mod's own "Controlled Character: Player N" notice (<see cref="NFullscreenTextVfx"/>, created by
    /// <c>LocalControlRuntime</c> from <see cref="CouchSpire.Scripts.Runtime.LocalModText.ControlledSlot"/>)
    /// — same self-freeing shape (a 500ms <c>Task.Delay</c> then <c>QueueFreeSafely</c>);</item>
    /// <item>two game tweens confirmed against a real run to leave the screen visibly mid-fade well after
    /// <see cref="NOverlayStack"/>'s own top/count already read as settled, which is exactly the kind of thing the
    /// generic rect-stability wait can't see (neither one moves a mod-panel or anchor rect): <see cref="NMapScreen"/>'s
    /// own open/close reveal tween (its private <c>_tween</c> field — <c>_backstop</c>/<c>_mapContainer</c> fading
    /// between transparent-black and full white over ~0.25s — explains every "map screen half-faded" shot, since
    /// every "map-open" checkpoint and the <c>layout</c> scenario's map checkpoint land on this same screen), and
    /// <see cref="NOverlayStack"/>'s own shared backstop fade (its private <c>_backstopFade</c> field — a 0.5s
    /// fade-in/out that <c>Pop</c> starts but doesn't wait for, so the stack's top/count can already read "closed"
    /// while the dim behind it is still fading out). Both are private, so read through <c>AccessTools</c> the same
    /// way <c>CouchTestContext.&lt;Area&gt;.cs</c> partials already cross that boundary.</item>
    /// </list>
    /// </summary>
    internal static string? FirstUnsettledScreenSignal(SceneTree tree)
    {
        if (NGame.Instance?.Transition is NTransition transition && transition.InTransition)
        {
            return "screen fade (NTransition.InTransition)";
        }

        // Gated on actually being on the map room (not just NMapScreen.Instance existing, which is a persistent
        // singleton for the whole run, never recreated per room): confirmed on two separate runs that the test
        // harness's debug "room <type>" console command (CouchTestContext.EnterRoom, used to set up almost every
        // non-map scenario) jumps straight to the target room without going through NMapScreen.Close(). That
        // leaves NMapScreen itself - and anything still animating under it, e.g. a just-started NActBanner - paused
        // mid-flight (Godot pauses a tween, and any coroutine awaiting one, when its bound node's process state
        // changes) rather than finished, killed or freed. Nothing ever un-pauses it again, so both NMapScreen's own
        // _tween (first regression, 'choice' scenario) and NActBanner sitting frozen as its child (second
        // regression, same scenario) read as "still showing" forever. Real map-room play (P1 opening/leaving the
        // map through the UI, or a real act transition) always drives these through a normal, bounded finish, so
        // gating on CurrentRoom == Map costs nothing there while no longer tripping on a paused singleton's leftover
        // state once play has moved to a different room.
        RunState? currentRunState = RunManager.Instance.DebugOnlyGetState();
        bool onMapRoom = currentRunState?.CurrentRoom?.RoomType == RoomType.Map;
        if (onMapRoom && IsTweenRunning(NMapScreen.Instance, MapScreenTweenField))
        {
            return "the map screen's own open/close reveal tween (NMapScreen)";
        }

        if (IsTweenRunning(NOverlayStack.Instance, OverlayBackstopFadeField))
        {
            return "the overlay backstop fade (NOverlayStack's shared dim behind a screen that just opened or closed)";
        }

        return FindTransientVfxNode(tree.Root, onMapRoom);
    }

    // AccessTools.Field returns null (never throws) when a target is missing, so caching these in static readonly
    // fields is safe; literal type+name here are what Layer A (Tests/CouchSpire.Tests) checks offline against the
    // installed sts2.dll.
    private static readonly FieldInfo? MapScreenTweenField = AccessTools.Field(typeof(NMapScreen), "_tween");
    private static readonly FieldInfo? OverlayBackstopFadeField = AccessTools.Field(typeof(NOverlayStack), "_backstopFade");

    private static bool IsTweenRunning(GodotObject? owner, FieldInfo? tweenField)
    {
        if (owner == null || tweenField == null)
        {
            return false;
        }

        return tweenField.GetValue(owner) is Tween tween && GodotObject.IsInstanceValid(tween) && tween.IsRunning();
    }

    /// <param name="node">Subtree root to search.</param>
    /// <param name="onMapRoom">See <see cref="FirstUnsettledScreenSignal"/>: when false, <see cref="NMapScreen"/>'s
    /// own subtree is skipped rather than descended into. <see cref="NActBanner"/> is added as NMapScreen's child,
    /// so without this it would read as "still showing" forever once a debug room jump leaves NMapScreen paused
    /// with the banner frozen inside it (confirmed on a run) - the same hazard <see cref="FirstUnsettledScreenSignal"/>
    /// already gates its direct NMapScreen tween check on.</param>
    private static string? FindTransientVfxNode(Node node, bool onMapRoom)
    {
        if (!onMapRoom && ReferenceEquals(node, NMapScreen.Instance))
        {
            return null;
        }

        switch (node)
        {
            case NCombatStartBanner:
                return "the combat start banner (\"Battle Start\")";
            case NPlayerTurnBanner:
                return "the player turn banner (\"Player Turn\")";
            case NEnemyTurnBanner:
                return "the enemy turn banner (\"Enemy Turn\")";
            case NActBanner:
                return "the act banner (its own darkening overlay is why a map screen can look half-faded)";
            case NAncientNameBanner:
                return "the Ancient name banner";
            case NFullscreenTextVfx:
                return "the mod's \"Controlled Character\" notice (NFullscreenTextVfx)";
        }

        foreach (Node child in node.GetChildren())
        {
            if (FindTransientVfxNode(child, onMapRoom) is string found)
            {
                return found;
            }
        }

        return null;
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

        // NCard is a special case, confirmed against a real run (rest_site's Smith upgrade choice, by
        // pixel-scanning a --review screenshot and temporary per-child diagnostic logging, since removed): neither
        // NCard's own Control.Size nor its children's is the card's visual footprint (both are a much larger
        // authoring canvas, room for glow/VFX bleed) — recursing into them, as the generic case below does, let a
        // child's raw Size dominate the union regardless of this fix. NCard.GetCurrentSize() ("takes scale into
        // account") documents the true footprint as NCard.defaultSize * Scale, so measure exactly that (through the
        // node's global transform, which folds in Scale and any ancestor transform) and stop there. Before this,
        // CouchTeammateChoicePanel's cards measured at roughly 2.4x their actual on-screen width and in the wrong
        // place, which made a windowed row of Smith-choice cards look like it overflowed the viewport and
        // overlapped CouchTeammateRelicBar when neither ever happens on screen.
        if (node is NCard card)
        {
            return NCardGlobalBounds(card);
        }

        Rect2? bounds = null;
        if (node is Control { Visible: true } control && control.Size.X > 0.5f && control.Size.Y > 0.5f)
        {
            // Control.GetGlobalRect() returns the node's unscaled logical Size, not its actual drawn footprint.
            // CouchCards.Glide shrinks teammate-panel cards via node.Scale (e.g. CouchTeammateChoicePanel's windowed
            // options), so a plain GetGlobalRect() here overstates their real on-screen bounds and can report a
            // panel as off-screen or overlapping a neighbor it never actually touches. Transform the local rect by
            // the node's global transform (position + rotation + scale) instead, matching what's actually drawn.
            bounds = control.GetGlobalTransform() * new Rect2(Vector2.Zero, control.Size);
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

    /// <summary>P1's named elements (docs/testing.md): the relic row, the top bar, the end-turn
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

        // A visible P2 mod panel must not cover any of P1's real action buttons, wherever the current screen keeps
        // them: Proceed (rest site, treasure and shop are all IRoomWithProceedButton; the rewards screen keeps its
        // own as a private field), or a card-select overlay's confirm/cancel buttons (e.g. the Smith upgrade grid's
        // ✓/back). Both come from the same mod-side helper the runtime column-floor measurement uses
        // (CouchColumnFloor.FindActionButtons), so the test can never check a different button set than the one the
        // column actually avoided. Folded into the generic named-anchor overlap check below like the relic row/top
        // bar/end-turn button, rather than a bespoke rule, so every mod panel is checked against it for free.
        if (CouchColumnFloor.FindProceedButton() is Control proceedButton && GodotObject.IsInstanceValid(proceedButton)
            && proceedButton.IsVisibleInTree() && proceedButton.Size.X > 0.5f && proceedButton.Size.Y > 0.5f)
        {
            anchors["P1ProceedButton"] = proceedButton.GetGlobalRect();
        }

        int buttonIndex = 0;
        foreach (Control button in CouchColumnFloor.FindCardSelectButtons())
        {
            if (!GodotObject.IsInstanceValid(button) || !button.IsVisibleInTree() || button.Size.X <= 0.5f || button.Size.Y <= 0.5f)
            {
                continue;
            }

            anchors[$"P1CardSelectButton{buttonIndex++}"] = button.GetGlobalRect();
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
    // Rules (docs/testing.md)
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
        // the HUD's top with the top of the players list — docs/testing.md "the HUD band starts
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

    /// <summary>
    /// New rule (approved spec): while a P2 column panel is visible, the row under its cursor and its hint line must
    /// lie inside the viewport <i>and</i> inside the panel's own drawn frame (<c>CouchPanel.Background</c>, sized by
    /// <c>FinishLayout</c>) — not just somewhere inside the aggregate bounding box every mod root is already checked
    /// against (<see cref="CheckContainment"/>/<see cref="CheckOverlaps"/>), which can't catch a row or hint that
    /// overflows past the frame's own bottom edge, since a bounding box always contains everything that was unioned
    /// into it. <c>CouchButton.IsFocused</c>/<c>CouchPanel.Hint</c> are read via <c>AccessTools</c>/the field itself
    /// (Hint is a protected property; <c>PanelHintProperty</c> crosses that boundary the same way
    /// <c>CouchTestContext.Rewards.cs</c> already does for other mod internals).
    /// </summary>
    private static void CheckColumnPanelCursorAndHint(Dictionary<string, Control> roots, Vector2 viewport, List<string> failures)
    {
        foreach (string name in ColumnPanelRootNames)
        {
            if (!roots.TryGetValue(name, out Control? root))
            {
                continue;
            }

            Rect2? frame = PanelBackgroundProperty?.GetValue(root) is Control { Visible: true } background && background.Size.X > 0.5f && background.Size.Y > 0.5f
                ? background.GetGlobalRect()
                : null;

            if (PanelHintProperty?.GetValue(root) is Label { Visible: true } hint && hint.Size.X > 0.5f && hint.Size.Y > 0.5f)
            {
                CheckWithinViewportAndFrame(name, "hint line", hint.GetGlobalRect(), viewport, frame, failures);
            }

            if (FindFocusedButton(root) is CouchButton cursorRow)
            {
                CheckWithinViewportAndFrame(name, "cursor row", cursorRow.GetGlobalRect(), viewport, frame, failures);
            }

            if (name == "CouchTeammateChoicePanel")
            {
                CheckPreviewCard(root, "before card", ChoicePanelBeforeField, viewport, frame, failures);
                CheckPreviewCard(root, "after card", ChoicePanelAfterField, viewport, frame, failures);
                CheckPreviewCard(root, "preview card", ChoicePanelFocusedField, viewport, frame, failures);
            }
        }
    }

    private static void CheckPreviewCard(Control root, string what, FieldInfo? field, Vector2 viewport, Rect2? frame, List<string> failures)
    {
        if (field?.GetValue(root) is NCard { Visible: true } card && NCardGlobalBounds(card) is Rect2 rect)
        {
            CheckWithinViewportAndFrame("CouchTeammateChoicePanel", what, rect, viewport, frame, failures);
        }
    }

    /// <summary>
    /// An <see cref="NCard"/>'s actual rendered footprint (see <see cref="VisualBounds"/>'s NCard case) - its own
    /// <c>Size</c> is a much larger authoring canvas, not what's drawn. <c>NCard</c> draws its art centered on its
    /// own origin, not from a top-left corner: card holders set <c>CardNode.Position = Vector2.Zero</c> at the
    /// holder's own center (<c>NCardHolder.ConnectSignals</c>, <c>NGridCardHolder.OnReturnedFromPool</c> in the
    /// decompiled source), which only holds together if the art spans -size/2 to +size/2 in the card's own local
    /// space. <c>PivotOffset</c> is unrelated to this (it only affects how rotation/scale pivot, not where the art
    /// itself sits relative to <c>Position</c>) - reading it (as this did in round 3, which normally reads (0, 0)
    /// on <c>NCard</c>) produced a top-left-shaped model that happened to validate a real, wrong placement and flag
    /// a correct one, backwards. Confirmed against real pixels (round 4): the rendered rect this computes converts
    /// to screenshot coordinates matching exactly where a card's visible edge sits in a --review PNG (see
    /// <c>CouchTestContext.LogChoicePanelPreviewCardRect</c>, which logs and converts it once for that check).
    /// </summary>
    private static Rect2? NCardGlobalBounds(NCard card)
    {
        if (!card.Visible)
        {
            return null;
        }

        Vector2 size = NCard.defaultSize * card.Scale;
        return new Rect2(card.GlobalPosition - size * 0.5f, size);
    }

    private static void CheckWithinViewportAndFrame(string panelName, string what, Rect2 rect, Vector2 viewport, Rect2? frame, List<string> failures)
    {
        if (rect.Position.X < -ViewportTolerancePx || rect.Position.Y < -ViewportTolerancePx
            || rect.End.X > viewport.X + ViewportTolerancePx || rect.End.Y > viewport.Y + ViewportTolerancePx)
        {
            failures.Add($"{panelName}'s {what} isn't fully inside the viewport: {FormatRect(rect)}, viewport {viewport.X:0}x{viewport.Y:0}.");
        }

        if (frame is Rect2 frameRect && !frameRect.Grow(AnchorTolerancePx).Encloses(rect))
        {
            failures.Add($"{panelName}'s {what} isn't fully inside its own panel frame: {what} {FormatRect(rect)}, frame {FormatRect(frameRect)}.");
        }
    }

    private static CouchButton? FindFocusedButton(Node node)
    {
        if (node is CouchButton { Visible: true } button && button.IsFocused)
        {
            return button;
        }

        foreach (Node child in node.GetChildren())
        {
            if (FindFocusedButton(child) is CouchButton found)
            {
                return found;
            }
        }

        return null;
    }

    private static string FormatRect(Rect2 rect) => $"({rect.Position.X:0}, {rect.Position.Y:0}, {rect.Size.X:0}x{rect.Size.Y:0})";

    // ---------------------------------------------------------------------------------------------------------
    // Snapshots: compare against the committed baseline, or write it under --bless (docs/testing.md)
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
    /// controls both sides of the format, so a full JSON parser isn't needed (docs/testing.md's Layer A
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
    // --repeat: cross-run comparison (docs/testing.md)
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
    // --review: checkpoint screenshots and the contact sheet (docs/testing.md). Never affects
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
