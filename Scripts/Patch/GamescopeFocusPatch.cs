using System.Reflection;
using HarmonyLib;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Under gamescope, sends the game's input focus checks to <see cref="GamescopeFocus.IsGameFocusedWindow"/>, so the
/// controller works from launch. <see cref="NGame.IsGameFocusedWindow"/> is small enough for the JIT to inline into its
/// callers, where a patch on it wouldn't run, so the call is swapped inside each caller instead. Not applied outside
/// gamescope.
/// </summary>
[HarmonyPatch]
internal static class GamescopeFocusPatch
{
    private static readonly MethodInfo? OriginalCheck = AccessTools.Method(typeof(NGame), nameof(NGame.IsGameFocusedWindow));

    private static readonly MethodInfo? ReplacementCheck = AccessTools.Method(typeof(GamescopeFocus), nameof(GamescopeFocus.IsGameFocusedWindow));

    // v0.107.1 has no NControllerManager.CheckForArrowKeyInput or NInputManager.ProcessFkbInput (dropped, not
    // renamed: no other method picked up their IsGameFocusedWindow check), and NInputManager's hotkey check is
    // named ProcessShortcutKeyInput here rather than ProcessHotkeyInput.
    private static readonly (Type Type, string Method)[] Callers =
    {
        (typeof(NControllerManager), "_Process"),
        (typeof(NControllerManager), "CheckForControllerInput"),
        (typeof(NInputManager), "_UnhandledInput"),
        (typeof(NInputManager), "ProcessShortcutKeyInput"),
        (typeof(NHotkeyManager), "_UnhandledInput")
    };

    private static bool _loggedEnvironment;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        if (!_loggedEnvironment)
        {
            _loggedEnvironment = true;
            CouchLog.Info($"Window focus: {GamescopeFocus.Describe()}.");
        }

        return GamescopeFocus.IsActive && OriginalCheck != null && ReplacementCheck != null;
    }

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach ((Type type, string name) in Callers)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(type, name);
            if (method == null)
            {
                CouchLog.Warn($"Window focus fix: {type.Name}.{name} not found; its focus check is left as is.");
                continue;
            }

            yield return method;
        }
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> TranspileFocusCheck(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(OriginalCheck!))
            {
                instruction.operand = ReplacementCheck;
                replaced++;
            }

            yield return instruction;
        }

        if (replaced == 0)
        {
            CouchLog.Warn($"Window focus fix: no focus check found in {original.DeclaringType?.Name}.{original.Name}.");
        }
    }
}
