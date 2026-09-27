using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CouchSpire.Tests;

internal enum TagKind
{
    Unknown,
    TypeToken,
    TypeLiteral,
    StringLiteral,
    IntLiteral,
    ArrayLiteral,
    TupleTypeString,
}

/// <summary>A symbolic value on our simulated evaluation stack. Most instructions just push <see cref="Unknown"/>
/// (we don't care what they compute); the handful of kinds below are the ones Test 2's IL walk needs to trace a
/// <c>typeof(T)</c> / string literal / <c>new[] { ... }</c> / <c>(Type, string)</c> tuple back to an
/// <c>AccessTools</c> call.</summary>
internal sealed class StackTag
{
    public TagKind Kind { get; private init; }

    public TypeReference? TypeRef { get; private init; }

    public string? StringValue { get; private init; }

    public int IntValue { get; private init; }

    public List<StackTag?>? ArrayElements { get; private init; }

    public TypeReference? TupleType { get; private init; }

    public string? TupleName { get; private init; }

    private static readonly StackTag UnknownInstance = new() { Kind = TagKind.Unknown };

    public static StackTag Unknown() => UnknownInstance;

    public static StackTag TypeToken(TypeReference type) => new() { Kind = TagKind.TypeToken, TypeRef = type };

    public static StackTag TypeLiteral(TypeReference type) => new() { Kind = TagKind.TypeLiteral, TypeRef = type };

    public static StackTag String(string value) => new() { Kind = TagKind.StringLiteral, StringValue = value };

    public static StackTag Int(int value) => new() { Kind = TagKind.IntLiteral, IntValue = value };

    public static StackTag Array(List<StackTag?> elements, TypeReference elementType) => new() { Kind = TagKind.ArrayLiteral, ArrayElements = elements, TypeRef = elementType };

    public static StackTag Tuple(TypeReference type, string name) => new() { Kind = TagKind.TupleTypeString, TupleType = type, TupleName = name };
}

/// <summary>One <c>call</c>/<c>callvirt</c> to <c>HarmonyLib.AccessTools::*</c>, with its arguments resolved as
/// far as the symbolic walk could trace them (a non-<see cref="TagKind.Unknown"/> tag means we know the literal
/// value; <see cref="TagKind.Unknown"/> means the call site goes on the "unchecked" list).</summary>
internal sealed class AccessToolsCallSite
{
    public required MethodDefinition ContainingMethod { get; init; }

    public required Instruction Instruction { get; init; }

    public required MethodReference TargetMethod { get; init; }

    public required IReadOnlyList<StackTag> Arguments { get; init; }

    public IReadOnlyList<TypeReference>? GenericArguments { get; init; }
}

/// <summary>One <c>stsfld</c> encountered during the walk, with the symbolic value stored. Used by Test 1's
/// <c>[HarmonyTargetMethods]</c> special case (§5) to pick out the <c>(Type, string)[]</c> table a patch class
/// builds in its static constructor, without hardcoding the field's name.</summary>
internal sealed class StaticFieldStore
{
    public required MethodDefinition ContainingMethod { get; init; }

    public required FieldReference Field { get; init; }

    public required StackTag Value { get; init; }
}

internal sealed class IlWalkResult
{
    public List<AccessToolsCallSite> AccessToolsCalls { get; } = new();

    public List<StaticFieldStore> StaticFieldStores { get; } = new();
}

/// <summary>
/// A best-effort symbolic evaluation-stack simulator over one method body's IL. It exists to answer one
/// question per <c>AccessTools</c> call site: "what literal <c>Type</c>/<c>string</c> values were pushed as its
/// arguments, if any?" It is intentionally not a full CIL interpreter: control flow is handled by clearing both
/// the simulated stack and the tracked locals at every merge point — a branch target or an exception-handler
/// entry (see <see cref="CollectMergePoints"/>) — which is exact for the straight-line patterns this mod
/// actually uses (field initializers, simple statements) and merely conservative (more "Unknown" tags, never
/// wrong ones) for anything more exotic, e.g. a local assigned a different literal on each side of a branch.
/// </summary>
internal static class IlSymbolicWalker
{
    public static IlWalkResult Walk(MethodDefinition method)
    {
        IlWalkResult result = new();
        if (!method.HasBody)
        {
            return result;
        }

        MethodBody body = method.Body;
        HashSet<Instruction> mergePoints = CollectMergePoints(body);

        Stack<StackTag> stack = new();
        Dictionary<int, StackTag> locals = new();

        bool first = true;
        foreach (Instruction instr in body.Instructions)
        {
            if (!first && mergePoints.Contains(instr))
            {
                // A merge point can be reached from more than one predecessor (another branch, a fallthrough, or
                // an exception unwind), so nothing we tracked coming into it can be trusted: clear the whole
                // simulated state instead of just the stack, otherwise a local set differently on each incoming
                // path (or a value pushed by a different predecessor) could leak a stale, wrongly-paired tag.
                stack.Clear();
                locals.Clear();
            }

            first = false;

            Process(instr, method, stack, locals, result);

            if (IsBranchFamily(instr.OpCode))
            {
                stack.Clear();
            }
        }

        return result;
    }

    /// <summary>Branch targets (the operand of every <c>br</c>/<c>brtrue</c>/.../<c>switch</c> instruction) plus
    /// exception-handler entry points (try/handler/filter starts) — anywhere control can arrive at from more
    /// than one place, or from an arbitrary point mid-block via an exception. Tracked by instruction identity,
    /// not <see cref="Instruction.Offset"/>: offsets are only populated once a method body has been laid out for
    /// serialization, so a freshly built in-memory body (as the synthetic IL-walk tests build) has every
    /// instruction at offset 0 — matching by reference is correct for both real, file-loaded bodies and
    /// synthetic ones.</summary>
    private static HashSet<Instruction> CollectMergePoints(MethodBody body)
    {
        HashSet<Instruction> mergePoints = new();
        foreach (Instruction ins in body.Instructions)
        {
            switch (ins.Operand)
            {
                case Instruction single:
                    mergePoints.Add(single);
                    break;
                case Instruction[] many:
                    foreach (Instruction t in many)
                    {
                        mergePoints.Add(t);
                    }

                    break;
            }
        }

        foreach (ExceptionHandler handler in body.ExceptionHandlers)
        {
            if (handler.TryStart != null)
            {
                mergePoints.Add(handler.TryStart);
            }

            if (handler.HandlerStart != null)
            {
                mergePoints.Add(handler.HandlerStart);
            }

            if (handler.FilterStart != null)
            {
                mergePoints.Add(handler.FilterStart);
            }
        }

        return mergePoints;
    }

    private static bool IsBranchFamily(OpCode op) =>
        op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw;

    private static void Process(Instruction instr, MethodDefinition method, Stack<StackTag> stack, Dictionary<int, StackTag> locals, IlWalkResult result)
    {
        switch (instr.OpCode.Code)
        {
            case Code.Nop:
            case Code.Break:
                break;

            case Code.Ldstr:
                stack.Push(StackTag.String((string)instr.Operand));
                break;

            case Code.Ldtoken:
                stack.Push(instr.Operand is TypeReference typeOperand ? StackTag.TypeToken(typeOperand) : StackTag.Unknown());
                break;

            case Code.Ldc_I4:
                stack.Push(StackTag.Int((int)instr.Operand));
                break;
            case Code.Ldc_I4_S:
                stack.Push(StackTag.Int((sbyte)instr.Operand));
                break;
            case Code.Ldc_I4_M1:
                stack.Push(StackTag.Int(-1));
                break;
            case Code.Ldc_I4_0:
                stack.Push(StackTag.Int(0));
                break;
            case Code.Ldc_I4_1:
                stack.Push(StackTag.Int(1));
                break;
            case Code.Ldc_I4_2:
                stack.Push(StackTag.Int(2));
                break;
            case Code.Ldc_I4_3:
                stack.Push(StackTag.Int(3));
                break;
            case Code.Ldc_I4_4:
                stack.Push(StackTag.Int(4));
                break;
            case Code.Ldc_I4_5:
                stack.Push(StackTag.Int(5));
                break;
            case Code.Ldc_I4_6:
                stack.Push(StackTag.Int(6));
                break;
            case Code.Ldc_I4_7:
                stack.Push(StackTag.Int(7));
                break;
            case Code.Ldc_I4_8:
                stack.Push(StackTag.Int(8));
                break;

            case Code.Dup:
                {
                    StackTag t = Pop(stack);
                    stack.Push(t);
                    stack.Push(t);
                    break;
                }

            case Code.Pop:
                Pop(stack);
                break;

            case Code.Newarr:
                {
                    StackTag lengthTag = Pop(stack);
                    List<StackTag?> elements = new();
                    if (lengthTag.Kind == TagKind.IntLiteral)
                    {
                        for (int i = 0; i < lengthTag.IntValue; i++)
                        {
                            elements.Add(null);
                        }
                    }

                    stack.Push(StackTag.Array(elements, (TypeReference)instr.Operand));
                    break;
                }

            case Code.Stelem_Ref:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
            case Code.Stelem_I:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_Any:
                {
                    StackTag value = Pop(stack);
                    StackTag index = Pop(stack);
                    StackTag array = Pop(stack);
                    if (array.Kind == TagKind.ArrayLiteral && index.Kind == TagKind.IntLiteral && array.ArrayElements != null)
                    {
                        while (array.ArrayElements.Count <= index.IntValue)
                        {
                            array.ArrayElements.Add(null);
                        }

                        array.ArrayElements[index.IntValue] = value;
                    }

                    break;
                }

            case Code.Ldelem_Ref:
            case Code.Ldelem_I4:
            case Code.Ldelem_I8:
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
            case Code.Ldelem_I:
            case Code.Ldelem_I1:
            case Code.Ldelem_I2:
            case Code.Ldelem_U1:
            case Code.Ldelem_U2:
            case Code.Ldelem_U4:
            case Code.Ldelem_Any:
                {
                    StackTag index = Pop(stack);
                    StackTag array = Pop(stack);
                    if (array.Kind == TagKind.ArrayLiteral && index.Kind == TagKind.IntLiteral && array.ArrayElements != null && index.IntValue < array.ArrayElements.Count)
                    {
                        stack.Push(array.ArrayElements[index.IntValue] ?? StackTag.Unknown());
                    }
                    else
                    {
                        stack.Push(StackTag.Unknown());
                    }

                    break;
                }

            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldloc_S:
            case Code.Ldloc:
                stack.Push(locals.TryGetValue(GetLocalIndex(instr), out StackTag? existing) ? existing : StackTag.Unknown());
                break;

            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Stloc_S:
            case Code.Stloc:
                locals[GetLocalIndex(instr)] = Pop(stack);
                break;

            case Code.Castclass:
            case Code.Isinst:
            case Code.Unbox_Any:
            case Code.Box:
            case Code.Unbox:
                {
                    StackTag t = Pop(stack);
                    stack.Push(t);
                    break;
                }

            case Code.Call:
            case Code.Callvirt:
                HandleCall((MethodReference)instr.Operand, method, instr, stack, result);
                break;

            case Code.Newobj:
                HandleNewobj((MethodReference)instr.Operand, stack);
                break;

            case Code.Stsfld:
                {
                    FieldReference field = (FieldReference)instr.Operand;
                    StackTag value = Pop(stack);
                    result.StaticFieldStores.Add(new StaticFieldStore { ContainingMethod = method, Field = field, Value = value });
                    break;
                }

            default:
                {
                    int pop = GenericPopCount(instr);
                    int push = GenericPushCount(instr);
                    for (int i = 0; i < pop; i++)
                    {
                        Pop(stack);
                    }

                    for (int i = 0; i < push; i++)
                    {
                        stack.Push(StackTag.Unknown());
                    }

                    break;
                }
        }
    }

    private static void HandleCall(MethodReference mref, MethodDefinition containingMethod, Instruction instr, Stack<StackTag> stack, IlWalkResult result)
    {
        IReadOnlyList<TypeReference>? genericArgs = mref is GenericInstanceMethod gim ? gim.GenericArguments.ToList() : null;

        int paramCount = mref.Parameters.Count;
        int popTotal = paramCount + (mref.HasThis ? 1 : 0);
        List<StackTag> popped = PopN(stack, popTotal);
        List<StackTag> args = mref.HasThis ? popped.Skip(1).ToList() : popped;
        bool returnsValue = mref.ReturnType.FullName != "System.Void";

        if (mref.DeclaringType.FullName == "System.Type" && mref.Name == "GetTypeFromHandle" && args.Count == 1)
        {
            StackTag handle = args[0];
            stack.Push(handle.Kind == TagKind.TypeToken ? StackTag.TypeLiteral(handle.TypeRef!) : StackTag.Unknown());
            return;
        }

        if (mref.DeclaringType.FullName == "HarmonyLib.AccessTools")
        {
            result.AccessToolsCalls.Add(new AccessToolsCallSite
            {
                ContainingMethod = containingMethod,
                Instruction = instr,
                TargetMethod = mref,
                Arguments = args,
                GenericArguments = genericArgs,
            });
        }

        if (returnsValue)
        {
            stack.Push(StackTag.Unknown());
        }
    }

    private static void HandleNewobj(MethodReference ctorRef, Stack<StackTag> stack)
    {
        int paramCount = ctorRef.Parameters.Count;
        List<StackTag> args = PopN(stack, paramCount);

        bool isTypeStringTuple = args.Count == 2 &&
            ctorRef.DeclaringType.Namespace == "System" &&
            (ctorRef.DeclaringType.Name == "ValueTuple`2" || ctorRef.DeclaringType.Name == "Tuple`2") &&
            args[0].Kind == TagKind.TypeLiteral && args[1].Kind == TagKind.StringLiteral;

        stack.Push(isTypeStringTuple ? StackTag.Tuple(args[0].TypeRef!, args[1].StringValue!) : StackTag.Unknown());
    }

    private static StackTag Pop(Stack<StackTag> stack) => stack.Count > 0 ? stack.Pop() : StackTag.Unknown();

    private static List<StackTag> PopN(Stack<StackTag> stack, int n)
    {
        List<StackTag> values = new(n);
        for (int i = 0; i < n; i++)
        {
            values.Add(Pop(stack));
        }

        values.Reverse();
        return values;
    }

    private static int GetLocalIndex(Instruction instr)
    {
        return instr.OpCode.Code switch
        {
            Code.Ldloc_0 or Code.Stloc_0 => 0,
            Code.Ldloc_1 or Code.Stloc_1 => 1,
            Code.Ldloc_2 or Code.Stloc_2 => 2,
            Code.Ldloc_3 or Code.Stloc_3 => 3,
            _ => ((VariableDefinition)instr.Operand).Index,
        };
    }

    private static int GenericPopCount(Instruction instr)
    {
        return instr.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            StackBehaviour.PopAll => 0,
            StackBehaviour.Varpop => 0, // Call/Callvirt/Newobj are handled before reaching here; remaining users (Ret) end the block anyway.
            _ => 0,
        };
    }

    private static int GenericPushCount(Instruction instr)
    {
        return instr.OpCode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Varpush => 0, // Call/Callvirt/Newobj are handled before reaching here.
            _ => 0,
        };
    }
}
