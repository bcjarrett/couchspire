using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Finds P1's real, currently-visible action buttons — Proceed (rest site/treasure/shop/rewards) and the confirm/
/// cancel buttons of a full-screen card-select overlay (e.g. the Smith upgrade grid) — so the shared P2 column can
/// end a small margin above whichever sits highest, instead of the old hand-tuned clearance constants. This is the
/// one place both the runtime panels (<see cref="CouchPanel.ColumnMaxY"/>) and the test's layout rule
/// (<see cref="CouchSpire.Scripts.Testing.CouchTestLayout"/>) look these buttons up, so they can never drift
/// apart.
/// </summary>
internal static class CouchColumnFloor
{
    /// <summary>Small margin kept between the column's content and whichever P1 button/viewport edge is the floor.</summary>
    public const float DefaultMargin = 24f;

    private static readonly FieldInfo? RewardsProceedButtonField = AccessTools.Field(typeof(NRewardsScreen), "_proceedButton");

    private static readonly FieldInfo? DeckSelectCloseButtonField = AccessTools.Field(typeof(NDeckCardSelectScreen), "_closeButton");
    private static readonly FieldInfo? DeckSelectConfirmButtonField = AccessTools.Field(typeof(NDeckCardSelectScreen), "_confirmButton");
    private static readonly FieldInfo? DeckSelectPreviewCancelButtonField = AccessTools.Field(typeof(NDeckCardSelectScreen), "_previewCancelButton");
    private static readonly FieldInfo? DeckSelectPreviewConfirmButtonField = AccessTools.Field(typeof(NDeckCardSelectScreen), "_previewConfirmButton");

    // The Smith upgrade grid (and similar "pick a card to upgrade" flows) use a different, dedicated screen type
    // with its own field names for the same idea (a close/X button, plus a preview confirm/cancel pair shown once
    // enough cards are picked) - confirmed against the maintainer's live screenshot and a --review run: Smith opens
    // NDeckUpgradeSelectScreen, not NDeckCardSelectScreen. It has separate single/multi preview button pairs
    // depending on whether more than one card is being picked at once (CardSelectorPrefs.MaxSelect); Smith's own
    // SmithRestSiteOption always asks for exactly one, so both pairs are covered here in case a future upgrade pick
    // asks for more than one.
    private static readonly FieldInfo? UpgradeSelectCloseButtonField = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_closeButton");
    private static readonly FieldInfo? UpgradeSelectSingleCancelButtonField = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_singlePreviewCancelButton");
    private static readonly FieldInfo? UpgradeSelectSingleConfirmButtonField = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_singlePreviewConfirmButton");
    private static readonly FieldInfo? UpgradeSelectMultiCancelButtonField = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_multiPreviewCancelButton");
    private static readonly FieldInfo? UpgradeSelectMultiConfirmButtonField = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_multiPreviewConfirmButton");

    /// <summary>P1's real Proceed button on whichever screen is up (rest site/treasure/shop rooms implement
    /// <c>IRoomWithProceedButton</c>; <c>NRewardsScreen</c> is an overlay, not a room, and keeps its own in a
    /// private field), or null off those screens.</summary>
    public static Control? FindProceedButton()
    {
        if (NRestSiteRoom.Instance is { } restSite)
        {
            return restSite.ProceedButton;
        }

        if (NRun.Instance?.TreasureRoom is { } treasure)
        {
            return treasure.ProceedButton;
        }

        if (NMerchantRoom.Instance is { } merchant)
        {
            return merchant.ProceedButton;
        }

        if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards)
        {
            return RewardsProceedButtonField?.GetValue(rewards) as Control;
        }

        return null;
    }

    /// <summary>True while P1 has a full-screen card-select overlay open (e.g. the Smith upgrade grid): P2's panel
    /// should stay at its natural, compact size then, rather than stretching down to the column floor, so it covers
    /// as little of P1's grid as possible (see <see cref="CouchPanel.RowsBudgetMaxY"/>).</summary>
    public static bool IsP1CardSelectOpen => NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen;

    /// <summary>P1's confirm/cancel buttons on a card-select overlay (Smith and the like): the main Close/Confirm
    /// pair, plus the preview's own Cancel/Confirm pair shown once enough cards are picked
    /// (<c>NDeckCardSelectScreen.PreviewSelection</c> / <c>NDeckUpgradeSelectScreen.OnCardClicked</c>). Whichever
    /// are actually visible right now is left to the caller to check.</summary>
    public static IEnumerable<Control> FindCardSelectButtons()
    {
        switch (NOverlayStack.Instance?.Peek())
        {
            case NDeckCardSelectScreen deckScreen:
                if (DeckSelectCloseButtonField?.GetValue(deckScreen) is Control close)
                {
                    yield return close;
                }

                if (DeckSelectConfirmButtonField?.GetValue(deckScreen) is Control confirm)
                {
                    yield return confirm;
                }

                if (DeckSelectPreviewCancelButtonField?.GetValue(deckScreen) is Control previewCancel)
                {
                    yield return previewCancel;
                }

                if (DeckSelectPreviewConfirmButtonField?.GetValue(deckScreen) is Control previewConfirm)
                {
                    yield return previewConfirm;
                }

                break;

            case NDeckUpgradeSelectScreen upgradeScreen:
                if (UpgradeSelectCloseButtonField?.GetValue(upgradeScreen) is Control upgradeClose)
                {
                    yield return upgradeClose;
                }

                if (UpgradeSelectSingleCancelButtonField?.GetValue(upgradeScreen) is Control singleCancel)
                {
                    yield return singleCancel;
                }

                if (UpgradeSelectSingleConfirmButtonField?.GetValue(upgradeScreen) is Control singleConfirm)
                {
                    yield return singleConfirm;
                }

                if (UpgradeSelectMultiCancelButtonField?.GetValue(upgradeScreen) is Control multiCancel)
                {
                    yield return multiCancel;
                }

                if (UpgradeSelectMultiConfirmButtonField?.GetValue(upgradeScreen) is Control multiConfirm)
                {
                    yield return multiConfirm;
                }

                break;
        }
    }

    /// <summary>Every P1 action button worth keeping the column clear of: Proceed plus the card-select buttons.</summary>
    public static IEnumerable<Control> FindActionButtons()
    {
        if (FindProceedButton() is Control proceed)
        {
            yield return proceed;
        }

        foreach (Control button in FindCardSelectButtons())
        {
            yield return button;
        }
    }

    /// <summary>
    /// The lowest safe y (viewport space) for the column's content: a small margin above the topmost visible,
    /// non-zero-size P1 action button whose x-range reaches into the column (<paramref name="columnLeft"/> to the
    /// viewport's right edge), or a margin above the viewport bottom when none is visible there.
    /// </summary>
    public static float ComputeFloorY(Rect2 viewport, float columnLeft, float margin = DefaultMargin)
    {
        float? topmost = null;
        foreach (Control button in FindActionButtons())
        {
            if (!CountsTowardFloor(button, viewport, columnLeft, out Rect2 rect))
            {
                continue;
            }

            topmost = topmost is float current ? Mathf.Min(current, rect.Position.Y) : rect.Position.Y;
        }

        float limit = topmost is float top ? top : viewport.Size.Y;
        return limit - margin;
    }

    /// <summary>
    /// True if <paramref name="button"/> should actually constrain the floor: visible in the tree (not just
    /// present), effectively opaque (not mid-fade-out, or faded out and left sitting there), non-zero size, reaches
    /// into the column's x-range, and its own rect actually lies inside the viewport (senior review: a button that
    /// exists and is technically "visible" but has animated off-screen, or is fully transparent, must not still
    /// count - it isn't something a player can actually see or would read the column as needing to clear).
    /// </summary>
    private static bool CountsTowardFloor(Control? button, Rect2 viewport, float columnLeft, out Rect2 rect)
    {
        rect = default;
        if (button == null || !GodotObject.IsInstanceValid(button) || !button.IsVisibleInTree() || button.Modulate.A <= 0.01f)
        {
            return false;
        }

        rect = button.GetGlobalRect();
        if (rect.Size.X <= 0.5f || rect.Size.Y <= 0.5f || rect.End.X < columnLeft)
        {
            return false;
        }

        // Fully inside the viewport, not merely overlapping it - an off-screen slide-in/out mid-animation (or a
        // button parked far outside the visible area while inactive) must not anchor the floor.
        const float tolerance = 0.5f;
        return rect.Position.X >= -tolerance && rect.Position.Y >= -tolerance
            && rect.End.X <= viewport.Size.X + tolerance && rect.End.Y <= viewport.Size.Y + tolerance;
    }

    /// <summary>One candidate button and whether it actually counted, for the one-time diagnostic
    /// <see cref="CouchSpire.Scripts.Testing.CouchTestContext"/> logs at a checkpoint under review
    /// (senior review round 2: "log which button CouchColumnFloor picked, its node path, IsVisibleInTree, and its
    /// global rect").</summary>
    public readonly record struct FloorCandidate(string Path, bool VisibleInTree, float Alpha, Rect2 GlobalRect, bool CountsTowardFloor);

    /// <summary>Every current candidate button with its visibility/rect/inclusion state, for diagnostics only -
    /// production code should call <see cref="ComputeFloorY"/> directly.</summary>
    public static IReadOnlyList<FloorCandidate> DebugCandidates(Rect2 viewport, float columnLeft)
    {
        List<FloorCandidate> list = new();
        foreach (Control button in FindActionButtons())
        {
            if (button == null || !GodotObject.IsInstanceValid(button))
            {
                list.Add(new FloorCandidate("(freed)", false, 0f, default, false));
                continue;
            }

            bool counts = CountsTowardFloor(button, viewport, columnLeft, out Rect2 rect);
            if (rect == default)
            {
                rect = button.GetGlobalRect();
            }

            list.Add(new FloorCandidate(button.GetPath().ToString(), button.IsVisibleInTree(), button.Modulate.A, rect, counts));
        }

        return list;
    }
}
