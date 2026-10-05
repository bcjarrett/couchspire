using System.Runtime.InteropServices;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Asks the X11 window manager to activate (raise and focus) a window by sending the EWMH <c>_NET_ACTIVE_WINDOW</c>
/// request to the root window, as <c>wmctrl -a</c> does. Godot's <c>WindowMoveToForeground</c> sends the same request
/// marked as coming from the application itself, which window managers may refuse as focus stealing; this one is marked
/// as coming from a pager (source indication 2), which they honor.
/// </summary>
internal static class X11WindowActivation
{
    private const string LibX11 = "libX11.so.6";

    private const int ClientMessage = 33;

    private const long SubstructureNotifyMask = 1L << 19;

    private const long SubstructureRedirectMask = 1L << 20;

    private const long SourceIndicationPager = 2;

    /// <summary>sizeof(XEvent) on 64-bit Linux; XSendEvent reads a whole XEvent.</summary>
    private const int XEventSize = 192;

    /// <summary>Sends the activation request; returns false (with a reason) if it could not be sent.</summary>
    public static bool TryActivate(long window, out string error)
    {
        IntPtr display = IntPtr.Zero;
        IntPtr xEvent = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                error = "XOpenDisplay failed";
                return false;
            }

            ulong root = XDefaultRootWindow(display);
            ulong activeWindowAtom = XInternAtom(display, "_NET_ACTIVE_WINDOW", 0);

            // XClientMessageEvent: type, serial, send_event, display, window, message_type, format, data.l[5].
            xEvent = Marshal.AllocHGlobal(XEventSize);
            for (int offset = 0; offset < XEventSize; offset += 8)
            {
                Marshal.WriteInt64(xEvent, offset, 0);
            }

            Marshal.WriteInt32(xEvent, 0, ClientMessage);
            Marshal.WriteInt32(xEvent, 16, 1);
            Marshal.WriteIntPtr(xEvent, 24, display);
            Marshal.WriteInt64(xEvent, 32, window);
            Marshal.WriteInt64(xEvent, 40, (long)activeWindowAtom);
            Marshal.WriteInt32(xEvent, 48, 32);
            Marshal.WriteInt64(xEvent, 56, SourceIndicationPager);
            // data.l[1] = 0 (CurrentTime), data.l[2] = 0 (no currently active window of ours).

            int sent = XSendEvent(display, root, 0, SubstructureRedirectMask | SubstructureNotifyMask, xEvent);
            XFlush(display);
            if (sent == 0)
            {
                error = "XSendEvent failed";
                return false;
            }

            error = "";
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            if (xEvent != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(xEvent);
            }

            if (display != IntPtr.Zero)
            {
                XCloseDisplay(display);
            }
        }
    }

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern ulong XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern ulong XInternAtom(IntPtr display, string atomName, int onlyIfExists);

    [DllImport(LibX11)]
    private static extern int XSendEvent(IntPtr display, ulong window, int propagate, long eventMask, IntPtr eventSend);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);
}
