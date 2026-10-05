using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Where the teammate's UI goes relative to the driver's: the players list on the left (names and health bars) and the
/// driver's relic row above it are left clear.
/// </summary>
internal static class CouchLayout
{
    /// <summary>The game slides the driver's relics (and top bar) away for the pause menu and similar.</summary>
    public static bool DriverRelicsHidden()
    {
        NRelicInventory? inventory = NRun.Instance?.GlobalUi?.RelicInventory;
        return inventory != null && (!inventory.IsVisibleInTree() || inventory.Position.Y < inventory.GetDefaultPosition().Y - 5f);
    }

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

    /// <summary>Room kept around an intent for its bobbing icon and number (<c>NIntent</c> bobs its holder ~18px).</summary>
    private const float IntentPadding = 12f;

    /// <summary>
    /// Screen rects of the living enemies' intents that are showing (the game fades them out during the enemy turn).
    /// </summary>
    public static IEnumerable<Rect2> EnemyIntents()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
        {
            yield break;
        }

        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || !creature.Entity.IsEnemy || !creature.Entity.IsAlive)
            {
                continue;
            }

            Control? container = creature.IntentContainer;
            if (container == null || !container.IsVisibleInTree() || container.Modulate.A < 0.05f)
            {
                continue;
            }

            foreach (Node child in container.GetChildren())
            {
                if (child is NIntent { Visible: true } intent && intent.Size.X > 0.5f && intent.Size.Y > 0.5f)
                {
                    yield return (intent.GetGlobalTransform() * new Rect2(Vector2.Zero, intent.Size)).Grow(IntentPadding);
                }
            }
        }
    }

    /// <summary>
    /// Left edge of the leftmost enemy intent that reaches into the horizontal band from <paramref name="top"/> to
    /// <paramref name="bottom"/> right of <paramref name="left"/>, or null when none does: what the teammate's hand
    /// should stop short of.
    /// </summary>
    public static float? IntentWall(float top, float bottom, float left)
    {
        float? wall = null;
        foreach (Rect2 rect in EnemyIntents())
        {
            if (rect.End.Y > top && rect.Position.Y < bottom && rect.End.X > left)
            {
                wall = Mathf.Min(wall ?? rect.Position.X, rect.Position.X);
            }
        }

        return wall;
    }
}
