using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CouchSpire.Tests;

/// <summary>
/// Tiny synthetic-IL unit tests for <see cref="IlSymbolicWalker"/> itself (docs/design/testing-plan.md §5 / WP1
/// brief: "write a couple of unit tests for your IL-walk on tiny synthetic cases"). Each test hand-builds a
/// throwaway method body with Mono.Cecil's <see cref="ILProcessor"/> reproducing one of the IL shapes the real
/// scanners rely on, so a regression in the walker itself is caught here instead of only showing up as a mystery
/// "unchecked" entry against the real mod/game assemblies.
/// </summary>
public class IlSymbolicWalkerTests
{
    private static (ModuleDefinition Module, TypeReference FooType, TypeReference IntType, TypeReference StringType, MethodReference GetTypeFromHandle) CreateScratchModule()
    {
        AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Scratch", new Version(1, 0)), "Scratch", ModuleKind.Dll);
        ModuleDefinition module = assembly.MainModule;

        TypeReference systemType = new("System", "Type", module, module);
        MethodReference getTypeFromHandle = new("GetTypeFromHandle", systemType, systemType)
        {
            HasThis = false,
            Parameters = { new ParameterDefinition(new TypeReference("System", "RuntimeTypeHandle", module, module)) },
        };

        TypeReference fooType = new("Test", "Foo", module, module);
        TypeReference intType = new("System", "Int32", module, module);
        TypeReference stringType = module.TypeSystem.String;

        return (module, fooType, intType, stringType, getTypeFromHandle);
    }

    private static MethodReference AccessToolsMethod(ModuleDefinition module, string name, params TypeReference[] paramTypes)
    {
        TypeReference accessTools = new("HarmonyLib", "AccessTools", module, module);
        MethodReference m = new(name, module.TypeSystem.Object, accessTools) { HasThis = false };
        foreach (TypeReference p in paramTypes)
        {
            m.Parameters.Add(new ParameterDefinition(p));
        }

        return m;
    }

    private static MethodDefinition CreateHostMethod(ModuleDefinition module)
    {
        TypeDefinition host = new("Test", "Host", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(host);
        MethodDefinition method = new("Run", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        host.Methods.Add(method);
        method.Body.InitLocals = true;
        return method;
    }

    [Fact]
    public void FindsFieldCallWithLiteralTypeAndName()
    {
        (ModuleDefinition module, TypeReference fooType, _, _, MethodReference getTypeFromHandle) = CreateScratchModule();
        MethodDefinition method = CreateHostMethod(module);
        ILProcessor il = method.Body.GetILProcessor();

        // AccessTools.Field(typeof(Foo), "Bar");
        il.Emit(OpCodes.Ldtoken, fooType);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Ldstr, "Bar");
        il.Emit(OpCodes.Call, AccessToolsMethod(module, "Field", fooType, module.TypeSystem.String));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        IlWalkResult result = IlSymbolicWalker.Walk(method);

        AccessToolsCallSite site = Assert.Single(result.AccessToolsCalls);
        Assert.Equal("Field", site.TargetMethod.Name);
        Assert.Equal(2, site.Arguments.Count);
        Assert.Equal(TagKind.TypeLiteral, site.Arguments[0].Kind);
        Assert.Same(fooType, site.Arguments[0].TypeRef);
        Assert.Equal(TagKind.StringLiteral, site.Arguments[1].Kind);
        Assert.Equal("Bar", site.Arguments[1].StringValue);
    }

    [Fact]
    public void FindsMethodCallWithLiteralArgumentTypesArray()
    {
        (ModuleDefinition module, TypeReference fooType, TypeReference intType, TypeReference stringType, MethodReference getTypeFromHandle) = CreateScratchModule();
        MethodDefinition method = CreateHostMethod(module);
        ILProcessor il = method.Body.GetILProcessor();
        TypeReference systemType = new("System", "Type", module, module);

        // AccessTools.Method(typeof(Foo), "Bar", new[] { typeof(int), typeof(string) });
        il.Emit(OpCodes.Ldtoken, fooType);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Ldstr, "Bar");
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, systemType);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, intType);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldtoken, stringType);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, AccessToolsMethod(module, "Method", fooType, module.TypeSystem.String, new ArrayType(systemType)));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        IlWalkResult result = IlSymbolicWalker.Walk(method);

        AccessToolsCallSite site = Assert.Single(result.AccessToolsCalls);
        Assert.Equal("Method", site.TargetMethod.Name);
        StackTag arrayArg = site.Arguments[2];
        Assert.Equal(TagKind.ArrayLiteral, arrayArg.Kind);
        Assert.NotNull(arrayArg.ArrayElements);
        Assert.Equal(2, arrayArg.ArrayElements!.Count);
        Assert.Equal(TagKind.TypeLiteral, arrayArg.ArrayElements[0]!.Kind);
        Assert.Same(intType, arrayArg.ArrayElements[0]!.TypeRef);
        Assert.Equal(TagKind.TypeLiteral, arrayArg.ArrayElements[1]!.Kind);
        Assert.Same(stringType, arrayArg.ArrayElements[1]!.TypeRef);
    }

    [Fact]
    public void FindsCallersStyleTypeStringTupleArrayStoredToStaticField()
    {
        (ModuleDefinition module, TypeReference fooType, _, _, MethodReference getTypeFromHandle) = CreateScratchModule();
        MethodDefinition method = CreateHostMethod(module);
        ILProcessor il = method.Body.GetILProcessor();

        TypeReference valueTupleType = new("System", "ValueTuple`2", module, module);
        MethodReference tupleCtor = new(".ctor", module.TypeSystem.Void, valueTupleType) { HasThis = true };
        tupleCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        tupleCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));

        TypeDefinition owner = (TypeDefinition)method.DeclaringType;
        FieldDefinition callersField = new("Callers", FieldAttributes.Private | FieldAttributes.Static, new ArrayType(valueTupleType));
        owner.Fields.Add(callersField);

        // Callers = new[] { (typeof(Foo), "Bar") };
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, valueTupleType);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, fooType);
        il.Emit(OpCodes.Call, getTypeFromHandle);
        il.Emit(OpCodes.Ldstr, "Bar");
        il.Emit(OpCodes.Newobj, tupleCtor);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stsfld, callersField);
        il.Emit(OpCodes.Ret);

        IlWalkResult result = IlSymbolicWalker.Walk(method);

        StaticFieldStore store = Assert.Single(result.StaticFieldStores);
        Assert.Equal("Callers", store.Field.Name);
        Assert.Equal(TagKind.ArrayLiteral, store.Value.Kind);
        StackTag element = Assert.Single(store.Value.ArrayElements!)!;
        Assert.Equal(TagKind.TupleTypeString, element.Kind);
        Assert.Same(fooType, element.TupleType);
        Assert.Equal("Bar", element.TupleName);
    }

    [Fact]
    public void DynamicTypeArgumentIsUnknownNotTypeLiteral()
    {
        (ModuleDefinition module, _, _, _, _) = CreateScratchModule();
        MethodDefinition method = CreateHostMethod(module);
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        ILProcessor il = method.Body.GetILProcessor();

        MethodReference getType = new("GetType", new TypeReference("System", "Type", module, module), module.TypeSystem.Object) { HasThis = true };

        // AccessTools.Field(someObject.GetType(), "X");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, getType);
        il.Emit(OpCodes.Ldstr, "X");
        il.Emit(OpCodes.Call, AccessToolsMethod(module, "Field", module.TypeSystem.Object, module.TypeSystem.String));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        IlWalkResult result = IlSymbolicWalker.Walk(method);

        AccessToolsCallSite site = Assert.Single(result.AccessToolsCalls);
        Assert.Equal(TagKind.Unknown, site.Arguments[0].Kind);
        Assert.Equal(TagKind.StringLiteral, site.Arguments[1].Kind);
    }

    [Fact]
    public void LocalSetDifferentlyOnEachBranchIsUnknownAfterTheJoin()
    {
        // if (cond) { local = typeof(Foo); } else { local = typeof(int); }
        // AccessTools.Field(local, "x");
        //
        // Regression case: a naive walker that only clears the simulated *stack* at branch targets (but keeps
        // `locals` across them) would let whichever branch runs last in program order — here the "else" — leak
        // its type into `local`, silently pairing the wrong declaring type with the field name. Since the two
        // branches disagree, the only sound answer at the join point is "unknown".
        (ModuleDefinition module, TypeReference fooType, TypeReference intType, _, MethodReference getTypeFromHandle) = CreateScratchModule();
        MethodDefinition method = CreateHostMethod(module);
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        TypeReference systemType = new("System", "Type", module, module);
        VariableDefinition local = new(systemType);
        method.Body.Variables.Add(local);
        ILProcessor il = method.Body.GetILProcessor();

        Instruction elseBranch = Instruction.Create(OpCodes.Ldtoken, intType);
        Instruction join = Instruction.Create(OpCodes.Ldloc, local);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Brfalse_S, elseBranch));

        // if-branch: local = typeof(Foo);
        il.Append(Instruction.Create(OpCodes.Ldtoken, fooType));
        il.Append(Instruction.Create(OpCodes.Call, getTypeFromHandle));
        il.Append(Instruction.Create(OpCodes.Stloc, local));
        il.Append(Instruction.Create(OpCodes.Br_S, join));

        // else-branch: local = typeof(int);
        il.Append(elseBranch);
        il.Append(Instruction.Create(OpCodes.Call, getTypeFromHandle));
        il.Append(Instruction.Create(OpCodes.Stloc, local));

        // join: AccessTools.Field(local, "x");
        il.Append(join);
        il.Append(Instruction.Create(OpCodes.Ldstr, "x"));
        il.Append(Instruction.Create(OpCodes.Call, AccessToolsMethod(module, "Field", systemType, module.TypeSystem.String)));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ret));

        IlWalkResult result = IlSymbolicWalker.Walk(method);

        AccessToolsCallSite site = Assert.Single(result.AccessToolsCalls);
        Assert.Equal(TagKind.Unknown, site.Arguments[0].Kind);
        Assert.Equal(TagKind.StringLiteral, site.Arguments[1].Kind);
        Assert.Equal("x", site.Arguments[1].StringValue);
    }
}
