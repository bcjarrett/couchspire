using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The game ignores all controller input while <see cref="NGame.IsGameFocusedWindow"/> is false. Under gamescope the
/// window can come up without ever being told it has focus: it opens small while mods load, then switches to
/// fullscreen, and the focus-in gets lost. The controller then does nothing until the player opens the Steam menu and
/// comes back. Gamescope only shows the game when it is the active app, so while the Steam overlay is closed the game
/// really is in front; <see cref="IsGameFocusedWindow"/> says so, and <c>GamescopeFocusPatch</c> routes the game's
/// focus checks through it.
/// </summary>
internal static class GamescopeFocus
{
    private static bool _loggedOverride;

    /// <summary>Running under gamescope with the fix enabled.</summary>
    public static readonly bool IsActive = CouchConfig.GamescopeFocusFix && IsGamescope();

    public static string Describe()
    {
        return $"gamescope={(IsGamescope() ? "yes" : "no")} (GAMESCOPE_WAYLAND_DISPLAY={Env("GAMESCOPE_WAYLAND_DISPLAY")}, "
            + $"XDG_CURRENT_DESKTOP={Env("XDG_CURRENT_DESKTOP")}), fix {(CouchConfig.GamescopeFocusFix ? "enabled" : "disabled")}";
    }

    /// <summary>Replacement for <see cref="NGame.IsGameFocusedWindow"/> in the game's input paths.</summary>
    public static bool IsGameFocusedWindow()
    {
        if (NGame.IsGameFocusedWindow())
        {
            return true;
        }

        if (!IsActive || PlatformUtil.IsPlatformOverlayOpen())
        {
            return false;
        }

        if (!_loggedOverride)
        {
            _loggedOverride = true;
            CouchLog.Info($"Window reports no focus under gamescope (WindowIsFocused={DisplayServer.WindowIsFocused()}); "
                + "treating it as focused so controller input works.");
        }

        return true;
    }

    private static bool IsGamescope()
    {
        return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"))
            || string.Equals(System.Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);
    }

    private static string Env(string name)
    {
        return System.Environment.GetEnvironmentVariable(name) ?? "unset";
    }
}
