#if COUCHSPIRE_TESTS
using System.Reflection;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Rewards-area helpers for the <c>rewards</c> scenario (docs/design/testing-plan.md §6.9). Post-combat rewards are
/// merged onto P1's screen (<c>CombatRoomOfferRewardsPatch</c>), while P2 takes their own rewards in
/// <see cref="CouchTeammateRewards"/>'s panel. Several fields this file needs (<c>NRewardsScreen._rewardsSet</c>,
/// <c>NRewardsScreen._proceedButton</c>, <c>CouchTeammateRewards._instance/_set/_cursor</c>) are private, so this
/// reads them via <c>AccessTools</c> the same way the mod's own reward code already crosses those boundaries
/// (e.g. <c>CouchTeammateRewards.SelectRewardForPlayerMethod</c>).
/// </summary>
internal sealed partial class CouchTestContext
{
    // Looked up on use, not in static initializers: a static initializer that throws would break every scenario
    // (this is a partial of the shared context), while this fails only the scenario that needs the field. Keep the
    // names literal so Layer A (Tests/CouchSpire.Tests) checks them offline.
    private static FieldInfo RequireField(FieldInfo? field, string what)
    {
        return field ?? throw new CouchTestExpectationFailedException($"API drift: {what} not found (run dotnet test Tests/CouchSpire.Tests).");
    }

    private static MethodInfo RequireMethod(MethodInfo? method, string what)
    {
        return method ?? throw new CouchTestExpectationFailedException($"API drift: {what} not found (run dotnet test Tests/CouchSpire.Tests).");
    }

    private static PropertyInfo RequireProperty(PropertyInfo? property, string what)
    {
        return property ?? throw new CouchTestExpectationFailedException($"API drift: {what} not found (run dotnet test Tests/CouchSpire.Tests).");
    }

    public NRewardsScreen RewardsScreen()
    {
        ThrowIfCancelled();
        return NOverlayStack.Instance?.Peek() as NRewardsScreen
            ?? throw new CouchTestExpectationFailedException(
                $"Expected the top overlay to be NRewardsScreen, found {NOverlayStack.Instance?.Peek()?.GetType().Name ?? "none"}.");
    }

    /// <summary>P1's displayed (merged) rewards set. Only rewards NOT sent to the teammate panel end up here
    /// (<c>CombatRoomOfferRewardsPatch</c> skips the teammate's set before registering/merging).</summary>
    public RewardsSet RewardsScreenSet(NRewardsScreen screen)
    {
        ThrowIfCancelled();
        return (RewardsSet)RequireField(AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet"), "NRewardsScreen._rewardsSet").GetValue(screen)!;
    }

    /// <summary>The real Proceed/Skip button node on <paramref name="screen"/>, for <see cref="ClickAsync"/>.</summary>
    public NClickableControl RewardsScreenProceedButton(NRewardsScreen screen)
    {
        ThrowIfCancelled();
        return (NClickableControl)RequireField(AccessTools.Field(typeof(NRewardsScreen), "_proceedButton"), "NRewardsScreen._proceedButton").GetValue(screen)!;
    }

    /// <summary>P2's teammate rewards panel, or null if it isn't open (e.g. simultaneous mode is off, or P2 already
    /// finished).</summary>
    public CouchTeammateRewards? TeammateRewardsPanel()
    {
        ThrowIfCancelled();
        return RequireField(AccessTools.Field(typeof(CouchTeammateRewards), "_instance"), "CouchTeammateRewards._instance").GetValue(null) as CouchTeammateRewards;
    }

    /// <summary>P2's own rewards set, as shown in their panel.</summary>
    public RewardsSet TeammateRewardsSet(CouchTeammateRewards panel)
    {
        ThrowIfCancelled();
        return (RewardsSet)RequireField(AccessTools.Field(typeof(CouchTeammateRewards), "_set"), "CouchTeammateRewards._set").GetValue(panel)!;
    }

    private int TeammateRewardsCursor(CouchTeammateRewards panel)
    {
        return (int)RequireField(AccessTools.Field(typeof(CouchTeammateRewards), "_cursor"), "CouchTeammateRewards._cursor").GetValue(panel)!;
    }

    /// <summary>
    /// Moves P2's reward-row cursor onto <paramref name="target"/> with repeated Right presses (wrapping/skipping
    /// claimed rows exactly as the panel does), settling after each press. Bounded so a stuck cursor fails the
    /// scenario instead of looping forever.
    /// </summary>
    public async Task P2MoveTeammateRewardCursorTo(CouchTeammateRewards panel, Reward target)
    {
        ThrowIfCancelled();
        RewardsSet set = TeammateRewardsSet(panel);
        int targetIndex = set.Rewards.IndexOf(target);
        if (targetIndex < 0)
        {
            throw new CouchTestExpectationFailedException(
                $"Reward {target.GetType().Name} is not part of the teammate's own reward set.");
        }

        int guard = set.Rewards.Count + 2;
        while (TeammateRewardsCursor(panel) != targetIndex)
        {
            if (guard-- <= 0)
            {
                throw new CouchTestExpectationFailedException(
                    $"Could not move the teammate reward cursor to index {targetIndex} ({target.GetType().Name}); stuck at {TeammateRewardsCursor(panel)}.");
            }

            await P2Press(CouchHudCommand.Right);
            await Settle();
        }
    }

    /// <summary>Navigates to and takes a plain (non-card) reward, e.g. gold, then settles.</summary>
    public async Task P2ClaimTeammateReward(CouchTeammateRewards panel, Reward target)
    {
        ThrowIfCancelled();
        await P2MoveTeammateRewardCursorTo(panel, target);
        await P2Press(CouchHudCommand.Accept);
        await Settle();
    }

    /// <summary>
    /// Navigates to and takes a card reward, then waits for the resulting card choice to become pending (the panel
    /// shows the offered cards). Returns the choice so the caller can checkpoint the game in this state before
    /// answering it with <see cref="P2AnswerCardRewardChoice"/> — this screen is easy to blink past otherwise.
    /// </summary>
    public async Task<CouchTeammateChoice> P2StartCardRewardChoice(CouchTeammateRewards panel, CardReward reward)
    {
        ThrowIfCancelled();
        await P2MoveTeammateRewardCursorTo(panel, reward);
        await P2Press(CouchHudCommand.Accept);

        await WaitUntil(
            () => CouchTeammateChoices.Pending.Any((choice) => choice.Player.NetId == P2Id && choice.Source == CouchTeammateRewards.CardRewardSource),
            "P2's card reward choice to become pending");

        return CouchTeammateChoices.Pending.First(
            (choice) => choice.Player.NetId == P2Id && choice.Source == CouchTeammateRewards.CardRewardSource);
    }

    /// <summary>
    /// Answers a pending card-reward choice (from <see cref="P2StartCardRewardChoice"/>) with the option at
    /// <paramref name="optionIndex"/> in the offered list (stable order: <c>CouchTeammateChoice.Options</c> is
    /// <c>CardReward.Cards.ToList()</c>), then settles. Returns the card that was picked.
    /// </summary>
    public async Task<CardModel> P2AnswerCardRewardChoice(CouchTeammateChoice pendingChoice, int optionIndex)
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
