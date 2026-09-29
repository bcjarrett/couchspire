using Mono.Cecil;

namespace CouchSpire.Tests;

internal enum PatchMethodKind
{
    Normal,
    Getter,
    Setter,
    Constructor,
    StaticConstructor,
    Enumerator,
}

/// <summary>
/// A resolved-so-far description of what a <c>[HarmonyPatch(...)]</c> attribute (or a merge of several) targets.
/// Mirrors the fields Harmony's own <c>HarmonyMethod</c> carries; null means "not specified at this level".
/// </summary>
internal sealed class PatchTargetSpec
{
    public TypeReference? TargetType { get; init; }

    public string? MethodName { get; init; }

    public IReadOnlyList<TypeReference>? ArgumentTypes { get; init; }

    public PatchMethodKind? MethodKind { get; init; }

    /// <summary>True for the <c>[HarmonyTargetMethods]</c> <c>Callers</c>-table special case, where the mod
    /// itself calls <c>AccessTools.DeclaredMethod</c> — i.e. no base-type walk.</summary>
    public bool DeclaredOnly { get; init; }

    public static readonly PatchTargetSpec Empty = new();

    /// <summary>Merges this spec (the base/class-level info) with <paramref name="overlay"/> (method-level info),
    /// per Harmony's rule: values the overlay specifies win, values it leaves out fall back to this spec's.</summary>
    public PatchTargetSpec MergeWith(PatchTargetSpec overlay)
    {
        return new PatchTargetSpec
        {
            TargetType = overlay.TargetType ?? TargetType,
            MethodName = overlay.MethodName ?? MethodName,
            ArgumentTypes = overlay.ArgumentTypes ?? ArgumentTypes,
            MethodKind = overlay.MethodKind ?? MethodKind,
            DeclaredOnly = overlay.DeclaredOnly || DeclaredOnly,
        };
    }
}

internal static class HarmonyAttributeParser
{
    public const string HarmonyPatchAttributeFullName = "HarmonyLib.HarmonyPatch";

    public static bool IsHarmonyPatchAttribute(CustomAttribute attribute) => attribute.AttributeType.FullName == HarmonyPatchAttributeFullName;

    /// <summary>Parses one <c>[HarmonyPatch(...)]</c> attribute instance by the shape of its constructor
    /// arguments (which constructor overload the compiler picked), not by trying to enumerate every overload
    /// HarmonyLib ships. Unknown shapes throw rather than being silently skipped/ignored, per the work package
    /// spec ("unknown forms must fail loudly").</summary>
    public static PatchTargetSpec Parse(CustomAttribute attribute)
    {
        IList<CustomAttributeArgument> args = attribute.ConstructorArguments;
        string where = DescribeLocation(attribute);

        switch (args.Count)
        {
            case 0:
                return PatchTargetSpec.Empty;

            case 1 when IsType(args[0]):
                return new PatchTargetSpec { TargetType = AsType(args[0]) };

            case 1 when IsString(args[0]):
                return new PatchTargetSpec { MethodName = AsString(args[0]) };

            case 2 when IsType(args[0]) && IsString(args[1]):
                return new PatchTargetSpec { TargetType = AsType(args[0]), MethodName = AsString(args[1]) };

            case 2 when IsType(args[0]) && IsMethodType(args[1]):
                return new PatchTargetSpec { TargetType = AsType(args[0]), MethodKind = AsMethodKind(args[1]) };

            case 2 when IsString(args[0]) && IsMethodType(args[1]):
                return new PatchTargetSpec { MethodName = AsString(args[0]), MethodKind = AsMethodKind(args[1]) };

            case 2 when IsType(args[0]) && IsTypeArray(args[1]):
                return new PatchTargetSpec { TargetType = AsType(args[0]), ArgumentTypes = AsTypeArray(args[1]) };

            case 3 when IsType(args[0]) && IsString(args[1]) && IsTypeArray(args[2]):
                return new PatchTargetSpec { TargetType = AsType(args[0]), MethodName = AsString(args[1]), ArgumentTypes = AsTypeArray(args[2]) };

            case 3 when IsType(args[0]) && IsString(args[1]) && IsMethodType(args[2]):
                return new PatchTargetSpec { TargetType = AsType(args[0]), MethodName = AsString(args[1]), MethodKind = AsMethodKind(args[2]) };

            case 3 when IsType(args[0]) && IsMethodType(args[1]) && IsTypeArray(args[2]):
                return new PatchTargetSpec { TargetType = AsType(args[0]), MethodKind = AsMethodKind(args[1]), ArgumentTypes = AsTypeArray(args[2]) };

            default:
                throw new NotSupportedException(
                    $"Unrecognized [HarmonyPatch] constructor shape at {where}: " +
                    string.Join(", ", args.Select((a) => a.Type.FullName)) +
                    ". Add support for this form to HarmonyAttributeParser.Parse instead of skipping it.");
        }
    }

    private static bool IsType(CustomAttributeArgument arg) => arg.Type.FullName == "System.Type";

    private static bool IsString(CustomAttributeArgument arg) => arg.Type.FullName == "System.String";

    private static bool IsTypeArray(CustomAttributeArgument arg) => arg.Type.FullName == "System.Type[]";

    private static bool IsMethodType(CustomAttributeArgument arg) => arg.Type.FullName == "HarmonyLib.MethodType";

    private static TypeReference AsType(CustomAttributeArgument arg) => (TypeReference)arg.Value;

    private static string AsString(CustomAttributeArgument arg) => (string)arg.Value;

    private static IReadOnlyList<TypeReference> AsTypeArray(CustomAttributeArgument arg)
    {
        return ((CustomAttributeArgument[])arg.Value).Select((a) => (TypeReference)a.Value).ToList();
    }

    private static PatchMethodKind AsMethodKind(CustomAttributeArgument arg)
    {
        TypeDefinition? enumType = MemberResolver.ResolveType(arg.Type);
        object rawValue = arg.Value;
        if (enumType != null)
        {
            FieldDefinition? literal = enumType.Fields.FirstOrDefault((f) => f.IsStatic && f.HasConstant && Equals(Convert.ChangeType(f.Constant, rawValue.GetType()), rawValue));
            if (literal != null && Enum.TryParse(literal.Name, out PatchMethodKind parsed))
            {
                return parsed;
            }
        }

        // Fall back to HarmonyLib's known MethodType ordinal order if the enum couldn't be resolved/mapped.
        int ordinal = Convert.ToInt32(rawValue);
        PatchMethodKind[] knownOrder = { PatchMethodKind.Normal, PatchMethodKind.Getter, PatchMethodKind.Setter, PatchMethodKind.Constructor, PatchMethodKind.StaticConstructor, PatchMethodKind.Enumerator };
        if (ordinal >= 0 && ordinal < knownOrder.Length)
        {
            return knownOrder[ordinal];
        }

        throw new NotSupportedException($"Could not map HarmonyLib.MethodType value '{rawValue}' to a known MethodType member.");
    }

    private static string DescribeLocation(CustomAttribute attribute) => $"attribute on constructor {attribute.Constructor.FullName}";
}
