using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The game ignores all controller input while <see cref="NGame.IsGameFocusedWindow"/> is false. Under gamescope the
/// window can come up without ever being told it has focus: it opens small while mods load, then switches to
/// fullscreen, and the focus-in gets lost. The controller then does nothing until the player opens the Steam menu and
/// comes back. The same happens on a Linux desktop (e.g. Bazzite's GNOME session) when the game is launched from Steam
/// Big Picture: the window goes fullscreen over Big Picture, gets a focus-out, and never the focus-in. Gamescope only
/// shows the game when it is the active app, and from Big Picture the game is the fullscreen app on the TV, so while the
/// Steam overlay is closed the game really is in front; <see cref="IsGameFocusedWindow"/> says so, and
/// <c>GamescopeFocusPatch</c> routes the game's focus checks through it.
/// </summary>
internal static class GamescopeFocus
{
    private static bool _loggedOverride;

    /// <summary>Running under gamescope, or from Big Picture on Linux, with the fix enabled.</summary>
    public static readonly bool IsActive = CouchConfig.GamescopeFocusFix && (IsGamescope() || IsLinuxBigPicture());

    public static string Describe()
    {
        return $"gamescope={(IsGamescope() ? "yes" : "no")} (GAMESCOPE_WAYLAND_DISPLAY={Env("GAMESCOPE_WAYLAND_DISPLAY")}, "
            + $"XDG_CURRENT_DESKTOP={Env("XDG_CURRENT_DESKTOP")}), "
            + $"Linux Big Picture={(IsLinuxBigPicture() ? "yes" : "no")} (SteamGamepadUI={Env("SteamGamepadUI")}), "
            + $"fix {(CouchConfig.GamescopeFocusFix ? "enabled" : "disabled")}, {(IsActive ? "applied" : "not applied")}";
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
            CouchLog.Info($"Window reports no focus under gamescope/Big Picture (WindowIsFocused={DisplayServer.WindowIsFocused()}); "
                + "treating it as focused so controller input works.");
        }

        return true;
    }

    private static bool IsGamescope()
    {
        return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"))
            || string.Equals(System.Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Steam sets <c>SteamGamepadUI=1</c> for games launched from Big Picture (and from Game Mode).</summary>
    private static bool IsLinuxBigPicture()
    {
        return OperatingSystem.IsLinux() && System.Environment.GetEnvironmentVariable("SteamGamepadUI") == "1";
    }

    private static string Env(string name)
    {
        return System.Environment.GetEnvironmentVariable(name) ?? "unset";
    }
}
