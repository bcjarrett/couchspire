#if COUCHSPIRE_TESTS
using System.Reflection;
using Godot;
using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Rest-site-area helpers for the <c>rest_site</c> scenario (docs/design/testing-plan.md §6.9). P1 rests through the
/// real <see cref="NRestSiteRoom"/> UI (own options are per-player, tracked by <c>RestSiteSynchronizer</c>). P2 picks
/// their own options in <see cref="CouchTeammateRestSite"/>'s panel; Smith opens a card pick in
/// <see cref="CouchTeammateChoicePanel"/> (source tag <see cref="RestSiteUpgradeChoiceSource"/>, set by
/// <c>CouchTeammateChoicePatch.PrefixFromDeckForUpgrade</c>). P1's real Proceed is blocked by
/// <c>CouchTeammateRoomsPatch.PrefixRestSiteProceed</c> while <see cref="CouchTeammateRestSite.IsActive"/>.
/// <see cref="CouchTeammateRestSite"/>'s private fields are read via <c>AccessTools</c>, the same way
/// <c>CouchTestContext.Rewards.cs</c> already crosses that boundary for <see cref="CouchTeammateRewards"/>.
/// </summary>
internal sealed partial class CouchTestContext
{
    /// <summary>
    /// Source tag <c>CouchTeammateChoicePatch.PrefixFromDeckForUpgrade</c> requests deck-upgrade picks under (matches
    /// <c>CouchTeammateChoicePanel</c>'s private <c>UpgradeSource</c> constant, which is also this literal).
    /// </summary>
    public const string RestSiteUpgradeChoiceSource = "deck upgrade";

    /// <summary>The rest site room node, or throws if we're not currently in one.</summary>
    public NRestSiteRoom RestSiteRoomNode()
    {
        ThrowIfCancelled();
        return NRestSiteRoom.Instance
            ?? throw new CouchTestExpectationFailedException("Expected NRestSiteRoom.Instance to be set (not currently in a rest site room).");
    }

    /// <summary>P1's real option button for <paramref name="optionId"/> (e.g. "HEAL", "SMITH"), for <see cref="CouchTestContext.ClickAsync"/>.</summary>
    public NClickableControl P1RestSiteOptionButton(string optionId)
    {
        ThrowIfCancelled();
        NRestSiteRoom room = RestSiteRoomNode();
        RestSiteOption option = room.Options.FirstOrDefault((candidate) => candidate.OptionId == optionId)
            ?? throw new CouchTestExpectationFailedException(
                $"P1's rest site options don't include '{optionId}' (found: {string.Join(",", room.Options.Select((candidate) => candidate.OptionId))}).");

        return room.GetButtonForOption(option)
            ?? throw new CouchTestExpectationFailedException($"No button found for P1's rest site option '{optionId}'.");
    }

    /// <summary>The real Proceed button node in the rest site, for <see cref="CouchTestContext.ClickAsync"/>.</summary>
    public NClickableControl RestSiteProceedButton()
    {
        ThrowIfCancelled();
        return RestSiteRoomNode().ProceedButton;
    }

    /// <summary>P2's own rest site panel, or throws if it isn't open.</summary>
    public CouchTeammateRestSite TeammateRestSitePanel()
    {
        ThrowIfCancelled();
        return RequireField(AccessTools.Field(typeof(CouchTeammateRestSite), "_instance"), "CouchTeammateRestSite._instance").GetValue(null) as CouchTeammateRestSite
            ?? throw new CouchTestExpectationFailedException("CouchTeammateRestSite.IsActive was true but no panel instance was found.");
    }

    private static IReadOnlyList<RestSiteOption> TeammateRestSiteShownOptions(CouchTeammateRestSite panel)
    {
        return (List<RestSiteOption>)RequireField(AccessTools.Field(typeof(CouchTeammateRestSite), "_shownOptions"), "CouchTeammateRestSite._shownOptions").GetValue(panel)!;
    }

    private static int TeammateRestSiteCursor(CouchTeammateRestSite panel)
    {
        return (int)RequireField(AccessTools.Field(typeof(CouchTeammateRestSite), "_cursor"), "CouchTeammateRestSite._cursor").GetValue(panel)!;
    }

    /// <summary>
    /// Moves P2's rest site row cursor onto <paramref name="targetRowIndex"/> (0-based; P2's own options first, the
    /// trailing "Skip the rest" row last) with repeated Right presses, settling after each. Bounded so a stuck cursor
    /// fails the scenario instead of looping forever.
    /// </summary>
    private async Task P2MoveTeammateRestSiteCursorTo(CouchTeammateRestSite panel, int targetRowIndex)
    {
        ThrowIfCancelled();
        int rowCount = TeammateRestSiteShownOptions(panel).Count + 1;
        if (targetRowIndex < 0 || targetRowIndex >= rowCount)
        {
            throw new CouchTestExpectationFailedException($"Rest site row index {targetRowIndex} is out of range (0-{rowCount - 1}).");
        }

        int guard = rowCount + 2;
        while (TeammateRestSiteCursor(panel) != targetRowIndex)
        {
            if (guard-- <= 0)
            {
                throw new CouchTestExpectationFailedException(
                    $"Could not move the teammate rest site cursor to row {targetRowIndex}; stuck at {TeammateRestSiteCursor(panel)}.");
            }

            await P2Press(CouchHudCommand.Right);
            await Settle();
        }
    }

    /// <summary>
    /// P2 picks their own rest site option by id (e.g. "SMITH"). For options that open a card pick (Smith), this
    /// leaves a pending teammate choice open; wait for it with <see cref="WaitForPendingTeammateChoice"/> rather than
    /// <see cref="CouchTestContext.Settle"/> (Settle requires zero pending choices, so it would time out here).
    /// </summary>
    public async Task P2ChooseRestSiteOption(CouchTeammateRestSite panel, string optionId)
    {
        ThrowIfCancelled();
        IReadOnlyList<RestSiteOption> options = TeammateRestSiteShownOptions(panel);
        int targetIndex = options.ToList().FindIndex((option) => option.OptionId == optionId);
        if (targetIndex < 0)
        {
            throw new CouchTestExpectationFailedException(
                $"P2's rest site options don't include '{optionId}' (found: {string.Join(",", options.Select((option) => option.OptionId))}).");
        }

        await P2MoveTeammateRestSiteCursorTo(panel, targetIndex);
        await P2Press(CouchHudCommand.Accept);
    }

    /// <summary>P2 skips whatever rest site options remain (the trailing "Skip the rest" row), then settles.</summary>
    public async Task P2SkipRemainingRestSiteOptions(CouchTeammateRestSite panel)
    {
        ThrowIfCancelled();
        int skipRowIndex = TeammateRestSiteShownOptions(panel).Count;
        await P2MoveTeammateRestSiteCursorTo(panel, skipRowIndex);
        await P2Press(CouchHudCommand.Accept);
        await Settle();
    }

    /// <summary>
    /// Waits for a pending teammate choice from <paramref name="source"/> (e.g. <see cref="RestSiteUpgradeChoiceSource"/>)
    /// and returns it, exactly like <c>CouchTestContext.Rewards.cs</c>'s <c>P2StartCardRewardChoice</c> does for card
    /// rewards.
    /// </summary>
    public async Task<CouchTeammateChoice> WaitForPendingTeammateChoice(string source)
    {
        ThrowIfCancelled();
        await WaitUntil(
            () => CouchTeammateChoices.Pending.Any((choice) => choice.Player.NetId == P2Id && choice.Source == source),
            $"P2's '{source}' choice to become pending");

        return CouchTeammateChoices.Pending.First((choice) => choice.Player.NetId == P2Id && choice.Source == source);
    }

    /// <summary>
    /// Answers a pending single-card teammate choice (e.g. the Smith upgrade pick opened by
    /// <see cref="P2ChooseRestSiteOption"/>) with the option at <paramref name="optionIndex"/> in the offered list
    /// (stable order: <c>CouchTeammateChoice.Options</c>), then settles. Returns the card that was picked.
    /// </summary>
    public async Task<CardModel> P2AnswerSingleCardTeammateChoice(CouchTeammateChoice pendingChoice, int optionIndex)
    {
        ThrowIfCancelled();
        if (optionIndex < 0 || optionIndex >= pendingChoice.Options.Count)
        {
            throw new CouchTestExpectationFailedException(
                $"Card choice option index {optionIndex} is out of range (offered {pendingChoice.Options.Count} cards).");
        }

        CardModel picked = pendingChoice.Options[optionIndex];
        for (int i = 0; i < optionIndex; i++)
        {
            // SettleDuringPendingChoice, not Settle: the choice we're navigating is still (correctly) pending until
            // Accept below, and Settle() requires CouchTeammateChoices.Pending to be empty, so it would time out on
            // every navigation press whenever optionIndex > 0.
            await P2Press(CouchHudCommand.Right);
            await SettleDuringPendingChoice();
        }

        await P2Press(CouchHudCommand.Accept);
        await Settle();
        return picked;
    }

    /// <summary>
    /// P1's currently-open card-select overlay (e.g. the Smith upgrade grid, opened by clicking a card-picking rest
    /// site option through the real UI), or throws if one isn't on top. Smith opens <see cref="NDeckUpgradeSelectScreen"/>
    /// specifically (a different type than the generic <see cref="NDeckCardSelectScreen"/>, confirmed against a
    /// --review run and the decompiled source), so this returns the shared base type; callers that need a
    /// type-specific button (<see cref="P1ConfirmCardSelectPreview"/>) switch on the concrete type.
    /// </summary>
    public NCardGridSelectionScreen P1CardSelectScreen()
    {
        ThrowIfCancelled();
        return NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen
            ?? throw new CouchTestExpectationFailedException(
                $"Expected P1's card-select overlay (NCardGridSelectionScreen) on top, found {NOverlayStack.Instance?.Peek()?.GetType().Name ?? "none"}.");
    }

    /// <summary>The first card currently offered in P1's open card-select grid (stable order: <c>NCardGrid.CurrentlyDisplayedCards</c>).</summary>
    public CardModel P1CardSelectFirstOption(NCardGridSelectionScreen screen)
    {
        ThrowIfCancelled();
        NCardGrid grid = P1CardSelectGrid(screen);
        return grid.CurrentlyDisplayedCards.FirstOrDefault()
            ?? throw new CouchTestExpectationFailedException("P1's card-select grid has no cards on offer.");
    }

    private static NCardGrid P1CardSelectGrid(NCardGridSelectionScreen screen)
    {
        return (NCardGrid)RequireField(AccessTools.Field(typeof(NCardGridSelectionScreen), "_grid"), "NCardGridSelectionScreen._grid").GetValue(screen)!;
    }

    private static readonly MethodInfo? CardHolderEmitPressedMethod = AccessTools.Method(typeof(NCardHolder), "EmitPressed");

    /// <summary>
    /// P1 clicks <paramref name="card"/> in their open card-select grid. Unlike a plain button,
    /// <see cref="NCardHolder"/> doesn't react to its <see cref="NCardHolder.Hitbox"/>'s <c>Released</c> signal (the
    /// one <see cref="ClickAsync"/>'s <c>ForceClick</c> emits) - it only reacts to raw mouse-hover input events on
    /// that hitbox, which a synthetic click doesn't produce. So this calls its private <c>EmitPressed</c> directly
    /// (confirmed against the decompiled source: that's exactly what a real click ends up calling), the same way
    /// <see cref="ClickAsync"/>'s underlying <c>NClickableControl.ForceClick</c> bypasses hover/focus for buttons.
    /// For a single-select screen (Smith), this immediately opens the preview with its own confirm/cancel buttons
    /// (<c>OnCardClicked</c> auto-triggering the preview once <c>MaxSelect</c> cards are picked - one, for Smith).
    /// </summary>
    public async Task P1ClickCardInSelectScreen(NCardGridSelectionScreen screen, CardModel card)
    {
        ThrowIfCancelled();
        NCardGrid grid = P1CardSelectGrid(screen);
        NGridCardHolder holder = grid.GetCardHolder(card)
            ?? throw new CouchTestExpectationFailedException($"P1's card-select grid has no holder for '{card.Title}' ({card.Id.Entry}).");
        RequireMethod(CardHolderEmitPressedMethod, "NCardHolder.EmitPressed").Invoke(holder, null);
        await Settle();
    }

    /// <summary>P1 confirms their card-select preview (the ✓ shown after picking a card), finishing the pick and closing the screen.</summary>
    public async Task P1ConfirmCardSelectPreview(NCardGridSelectionScreen screen)
    {
        ThrowIfCancelled();
        NClickableControl confirm = screen switch
        {
            NDeckUpgradeSelectScreen => (NClickableControl)RequireField(
                AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_singlePreviewConfirmButton"), "NDeckUpgradeSelectScreen._singlePreviewConfirmButton").GetValue(screen)!,
            NDeckCardSelectScreen => (NClickableControl)RequireField(
                AccessTools.Field(typeof(NDeckCardSelectScreen), "_previewConfirmButton"), "NDeckCardSelectScreen._previewConfirmButton").GetValue(screen)!,
            _ => throw new CouchTestExpectationFailedException($"Don't know how to confirm a preview on {screen.GetType().Name}.")
        };
        await ClickAsync(confirm);
        await Settle();
    }

    /// <summary>
    /// Diagnostics for the senior-review assertion on <see cref="CouchTeammateChoicePanel"/>'s Smith/upgrade
    /// preview (regression: the panel stopped well short of the live column floor and rendered a tiny preview even
    /// when nothing else - no P1 picker - was constraining it). Reads the panel's private state the same way
    /// <c>CouchTestContext.Rewards.cs</c> already crosses that boundary for other mod internals.
    /// </summary>
    public (float PanelBottomY, float FloorY, float ViewportHeight, bool RowsScrolled, float PreviewCardHeight) TeammateChoicePanelPreviewInfo()
    {
        ThrowIfCancelled();
        CouchTeammateChoicePanel panel = RequireField(
            AccessTools.Field(typeof(CouchTeammateChoicePanel), "_instance"), "CouchTeammateChoicePanel._instance").GetValue(null) as CouchTeammateChoicePanel
            ?? throw new CouchTestExpectationFailedException("Expected CouchTeammateChoicePanel to be open.");

        PropertyInfo backgroundProperty = RequireProperty(AccessTools.Property(typeof(CouchPanel), "Background"), "CouchPanel.Background");
        CouchFrame frame = backgroundProperty.GetValue(panel) as CouchFrame
            ?? throw new CouchTestExpectationFailedException("CouchTeammateChoicePanel's Background frame was null.");

        Label? scrollDown = RequireField(AccessTools.Field(typeof(CouchPanel), "_scrollDownIndicator"), "CouchPanel._scrollDownIndicator").GetValue(panel) as Label;
        Label? scrollUp = RequireField(AccessTools.Field(typeof(CouchPanel), "_scrollUpIndicator"), "CouchPanel._scrollUpIndicator").GetValue(panel) as Label;
        bool rowsScrolled = (scrollDown?.Visible ?? false) || (scrollUp?.Visible ?? false);

        NCard? before = RequireField(AccessTools.Field(typeof(CouchTeammateChoicePanel), "_before"), "CouchTeammateChoicePanel._before").GetValue(panel) as NCard;
        NCard? focused = RequireField(AccessTools.Field(typeof(CouchTeammateChoicePanel), "_focusedPreview"), "CouchTeammateChoicePanel._focusedPreview").GetValue(panel) as NCard;
        NCard? previewCard = before ?? focused
            ?? throw new CouchTestExpectationFailedException("CouchTeammateChoicePanel has no preview card showing.");
        float previewCardHeight = previewCard.Scale.Y * NCard.defaultSize.Y;

        Vector2 viewport = _tree.Root.GetVisibleRect().Size;
        float floorY = CouchColumnFloor.ComputeFloorY(new Rect2(Vector2.Zero, viewport), panel.Position.X);
        float panelBottomY = panel.Position.Y + frame.Size.Y;

        return (panelBottomY, floorY, viewport.Y, rowsScrolled, previewCardHeight);
    }

    /// <summary>
    /// One-time diagnostic (senior review round 2): logs every candidate button <see cref="CouchColumnFloor"/> is
    /// currently considering for the column floor - its node path, whether it's visible in the tree, its alpha, its
    /// global rect, and whether it actually counted - so a wrong floor can be traced to the specific button (or lack
    /// of one) that produced it, instead of guessed at.
    /// </summary>
    public bool LogColumnFloorCandidates(string label)
    {
        ThrowIfCancelled();
        CouchTeammateChoicePanel panel = RequireField(
            AccessTools.Field(typeof(CouchTeammateChoicePanel), "_instance"), "CouchTeammateChoicePanel._instance").GetValue(null) as CouchTeammateChoicePanel
            ?? throw new CouchTestExpectationFailedException("Expected CouchTeammateChoicePanel to be open.");

        Vector2 viewport = _tree.Root.GetVisibleRect().Size;
        IReadOnlyList<CouchColumnFloor.FloorCandidate> candidates = CouchColumnFloor.DebugCandidates(new Rect2(Vector2.Zero, viewport), panel.Position.X);
        string details = candidates.Count == 0
            ? "(no candidate buttons found)"
            : string.Join("; ", candidates.Select((c) =>
                $"{c.Path} visible={c.VisibleInTree} alpha={c.Alpha:0.00} rect=({c.GlobalRect.Position.X:0},{c.GlobalRect.Position.Y:0},{c.GlobalRect.Size.X:0}x{c.GlobalRect.Size.Y:0}) counts={c.CountsTowardFloor}"));
        CouchTestLog.Info($"Column floor candidates at {label} (panel x={panel.Position.X:0}, viewport {viewport.X:0}x{viewport.Y:0}): {details}");
        return candidates.Any((c) => c.CountsTowardFloor);
    }

    /// <summary>
    /// One-time model-vs-pixels validation (senior review round 4): logs the left ("before") preview card's
    /// computed global rect from <see cref="CouchColumnFloor"/>'s sibling model in
    /// <see cref="CouchSpire.Scripts.Testing.CouchTestLayout.NCardGlobalBounds"/>-equivalent terms (mirrored
    /// here rather than called directly, since that method is private), converted to the actual screenshot pixel
    /// space (the mod's logical UI runs at <c>ExpectedViewport</c>, e.g. 1920x1080, but the saved PNG is the
    /// window's own size, e.g. 1600x900) - so the number in the log can be checked by eye against where the card's
    /// edge actually falls in the checkpoint PNG, instead of trusting the model.
    /// </summary>
    public void LogChoicePanelPreviewCardRect(string label)
    {
        ThrowIfCancelled();
        CouchTeammateChoicePanel panel = RequireField(
            AccessTools.Field(typeof(CouchTeammateChoicePanel), "_instance"), "CouchTeammateChoicePanel._instance").GetValue(null) as CouchTeammateChoicePanel
            ?? throw new CouchTestExpectationFailedException("Expected CouchTeammateChoicePanel to be open.");

        NCard? before = RequireField(AccessTools.Field(typeof(CouchTeammateChoicePanel), "_before"), "CouchTeammateChoicePanel._before").GetValue(panel) as NCard;
        if (before == null)
        {
            CouchTestLog.Info($"Choice panel preview card rect at {label}: no 'before' card showing.");
            return;
        }

        Vector2 size = NCard.defaultSize * before.Scale;
        Vector2 globalTopLeft = before.GlobalPosition - size * 0.5f;

        CouchTestAspectPass pass = CouchTestLayout.ResolveAspect(_layoutOptions.Aspect);
        float toScreenshot = pass.WindowSize.X / (float)pass.ExpectedViewport.X;
        Vector2 screenshotTopLeft = globalTopLeft * toScreenshot;
        Vector2 screenshotSize = size * toScreenshot;
        CouchTestLog.Info(
            $"Choice panel preview card rect at {label}: global=({globalTopLeft.X:0},{globalTopLeft.Y:0},{size.X:0}x{size.Y:0}) " +
            $"-> screenshot px (x{toScreenshot:0.###}, {pass.WindowSize.X}x{pass.WindowSize.Y} window)=" +
            $"({screenshotTopLeft.X:0},{screenshotTopLeft.Y:0},{screenshotSize.X:0}x{screenshotSize.Y:0}). " +
            "Check this left edge against the checkpoint PNG by eye.");
    }
}
#endif
