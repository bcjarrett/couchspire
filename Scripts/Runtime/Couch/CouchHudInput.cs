using Godot;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Maps teammate inputs to <see cref="CouchHudCommand"/>s. Controller inputs arrive with the names the router uses:
/// Steam Input action names ("Select", "Left", "Joy_Left"...), raw Godot joypad buttons ("btn0"...), or the game's
/// raw analog actions ("raw_l_stick_left"...). The keyboard block is J/L move, I select, K back, O confirm (discard in
/// the potion row), P end turn, U potions.
/// </summary>
internal static class CouchHudInput
{
    public static bool TryMapController(string input, out CouchHudCommand command)
    {
        switch (input)
        {
            case "Left":
            case "Joy_Left":
            case "btn13":
            case "raw_l_stick_left":
                command = CouchHudCommand.Left;
                return true;
            case "Right":
            case "Joy_Right":
            case "btn14":
            case "raw_l_stick_right":
                command = CouchHudCommand.Right;
                return true;
            case "Up":
            case "Joy_Up":
            case "btn11":
            case "raw_l_stick_up":
                command = CouchHudCommand.Up;
                return true;
            case "Down":
            case "Joy_Down":
            case "btn12":
            case "raw_l_stick_down":
                command = CouchHudCommand.Down;
                return true;
            // A / Cross: the game's "Select".
            case "Select":
            case "btn0":
                command = CouchHudCommand.Accept;
                return true;
            // B / Circle: the game's "Cancel".
            case "Cancel":
            case "btn1":
                command = CouchHudCommand.Back;
                return true;
            // X / Square: the game's "Top_Panel". Confirms a multi-card choice; discards a potion.
            case "Top_Panel":
            case "btn2":
                command = CouchHudCommand.Submit;
                return true;
            // View / Back / Select: the game's "View_Map". Opens the teammate's deck/relics view.
            case "View_Map":
            case "btn4":
                command = CouchHudCommand.Info;
                return true;
            // LB / RB: the game's "Tab_Left" (the driver's view deck) and "Tab_Right".
            case "Tab_Left":
            case "btn9":
                command = CouchHudCommand.TabLeft;
                return true;
            case "Tab_Right":
            case "btn10":
                command = CouchHudCommand.TabRight;
                return true;
            // Y / Triangle: the game's "Confirm" ("Proceed / End Turn").
            case "Confirm":
            case "btn3":
                command = CouchHudCommand.SubmitOrEndTurn;
                return true;
            default:
                command = default;
                return false;
        }
    }

    /// <summary>
    /// Stick clicks on the teammate's controller swap who drives the main screen (L3 is the game's "Peek" action; R3 only
    /// reaches the game through raw joypad input). An emergency hatch for anything the teammate panels can't do.
    /// </summary>
    public static bool IsBreakGlass(string input)
    {
        return input is "Peek" or "btn7" or "btn8";
    }

    public static bool TryMapKey(Key key, out CouchHudCommand command)
    {
        switch (key)
        {
            case Key.J:
                command = CouchHudCommand.Left;
                return true;
            case Key.L:
                command = CouchHudCommand.Right;
                return true;
            case Key.I:
                command = CouchHudCommand.Accept;
                return true;
            case Key.K:
                command = CouchHudCommand.Back;
                return true;
            case Key.O:
                command = CouchHudCommand.Submit;
                return true;
            case Key.P:
                command = CouchHudCommand.EndTurn;
                return true;
            case Key.U:
                command = CouchHudCommand.ToggleRow;
                return true;
            case Key.V:
                command = CouchHudCommand.Info;
                return true;
            default:
                command = default;
                return false;
        }
    }
}
