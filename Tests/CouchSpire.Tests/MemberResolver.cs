using Mono.Cecil;

namespace CouchSpire.Tests;

internal enum ResolveOutcome
{
    Found,
    NotFound,
    Ambiguous,
    TypeNotResolved,
}

internal sealed record ResolveResult(ResolveOutcome Outcome, string Detail)
{
    public bool Success => Outcome == ResolveOutcome.Found;

    public static ResolveResult Found() => new(ResolveOutcome.Found, string.Empty);
}

/// <summary>
/// Resolves Harmony-style string-named lookups (<c>[HarmonyPatch]</c> targets and <c>AccessTools.*</c> calls)
/// against the game assemblies, walking base types the way Harmony's own <c>AccessTools</c> does: search the
/// exact type first (declared members only), and only continue to the base type if nothing by that name was
/// declared there. A name match at a given level that turns out ambiguous (multiple same-named candidates,
/// no way to disambiguate) is reported as a failure, mirroring the <see cref="System.Reflection.AmbiguousMatchException"/>
/// that plain reflection (and Harmony) would throw at runtime.
/// </summary>
internal static class MemberResolver
{
    public static TypeDefinition? ResolveType(TypeReference? reference)
    {
        if (reference == null)
        {
            return null;
        }

        try
        {
            return reference.Resolve();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<TypeDefinition> WalkTypeHierarchy(TypeDefinition start, bool declaredOnly)
    {
        TypeDefinition? current = start;
        bool first = true;
        while (current != null)
        {
            yield return current;
            if (declaredOnly && !first)
            {
                yield break;
            }

            first = false;
            current = current.BaseType == null ? null : ResolveType(current.BaseType);
        }
    }

    private static bool TypeRefEquals(TypeReference a, TypeReference b) => a.FullName == b.FullName;

    public static ResolveResult FindField(TypeDefinition startType, string name, bool declaredOnly)
    {
        foreach (TypeDefinition level in WalkTypeHierarchy(startType, declaredOnly))
        {
            List<FieldDefinition> matches = level.Fields.Where((f) => f.Name == name).ToList();
            if (matches.Count == 1)
            {
                return ResolveResult.Found();
            }

            if (matches.Count > 1)
            {
                return new ResolveResult(ResolveOutcome.Ambiguous, $"{matches.Count} fields named '{name}' declared on {level.FullName}.");
            }
        }

        return new ResolveResult(ResolveOutcome.NotFound, $"no field '{name}' found on {startType.FullName} or its base types.");
    }

    public static ResolveResult FindMethod(TypeDefinition startType, string name, IReadOnlyList<TypeReference>? argTypes, bool declaredOnly)
    {
        foreach (TypeDefinition level in WalkTypeHierarchy(startType, declaredOnly))
        {
            List<MethodDefinition> named = level.Methods.Where((m) => m.Name == name).ToList();
            if (named.Count == 0)
            {
                continue;
            }

            if (argTypes == null)
            {
                if (named.Count == 1)
                {
                    return ResolveResult.Found();
                }

                return new ResolveResult(
                    ResolveOutcome.Ambiguous,
                    $"{named.Count} overloads named '{name}' declared on {level.FullName}, and no argument types were given to disambiguate.");
            }

            List<MethodDefinition> signatureMatches = named
                .Where((m) => m.Parameters.Count == argTypes.Count && m.Parameters
                    .Select((p) => p.ParameterType)
                    .Zip(argTypes, TypeRefEquals)
                    .All((match) => match))
                .ToList();
            if (signatureMatches.Count == 1)
            {
                return ResolveResult.Found();
            }

            if (signatureMatches.Count > 1)
            {
                return new ResolveResult(ResolveOutcome.Ambiguous, $"{signatureMatches.Count} methods named '{name}' matched the given argument types on {level.FullName}.");
            }

            return new ResolveResult(
                ResolveOutcome.NotFound,
                $"'{name}'({string.Join(", ", argTypes.Select((t) => t.FullName))}) not found on {level.FullName}; " +
                $"{named.Count} overload(s) named '{name}' exist there with different signatures: " +
                string.Join(" | ", named.Select((m) => $"({string.Join(", ", m.Parameters.Select((p) => p.ParameterType.FullName))})")));
        }

        return new ResolveResult(ResolveOutcome.NotFound, $"no method '{name}' found on {startType.FullName} or its base types.");
    }

    public static ResolveResult FindConstructor(TypeDefinition startType, IReadOnlyList<TypeReference>? argTypes, bool declaredOnly)
    {
        // Constructors are never inherited; a base-type walk would be meaningless (and wrong) here.
        _ = declaredOnly;
        List<MethodDefinition> ctors = startType.Methods.Where((m) => m.IsConstructor && !m.IsStatic).ToList();
        if (argTypes == null)
        {
            if (ctors.Count == 1)
            {
                return ResolveResult.Found();
            }

            if (ctors.Count > 1)
            {
                return new ResolveResult(ResolveOutcome.Ambiguous, $"{ctors.Count} constructors declared on {startType.FullName}, and no argument types were given to disambiguate.");
            }

            return new ResolveResult(ResolveOutcome.NotFound, $"no constructor found on {startType.FullName}.");
        }

        List<MethodDefinition> matches = ctors
            .Where((m) => m.Parameters.Count == argTypes.Count && m.Parameters
                .Select((p) => p.ParameterType)
                .Zip(argTypes, TypeRefEquals)
                .All((match) => match))
            .ToList();
        if (matches.Count == 1)
        {
            return ResolveResult.Found();
        }

        if (matches.Count > 1)
        {
            return new ResolveResult(ResolveOutcome.Ambiguous, $"{matches.Count} constructors matched the given argument types on {startType.FullName}.");
        }

        return new ResolveResult(
            ResolveOutcome.NotFound,
            $"constructor({string.Join(", ", argTypes.Select((t) => t.FullName))}) not found on {startType.FullName}; " +
            $"{ctors.Count} constructor(s) exist there with different signatures.");
    }

    public enum PropertyAccessor
    {
        Either,
        Getter,
        Setter,
    }

    public static ResolveResult FindProperty(TypeDefinition startType, string name, bool declaredOnly, PropertyAccessor accessor)
    {
        foreach (TypeDefinition level in WalkTypeHierarchy(startType, declaredOnly))
        {
            List<PropertyDefinition> named = level.Properties.Where((p) => p.Name == name).ToList();
            if (named.Count == 0)
            {
                continue;
            }

            List<PropertyDefinition> withAccessor = accessor switch
            {
                PropertyAccessor.Getter => named.Where((p) => p.GetMethod != null).ToList(),
                PropertyAccessor.Setter => named.Where((p) => p.SetMethod != null).ToList(),
                _ => named,
            };

            if (withAccessor.Count == 1)
            {
                return ResolveResult.Found();
            }

            if (withAccessor.Count > 1)
            {
                return new ResolveResult(ResolveOutcome.Ambiguous, $"{withAccessor.Count} properties named '{name}' declared on {level.FullName}.");
            }

            string accessorWord = accessor switch { PropertyAccessor.Getter => "a getter", PropertyAccessor.Setter => "a setter", _ => "an accessor" };
            return new ResolveResult(ResolveOutcome.NotFound, $"property '{name}' exists on {level.FullName} but has no {accessorWord}.");
        }

        return new ResolveResult(ResolveOutcome.NotFound, $"no property '{name}' found on {startType.FullName} or its base types.");
    }

    public static ResolveResult FindInner(TypeDefinition startType, string name, bool declaredOnly)
    {
        foreach (TypeDefinition level in WalkTypeHierarchy(startType, declaredOnly))
        {
            List<TypeDefinition> matches = level.NestedTypes.Where((t) => t.Name == name).ToList();
            if (matches.Count == 1)
            {
                return ResolveResult.Found();
            }

            if (matches.Count > 1)
            {
                return new ResolveResult(ResolveOutcome.Ambiguous, $"{matches.Count} nested types named '{name}' declared on {level.FullName}.");
            }
        }

        return new ResolveResult(ResolveOutcome.NotFound, $"no nested type '{name}' found on {startType.FullName} or its base types.");
    }
}
