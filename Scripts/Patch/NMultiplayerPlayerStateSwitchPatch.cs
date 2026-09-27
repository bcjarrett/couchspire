using MegaCrit.Sts2.Core.Combat;
using Godot;
using HarmonyLib;
using LocalMultiControl.Scripts.Runtime;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Patch;

[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
internal static class NMultiplayerPlayerStateReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMultiplayerPlayerState __instance)
    {
        LocalMultiplayerPlayerStateSwitchUi.Ensure(__instance);
    }
}

internal static class LocalMultiplayerPlayerStateSwitchUi
{
    private const string RightClickMetaKey = "LocalSwitchPlayerRightClickBound";
    private const string TrackerName = "LocalSwitchPlayerTracker";

    public static void Ensure(NMultiplayerPlayerState state)
    {
        BindRightClick(state);
        EnsureTracker(state);
        Refresh(state);
    }

    public static void Refresh(NMultiplayerPlayerState state)
    {
        if (LocalSelfCoopContext.IsEnabled && RunManager.Instance.IsInProgress)
        {
            RefreshCombatTrackerState(state);
        }
    }

    private static void EnsureTracker(NMultiplayerPlayerState state)
    {
        if (state.GetNodeOrNull<LocalPlayerStateSwitchTracker>(TrackerName) != null)
        {
            return;
        }

        LocalPlayerStateSwitchTracker tracker = new()
        {
            Name = TrackerName
        };
        tracker.Initialize(state);
        state.AddChild(tracker);
    }

    private static void BindRightClick(NMultiplayerPlayerState state)
    {
        if (state.Hitbox.HasMeta(RightClickMetaKey))
        {
            return;
        }

        state.Hitbox.SetMeta(RightClickMetaKey, true);
        state.Hitbox.Connect(
            NClickableControl.SignalName.MouseReleased,
            Callable.From<InputEvent>((inputEvent) => OnHitboxMouseReleased(state, inputEvent)));
    }

    private static void OnHitboxMouseReleased(NMultiplayerPlayerState state, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton mouseButton ||
            mouseButton.ButtonIndex != MouseButton.Right ||
            mouseButton.IsPressed())
        {
            return;
        }

        TrySwitchToPlayer(state.Player, "player-state-right-click");
    }

    private static void TrySwitchToPlayer(Player player, string source)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        if (!LocalControlSwitchGuard.TrySwitchTo(player.NetId, source))
        {
            return;
        }

        TreasureRoomRelicSynchronizer? treasureSynchronizer = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        if (treasureSynchronizer == null || treasureSynchronizer.CurrentRelics == null)
        {
            return;
        }

        _ = TreasureRoomRelicSynchronizerPatch.TryAutoSwitchToNextUnpickedPlayer(
            treasureSynchronizer,
            player.NetId,
            "treasure-skip-picked-player");
    }

    private static void RefreshCombatTrackerState(NMultiplayerPlayerState state)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        Control? energyContainer = AccessTools.Field(typeof(NMultiplayerPlayerState), "_energyContainer")?.GetValue(state) as Control;
        Control? starContainer = AccessTools.Field(typeof(NMultiplayerPlayerState), "_starContainer")?.GetValue(state) as Control;
        Control? cardContainer = AccessTools.Field(typeof(NMultiplayerPlayerState), "_cardContainer")?.GetValue(state) as Control;
        if (energyContainer == null || starContainer == null || cardContainer == null || state.Player.PlayerCombatState == null)
        {
            return;
        }

        bool shouldShowCombatInfo = CombatManager.Instance.IsInProgress;
        if (!shouldShowCombatInfo)
        {
            energyContainer.Visible = false;
            starContainer.Visible = false;
            cardContainer.Visible = false;
            return;
        }

        energyContainer.Visible = true;
        cardContainer.Visible = true;
        starContainer.Visible = state.Player.Character is Regent || state.Player.PlayerCombatState.Stars > 0;
        AccessTools.Method(typeof(NMultiplayerPlayerState), "RefreshCombatValues")?.Invoke(state, Array.Empty<object>());
        AccessTools.Method(typeof(NMultiplayerPlayerState), "UpdateSelectionReticleWidth")?.Invoke(state, Array.Empty<object>());
    }

}

internal sealed partial class LocalPlayerStateSwitchTracker : Node
{
    private NMultiplayerPlayerState? _state;

    public void Initialize(NMultiplayerPlayerState state)
    {
        _state = state;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_state == null || !GodotObject.IsInstanceValid(_state))
        {
            QueueFree();
            return;
        }

        LocalMultiplayerPlayerStateSwitchUi.Refresh(_state);
    }
}
