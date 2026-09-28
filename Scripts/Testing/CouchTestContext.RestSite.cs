#if COUCHSPIRE_TESTS
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace LocalMultiControl.Scripts.Testing;

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
}
#endif
