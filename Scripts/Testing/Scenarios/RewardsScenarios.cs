#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Rewards;
using LocalMultiControl.Scripts.Runtime;
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §6.9, row 4: post-combat rewards. `win` triggers
/// <c>CombatRoomOfferRewardsPatch</c>, which generates each player's rewards exactly once, switches the driver to
/// the first living player, shows P1 only P1's (merged) rewards, and gives P2 their own panel
/// (<see cref="CouchTeammateRewards"/>). <c>CouchRewardsProceedPatch</c> blocks P1's Proceed button until that
/// panel is done.
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes here once the aspect mechanism (WP4) lands. As of this
/// writing <see cref="ICouchTestScenario"/> has no aspect list and the runner always runs a single 16:9 pass
/// (<c>CouchTestRunner.DefaultAspectLabel</c>), so there is nothing to declare against yet.
/// </summary>
internal sealed class RewardsScenarios : CouchTestScenarioBase
{
    public override string Name => "rewards";

    public override async Task RunAsync(CouchTestContext context)
    {
        await context.EnterRoom(RoomType.Monster);

        await context.Console(context.P1, "win");
        await context.Settle();
        await context.Checkpoint("rewards-open");

        context.Expect(
            LocalContext.NetId == context.P1Id,
            $"Expected the driver to be P1 ({context.P1Id}) after the merged-rewards driver switch (docs/design/testing-plan.md §4), found {LocalContext.NetId?.ToString() ?? "null"}.");

        NRewardsScreen p1Screen = context.RewardsScreen();
        RewardsSet p1DisplaySet = context.RewardsScreenSet(p1Screen);
        string p1Label = LocalModText.RoleSlot(LocalSelfCoopContext.GetSlotLabel(context.P1Id));

        // P1's screen lists only P1's rewards: every reward on it is registry-tagged as P1's, and none is one of
        // P2's actual Reward instances (RewardPlayerLabelRegistry.Register only runs for the non-teammate sets;
        // CombatRoomOfferRewardsPatch skips the teammate's set with `continue` before merging/registering).
        context.Expect(p1DisplaySet.Rewards.Count > 0, "Expected P1's merged reward screen to list at least one reward.");
        foreach (Reward reward in p1DisplaySet.Rewards)
        {
            bool hasLabel = RewardPlayerLabelRegistry.TryGetLabel(reward, out string? label);
            context.Expect(
                hasLabel && label == p1Label,
                $"Expected every reward on P1's screen to be tagged '{p1Label}', found {(hasLabel ? $"'{label}'" : "no label")} for a {reward.GetType().Name}.");
        }

        // P2's rewards panel is visible, tied to P1's screen (so it blocks Proceed), and lists P2's own rewards.
        context.Expect(CouchTeammateRewards.IsActive, "Expected P2's CouchTeammateRewards panel to be active after `win`.");
        context.Expect(CouchTeammateRewards.BlocksProceed(p1Screen), "Expected P2's rewards panel to be tied to (block) P1's reward screen.");
        CouchTeammateRewards teammatePanel = context.TeammateRewardsPanel()
            ?? throw new CouchTestExpectationFailedException("CouchTeammateRewards.IsActive was true but no panel instance was found.");
        RewardsSet p2Set = context.TeammateRewardsSet(teammatePanel);
        context.Expect(p2Set.Player.NetId == context.P2Id, $"Expected the teammate panel's rewards to belong to P2 ({context.P2Id}), found player {p2Set.Player.NetId}.");
        context.Expect(p2Set.Rewards.Count > 0, "Expected P2's teammate reward panel to list at least one reward.");
        context.Expect(
            p2Set.Rewards.All((reward) => !p1DisplaySet.Rewards.Contains(reward)),
            "Expected none of P2's rewards to also appear on P1's merged reward screen.");
        context.Expect(
            p2Set.Rewards.All((reward) => !RewardPlayerLabelRegistry.TryGetLabel(reward, out _)),
            "Expected P2's own rewards to never be registered in RewardPlayerLabelRegistry (that only happens for rewards merged onto P1's screen).");

        int p1GoldBefore = context.P1.Gold;
        int p2GoldBefore = context.P2.Gold;
        int p1DeckSizeBefore = context.P1.Deck.Cards.Count;
        int p2DeckSizeBefore = context.P2.Deck.Cards.Count;

        // P2 takes the gold reward: their gold rises by exactly the reward's own (seeded) amount; P1's is untouched.
        GoldReward p2GoldReward = p2Set.Rewards.OfType<GoldReward>().SingleOrDefault()
            ?? throw new CouchTestExpectationFailedException("Expected exactly one GoldReward in P2's post-Monster-combat rewards.");
        int goldRewardAmount = p2GoldReward.Amount;
        context.Expect(goldRewardAmount >= 0, $"Expected P2's gold reward to already be populated (Amount>=0) at rewards-open, found {goldRewardAmount}.");

        await context.P2ClaimTeammateReward(teammatePanel, p2GoldReward);
        context.Expect(
            context.P2.Gold == p2GoldBefore + goldRewardAmount,
            $"Expected P2's gold to rise by the reward's amount ({goldRewardAmount}): {p2GoldBefore} -> {context.P2.Gold}.");
        context.Expect(
            context.P1.Gold == p1GoldBefore,
            $"Expected P1's gold to stay at {p1GoldBefore} while P2 claims their own gold reward, found {context.P1.Gold}.");

        // Before P2 is done: at least the card reward below is still unclaimed (a Monster room always offers one),
        // so the panel is still active. P1's real Proceed button must be blocked.
        context.Expect(CouchTeammateRewards.IsActive, "Expected P2's rewards panel to still be active after only the gold reward is taken (the card reward is still unclaimed).");
        NClickableControl proceedButton = context.RewardsScreenProceedButton(p1Screen);
        await context.ClickAsync(proceedButton);
        await context.Settle();
        context.Expect(
            !(NMapScreen.Instance?.IsOpen ?? false),
            "Expected P1's Proceed to be blocked while P2 is still taking their rewards (CouchRewardsProceedPatch), but the map opened.");
        context.Expect(
            ReferenceEquals(NOverlayStack.Instance?.Peek(), p1Screen),
            "Expected the reward screen to remain the top overlay after a blocked Proceed click.");

        // P2 takes the card reward, picking the first offered card (stable order: CouchTeammateChoice.Options).
        CardReward p2CardReward = p2Set.Rewards.OfType<CardReward>().SingleOrDefault()
            ?? throw new CouchTestExpectationFailedException("Expected exactly one CardReward in P2's post-Monster-combat rewards.");

        CouchTeammateChoice p2CardChoice = await context.P2StartCardRewardChoice(teammatePanel, p2CardReward);
        await context.Checkpoint("p2-card-choice-open");
        CardModel pickedCard = await context.P2AnswerCardRewardChoice(p2CardChoice, optionIndex: 0);
        context.Expect(
            context.P2.Deck.Cards.Count == p2DeckSizeBefore + 1,
            $"Expected P2's deck to grow by 1 card, found {p2DeckSizeBefore} -> {context.P2.Deck.Cards.Count}.");
        context.Expect(
            context.P2.Deck.Cards.Any((card) => card.Id.Entry == pickedCard.Id.Entry),
            $"Expected P2's deck to contain the picked card {pickedCard.Id.Entry}.");
        context.Expect(
            context.P1.Deck.Cards.Count == p1DeckSizeBefore,
            $"Expected P1's deck to stay at {p1DeckSizeBefore} cards while P2 claims a card reward, found {context.P1.Deck.Cards.Count}.");

        // P2 signals done. With this seed, P2's set is exactly [gold, card] (no potion roll), so claiming the card
        // above already completes every reward and the panel finishes itself
        // (CouchTeammateRewards._Process: "!_busy && _set.Rewards.All(SuccessfullySelected)" -> Finish(allTaken: true)).
        // If a future reseed ever leaves something unclaimed (e.g. a potion), finish explicitly via the Done row,
        // exactly as the controller's Y / keyboard end-turn key would.
        if (CouchTeammateRewards.IsActive)
        {
            await context.P2Press(CouchHudCommand.SubmitOrEndTurn);
            await context.Settle();
            await context.P2Press(CouchHudCommand.Accept);
            await context.Settle();
        }

        context.Expect(!CouchTeammateRewards.IsActive, "Expected P2's rewards panel to be closed once P2 is done (either by taking everything, or by pressing Done).");

        // Now P1's Proceed opens the map.
        await context.ClickAsync(proceedButton);
        await context.WaitUntil(() => NMapScreen.Instance?.IsOpen ?? false, "the map to open after P1's Proceed once P2 is done");
        await context.Checkpoint("map-open");

        context.Expect(
            LocalContext.NetId == context.P1Id,
            $"Expected the driver to remain P1 ({context.P1Id}) on the map screen, found {LocalContext.NetId?.ToString() ?? "null"}.");
    }
}
#endif
