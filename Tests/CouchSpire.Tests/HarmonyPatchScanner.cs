using Mono.Cecil;

namespace CouchSpire.Tests;

/// <summary>One resolvable Harmony patch target, after merging class- and method-level <c>[HarmonyPatch]</c>
/// attributes (or, for the <c>[HarmonyTargetMethods]</c> special case, after reading the mod's own
/// <c>(Type, string)</c> table out of its IL — see <see cref="HarmonyPatchScanner.ScanTargetMethodsSpecialCase"/>).</summary>
internal sealed class HarmonyTargetCheck
{
    public required TypeDefinition ModType { get; init; }

    public MethodDefinition? ModMethod { get; init; }

    public required PatchTargetSpec Spec { get; init; }

    public required string Description { get; init; }
}

internal static class HarmonyPatchScanner
{
    public static List<HarmonyTargetCheck> ScanModTypes(CecilContext ctx)
    {
        List<HarmonyTargetCheck> checks = new();
        foreach (TypeDefinition type in ctx.AllModTypes())
        {
            List<CustomAttribute> classAttrs = type.CustomAttributes.Where(HarmonyAttributeParser.IsHarmonyPatchAttribute).ToList();
            List<MethodDefinition> targetMethodsMethods = type.Methods
                .Where((m) => m.CustomAttributes.Any((a) => a.AttributeType.Name is "HarmonyTargetMethods" or "HarmonyTargetMethod"))
                .ToList();
            List<MethodDefinition> methodsWithOwnAttr = type.Methods
                .Where((m) => m.CustomAttributes.Any(HarmonyAttributeParser.IsHarmonyPatchAttribute))
                .ToList();

            if (classAttrs.Count == 0 && targetMethodsMethods.Count == 0 && methodsWithOwnAttr.Count == 0)
            {
                continue; // Not a Harmony patch class.
            }

            PatchTargetSpec classSpec = classAttrs.Aggregate(PatchTargetSpec.Empty, (acc, a) => acc.MergeWith(HarmonyAttributeParser.Parse(a)));

            if (targetMethodsMethods.Count > 0)
            {
                checks.AddRange(ScanTargetMethodsSpecialCase(type, targetMethodsMethods));
                continue;
            }

            if (methodsWithOwnAttr.Count > 0)
            {
                foreach (MethodDefinition m in methodsWithOwnAttr)
                {
                    List<CustomAttribute> ownAttrs = m.CustomAttributes.Where(HarmonyAttributeParser.IsHarmonyPatchAttribute).ToList();
                    PatchTargetSpec ownSpec = ownAttrs.Aggregate(PatchTargetSpec.Empty, (acc, a) => acc.MergeWith(HarmonyAttributeParser.Parse(a)));
                    PatchTargetSpec merged = classSpec.MergeWith(ownSpec);
                    checks.Add(new HarmonyTargetCheck
                    {
                        ModType = type,
                        ModMethod = m,
                        Spec = merged,
                        Description = $"{type.FullName}.{m.Name} [HarmonyPatch]",
                    });
                }

                continue;
            }

            if (classSpec.TargetType != null)
            {
                checks.Add(new HarmonyTargetCheck
                {
                    ModType = type,
                    ModMethod = null,
                    Spec = classSpec,
                    Description = $"{type.FullName} [HarmonyPatch] (class-level)",
                });
                continue;
            }

            throw new InvalidOperationException(
                $"{type.FullName} has a [HarmonyPatch] attribute with no resolvable target type, no per-method " +
                "[HarmonyPatch] attributes, and no [HarmonyTargetMethods]/[HarmonyTargetMethod]. This is an " +
                "unsupported/unknown Harmony patch shape; add support in HarmonyPatchScanner instead of skipping it.");
        }

        return checks;
    }

    /// <summary>The <c>[HarmonyTargetMethods]</c> special case: a class with a bare <c>[HarmonyPatch]</c> and
    /// a <c>TargetMethods()</c> method that resolves its real targets from a static <c>(Type, string)[]</c> table
    /// (e.g. <c>GamescopeFocusPatch.Callers</c>), consumed via <c>AccessTools.DeclaredMethod</c>. We find that
    /// table by symbolically walking the type's own IL for a static field store whose value is an array of
    /// <c>(Type, string)</c> tuples, rather than hardcoding the field's name.</summary>
    private static IEnumerable<HarmonyTargetCheck> ScanTargetMethodsSpecialCase(TypeDefinition type, List<MethodDefinition> targetMethodsMethods)
    {
        List<(TypeReference Type, string Name)> pairs = new();
        foreach (MethodDefinition m in type.Methods.Where((m) => m.HasBody))
        {
            IlWalkResult walk = IlSymbolicWalker.Walk(m);
            foreach (StaticFieldStore store in walk.StaticFieldStores)
            {
                if (store.Field.DeclaringType.FullName != type.FullName)
                {
                    continue;
                }

                if (store.Value is { Kind: TagKind.ArrayLiteral, ArrayElements: { } elements })
                {
                    foreach (StackTag? elem in elements)
                    {
                        if (elem is { Kind: TagKind.TupleTypeString })
                        {
                            pairs.Add((elem.TupleType!, elem.TupleName!));
                        }
                    }
                }
            }
        }

        if (pairs.Count == 0)
        {
            throw new InvalidOperationException(
                $"{type.FullName} has [HarmonyTargetMethods]/[HarmonyTargetMethod] but the special-cased IL scan " +
                "found no (Type, string) table built in its own methods. If this type doesn't use the " +
                "Callers-table pattern (docs/testing.md, GamescopeFocusPatch), add a different " +
                "special case in HarmonyPatchScanner instead of silently skipping verification.");
        }

        string methodNames = string.Join(",", targetMethodsMethods.Select((m) => m.Name));
        foreach ((TypeReference callerType, string callerMethodName) in pairs)
        {
            yield return new HarmonyTargetCheck
            {
                ModType = type,
                ModMethod = null,
                Spec = new PatchTargetSpec { TargetType = callerType, MethodName = callerMethodName, DeclaredOnly = true },
                Description = $"{type.FullName}.[{methodNames}] Callers-table entry ({callerType.FullName}, \"{callerMethodName}\")",
            };
        }
    }

}
