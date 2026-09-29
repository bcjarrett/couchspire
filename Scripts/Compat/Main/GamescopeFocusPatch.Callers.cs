using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace CouchSpire.Scripts.Patch;

internal static partial class GamescopeFocusPatch
{
    /// <summary>Game methods that call <c>NGame.IsGameFocusedWindow</c> on the main branch.</summary>
    private static readonly (Type Type, string Method)[] Callers =
    {
        (typeof(NControllerManager), "_Process"),
        (typeof(NControllerManager), "CheckForControllerInput"),
        (typeof(NInputManager), "_UnhandledInput"),
        (typeof(NInputManager), "ProcessShortcutKeyInput"),
        (typeof(NHotkeyManager), "_UnhandledInput")
    };
}
