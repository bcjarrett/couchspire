using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace CouchSpire.Tests;

/// <summary>
/// Test 2 (docs/design/testing-plan.md §5, §8 WP1): every string-named <c>HarmonyLib.AccessTools</c> call in the
/// mod's IL, wherever it appears (including lambdas/async state machines/local functions), must resolve against
/// the game assemblies — unless its type/name isn't a literal (printed as "unchecked", never a failure) or it's
/// a known, commented legacy-name fallback (<see cref="AccessToolsAllowList"/>).
/// </summary>
[Collection(CecilCollection.Name)]
public class AccessToolsTests
{
    private readonly CecilContext _ctx;
    private readonly ITestOutputHelper _output;

    public AccessToolsTests(CecilFixture fixture, ITestOutputHelper output)
    {
        _ctx = fixture.Context;
        _output = output;
    }

    [Fact]
    public void AllAccessToolsCallSitesResolve()
    {
        List<ResolvedAccessToolsCall> calls = AccessToolsScanner.ScanModTypes(_ctx);
        Assert.NotEmpty(calls);

        List<string> failures = new();
        List<string> uncheckedLines = new();
        List<string> allowlistedLines = new();
        int checkedCount = 0;

        foreach (ResolvedAccessToolsCall call in calls)
        {
            switch (call.Outcome)
            {
                case AccessToolsCallOutcome.UnrecognizedVariant:
                    failures.Add($"{Describe(call)}: {call.UncheckedReason}");
                    break;

                case AccessToolsCallOutcome.Unchecked:
                    uncheckedLines.Add($"{Describe(call)}: {call.UncheckedReason}");
                    break;

                case AccessToolsCallOutcome.Checked:
                    checkedCount++;
                    ResolveResult result = Resolve(call);
                    if (!result.Success)
                    {
                        if (AccessToolsAllowList.TryGetReason(call.TargetType?.FullName, call.MemberName ?? string.Empty, out string reason))
                        {
                            allowlistedLines.Add($"{Describe(call)} — {reason}");
                        }
                        else
                        {
                            failures.Add($"{Describe(call)} — {result.Outcome}: {result.Detail}");
                        }
                    }

                    break;
            }
        }

        _output.WriteLine($"AccessTools call sites found: {calls.Count}. Checked: {checkedCount}. Unchecked: {uncheckedLines.Count}. Allow-listed legacy fallbacks: {allowlistedLines.Count}.");
        _output.WriteLine(string.Empty);
        _output.WriteLine($"Unchecked call sites ({uncheckedLines.Count}) — no static type/literal name to verify, printed for visibility only:");
        foreach (string line in uncheckedLines)
        {
            _output.WriteLine("  " + line);
        }

        _output.WriteLine(string.Empty);
        _output.WriteLine($"Allow-listed legacy fallbacks ({allowlistedLines.Count}):");
        foreach (string line in allowlistedLines)
        {
            _output.WriteLine("  " + line);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} AccessTools call site(s) failed to resolve against {GamePaths.Sts2DllPath}:\n" + string.Join("\n", failures));
    }

    private static ResolveResult Resolve(ResolvedAccessToolsCall call)
    {
        if (call.VariantName == "TypeByName")
        {
            return ResolveTypeByName(call.MemberName!);
        }

        TypeDefinition? typeDef = MemberResolver.ResolveType(call.TargetType);
        if (typeDef == null)
        {
            return new ResolveResult(ResolveOutcome.TypeNotResolved, $"game type '{call.TargetType?.FullName}' could not be resolved.");
        }

        bool declaredOnly = call.VariantName.StartsWith("Declared", StringComparison.Ordinal);
        string memberName = call.MemberName!;

        return call.VariantName switch
        {
            "Field" or "DeclaredField" => MemberResolver.FindField(typeDef, memberName, declaredOnly),
            "FieldRefAccess" or "StaticFieldRefAccess" => MemberResolver.FindField(typeDef, memberName, declaredOnly: false),
            "Method" or "DeclaredMethod" => MemberResolver.FindMethod(typeDef, memberName, call.ArgumentTypes, declaredOnly),
            "Property" or "DeclaredProperty" => MemberResolver.FindProperty(typeDef, memberName, declaredOnly, MemberResolver.PropertyAccessor.Either),
            "PropertyGetter" => MemberResolver.FindProperty(typeDef, memberName, declaredOnly: false, MemberResolver.PropertyAccessor.Getter),
            "PropertySetter" => MemberResolver.FindProperty(typeDef, memberName, declaredOnly: false, MemberResolver.PropertyAccessor.Setter),
            "Inner" => MemberResolver.FindInner(typeDef, memberName, declaredOnly: false),
            "Constructor" or "DeclaredConstructor" => MemberResolver.FindConstructor(typeDef, call.ArgumentTypes, declaredOnly),
            _ => new ResolveResult(ResolveOutcome.NotFound, $"unhandled variant '{call.VariantName}' (should have been caught as UnrecognizedVariant)."),
        };
    }

    private static ResolveResult ResolveTypeByName(string fullName)
    {
        // Defensive support only: not used by the mod today (0 call sites), but kept honest rather than skipped.
        return new ResolveResult(ResolveOutcome.NotFound, $"AccessTools.TypeByName(\"{fullName}\") resolution is not wired to a module search in this test yet.");
    }

    private static string Describe(ResolvedAccessToolsCall call)
    {
        MethodDefinition m = call.Site.ContainingMethod;
        // Print whatever the walker actually resolved for each side, even when only one of them is a literal
        // (e.g. AccessTools.Method(__instance.GetType(), "AddCard") should read Method(<dynamic type>, "AddCard"),
        // not blank out the member name just because the type wasn't traceable).
        string target = call.TargetType != null ? call.TargetType.FullName : "<dynamic type>";
        string member = call.MemberName != null ? $"\"{call.MemberName}\"" : "<dynamic name>";
        return $"{m.DeclaringType.FullName}.{m.Name} (IL_{call.Site.Instruction.Offset:x4}): AccessTools.{call.VariantName}({target}, {member})";
    }
}
