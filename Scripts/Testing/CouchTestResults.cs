#if COUCHSPIRE_TESTS
using System.Text;

namespace CouchSpire.Scripts.Testing;

internal enum CouchTestOutcome
{
    Pass,
    Fail,
    Timeout
}

/// <summary>One scenario's (or one scenario/aspect pass's — see <see cref="Aspect"/>) result.</summary>
internal sealed record CouchTestScenarioResult(
    string Name,
    string Aspect,
    CouchTestOutcome Outcome,
    string Message,
    string Seed,
    long DurationMs);

/// <summary>
/// Writes <c>results.json</c> and <c>summary.txt</c> (docs/testing.md). Hand-rolled JSON: the
/// shape is tiny and fixed, and this avoids any question about reflection-based serialization inside a Godot mod
/// assembly. <c>durationMs</c> is the only field allowed to differ between two otherwise-identical runs (see
/// docs/testing.md).
/// </summary>
internal static class CouchTestResultsWriter
{
    public static void Write(string outDir, IReadOnlyList<CouchTestScenarioResult> results, IReadOnlyList<string> notes)
    {
        Directory.CreateDirectory(outDir);
        bool passed = results.Count > 0 && results.All((result) => result.Outcome == CouchTestOutcome.Pass);
        File.WriteAllText(Path.Combine(outDir, "results.json"), BuildResultsJson(passed, results));
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), BuildSummaryText(passed, results, notes));
    }

    private static string BuildResultsJson(bool passed, IReadOnlyList<CouchTestScenarioResult> results)
    {
        StringBuilder json = new();
        json.Append("{\n");
        json.Append($"  \"passed\": {(passed ? "true" : "false")},\n");
        json.Append("  \"scenarios\": [\n");
        for (int i = 0; i < results.Count; i++)
        {
            CouchTestScenarioResult result = results[i];
            json.Append("    {\n");
            json.Append($"      \"name\": {JsonString(result.Name)},\n");
            json.Append($"      \"aspect\": {JsonString(result.Aspect)},\n");
            json.Append($"      \"outcome\": {JsonString(OutcomeText(result.Outcome))},\n");
            json.Append($"      \"message\": {JsonString(result.Message)},\n");
            json.Append($"      \"seed\": {JsonString(result.Seed)},\n");
            json.Append($"      \"durationMs\": {result.DurationMs}\n");
            json.Append(i == results.Count - 1 ? "    }\n" : "    },\n");
        }

        json.Append("  ]\n");
        json.Append("}\n");
        return json.ToString();
    }

    private static string BuildSummaryText(bool passed, IReadOnlyList<CouchTestScenarioResult> results, IReadOnlyList<string> notes)
    {
        int passCount = results.Count((result) => result.Outcome == CouchTestOutcome.Pass);
        StringBuilder text = new();
        text.AppendLine($"CouchSpire in-game test run: {(passed ? "PASS" : "FAIL")}");
        text.AppendLine($"{passCount}/{results.Count} scenario(s) passed.");
        text.AppendLine();
        foreach (CouchTestScenarioResult result in results)
        {
            text.AppendLine($"[{OutcomeText(result.Outcome).ToUpperInvariant()}] {result.Name} ({result.Aspect}, seed={result.Seed}, {result.DurationMs}ms)");
            if (result.Outcome != CouchTestOutcome.Pass)
            {
                text.AppendLine($"    {result.Message}");
            }
        }

        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Notes:");
            foreach (string note in notes)
            {
                text.AppendLine($"  - {note}");
            }
        }

        return text.ToString();
    }

    /// <summary>Lowercase outcome text for JSON/prose. See also <see cref="CouchTestLog"/> callers that need the
    /// uppercase form ("FAIL"/"TIMEOUT") to match <c>deploy.sh test</c>'s failing-log grep.</summary>
    public static string OutcomeText(CouchTestOutcome outcome)
    {
        return outcome switch
        {
            CouchTestOutcome.Pass => "pass",
            CouchTestOutcome.Fail => "fail",
            CouchTestOutcome.Timeout => "timeout",
            _ => "unknown"
        };
    }

    private static string JsonString(string value)
    {
        StringBuilder sb = new();
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
#endif
