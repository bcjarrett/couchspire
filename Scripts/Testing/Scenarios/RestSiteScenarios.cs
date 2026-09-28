#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §6.9, row `rest_site`. Every player has their own rest site options
/// (<c>RestSiteSynchronizer</c>, one <c>PlayerRestSite</c> per player); P1 acts through the real
/// <see cref="NRestSiteRoom"/> UI, P2 through <see cref="CouchTeammateRestSite"/>'s panel. Smith opens a card pick
/// (<see cref="CouchTeammateChoicePanel"/>); P1's real Proceed is blocked by
/// <c>CouchTeammateRoomsPatch.PrefixRestSiteProceed</c> until P2 is done
/// (<see cref="CouchTeammateRestSite.BlocksProceed"/>).
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes here once the aspect mechanism (WP4) lands. As of this
/// writing <see cref="ICouchTestScenario"/> has no aspect list and the runner always runs a single 16:9 pass
/// (<c>CouchTestRunner.DefaultAspectLabel</c>), so there is nothing to declare against yet.
/// </summary>
internal sealed class RestSiteScenarios : CouchTestScenarioBase
{
    public override string Name => "rest_site";

    public override async Task RunAsync(CouchTestContext context)
    {
        // Damaging P1 requires combat (DamageConsoleCmd only runs while CombatManager.Instance.IsInProgress), so this
        // borrows a Monster room rather than the rest site itself. Target index 0 is P1's own creature: CombatState's
        // allies are added in RunState.Players order (P1, then P2; see StartScenario's session-order assertion), so
        // ally 0 is always P1's at the very start of combat, before anything else can join the allies list.
        await context.EnterRoom(RoomType.Monster);
        await context.Console(context.P1, "damage 20 0");
        await context.Settle();

        int p1HpAfterDamage = context.P1.Creature.CurrentHp;
        context.Expect(
            p1HpAfterDamage < context.P1.Creature.MaxHp,
            $"Expected P1 to be damaged before resting, found {p1HpAfterDamage}/{context.P1.Creature.MaxHp} HP.");

        // Give P2 an upgradable card at a known, reproducible deck position: CardPileCmd.Add's default position is
        // Bottom, so the seeded card ends up last in deck order, and (nothing else here is upgraded yet) last among
        // the deck's upgradable cards too — the exact order SmithRestSiteOption's card choice offers them in.
        await context.Console(context.P2, "card NEUTRALIZE deck");
        await context.Settle();

        CardModel p2SeededCard = context.P2.Deck.Cards.Last();
        context.Expect(
            p2SeededCard.Id.Entry == "NEUTRALIZE",
            $"Expected the card just added to P2's deck to be NEUTRALIZE, found {p2SeededCard.Id.Entry}.");
        context.Expect(
            p2SeededCard.IsUpgradable && !p2SeededCard.IsUpgraded,
            "Expected the seeded NEUTRALIZE card to start unupgraded and upgradable.");

        // Jump straight to the rest site: EnterRoomDebug exits the current room (abandoning the unfinished combat)
        // before entering the next one, exactly like the runner's own abandon path.
        await context.EnterRoom(RoomType.RestSite);
        await context.Checkpoint("rest-site-open");

        context.Expect(CouchTeammateRestSite.IsActive, "Expected P2's rest site panel to be visible after entering the rest site.");

        // Snapshot both decks' upgrade state before anyone acts, so the post-Smith assertion can prove exactly one
        // card changed (by reference; CardModel instances are mutated in place, not replaced, by CardCmd.Upgrade).
        List<CardModel> p1Cards = context.P1.Deck.Cards.ToList();
        List<bool> p1UpgradedBefore = p1Cards.Select((card) => card.IsUpgraded).ToList();
        List<CardModel> p2Cards = context.P2.Deck.Cards.ToList();
        List<bool> p2UpgradedBefore = p2Cards.Select((card) => card.IsUpgraded).ToList();

        int p1HpBeforeHeal = context.P1.Creature.CurrentHp;
        int p2HpBeforeHeal = context.P2.Creature.CurrentHp;

        // P1 rests: click the real HEAL option button.
        NClickableControl healButton = context.P1RestSiteOptionButton("HEAL");
        await context.ClickAsync(healButton);
        await context.Settle();

        context.Expect(
            context.P1.Creature.CurrentHp > p1HpBeforeHeal,
            $"Expected P1's HP to rise after resting, found {p1HpBeforeHeal} -> {context.P1.Creature.CurrentHp}.");
        context.Expect(
            context.P2.Creature.CurrentHp == p2HpBeforeHeal,
            $"Expected P2's HP to stay at {p2HpBeforeHeal} while P1 rests, found {context.P2.Creature.CurrentHp}.");

        // Before P2 has chosen: P1's real Proceed must be blocked (CouchTeammateRoomsPatch.PrefixRestSiteProceed).
        NClickableControl proceedButton = context.RestSiteProceedButton();
        await context.ClickAsync(proceedButton);
        await context.Settle();
        context.Expect(
            !(NMapScreen.Instance?.IsOpen ?? false),
            "Expected P1's Proceed to be blocked while P2 hasn't chosen at the rest site (CouchTeammateRoomsPatch), but the map opened.");
        context.Expect(NRestSiteRoom.Instance != null, "Expected to still be in the rest site room after a blocked Proceed click.");

        // P2 picks Smith, then a specific card by index (stable order: CouchTeammateChoice.Options, the deck's
        // upgradable cards in deck order).
        CouchTeammateRestSite restSitePanel = context.TeammateRestSitePanel();
        await context.P2ChooseRestSiteOption(restSitePanel, "SMITH");

        CouchTeammateChoice upgradeChoice = await context.WaitForPendingTeammateChoice(CouchTestContext.RestSiteUpgradeChoiceSource);
        await context.Checkpoint("p2-smith-card-choice-open");

        int seededCardOptionIndex = upgradeChoice.Options.ToList().FindIndex((card) => ReferenceEquals(card, p2SeededCard));
        context.Expect(seededCardOptionIndex >= 0, "Expected the seeded NEUTRALIZE card to be offered in P2's Smith upgrade choice.");

        CardModel pickedCard = await context.P2AnswerSingleCardTeammateChoice(upgradeChoice, seededCardOptionIndex);
        context.Expect(ReferenceEquals(pickedCard, p2SeededCard), "Expected P2's Smith pick to be the seeded NEUTRALIZE card.");

        // Exactly one card in P2's deck went from not-upgraded to upgraded (the seeded card); P1's deck is untouched.
        List<CardModel> p1CardsAfter = context.P1.Deck.Cards.ToList();
        context.Expect(
            p1CardsAfter.Count == p1Cards.Count && p1CardsAfter.Select((card) => card.IsUpgraded).SequenceEqual(p1UpgradedBefore),
            "Expected P1's deck upgrade state to be unchanged while P2 smiths.");

        List<CardModel> p2CardsAfter = context.P2.Deck.Cards.ToList();
        context.Expect(
            p2CardsAfter.Count == p2Cards.Count,
            $"Expected P2's deck size to stay at {p2Cards.Count} cards while smithing, found {p2CardsAfter.Count}.");
        for (int i = 0; i < p2Cards.Count; i++)
        {
            CardModel card = p2Cards[i];
            bool wasUpgraded = p2UpgradedBefore[i];
            bool isUpgradedNow = card.IsUpgraded;
            if (ReferenceEquals(card, p2SeededCard))
            {
                context.Expect(
                    !wasUpgraded && isUpgradedNow,
                    $"Expected the seeded card to go from not-upgraded to upgraded, found {wasUpgraded} -> {isUpgradedNow}.");
            }
            else
            {
                context.Expect(
                    wasUpgraded == isUpgradedNow,
                    $"Expected only the seeded card's upgrade state to change; P2's deck card at index {i} ({card.Id.Entry}) changed from {wasUpgraded} to {isUpgradedNow}.");
            }
        }

        // P2 is left with Heal and Mend still on offer; skip them to finish P2's rest site.
        await context.P2SkipRemainingRestSiteOptions(restSitePanel);
        context.Expect(!CouchTeammateRestSite.IsActive, "Expected P2's rest site panel to close once P2 skips the rest.");

        // Now P1's Proceed opens the map.
        await context.ClickAsync(proceedButton);
        await context.WaitUntil(() => NMapScreen.Instance?.IsOpen ?? false, "the map to open after P1's Proceed once P2 is done");
        await context.Checkpoint("map-open");
    }
}
#endif
