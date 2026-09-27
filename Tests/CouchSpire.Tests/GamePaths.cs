using System.Reflection;

namespace CouchSpire.Tests;

/// <summary>
/// Reads the game/mod file paths the build passed in via <c>AssemblyMetadata</c> MSBuild items (see
/// CouchSpire.Tests.csproj), instead of hardcoding absolute paths in source.
/// </summary>
internal static class GamePaths
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Metadata = new(ReadMetadata);

    /// <summary>Directory containing sts2.dll, GodotSharp.dll, 0Harmony.dll, etc. (<c>$(Sts2DataDir)</c>).</summary>
    public static string Sts2DataDir => GetRequired("Sts2DataDir");

    /// <summary>Path to the mod DLL built by the main project (<c>CouchSpire.dll</c> at the repo root).</summary>
    public static string ModDllPath => GetRequired("ModDllPath");

    public static string Sts2DllPath => Path.Combine(Sts2DataDir, "sts2.dll");

    private static string GetRequired(string key)
    {
        if (!Metadata.Value.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Assembly metadata '{key}' is missing or empty. Expected it to come from CouchSpire.Tests.csproj's " +
                "<AssemblyMetadata> items (backed by Sts2Paths.props). Rebuild via 'dotnet test Tests/CouchSpire.Tests'.");
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata()
    {
        Dictionary<string, string> result = new();
        foreach (AssemblyMetadataAttribute attribute in typeof(GamePaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key != null && attribute.Value != null)
            {
                result[attribute.Key] = attribute.Value;
            }
        }

        return result;
    }
}
