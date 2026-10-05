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
/// <para>
/// On the desktop that is not enough: the window manager hands focus back to the Big Picture window behind the game,
/// and Steam Input sends the controller to whichever window has focus, so the game never sees the pad (and mutes,
/// since it is in the background). So for a short while after launch, an unfocused game window asks the X11 window
/// manager to activate it (<see cref="X11WindowActivation"/>), as opening and closing the Steam overlay would.
/// </para>
/// </summary>
internal static class GamescopeFocus
{
    /// <summary>Only right after launch, so a player who switches away on purpose later is left alone.</summary>
    private const ulong ActivationWindowMs = 120_000;

    private const ulong ActivationIntervalMs = 1_500;

    private const int MaxActivationAttempts = 5;

    private static bool _loggedOverride;

    private static int _activationAttempts;

    private static ulong _lastActivationMs;

    private static readonly bool CanActivateWindow = IsActive && !IsGamescope() && IsLinuxBigPicture();

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

        TryActivateWindow();
        return true;
    }

    /// <summary>Asks the window manager to focus the game window, a few times at most, early after launch.</summary>
    private static void TryActivateWindow()
    {
        if (!CanActivateWindow || _activationAttempts >= MaxActivationAttempts)
        {
            return;
        }

        ulong now = Time.GetTicksMsec();
        if (now > ActivationWindowMs || (_activationAttempts > 0 && now - _lastActivationMs < ActivationIntervalMs))
        {
            return;
        }

        if (DisplayServer.WindowIsFocused() || DisplayServer.GetName() != "X11")
        {
            return;
        }

        _activationAttempts++;
        _lastActivationMs = now;
        long window = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        bool sent = X11WindowActivation.TryActivate(window, out string error);
        CouchLog.Info($"Window focus: game window 0x{window:x} unfocused under Big Picture; asked the window manager to "
            + $"activate it (attempt {_activationAttempts}/{MaxActivationAttempts}{(sent ? "" : $", failed: {error}")}).");
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
