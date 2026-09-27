using Mono.Cecil;

namespace CouchSpire.Tests;

internal enum AccessToolsCallOutcome
{
    /// <summary>A static type and a literal member name were both traced; the call site should be resolved
    /// against the game assemblies and is a real failure if it doesn't resolve.</summary>
    Checked,

    /// <summary>Type and/or name came from something dynamic (e.g. <c>x.GetType()</c>) rather than a literal;
    /// printed to test output, never fails the test.</summary>
    Unchecked,

    /// <summary>An <c>AccessTools</c> member name this scanner doesn't know how to interpret. Unlike
    /// <see cref="Unchecked"/>, this is a gap in the test itself, so it fails loudly instead of being skipped.</summary>
    UnrecognizedVariant,
}

internal sealed class ResolvedAccessToolsCall
{
    public required AccessToolsCallSite Site { get; init; }

    public required string VariantName { get; init; }

    public required AccessToolsCallOutcome Outcome { get; init; }

    public TypeReference? TargetType { get; init; }

    public string? MemberName { get; init; }

    public IReadOnlyList<TypeReference>? ArgumentTypes { get; init; }

    public string? UncheckedReason { get; init; }
}

/// <summary>
/// Scans every method body in the mod assembly (including nested/compiler-generated types — lambdas, async
/// state machines, local functions are all just ordinary nested types/methods to Cecil) for
/// <c>call HarmonyLib.AccessTools::*</c>, and classifies each call site per the variant table in
/// docs/design/testing-plan.md §5 / the WP1 brief.
/// </summary>
internal static class AccessToolsScanner
{
    public static List<ResolvedAccessToolsCall> ScanModTypes(CecilContext ctx)
    {
        List<ResolvedAccessToolsCall> results = new();
        foreach (TypeDefinition type in ctx.AllModTypes())
        {
            foreach (MethodDefinition method in type.Methods.Where((m) => m.HasBody))
            {
                IlWalkResult walk = IlSymbolicWalker.Walk(method);
                foreach (AccessToolsCallSite site in walk.AccessToolsCalls)
                {
                    results.Add(Classify(site));
                }
            }
        }

        return results;
    }

    private static ResolvedAccessToolsCall Classify(AccessToolsCallSite site)
    {
        string name = site.TargetMethod.Name;
        IReadOnlyList<StackTag> args = site.Arguments;

        switch (name)
        {
            case "Field":
            case "DeclaredField":
            case "Property":
            case "PropertyGetter":
            case "PropertySetter":
            case "DeclaredProperty":
            case "Inner":
                return ClassifyTypeAndName(site, name, args, argumentTypes: null);

            case "Method":
            case "DeclaredMethod":
                return ClassifyTypeAndName(site, name, args, argumentTypes: ExtractTrailingTypeArray(args, startIndex: 2));

            case "Constructor":
            case "DeclaredConstructor":
                return ClassifyConstructor(site, name, args);

            case "TypeByName":
                return ClassifyTypeByName(site, name, args);

            case "FieldRefAccess":
            case "StaticFieldRefAccess":
                return ClassifyGenericFieldRef(site, name, args);

            default:
                return new ResolvedAccessToolsCall
                {
                    Site = site,
                    VariantName = name,
                    Outcome = AccessToolsCallOutcome.UnrecognizedVariant,
                    UncheckedReason = $"AccessTools.{name} is not a variant this scanner knows how to interpret.",
                };
        }
    }

    private static ResolvedAccessToolsCall ClassifyTypeAndName(AccessToolsCallSite site, string name, IReadOnlyList<StackTag> args, IReadOnlyList<TypeReference>? argumentTypes)
    {
        if (args.Count < 2)
        {
            return Unchecked(site, name, $"expected at least 2 arguments, found {args.Count}.");
        }

        StackTag typeArg = args[0];
        StackTag nameArg = args[1];
        TypeReference? knownType = typeArg.Kind == TagKind.TypeLiteral ? typeArg.TypeRef : null;
        string? knownName = nameArg.Kind == TagKind.StringLiteral ? nameArg.StringValue : null;

        if (knownType == null)
        {
            return Unchecked(site, name, $"the declaring-type argument is not a literal 'typeof(...)' (got {Describe(typeArg)}).", knownType, knownName);
        }

        if (knownName == null)
        {
            return Unchecked(site, name, $"the member-name argument is not a string literal (got {Describe(nameArg)}).", knownType, knownName);
        }

        return new ResolvedAccessToolsCall
        {
            Site = site,
            VariantName = name,
            Outcome = AccessToolsCallOutcome.Checked,
            TargetType = knownType,
            MemberName = knownName,
            ArgumentTypes = argumentTypes,
        };
    }

    private static ResolvedAccessToolsCall ClassifyConstructor(AccessToolsCallSite site, string name, IReadOnlyList<StackTag> args)
    {
        if (args.Count < 1)
        {
            return Unchecked(site, name, $"expected at least 1 argument, found {args.Count}.");
        }

        StackTag typeArg = args[0];
        if (typeArg.Kind != TagKind.TypeLiteral)
        {
            return Unchecked(site, name, $"the declaring-type argument is not a literal 'typeof(...)' (got {Describe(typeArg)}).", knownType: null, knownName: ".ctor");
        }

        return new ResolvedAccessToolsCall
        {
            Site = site,
            VariantName = name,
            Outcome = AccessToolsCallOutcome.Checked,
            TargetType = typeArg.TypeRef,
            MemberName = ".ctor",
            ArgumentTypes = ExtractTrailingTypeArray(args, startIndex: 1),
        };
    }

    private static ResolvedAccessToolsCall ClassifyTypeByName(AccessToolsCallSite site, string name, IReadOnlyList<StackTag> args)
    {
        if (args.Count < 1 || args[0].Kind != TagKind.StringLiteral)
        {
            return Unchecked(site, name, "the type-name argument is not a string literal.");
        }

        return new ResolvedAccessToolsCall
        {
            Site = site,
            VariantName = name,
            Outcome = AccessToolsCallOutcome.Checked,
            TargetType = null,
            MemberName = args[0].StringValue,
        };
    }

    private static ResolvedAccessToolsCall ClassifyGenericFieldRef(AccessToolsCallSite site, string name, IReadOnlyList<StackTag> args)
    {
        if (site.GenericArguments is not { Count: >= 1 } generics)
        {
            return Unchecked(site, name, "no generic type arguments on the call (non-generic overload?).");
        }

        StackTag? nameArg = args.Count > 0 ? args[^1] : null;
        if (nameArg is not { Kind: TagKind.StringLiteral })
        {
            return Unchecked(site, name, $"the field-name argument is not a string literal (got {Describe(nameArg)}).", knownType: generics[0], knownName: null);
        }

        return new ResolvedAccessToolsCall
        {
            Site = site,
            VariantName = name,
            Outcome = AccessToolsCallOutcome.Checked,
            TargetType = generics[0],
            MemberName = nameArg.StringValue,
        };
    }

    /// <summary>Extracts a fully-literal <c>Type[]</c> argument (e.g. the trailing <c>new[] { typeof(A), typeof(B) }</c>
    /// in <c>AccessTools.Method(Type, string, Type[])</c>) if present and every element resolved to a type
    /// literal; otherwise returns null, which callers treat as "no argument types to disambiguate with" rather
    /// than a failure — the name-only lookup still runs.</summary>
    private static IReadOnlyList<TypeReference>? ExtractTrailingTypeArray(IReadOnlyList<StackTag> args, int startIndex)
    {
        if (args.Count <= startIndex || args[startIndex].Kind != TagKind.ArrayLiteral || args[startIndex].ArrayElements == null)
        {
            return null;
        }

        List<StackTag?> elements = args[startIndex].ArrayElements!;
        if (elements.Any((e) => e is not { Kind: TagKind.TypeLiteral }))
        {
            return null;
        }

        return elements.Select((e) => e!.TypeRef!).ToList();
    }

    /// <summary>Builds an "unchecked" result. <paramref name="knownType"/>/<paramref name="knownName"/> carry
    /// through whichever side of the call the walker DID manage to resolve (e.g. a literal member name next to
    /// a dynamic <c>x.GetType()</c> type), so the test output shows what's actually known instead of blanking
    /// out both sides just because one of them wasn't a literal.</summary>
    private static ResolvedAccessToolsCall Unchecked(AccessToolsCallSite site, string name, string reason, TypeReference? knownType = null, string? knownName = null) => new()
    {
        Site = site,
        VariantName = name,
        Outcome = AccessToolsCallOutcome.Unchecked,
        UncheckedReason = reason,
        TargetType = knownType,
        MemberName = knownName,
    };

    private static string Describe(StackTag? tag) => tag == null
        ? "<missing argument>"
        : tag.Kind switch
        {
            TagKind.Unknown => "a dynamic/non-literal value",
            TagKind.TypeToken => "a raw type token (unexpected)",
            TagKind.IntLiteral => $"int literal {tag.IntValue}",
            TagKind.ArrayLiteral => "an array literal",
            TagKind.TupleTypeString => "a (Type, string) tuple (unexpected here)",
            _ => tag.Kind.ToString(),
        };
}
