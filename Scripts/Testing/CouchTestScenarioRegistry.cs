#if COUCHSPIRE_TESTS
using LocalMultiControl.Scripts.Testing.Scenarios;

namespace LocalMultiControl.Scripts.Testing;

/// <summary>
/// Every scenario the runner knows about (docs/design/testing-plan.md §6.9). Add one factory line per new
/// <c>Scripts/Testing/Scenarios/*.cs</c> file; nothing else needs to change to pick it up.
/// </summary>
internal static class CouchTestScenarioRegistry
{
    private static readonly Func<ICouchTestScenario>[] Factories =
    {
        () => new StartScenario()
    };

    /// <summary>Every registered scenario, in a fixed order (name, ordinal) so <c>all</c> is deterministic.</summary>
    public static IReadOnlyList<ICouchTestScenario> All()
    {
        return Factories.Select((factory) => factory())
            .OrderBy((scenario) => scenario.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Resolves <c>--couch-test</c>'s value: "all", or a comma-separated list of scenario names.</summary>
    /// <exception cref="ArgumentException">The value is empty, or names an unknown scenario.</exception>
    public static List<ICouchTestScenario> Resolve(string selection)
    {
        List<ICouchTestScenario> all = All().ToList();
        if (string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase))
        {
            return all;
        }

        List<ICouchTestScenario> selected = new();
        foreach (string rawName in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ICouchTestScenario? match = all.FirstOrDefault((scenario) => string.Equals(scenario.Name, rawName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new ArgumentException($"Unknown scenario '{rawName}'. Known scenarios: {string.Join(", ", all.Select((scenario) => scenario.Name))}.");
            }

            selected.Add(match);
        }

        if (selected.Count == 0)
        {
            throw new ArgumentException("--couch-test was given with no scenario names.");
        }

        return selected;
    }
}
#endif
