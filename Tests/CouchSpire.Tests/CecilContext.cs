using Mono.Cecil;

namespace CouchSpire.Tests;

/// <summary>
/// Loads the mod DLL and the game's sts2.dll with Mono.Cecil (never <c>Assembly.Load</c> — the mod needs Godot
/// at runtime, so it is only ever read as metadata). Shared across all tests in the assembly via
/// <see cref="CecilFixture"/> / <see cref="ICollectionFixture{T}"/>.
/// </summary>
internal sealed class CecilContext : IDisposable
{
    public CecilContext()
    {
        DefaultAssemblyResolver resolver = new();
        resolver.AddSearchDirectory(GamePaths.Sts2DataDir);
        // Base-type walks can reach into the plain .NET runtime (e.g. System.Object); give the resolver a shot
        // at the runtime directory too so those don't dead-end the walk.
        string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            resolver.AddSearchDirectory(runtimeDir);
        }

        Resolver = resolver;

        ReaderParameters readerParameters = new() { AssemblyResolver = resolver, InMemory = true };

        if (!File.Exists(GamePaths.ModDllPath))
        {
            throw new InvalidOperationException(
                $"Mod DLL not found at '{GamePaths.ModDllPath}'. Build CouchSpire.csproj first " +
                "(dotnet test Tests/CouchSpire.Tests builds it automatically via a ProjectReference).");
        }

        if (!File.Exists(GamePaths.Sts2DllPath))
        {
            throw new InvalidOperationException(
                $"Game assembly not found at '{GamePaths.Sts2DllPath}'. Check Sts2Paths.props / -p:Sts2Dir=...");
        }

        ModAssembly = AssemblyDefinition.ReadAssembly(GamePaths.ModDllPath, readerParameters);
        GameAssembly = AssemblyDefinition.ReadAssembly(GamePaths.Sts2DllPath, readerParameters);
    }

    public DefaultAssemblyResolver Resolver { get; }

    public AssemblyDefinition ModAssembly { get; }

    public AssemblyDefinition GameAssembly { get; }

    /// <summary>Every type defined in the mod module, including nested types (compiler-generated lambda/async
    /// state-machine/local-function types are ordinary nested types to Cecil, so this alone covers them).</summary>
    public IEnumerable<TypeDefinition> AllModTypes()
    {
        return AllTypesRecursive(ModAssembly.MainModule.Types);
    }

    private static IEnumerable<TypeDefinition> AllTypesRecursive(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypesRecursive(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    public void Dispose()
    {
        ModAssembly.Dispose();
        GameAssembly.Dispose();
        Resolver.Dispose();
    }
}

/// <summary>xunit collection fixture wrapper so <see cref="CecilContext"/> is created once per test run.</summary>
public sealed class CecilFixture : IDisposable
{
    public CecilFixture()
    {
        Context = new CecilContext();
    }

    internal CecilContext Context { get; }

    public void Dispose() => Context.Dispose();
}

[Xunit.CollectionDefinition(Name)]
public sealed class CecilCollection : Xunit.ICollectionFixture<CecilFixture>
{
    public const string Name = "Cecil assemblies";
}
