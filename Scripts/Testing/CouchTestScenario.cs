#if COUCHSPIRE_TESTS
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// One scenario (docs/design/testing-plan.md §6.9). Implementations live under <c>Scripts/Testing/Scenarios/</c>,
/// one file per area, and are picked up by <see cref="CouchTestScenarioRegistry"/>.
/// </summary>
internal interface ICouchTestScenario
{
    /// <summary>Short, stable, lowercase; used on the command line (<c>--couch-test</c>) and in output file names.</summary>
    string Name { get; }

    /// <summary>Fixed per scenario (docs/design/testing-plan.md §6.6 rule 2); never derived from the wall clock.</summary>
    string Seed { get; }

    CharacterModel P1Character { get; }

    CharacterModel P2Character { get; }

    /// <summary>How long the scenario body may run before the runner treats it as a soft-lock (§6.3).</summary>
    TimeSpan Timeout { get; }

    /// <summary>
    /// The aspect ratio passes this scenario runs at (docs/design/testing-plan.md §6.5.3): a fresh couch run is
    /// started once per label here, with the display pinned first (<see cref="CouchTestLayout.PinAsync"/>). Labels
    /// must be one of <see cref="CouchTestLayout.KnownAspects"/>.
    /// </summary>
    IReadOnlyList<string> Aspects { get; }

    /// <summary>
    /// Drives the scenario. Touch the game only through <paramref name="context"/> — never call game/mod APIs
    /// directly. On timeout the runner cancels <paramref name="context"/>'s token and moves on to abandon the run;
    /// every <see cref="CouchTestContext"/> method checks that token before touching the game, which is what stops
    /// a timed-out scenario from running on as a "zombie" that presses buttons inside the *next* scenario's run.
    /// A scenario that reaches into the game directly (bypassing the context) has no such guard.
    /// </summary>
    Task RunAsync(CouchTestContext context);
}

/// <summary>Sensible defaults for a scenario: P1 Ironclad, P2 Silent, a 90s timeout, seed = the scenario's name.</summary>
internal abstract class CouchTestScenarioBase : ICouchTestScenario
{
    public abstract string Name { get; }

    public virtual string Seed => Name.ToUpperInvariant();

    public virtual CharacterModel P1Character => ModelDb.Character<Ironclad>();

    public virtual CharacterModel P2Character => ModelDb.Character<Silent>();

    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(90);

    /// <summary>16:9 only by default (docs/design/testing-plan.md §6.5.3); layout-sensitive scenarios override this.</summary>
    public virtual IReadOnlyList<string> Aspects { get; } = new[] { "16:9" };

    public abstract Task RunAsync(CouchTestContext context);
}
#endif
