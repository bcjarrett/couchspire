using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// Where the teammate's UI goes relative to the driver's: the players list on the left (names and health bars) and the
/// driver's relic row above it are left clear.
/// </summary>
internal static class CouchLayout
{
    /// <summary>Screen rect covered by the players list's visible entries, if it's showing.</summary>
    public static Rect2? PlayersList()
    {
        Control? list = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (list == null || !GodotObject.IsInstanceValid(list) || !list.IsVisibleInTree())
        {
            return null;
        }

        // The container itself may be stretched; its entries are what's drawn.
        Rect2? covered = null;
        foreach (Node child in list.GetChildren())
        {
            if (child is Control { Visible: true } entry && entry.Size.X > 0f)
            {
                Rect2 rect = entry.GetGlobalRect();
                covered = covered?.Merge(rect) ?? rect;
            }
        }

        return covered;
    }

    /// <summary><paramref name="top"/>, or just below the players list for panels on the left.</summary>
    public static float BelowPlayersList(float top)
    {
        return PlayersList() is Rect2 list ? Mathf.Max(top, list.End.Y + 14f) : top;
    }

    /// <summary><paramref name="left"/>, or just right of the players list.</summary>
    public static float RightOfPlayersList(float left)
    {
        return PlayersList() is Rect2 list ? Mathf.Max(left, list.End.X + 24f) : left;
    }
}
