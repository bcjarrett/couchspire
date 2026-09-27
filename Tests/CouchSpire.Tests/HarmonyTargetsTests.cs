using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace CouchSpire.Tests;

/// <summary>
/// Test 1 (docs/design/testing-plan.md §5, §8 WP1): every <c>[HarmonyPatch]</c> target in the mod must resolve
/// against the game assemblies, without starting Godot.
/// </summary>
[Collection(CecilCollection.Name)]
public class HarmonyTargetsTests
{
    private readonly CecilContext _ctx;
    private readonly ITestOutputHelper _output;

    public HarmonyTargetsTests(CecilFixture fixture, ITestOutputHelper output)
    {
        _ctx = fixture.Context;
        _output = output;
    }

    [Fact]
    public void AllHarmonyPatchTargetsResolve()
    {
        List<HarmonyTargetCheck> checks = HarmonyPatchScanner.ScanModTypes(_ctx);
        Assert.NotEmpty(checks);

        List<string> failures = new();
        foreach (HarmonyTargetCheck check in checks)
        {
            string? failure = ResolveCheck(check);
            if (failure != null)
            {
                failures.Add(failure);
            }
        }

        _output.WriteLine($"Checked {checks.Count} Harmony patch target(s).");

        Assert.True(failures.Count == 0, $"{failures.Count} Harmony patch target(s) failed to resolve against {GamePaths.Sts2DllPath}:\n" + string.Join("\n", failures));
    }

    private static string? ResolveCheck(HarmonyTargetCheck check)
    {
        PatchTargetSpec spec = check.Spec;
        if (spec.TargetType == null)
        {
            return $"{check.Description}: no target type in the merged spec.";
        }

        TypeDefinition? targetType = MemberResolver.ResolveType(spec.TargetType);
        if (targetType == null)
        {
            return $"{check.Description}: game type '{spec.TargetType.FullName}' could not be resolved (renamed, removed, or in an unreferenced assembly).";
        }

        PatchMethodKind kind = spec.MethodKind ?? PatchMethodKind.Normal;
        ResolveResult result = kind switch
        {
            PatchMethodKind.Getter => spec.MethodName == null
                ? new ResolveResult(ResolveOutcome.NotFound, "no property name in the merged spec")
                : MemberResolver.FindProperty(targetType, spec.MethodName, spec.DeclaredOnly, MemberResolver.PropertyAccessor.Getter),
            PatchMethodKind.Setter => spec.MethodName == null
                ? new ResolveResult(ResolveOutcome.NotFound, "no property name in the merged spec")
                : MemberResolver.FindProperty(targetType, spec.MethodName, spec.DeclaredOnly, MemberResolver.PropertyAccessor.Setter),
            PatchMethodKind.Constructor => MemberResolver.FindConstructor(targetType, spec.ArgumentTypes, spec.DeclaredOnly),
            PatchMethodKind.StaticConstructor => MemberResolver.FindMethod(targetType, ".cctor", spec.ArgumentTypes, spec.DeclaredOnly),
            _ => spec.MethodName == null
                ? new ResolveResult(ResolveOutcome.NotFound, "no method name in the merged spec")
                : MemberResolver.FindMethod(targetType, spec.MethodName, spec.ArgumentTypes, spec.DeclaredOnly),
        };

        return result.Success ? null : $"{check.Description}: target {spec.TargetType.FullName}.{spec.MethodName ?? "?"} ({kind}) — {result.Outcome}: {result.Detail}";
    }
}
