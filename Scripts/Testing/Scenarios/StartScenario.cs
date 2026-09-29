#if COUCHSPIRE_TESTS
using CouchSpire.Scripts.Runtime;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Testing.Scenarios;

/// <summary>
/// docs/design/testing-plan.md §6.9, row 1: a couch run starts from the menu with the expected session order,
/// driver, characters, combat-sync suppression, and save tag. No room/combat interaction — this only checks that the
/// run starts correctly. Guaranteed by <c>NMultiplayerHostSubmenuPatch.cs</c>, <c>LocalControlRuntime.cs</c>,
/// and <c>LocalSelfCoopSaveTag.cs</c>.
/// </summary>
internal sealed class StartScenario : CouchTestScenarioBase
{
    public override string Name => "start";

    public override async Task RunAsync(CouchTestContext context)
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new CouchTestExpectationFailedException("No run state after the couch run started.");

        context.Expect(runState.Players.Count == 2, $"Expected 2 players in the run, found {runState.Players.Count}.");
        context.Expect(
            runState.Players[0].NetId == context.P1Id && runState.Players[1].NetId == context.P2Id,
            $"Expected session order [{context.P1Id},{context.P2Id}], found [{string.Join(",", runState.Players.Select((player) => player.NetId))}].");

        context.Expect(
            LocalContext.NetId == context.P1Id,
            $"Expected the driver (LocalContext.NetId) to be P1 ({context.P1Id}), found {LocalContext.NetId?.ToString() ?? "null"}.");

        CharacterModel expectedP1 = ModelDb.Character<Ironclad>();
        CharacterModel expectedP2 = ModelDb.Character<Silent>();
        context.Expect(
            context.P1.Character == expectedP1,
            $"Expected P1's character to be {expectedP1.Id.Entry}, found {context.P1.Character.Id.Entry}.");
        context.Expect(
            context.P2.Character == expectedP2,
            $"Expected P2's character to be {expectedP2.Id.Entry}, found {context.P2.Character.Id.Entry}.");

        context.Expect(
            RunManager.Instance.CombatStateSynchronizer.IsDisabled,
            "Expected CombatStateSynchronizer.IsDisabled to be true for a couch co-op run (LocalControlRuntime.OnRunLaunched).");

        bool tagRead = LocalSelfCoopSaveTag.TryReadCurrentProfile(out List<ulong> taggedPlayers);
        context.Expect(tagRead, "Expected a local co-op save tag (v3:players=...) to be present after starting a couch run.");
        context.Expect(
            taggedPlayers.Count == 2 && taggedPlayers[0] == context.P1Id && taggedPlayers[1] == context.P2Id,
            $"Expected save tag players [{context.P1Id},{context.P2Id}], found [{string.Join(",", taggedPlayers)}].");

        await context.Checkpoint("run-started");
        await context.Settle();
    }
}
#endif
