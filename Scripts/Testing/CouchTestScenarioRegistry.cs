#if COUCHSPIRE_TESTS
namespace CouchSpire.Scripts.Testing;

/// <summary>
/// Every scenario the runner knows about (docs/testing.md): each concrete
/// <see cref="ICouchTestScenario"/> in this assembly with a parameterless constructor. Adding a scenario class is
/// enough; there is no list to edit (a shared list conflicted on every parallel merge).
/// </summary>
internal static class CouchTestScenarioRegistry
{
    /// <summary>Every scenario, ordered by name (ordinal), so <c>all</c> is deterministic.</summary>
    public static IReadOnlyList<ICouchTestScenario> All()
    {
        return typeof(CouchTestScenarioRegistry).Assembly.GetTypes()
            .Where((type) => typeof(ICouchTestScenario).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface
                && type.GetConstructor(Type.EmptyTypes) != null)
            .Select((type) => (ICouchTestScenario)Activator.CreateInstance(type)!)
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
