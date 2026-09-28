#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;

namespace LocalMultiControl.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §6.9, row `shop`: each player gets their own <see cref="MerchantInventory"/>
/// (<see cref="LocalMerchantInventoryRuntime"/>/<c>NMerchantInventoryPatch</c>). P2 buys a card, a potion and a relic
/// through their own panel (<see cref="CouchTeammateShop"/>); each purchase drops exactly P2's own gold and grows
/// exactly P2's own deck/potions/relics, leaving P1 untouched. P1's real leave button is blocked
/// (<c>CouchTeammateRoomsPatch.PrefixMerchantLeave</c>) until P2 is done shopping.
///
/// TODO(aspects): declare 16:9 and 16:10 aspect passes for this scenario once the aspect mechanism (WP4, landing in
/// parallel) is available; the runner currently only supports a single fixed 16:9 pass.
/// </summary>
internal sealed class ShopScenarios : CouchTestScenarioBase
{
    public override string Name => "shop";

    public override async Task RunAsync(CouchTestContext context)
    {
        // `gold <amount>` adds to the issuing player's own gold (GoldConsoleCmd -> PlayerCmd.GainGold); it does not
        // set an absolute value, and it's per player, so both players need their own call.
        await context.Console(context.P1, "gold 500");
        await context.Settle();
        await context.Console(context.P2, "gold 500");
        await context.Settle();

        await context.EnterRoom(RoomType.Shop);
        context.Checkpoint("shop-open");

        context.Expect(CouchTeammateShop.IsActive, "Expected P2's CouchTeammateShop panel to be visible after entering the shop.");
        CouchTeammateShop panel = context.TeammateShopPanel()
            ?? throw new CouchTestExpectationFailedException("CouchTeammateShop.IsActive was true but no panel instance was found.");

        MerchantInventory p1Inventory = context.PlayerShopInventory(context.P1);
        MerchantInventory p2Inventory = context.PlayerShopInventory(context.P2);
        context.Expect(!ReferenceEquals(p1Inventory, p2Inventory), "Expected P1 and P2 to each have their own MerchantInventory in the shop.");

        int p1GoldBefore = context.P1.Gold;
        int p2GoldBefore = context.P2.Gold;
        int p1DeckBefore = context.P1.Deck.Cards.Count;
        int p2DeckBefore = context.P2.Deck.Cards.Count;
        int p1PotionsBefore = context.P1.Potions.Count();
        int p2PotionsBefore = context.P2.Potions.Count();
        int p1RelicsBefore = context.P1.Relics.Count;
        int p2RelicsBefore = context.P2.Relics.Count;

        void ExpectP1Unchanged(string when)
        {
            context.Expect(context.P1.Gold == p1GoldBefore, $"Expected P1's gold to stay at {p1GoldBefore} {when}, found {context.P1.Gold}.");
            context.Expect(context.P1.Deck.Cards.Count == p1DeckBefore, $"Expected P1's deck to stay at {p1DeckBefore} cards {when}, found {context.P1.Deck.Cards.Count}.");
            context.Expect(context.P1.Potions.Count() == p1PotionsBefore, $"Expected P1's potions to stay at {p1PotionsBefore} {when}, found {context.P1.Potions.Count()}.");
            context.Expect(context.P1.Relics.Count == p1RelicsBefore, $"Expected P1's relics to stay at {p1RelicsBefore} {when}, found {context.P1.Relics.Count}.");
        }

        // --- Card ---
        List<MerchantEntry> entries = context.ShopPanelOrder(p2Inventory).ToList();
        MerchantCardEntry cardEntry = entries.OfType<MerchantCardEntry>().FirstOrDefault((MerchantCardEntry e) => e.IsStocked && e.EnoughGold)
            ?? throw new CouchTestExpectationFailedException(
                "No affordable card in P2's shop with this seed. Don't force the rule; pick a different fixed seed instead.");
        int cardIndex = entries.IndexOf(cardEntry);
        int cardPrice = cardEntry.Cost;
        CardModel boughtCard = cardEntry.CreationResult!.Card;

        await context.P2BuyShopEntryAt(panel, cardIndex);
        context.Expect(
            context.P2.Gold == p2GoldBefore - cardPrice,
            $"Expected P2's gold to drop by the card's price ({cardPrice}): {p2GoldBefore} -> expected {p2GoldBefore - cardPrice}, found {context.P2.Gold}.");
        context.Expect(
            context.P2.Deck.Cards.Count == p2DeckBefore + 1,
            $"Expected P2's deck to grow by exactly 1 card, found {p2DeckBefore} -> {context.P2.Deck.Cards.Count}.");
        context.Expect(
            context.P2.Deck.Cards.Any((CardModel c) => c.Id.Entry == boughtCard.Id.Entry),
            $"Expected P2's deck to contain the bought card {boughtCard.Id.Entry}.");
        ExpectP1Unchanged("after P2 buys a card");
        int p2GoldAfterCard = context.P2.Gold;

        // --- Potion ---
        context.Expect(context.P2.HasOpenPotionSlots, "Expected P2 to have an open potion slot before buying a potion (a full belt needs a different fixed seed).");
        entries = context.ShopPanelOrder(p2Inventory).ToList();
        MerchantPotionEntry potionEntry = entries.OfType<MerchantPotionEntry>().FirstOrDefault((MerchantPotionEntry e) => e.IsStocked && e.EnoughGold)
            ?? throw new CouchTestExpectationFailedException(
                "No affordable potion in P2's shop after buying a card. Don't force the rule; pick a different fixed seed instead.");
        int potionIndex = entries.IndexOf(potionEntry);
        int potionPrice = potionEntry.Cost;
        PotionModel boughtPotion = potionEntry.Model!;

        await context.P2BuyShopEntryAt(panel, potionIndex);
        context.Expect(
            context.P2.Gold == p2GoldAfterCard - potionPrice,
            $"Expected P2's gold to drop by the potion's price ({potionPrice}): {p2GoldAfterCard} -> expected {p2GoldAfterCard - potionPrice}, found {context.P2.Gold}.");
        context.Expect(
            context.P2.Potions.Count() == p2PotionsBefore + 1,
            $"Expected P2's potions to grow by exactly 1, found {p2PotionsBefore} -> {context.P2.Potions.Count()}.");
        context.Expect(
            context.P2.Potions.Any((PotionModel p) => p.Id.Entry == boughtPotion.Id.Entry),
            $"Expected P2's potions to contain the bought potion {boughtPotion.Id.Entry}.");
        ExpectP1Unchanged("after P2 buys a potion");
        int p2GoldAfterPotion = context.P2.Gold;

        // --- Relic ---
        entries = context.ShopPanelOrder(p2Inventory).ToList();
        MerchantRelicEntry relicEntry = entries.OfType<MerchantRelicEntry>().FirstOrDefault((MerchantRelicEntry e) => e.IsStocked && e.EnoughGold)
            ?? throw new CouchTestExpectationFailedException(
                "No affordable relic in P2's shop after buying a card and a potion. Don't force the rule; pick a different fixed seed instead.");
        int relicIndex = entries.IndexOf(relicEntry);
        int relicPrice = relicEntry.Cost;
        RelicModel boughtRelic = relicEntry.Model!;

        await context.P2BuyShopEntryAt(panel, relicIndex);
        context.Expect(
            context.P2.Gold == p2GoldAfterPotion - relicPrice,
            $"Expected P2's gold to drop by the relic's price ({relicPrice}): {p2GoldAfterPotion} -> expected {p2GoldAfterPotion - relicPrice}, found {context.P2.Gold}.");
        context.Expect(
            context.P2.Relics.Count == p2RelicsBefore + 1,
            $"Expected P2's relics to grow by exactly 1, found {p2RelicsBefore} -> {context.P2.Relics.Count}.");
        context.Expect(
            context.P2.Relics.Any((RelicModel r) => r.Id.Entry == boughtRelic.Id.Entry),
            $"Expected P2's relics to contain the bought relic {boughtRelic.Id.Entry}.");
        ExpectP1Unchanged("after P2 buys a relic");

        context.Checkpoint("p2-bought-card-potion-relic");

        // P1 can't leave yet: the real Proceed/leave button must be blocked while P2 is still shopping.
        await context.ClickAsync(context.MerchantProceedButton());
        await context.Settle();
        context.Expect(
            !(NMapScreen.Instance?.IsOpen ?? false),
            "Expected P1's leave button to be blocked while P2 is still shopping (CouchTeammateRoomsPatch.PrefixMerchantLeave), but the map opened.");
        context.Expect(CouchTeammateShop.IsActive, "Expected P2's shop panel to still be open after P1's blocked leave attempt.");

        // P2 signals done shopping.
        await context.P2FinishShopping(panel);
        context.Expect(!CouchTeammateShop.IsActive, "Expected P2's shop panel to close once P2 signals they're done shopping.");

        // Now P1's real leave button opens the map.
        await context.ClickAsync(context.MerchantProceedButton());
        await context.WaitUntil(() => NMapScreen.Instance?.IsOpen ?? false, "the map to open after P1 leaves once P2 is done shopping");
        context.Checkpoint("map-open");
    }
}
#endif
