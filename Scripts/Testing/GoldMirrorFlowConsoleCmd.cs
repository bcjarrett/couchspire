#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Test-only console command for the <c>gold_mirror</c> scenario (docs/design/testing-plan.md §8 WP7).
///
/// No shipped event, relic, card or reward combines "obtain a relic" and "gain gold" as two <c>await</c>s in one
/// method the way WP7's bug needs (checked against every event, reward and rest-site option in the decompiled
/// source that touches both <c>RelicCmd.Obtain</c> and <c>PlayerCmd.GainGold</c>: each one picks either a relic
/// branch or a gold branch, never both in sequence in the same flow). A relic's own on-pickup gold effect (e.g.
/// <see cref="OldCoin"/>) doesn't reproduce it either: that <c>GainGold</c> call happens *nested inside*
/// <c>RelicCmd.Obtain</c> (via <c>RelicModel.AfterObtained</c>), before the leak has a chance to matter, not in the
/// caller's flow *after* <c>Obtain</c> returns.
///
/// This command calls <c>RelicCmd.Obtain</c> then <c>PlayerCmd.GainGold</c> as two plain, sequential <c>await</c>s
/// in one async method — structurally identical to how a real event option would (see e.g.
/// <c>SunkenStatue.DiveIntoWater</c> or <c>ThisOrThat.Plain</c>, which each chain two different patched commands
/// the same way, just not this particular pair). It exercises the exact same Harmony-patched entry points
/// (<c>RelicCmdObtainPatch</c>, <c>PlayerGainGoldMirrorPatch</c>) and the same <c>AsyncLocal</c>-based
/// <c>GoldMirrorSuppressionContext</c> that real content runs through, so it is a faithful reproduction of the
/// call shape, not a mock.
/// </summary>
public sealed class GoldMirrorFlowConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "goldmirrorflow";

    public override string Args => "<gold:int>";

    public override string Description =>
        "Test-only (WP7): obtains a relic, then gains gold, as two awaits in one flow, to guard the gold-mirror " +
        "suppression handoff between RelicCmdObtainPatch and PlayerGainGoldMirrorPatch (see CHANGELOG 06b185a).";

    public override bool IsNetworked => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null || !RunManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "A run does not appear to be in progress");
        }

        if (args.Length < 1 || !int.TryParse(args[0], out int amount))
        {
            return new CmdResult(success: false, "A gold amount (int) is required.");
        }

        ModLog.Info($"[Testing] goldmirrorflow: player={issuingPlayer.NetId}, amount={amount}");
        Task task = ObtainRelicThenGainGoldAsync(issuingPlayer, amount);
        return new CmdResult(task, success: true, $"Obtaining a relic, then granting {amount} gold, in one flow.");
    }

    /// <summary>
    /// The exact call shape WP7 needs: <c>await RelicCmd.Obtain(...)</c> immediately followed, in the same async
    /// method/continuation, by <c>await PlayerCmd.GainGold(...)</c>. Uses <see cref="Akabeko"/>: a plain relic with
    /// no on-pickup effect of its own (no <c>AfterObtained</c> override), no chain-mirror special case
    /// (<c>RelicCmdObtainPatch.ShouldSkipChainMirror</c>), and no gold generation, so the only gold this flow grants
    /// is the explicit <see cref="PlayerCmd.GainGold"/> call below.
    /// </summary>
    private static async Task ObtainRelicThenGainGoldAsync(Player player, int amount)
    {
        await RelicCmd.Obtain(ModelDb.Relic<Akabeko>().ToMutable(), player);
        await PlayerCmd.GainGold(amount, player);
    }
}
#endif
