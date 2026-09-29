using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using CouchSpire.Scripts.Patch;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using Godot;

namespace CouchSpire.Scripts.Runtime;

internal static class LocalControlRuntime
{
    private static readonly LocalMultiSessionState Session = new LocalMultiSessionState();

    private static readonly HashSet<string> _fieldSyncFailures = new HashSet<string>();
    private static readonly HashSet<string> _autoEndIssued = new HashSet<string>();
    private static readonly HashSet<int> _allPlayersAutoEndedRounds = new HashSet<int>();
    private static readonly Dictionary<string, int> _flowBlockSignalCounts = new Dictionary<string, int>();
    private static readonly HashSet<string> _flowBlockSignalDedupeRoundPlayer = new HashSet<string>();
    private static int _lastAutoEndCombatIdentity = -1;
    private static Vector2? _combatEnergyContainerDefaultPosition;
    private static long _flowBlockSignalWindowStartMs;
    private static ulong? _pendingManualEndTurnPlayerId;
    private static int _pendingManualEndTurnRound = -1;

    public static LocalMultiSessionState SessionState => Session;

    public static void OnRunLaunched(RunState runState)
    {
        ModLog.Info("Detected RunManager.Launch, starting to initialize local co-op session.");
        if (LocalSelfCoopContext.IsEnabled)
        {
            RunManager.Instance.CombatStateSynchronizer.IsDisabled = true;
            ModLog.Info("Local co-op mode has disabled combat sync waiting to avoid single-process loopback blocking.");
        }

        Session.InitializeFromRunState(runState);
        if (Session.CurrentControlledPlayerId.HasValue)
        {
            ApplyControlContext("run-launched");
        }
        else
        {
            ModLog.Info("The current run has not enabled a local co-op session.");
        }
    }

    public static void OnRunCleanup()
    {
        Session.Reset("RunManager.CleanUp");
        _autoEndIssued.Clear();
        _allPlayersAutoEndedRounds.Clear();
        _lastAutoEndCombatIdentity = -1;
        _pendingManualEndTurnPlayerId = null;
        _pendingManualEndTurnRound = -1;
        _flowBlockSignalCounts.Clear();
        _flowBlockSignalDedupeRoundPlayer.Clear();
        _flowBlockSignalWindowStartMs = 0L;
        LocalMerchantInventoryRuntime.Clear();
        CouchTeammateChoices.ClearAll("RunManager.CleanUp");
        LocalSelfCoopContext.Disable("RunManager.CleanUp");
        ModLog.Info("Local co-op session cleanup completed after RunManager.CleanUp.");
    }

    public static void SwitchNextControlledPlayer(string source)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return;
        }

        if (CombatManager.Instance.IsInProgress)
        {
            // Risk: during combat, blindly switching by "session order" may switch to a player not present in the current CombatState,
            // which then triggers a mismatch between the hand UI and the action queue owner, manifesting as "cannot play cards / switched to an empty player".
            if (!CanSwitchDuringCombat(source))
            {
                return;
            }

            if (TrySwitchCombatPlayer(next: true, source))
            {
                return;
            }
        }

        if (Session.SwitchNextPlayer())
        {
            ApplyControlContext(source);
        }
    }

    public static void SwitchControlledPlayerTo(ulong playerId, string source)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return;
        }

        if (Session.TrySetCurrentPlayer(playerId))
        {
            ApplyControlContext(source);
        }
    }

    public static void TryRunPendingEventAutoSwitch(string source)
    {
        if (!LocalSelfCoopContext.TryConsumePendingEventAutoSwitch())
        {
            return;
        }

        // Couch simultaneous mode: the driver keeps the screen after events.
        if (Couch.CouchConfig.SimultaneousEnabled)
        {
            Couch.CouchLog.Info($"Skipped the base mod's event auto-switch ({source}); the driver keeps the screen.");
            return;
        }

        SwitchNextControlledPlayer(source);
    }

    public static bool TryManualEndTurnAutoCloseAllPlayers()
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress || !CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            return false;
        }

        NPlayerHand hand = combatUi.Hand;
        if (hand.InCardPlay || hand.IsInCardSelection || (NTargetManager.Instance?.IsInSelection ?? false))
        {
            return false;
        }

        CombatState? combatState = TryGetCombatState(combatUi);
        if (combatState == null || combatState.CurrentSide != CombatSide.Player)
        {
            return false;
        }

        if (RunManager.Instance.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase)
        {
            return false;
        }

        RefreshAutoEndTrackingForCombat(combatState);

        foreach (Player player in combatState.Players)
        {
            if (player?.Creature == null || !player.Creature.IsAlive)
            {
                continue;
            }

            bool hasPlayableCards = PileType.Hand.GetPile(player).Cards.Any((card) => card.CanPlay());
            if (hasPlayableCards)
            {
                ModLog.Info($"Detected a player who can still play cards while another player manually ended their turn; not triggering an all-players end: round={combatState.RoundNumber}");
                return false;
            }
        }

        return TryEndAllPlayersWhenNoCards(combatState, "manual-end-turn");
    }

    private static bool TryEndAllPlayersWhenNoCards(CombatState combatState, string source)
    {
        if (_allPlayersAutoEndedRounds.Contains(combatState.RoundNumber))
        {
            return false;
        }

        bool endedAnyPlayer = false;
        foreach (Player player in combatState.Players)
        {
            if (player?.Creature == null || !player.Creature.IsAlive)
            {
                continue;
            }

            if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            {
                continue;
            }

            string key = $"{_lastAutoEndCombatIdentity}:{combatState.RoundNumber}:{player.NetId}";
            if (!_autoEndIssued.Add(key))
            {
                continue;
            }

            MegaCrit.Sts2.Core.Commands.PlayerCmd.EndTurn(player, canBackOut: false);
            endedAnyPlayer = true;
        }

        if (endedAnyPlayer)
        {
            _allPlayersAutoEndedRounds.Add(combatState.RoundNumber);
            ModLog.Info($"Detected that no player can play any cards; automatically ended all players' turns: round={combatState.RoundNumber}, source={source}");
        }

        return endedAnyPlayer;
    }

    private static void RefreshAutoEndTrackingForCombat(CombatState combatState)
    {
        int combatIdentity = RuntimeHelpers.GetHashCode(combatState);
        if (_lastAutoEndCombatIdentity == combatIdentity)
        {
            return;
        }

        _lastAutoEndCombatIdentity = combatIdentity;
        _autoEndIssued.Clear();
        _allPlayersAutoEndedRounds.Clear();
        _pendingManualEndTurnPlayerId = null;
        _pendingManualEndTurnRound = -1;
        ModLog.Info($"New combat; auto end-turn tracking reset: combat={combatIdentity}");
    }

    public static void RecordManualEndTurnIntent(ulong playerId, string source)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        CombatState? combatState = combatUi != null ? TryGetCombatState(combatUi) : null;
        _pendingManualEndTurnPlayerId = playerId;
        _pendingManualEndTurnRound = combatState?.RoundNumber ?? -1;
        ModLog.Info($"Recorded manual end-turn intent: player={playerId}, round={_pendingManualEndTurnRound}, source={source}");
    }

    private static void ApplyControlContext(string source)
    {
        ulong? currentControlledPlayerId = Session.CurrentControlledPlayerId;
        if (!currentControlledPlayerId.HasValue)
        {
            return;
        }

        if (CombatManager.Instance.IsInProgress)
        {
            NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
            CombatState? combatState = combatUi != null ? TryGetCombatState(combatUi) : null;
            if (combatState != null && combatState.GetPlayer(currentControlledPlayerId.Value) == null)
            {
                ModLog.Warn($"Detected an invalid combat player ID, falling back to slot 1: {currentControlledPlayerId.Value}");
                ulong fallbackPlayerId = Session.OrderedPlayerIds.FirstOrDefault();
                if (fallbackPlayerId != 0 && Session.TrySetCurrentPlayer(fallbackPlayerId))
                {
                    currentControlledPlayerId = Session.CurrentControlledPlayerId;
                }

                if (!currentControlledPlayerId.HasValue || combatState.GetPlayer(currentControlledPlayerId.Value) == null)
                {
                    return;
                }
            }
        }

        ulong? previousNetId = LocalContext.NetId;
        LocalContext.NetId = currentControlledPlayerId.Value;
        LocalSelfCoopContext.NetService?.SetCurrentSenderId(currentControlledPlayerId.Value);
        SyncRunSynchronizerLocalPlayerId(currentControlledPlayerId.Value);

        bool combatUiRefreshSucceeded = RefreshCombatUiForControlledPlayer(currentControlledPlayerId.Value);
        if (CombatManager.Instance.IsInProgress && !combatUiRefreshSucceeded)
        {
            // Risk: if LocalContext has already switched but the combat UI refresh fails, the "logical owner" and "displayed owner" become separated.
            // That state would enqueue subsequent card plays to the wrong player's queue, so we must roll back the context immediately here.
            ModLog.Warn($"Control context switch rolled back: combat UI refresh failed, target={currentControlledPlayerId.Value}");
            LocalContext.NetId = previousNetId;
            if (previousNetId.HasValue)
            {
                LocalSelfCoopContext.NetService?.SetCurrentSenderId(previousNetId.Value);
                SyncRunSynchronizerLocalPlayerId(previousNetId.Value);
            }

            return;
        }

        RefreshTopBarForControlledPlayer(currentControlledPlayerId.Value);
        RefreshDeckViewForControlledPlayer(currentControlledPlayerId.Value);
        RefreshRestSiteForControlledPlayer(currentControlledPlayerId.Value);
        RefreshEventRoomForControlledPlayer(currentControlledPlayerId.Value);
        LocalMerchantInventoryRuntime.RefreshShopRoomForPlayer(currentControlledPlayerId.Value);
        EnsureTreasureCursorVisibleAfterSwitch(source);
        ModLog.Info($"Control context updated: {previousNetId?.ToString() ?? "null"} -> {currentControlledPlayerId.Value}, source={source}");
        if (source != "run-launched")
        {
            string slotLabel = LocalSelfCoopContext.GetSlotLabel(currentControlledPlayerId.Value);
            NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create(LocalModText.ControlledSlot(slotLabel)));
        }
    }

    private static void SyncRunSynchronizerLocalPlayerId(ulong playerId)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return;
        }

        ulong eventOwnerPlayerId = LocalSelfCoopContext.UseSingleEventFlow
            ? LocalSelfCoopContext.PrimaryPlayerId
            : playerId;
        TrySetLocalPlayerId(RunManager.Instance.EventSynchronizer, eventOwnerPlayerId, nameof(RunManager.EventSynchronizer));
        TrySetLocalPlayerId(RunManager.Instance.RewardsSetSynchronizer, playerId, nameof(RunManager.RewardsSetSynchronizer));
        TrySetLocalPlayerId(RunManager.Instance.RewardSynchronizer, playerId, nameof(RunManager.RewardSynchronizer));
        TrySetLocalPlayerId(RunManager.Instance.RestSiteSynchronizer, playerId, nameof(RunManager.RestSiteSynchronizer));
        TrySetLocalPlayerId(RunManager.Instance.OneOffSynchronizer, playerId, nameof(RunManager.OneOffSynchronizer));
        TrySetLocalPlayerId(RunManager.Instance.TreasureRoomRelicSynchronizer, playerId, nameof(RunManager.TreasureRoomRelicSynchronizer));
        TrySetLocalPlayerId(RunManager.Instance.FlavorSynchronizer, playerId, nameof(RunManager.FlavorSynchronizer));
    }

    public static void AlignContextForActionOwner(ulong playerId, string source)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return;
        }

        if (LocalContext.NetId == playerId)
        {
            SyncRunSynchronizerLocalPlayerId(playerId);
            return;
        }

        ulong? previousNetId = LocalContext.NetId;
        LocalContext.NetId = playerId;
        LocalSelfCoopContext.NetService?.SetCurrentSenderId(playerId);
        SyncRunSynchronizerLocalPlayerId(playerId);
        ModLog.Warn(
            $"Detected manual card-play context drift, forcibly corrected: {previousNetId?.ToString() ?? "null"} -> {playerId}, source={source}");
    }

    private static void TrySetLocalPlayerId(object? target, ulong playerId, string componentName)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            AccessTools.Field(target.GetType(), "_localPlayerId")?.SetValue(target, playerId);
        }
        catch (Exception exception)
        {
            string key = $"{componentName}:{target.GetType().Name}";
            if (_fieldSyncFailures.Add(key))
            {
                ModLog.Warn($"Failed to sync _localPlayerId for {key}: {exception.Message}");
            }
        }
    }

    public static void TryAutoSwitchAfterEndTurn(ulong endedPlayerId)
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress || !CombatManager.Instance.IsInProgress)
        {
            return;
        }

        // Couch simultaneous combat: the driver keeps the screen for the whole combat.
        if (CouchTeammate.DriverKeepsScreen)
        {
            return;
        }

        bool matchedManualEndTurn = TryConsumeManualEndTurnIntent(endedPlayerId);
        if (!matchedManualEndTurn && Session.CurrentControlledPlayerId != endedPlayerId)
        {
            ModLog.Info(
                $"Skipping auto-switch after end turn: ended={endedPlayerId}, controlled={Session.CurrentControlledPlayerId?.ToString() ?? "null"}, manualMatched={matchedManualEndTurn}");
            return;
        }

        if (CombatManager.Instance.AllPlayersReadyToEndTurn())
        {
            ModLog.Info("All players have ended their turn; skipping auto-switch and waiting for the enemy turn to proceed.");
            return;
        }

        ModLog.Info($"Detected player {endedPlayerId} ending their turn; automatically switching to the next player.");
        Callable.From(delegate
        {
            if (TrySwitchToNextPlayablePlayer(endedPlayerId, "auto-end-turn-next-playable"))
            {
                return;
            }

            NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
            CombatState? combatState = combatUi != null ? TryGetCombatState(combatUi) : null;
            if (combatState != null)
            {
                TryEndAllPlayersWhenNoCards(combatState, "auto-end-turn-fallback");
            }
        }).CallDeferred();
    }

    private static bool CanSwitchDuringCombat(string source)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            ModLog.Info($"Ignoring switch request ({source}): combat UI not ready.");
            return false;
        }

        NPlayerHand hand = combatUi.Hand;
        if (hand.InCardPlay || hand.IsInCardSelection || (NTargetManager.Instance?.IsInSelection ?? false))
        {
            // Risk: switching during card dragging, target selection, or card selection UI would interrupt the NCardPlay/selection context mid-flight,
            // which easily triggers an NMouseCardPlay._ExitTree null reference and puts the action queue into a cancel-all state.
            ModLog.Info($"Ignoring switch request ({source}): a card-play/card-selection operation is currently in progress.");
            return false;
        }

        ActionSynchronizerCombatState combatSyncState = RunManager.Instance.ActionQueueSynchronizer.CombatState;
        if (combatSyncState != ActionSynchronizerCombatState.PlayPhase)
        {
            // Risk: switching owner outside of PlayPhase causes actions to be delayed/rejected from the queue, resulting in "card presses doing nothing".
            ModLog.Info($"Ignoring switch request ({source}): combat sync phase={combatSyncState}.");
            return false;
        }

        CombatState? combatState = TryGetCombatState(combatUi);
        if (combatState == null || combatState.CurrentSide != CombatSide.Player)
        {
            ModLog.Info($"Ignoring switch request ({source}): not currently in the player's card-play phase.");
            return false;
        }

        return true;
    }

    private static bool TrySwitchCombatPlayer(bool next, string source)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            return false;
        }

        CombatState? combatState = TryGetCombatState(combatUi);
        if (combatState == null)
        {
            return false;
        }

        List<ulong> combatPlayerIds = combatState.Players.Select((player) => player.NetId).Distinct().ToList();
        if (combatPlayerIds.Count < 2)
        {
            // Risk: the current implementation only guarantees "two-player local co-op"; continuing to switch with an unexpected player count could cause unpredictable owner binding.
            ModLog.Warn($"Combat player switch requires at least 2 players, current count={combatPlayerIds.Count}");
            return false;
        }

        ulong currentPlayerId = Session.CurrentControlledPlayerId ?? LocalContext.NetId ?? combatPlayerIds[0];
        int currentIndex = combatPlayerIds.IndexOf(currentPlayerId);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int targetIndex = (currentIndex + (next ? 1 : combatPlayerIds.Count - 1)) % combatPlayerIds.Count;
        ulong targetPlayerId = combatPlayerIds[targetIndex];
        if (targetPlayerId == currentPlayerId)
        {
            return false;
        }

        if (!Session.TrySetCurrentPlayer(targetPlayerId))
        {
            return false;
        }

        ApplyControlContext(source);
        return true;
    }

    private static bool TrySwitchToNextPlayablePlayer(ulong currentPlayerId, string source)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            return false;
        }

        CombatState? combatState = TryGetCombatState(combatUi);
        if (combatState == null)
        {
            return false;
        }

        List<ulong> combatPlayerIds = combatState.Players.Select((player) => player.NetId).Distinct().ToList();
        if (combatPlayerIds.Count < 2)
        {
            return false;
        }

        int currentIndex = combatPlayerIds.IndexOf(currentPlayerId);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        for (int offset = 1; offset < combatPlayerIds.Count; offset++)
        {
            int targetIndex = (currentIndex + offset) % combatPlayerIds.Count;
            ulong targetPlayerId = combatPlayerIds[targetIndex];
            Player? targetPlayer = combatState.GetPlayer(targetPlayerId);
            if (targetPlayer?.Creature == null || !targetPlayer.Creature.IsAlive)
            {
                continue;
            }

            if (CombatManager.Instance.IsPlayerReadyToEndTurn(targetPlayer))
            {
                continue;
            }

            bool hasPlayableCards = PileType.Hand.GetPile(targetPlayer).Cards.Any((card) => card.CanPlay());
            if (!hasPlayableCards)
            {
                continue;
            }

            if (!Session.TrySetCurrentPlayer(targetPlayerId))
            {
                return false;
            }

            ApplyControlContext(source);
            ModLog.Info($"Switched to the next player who can play cards after ending turn: {currentPlayerId} -> {targetPlayerId}");
            return true;
        }

        return false;
    }

    private static bool TryConsumeManualEndTurnIntent(ulong endedPlayerId)
    {
        if (!_pendingManualEndTurnPlayerId.HasValue)
        {
            return false;
        }

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        CombatState? combatState = combatUi != null ? TryGetCombatState(combatUi) : null;
        int round = combatState?.RoundNumber ?? -1;
        bool roundMatches = _pendingManualEndTurnRound < 0 || _pendingManualEndTurnRound == round;
        bool matched = roundMatches && _pendingManualEndTurnPlayerId.Value == endedPlayerId;

        if (!matched && round != _pendingManualEndTurnRound)
        {
            _pendingManualEndTurnPlayerId = null;
            _pendingManualEndTurnRound = -1;
            return false;
        }

        if (!matched)
        {
            return false;
        }

        _pendingManualEndTurnPlayerId = null;
        _pendingManualEndTurnRound = -1;
        return true;
    }

    private static CombatState? TryGetCombatState(NCombatUi combatUi)
    {
        return AccessTools.Field(typeof(NEndTurnButton), "_combatState")?.GetValue(combatUi.EndTurnButton) as CombatState;
    }

    private static bool RefreshCombatUiForControlledPlayer(ulong playerId)
    {
        if (!CombatManager.Instance.IsInProgress)
        {
            return true;
        }

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null)
        {
            return true;
        }

        CombatState? combatState = TryGetCombatState(combatUi);
        if (combatState == null)
        {
            return true;
        }

        Player? player = combatState.GetPlayer(playerId);
        if (player == null)
        {
            ModLog.Warn($"Failed to refresh combat UI: player {playerId} not found");
            return false;
        }

        try
        {
            NPlayerHand hand = combatUi.Hand;
            if (hand.InCardPlay || hand.IsInCardSelection || (NTargetManager.Instance?.IsInSelection ?? false))
            {
                // Risk: rebuilding the hand container at this point would destroy holder/cardplay nodes still in their lifecycle.
                ModLog.Info($"Combat UI refresh deferred: a card-play/card-selection operation is currently in progress, player={playerId}");
                return false;
            }

            CardPile handPile = PileType.Hand.GetPile(player);
            AccessTools.Field(typeof(NEndTurnButton), "_playerHand")?.SetValue(combatUi.EndTurnButton, handPile);
            combatUi.DrawPile.Initialize(player);
            combatUi.DiscardPile.Initialize(player);
            combatUi.ExhaustPile.Initialize(player);

            hand.CancelAllCardPlay();
            foreach (Node child in hand.CardHolderContainer.GetChildren().ToList())
            {
                if (child is not NCardHolder holder || !GodotObject.IsInstanceValid(holder))
                {
                    continue;
                }

                try
                {
                    hand.RemoveCardHolder(holder);
                }
                catch
                {
                    // Defensive fallback: past logs have shown a node lifecycle race here (an already-freed object accessed a second time).
                    // Keep this minimally destructive forced-removal path; change this branch with caution going forward.
                    holder.GetParent()?.RemoveChild(holder);
                    holder.QueueFreeSafely();
                }
            }

            foreach (CardModel card in handPile.Cards)
            {
                NCard? cardNode = NCard.Create(card);
                if (cardNode != null)
                {
                    hand.Add(cardNode);
                }
            }

            hand.ForceRefreshCardIndices();
            RefreshCombatEnergyUi(combatUi, player);
            ReevaluateEndTurnButtonState(combatUi, combatState, player);
            ModLog.Info($"Combat UI refreshed to current player {playerId}, hand card count={handPile.Cards.Count}");
            return true;
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to refresh combat UI: {exception.Message}");
            return false;
        }
    }

    private static void RefreshCombatEnergyUi(NCombatUi combatUi, Player player)
    {
        NStarCounter? starCounter = AccessTools.Field(typeof(NCombatUi), "_starCounter")?.GetValue(combatUi) as NStarCounter;
        NEnergyCounter? oldEnergyCounter = AccessTools.Field(typeof(NCombatUi), "_energyCounter")?.GetValue(combatUi) as NEnergyCounter;
        PlayerCombatState? playerCombatState = player.PlayerCombatState;
        if (!_combatEnergyContainerDefaultPosition.HasValue)
        {
            _combatEnergyContainerDefaultPosition = combatUi.EnergyCounterContainer.Position;
        }

        if (starCounter != null)
        {
            Player? previousPlayer = AccessTools.Field(typeof(NStarCounter), "_player")?.GetValue(starCounter) as Player;
            if (previousPlayer != null)
            {
                MethodInfo? onStarsChangedMethod = AccessTools.Method(typeof(NStarCounter), "OnStarsChanged");
                if (onStarsChangedMethod != null)
                {
                    Action<int, int> onStarsChanged = (Action<int, int>)onStarsChangedMethod.CreateDelegate(typeof(Action<int, int>), starCounter);
                    if (previousPlayer.PlayerCombatState != null)
                    {
                        previousPlayer.PlayerCombatState.StarsChanged -= onStarsChanged;
                    }
                }
            }

            starCounter.Initialize(player);
            AccessTools.Method(typeof(NStarCounter), "RefreshVisibility")?.Invoke(starCounter, Array.Empty<object>());
        }

        if (oldEnergyCounter != null)
        {
            oldEnergyCounter.QueueFreeSafely();
        }

        NEnergyCounter? newEnergyCounter = NEnergyCounter.Create(player);
        if (newEnergyCounter != null)
        {
            Vector2 targetPosition = player.Character.ShouldAlwaysShowStarCounter
                ? new Vector2(100f, 806f)
                : _combatEnergyContainerDefaultPosition ?? combatUi.EnergyCounterContainer.Position;
            combatUi.EnergyCounterContainer.SetPosition(targetPosition, keepOffsets: true);
            combatUi.EnergyCounterContainer.AddChildSafely(newEnergyCounter);
            starCounter?.Reparent(newEnergyCounter);
            if (starCounter != null)
            {
                starCounter.Visible = player.Character.ShouldAlwaysShowStarCounter || (playerCombatState?.Stars ?? 0) > 0;
            }

            AccessTools.Field(typeof(NCombatUi), "_energyCounter")?.SetValue(combatUi, newEnergyCounter);
        }
    }

    private static void RefreshTopBarForControlledPlayer(ulong playerId)
    {
        NTopBar? topBar = NRun.Instance?.GlobalUi?.TopBar;
        NRun? runNode = NRun.Instance;
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (topBar == null || runState == null || runNode?.GlobalUi == null)
        {
            return;
        }

        Player? player = runState.GetPlayer(playerId);
        if (player == null)
        {
            return;
        }

        try
        {
            RefreshTopBarDeck(topBar.Deck, player);
            topBar.Gold.Initialize(player);
            topBar.Hp.Initialize(player);
            foreach (Node child in topBar.Portrait.GetChildren())
            {
                child.QueueFreeSafely();
            }

            topBar.Portrait.Initialize(player);

            NPotionContainerPatch.TryBindPotionContainerToPlayer(runNode.GlobalUi.TopBar.PotionContainer, runState, playerId);

            // Note: relic refresh must clean up old nodes before rebuilding, to avoid stacked layers after switching players.
            NRelicInventoryPatch.TryRebuildRelicInventoryToPlayer(runNode.GlobalUi.RelicInventory, runState, playerId);
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory), "UpdateNavigation")
                ?.Invoke(runNode.GlobalUi.RelicInventory, Array.Empty<object>());
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to refresh top bar: {exception.Message}");
        }
    }

    public static void RefreshCombatEnergyForCurrentPlayer(string source)
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress || !CombatManager.Instance.IsInProgress)
        {
            return;
        }

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        CombatState? combatState = combatUi != null ? TryGetCombatState(combatUi) : null;
        if (combatUi == null || combatState == null)
        {
            return;
        }

        ulong playerId = Session.CurrentControlledPlayerId ?? LocalContext.NetId ?? LocalSelfCoopContext.PrimaryPlayerId;
        Player? player = combatState.GetPlayer(playerId);
        if (player == null)
        {
            return;
        }

        try
        {
            RefreshCombatEnergyUi(combatUi, player);
            ModLog.Info($"Combat-entry energy display refreshed: player={playerId}, source={source}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Combat-entry energy display refresh failed: player={playerId}, source={source}, error={exception.Message}");
        }
    }

    public static void RefreshSharedTopBarForCombat(string source)
    {
        if (!LocalSelfCoopContext.IsEnabled || !LocalSelfCoopContext.UseSingleAdventureMode || !RunManager.Instance.IsInProgress)
        {
            return;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        NRun? runNode = NRun.Instance;
        if (runState == null || runNode?.GlobalUi == null)
        {
            return;
        }

        bool potionRefreshed = false;
        bool relicRefreshed = false;

        try
        {
            ulong playerId = Session.CurrentControlledPlayerId ?? LocalContext.NetId ?? LocalSelfCoopContext.PrimaryPlayerId;
            potionRefreshed = NPotionContainerPatch.TryBindPotionContainerToPlayer(runNode.GlobalUi.TopBar.PotionContainer, runState, playerId);
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Pre-combat potion bar refresh failed: {exception.Message}");
        }

        try
        {
            relicRefreshed = NRelicInventoryPatch.TryRebuildRelicInventoryToPrimaryPlayer(runNode.GlobalUi.RelicInventory, runState);
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Pre-combat relic bar refresh failed: {exception.Message}");
        }

        if (!potionRefreshed && !relicRefreshed)
        {
            ModLog.Warn($"Pre-combat top bar refresh had no effect: source={source}");
            return;
        }

        AccessTools.Method(typeof(NTopBar), "UpdateNavigation")?.Invoke(runNode.GlobalUi.TopBar, Array.Empty<object>());
        AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory), "UpdateNavigation")?.Invoke(runNode.GlobalUi.RelicInventory, Array.Empty<object>());
        ModLog.Info($"Pre-combat top bar refresh complete: source={source}, potion={potionRefreshed}, relic={relicRefreshed}");
    }

    private static void RefreshTopBarDeck(NTopBarDeckButton deckButton, Player player)
    {
        CardPile? oldPile = AccessTools.Field(typeof(NTopBarDeckButton), "_pile")?.GetValue(deckButton) as CardPile;
        MethodInfo? updateMethod = AccessTools.Method(typeof(NTopBarDeckButton), "OnPileContentsChanged");
        if (oldPile != null && updateMethod != null)
        {
            Action updateHandler = (Action)Delegate.CreateDelegate(typeof(Action), deckButton, updateMethod);
            oldPile.CardAddFinished -= updateHandler;
            oldPile.CardRemoveFinished -= updateHandler;
        }

        deckButton.Initialize(player);
    }

    private static void RefreshDeckViewForControlledPlayer(ulong playerId)
    {
        NDeckViewScreen? deckView = NCapstoneContainer.Instance?.CurrentCapstoneScreen as NDeckViewScreen;
        if (deckView == null)
        {
            return;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        Player? player = runState?.GetPlayer(playerId);
        if (runState == null || player == null)
        {
            return;
        }

        try
        {
            CardPile? oldPile = AccessTools.Field(typeof(NDeckViewScreen), "_pile")?.GetValue(deckView) as CardPile;
            MethodInfo? onPileContentsChangedMethod = AccessTools.Method(typeof(NDeckViewScreen), "OnPileContentsChanged");
            if (oldPile != null && onPileContentsChangedMethod != null)
            {
                Action handler = (Action)Delegate.CreateDelegate(typeof(Action), deckView, onPileContentsChangedMethod);
                oldPile.ContentsChanged -= handler;
            }

            CardPile newPile = PileType.Deck.GetPile(player);
            AccessTools.Field(typeof(NDeckViewScreen), "_player")?.SetValue(deckView, player);
            AccessTools.Field(typeof(NDeckViewScreen), "_pile")?.SetValue(deckView, newPile);

            if (onPileContentsChangedMethod != null)
            {
                Action handler = (Action)Delegate.CreateDelegate(typeof(Action), deckView, onPileContentsChangedMethod);
                newPile.ContentsChanged += handler;
                onPileContentsChangedMethod.Invoke(deckView, Array.Empty<object>());
            }
            else
            {
                AccessTools.Method(typeof(NDeckViewScreen), "DisplayCards")?.Invoke(deckView, Array.Empty<object>());
            }

            ModLog.Info($"Deck view screen switched to current player: {playerId}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to refresh deck view screen: {exception.Message}");
        }
    }

    private static void RefreshRestSiteForControlledPlayer(ulong playerId)
    {
        NRestSiteRoom? restSiteRoom = NRestSiteRoom.Instance;
        if (restSiteRoom == null)
        {
            return;
        }

        try
        {
            RunManager.Instance.RestSiteSynchronizer.LocalOptionHovered(null);
            AccessTools.Field(typeof(NRestSiteRoom), "_lastFocused")?.SetValue(restSiteRoom, null);
            AccessTools.Method(typeof(NRestSiteRoom), "UpdateRestSiteOptions")?.Invoke(restSiteRoom, null);
            RestSiteUiRefreshUtil.EnsureChoicesVisibleForLocalPlayer(restSiteRoom, $"runtime-switch-{playerId}");
            ModLog.Info($"Rest site UI refreshed to current player: {playerId}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to refresh rest site UI: {exception.Message}");
        }
    }

    private static void RefreshEventRoomForControlledPlayer(ulong playerId)
    {
        if (LocalSelfCoopContext.UseSingleEventFlow)
        {
            return;
        }

        NEventRoom? eventRoom = NEventRoom.Instance;
        EventSynchronizer synchronizer = RunManager.Instance.EventSynchronizer;
        if (eventRoom == null || synchronizer.IsShared)
        {
            return;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        Player? player = runState?.GetPlayer(playerId);
        if (runState == null || player == null)
        {
            return;
        }

        EventModel targetEvent = synchronizer.GetEventForPlayer(player);
        EventModel? currentEvent = AccessTools.Field(typeof(NEventRoom), "_event")?.GetValue(eventRoom) as EventModel;
        if (currentEvent == null || currentEvent == targetEvent)
        {
            return;
        }

        try
        {
            ResetEventNodeReference(currentEvent);
            ResetEventNodeReference(targetEvent);

            if (targetEvent.LayoutType == EventLayoutType.Combat && targetEvent.Node == null)
            {
                synchronizer.GenerateInternalCombatStateIfNecessary(targetEvent);
            }

            bool isPreFinished = runState.CurrentRoom is EventRoom currentEventRoom && currentEventRoom.IsPreFinished;
            NEventRoom? refreshedRoom = NEventRoom.Create(targetEvent, runState, isPreFinished);
            if (refreshedRoom == null)
            {
                ModLog.Warn($"Failed to rebuild event room: Create returned null, player={playerId}");
                return;
            }

            NRun.Instance?.SetCurrentRoom(refreshedRoom);
            ModLog.Info($"Non-shared event room rebuilt for the current player: player={playerId}, event={targetEvent.Id.Entry}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to switch non-shared event view: {exception.Message}");
        }
    }

    private static void ResetEventNodeReference(EventModel eventModel)
    {
        AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Node))
            ?.Invoke(eventModel, new object?[] { null });
    }

    public static void RecordFlowBlockSignal(
        string signal,
        string reason,
        ulong playerId,
        string source,
        int round,
        bool dedupePerRoundPlayer = false)
    {
        if (!LocalSelfCoopContext.IsEnabled)
        {
            return;
        }

        if (dedupePerRoundPlayer && round >= 0)
        {
            string dedupeKey = $"{signal}:{round}:{playerId}";
            if (!_flowBlockSignalDedupeRoundPlayer.Add(dedupeKey))
            {
                return;
            }
        }

        long nowMs = (long)Time.GetTicksMsec();
        if (_flowBlockSignalWindowStartMs <= 0L)
        {
            _flowBlockSignalWindowStartMs = nowMs;
        }

        string key = $"{signal}:{reason}";
        _flowBlockSignalCounts[key] = (_flowBlockSignalCounts.TryGetValue(key, out int count) ? count : 0) + 1;

        if (nowMs - _flowBlockSignalWindowStartMs < 2000L)
        {
            return;
        }

        string signalSummary = _flowBlockSignalCounts.Count == 0
            ? "none"
            : string.Join(",", _flowBlockSignalCounts.Select((entry) => $"{entry.Key}:{entry.Value}"));
        ModLog.Warn(
            $"Flow-block watchdog: windowMs={nowMs - _flowBlockSignalWindowStartMs}, signals={signalSummary}, player={playerId}, round={round}, source={source}");

        _flowBlockSignalWindowStartMs = nowMs;
        _flowBlockSignalCounts.Clear();
    }

    private static void ReevaluateEndTurnButtonState(NCombatUi combatUi, CombatState combatState, Player currentPlayer)
    {
        if (combatState.CurrentSide != CombatSide.Player)
        {
            return;
        }

        try
        {
            bool shouldDisable = CombatManager.Instance.IsPlayerReadyToEndTurn(currentPlayer);
            AccessTools.PropertySetter(typeof(CombatManager), "PlayerActionsDisabled")?.Invoke(CombatManager.Instance, new object[] { shouldDisable });

            Type? stateType = AccessTools.Inner(typeof(NEndTurnButton), "State");
            MethodInfo? setStateMethod = AccessTools.Method(typeof(NEndTurnButton), "SetState");
            if (stateType != null && setStateMethod != null)
            {
                bool canTakeAction = !shouldDisable;
                object stateValue = Enum.ToObject(stateType, canTakeAction ? 0 : 1);
                setStateMethod.Invoke(combatUi.EndTurnButton, new object[] { stateValue });
            }

            combatUi.EndTurnButton.RefreshEnabled();
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to refresh end-turn button state: {exception.Message}");
        }
    }

    private static void EnsureTreasureCursorVisibleAfterSwitch(string source)
    {
        if (!IsTreasurePickingActive())
        {
            return;
        }

        try
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Callable.From(delegate
            {
                Input.MouseMode = Input.MouseModeEnum.Visible;
            }).CallDeferred();
            ModLog.Info($"Forced mouse cursor visible after treasure room player switch: source={source}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"Failed to restore mouse cursor after treasure room player switch: source={source}, error={exception.Message}");
        }
    }

    private static bool IsTreasurePickingActive()
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return false;
        }

        TreasureRoomRelicSynchronizer? synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (synchronizer?.CurrentRelics == null || synchronizer.CurrentRelics.Count == 0)
        {
            return false;
        }

        object? votesObject = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes")?.GetValue(synchronizer);
        if (votesObject is not System.Collections.IEnumerable votesEnumerable)
        {
            return false;
        }

        foreach (object? vote in votesEnumerable)
        {
            if (vote == null)
            {
                return true;
            }

            bool? voteReceived = AccessTools.Field(vote.GetType(), "voteReceived")?.GetValue(vote) as bool?;
            if (voteReceived == false)
            {
                return true;
            }
        }

        return false;
    }

}
