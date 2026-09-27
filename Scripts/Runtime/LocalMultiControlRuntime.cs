using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LocalMultiControl.Scripts.Patch;
using LocalMultiControl.Scripts.Runtime.Couch;
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

namespace LocalMultiControl.Scripts.Runtime;

internal static class LocalMultiControlRuntime
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
        LocalMultiControlLogger.Info("检测到 RunManager.Launch，开始初始化本地多控会话。");
        if (LocalSelfCoopContext.IsEnabled)
        {
            RunManager.Instance.CombatStateSynchronizer.IsDisabled = true;
            LocalMultiControlLogger.Info("本地双人模式已禁用战斗同步等待，避免单进程回环阻塞。");
        }

        Session.InitializeFromRunState(runState);
        if (Session.CurrentControlledPlayerId.HasValue)
        {
            ApplyControlContext("run-launched");
        }
        else
        {
            LocalMultiControlLogger.Info("当前运行未启用本地多控会话。");
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
        LocalSelfCoopContext.Disable("RunManager.CleanUp");
        LocalMultiControlLogger.Info("RunManager.CleanUp 后已完成本地多控会话清理。");
    }

    public static void SwitchNextControlledPlayer(string source)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return;
        }

        if (CombatManager.Instance.IsInProgress)
        {
            // 风险点：战斗中如果按“会话顺序”盲切，可能切到不在当前 CombatState 的角色，
            // 进而触发手牌UI与动作队列 owner 不一致，表现为“无法出牌/切到空角色”。
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
                LocalMultiControlLogger.Info($"玩家主动结束回合时检测到仍有可出牌角色，不触发全员结束: round={combatState.RoundNumber}");
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
            LocalMultiControlLogger.Info($"检测到全员无牌可出，已自动结束全部角色回合: round={combatState.RoundNumber}, source={source}");
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
        LocalMultiControlLogger.Info($"New combat; auto end-turn tracking reset: combat={combatIdentity}");
    }

    public static void RecordManualEndTurnIntent(ulong playerId, string source)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        CombatState? combatState = combatUi != null ? TryGetCombatState(combatUi) : null;
        _pendingManualEndTurnPlayerId = playerId;
        _pendingManualEndTurnRound = combatState?.RoundNumber ?? -1;
        LocalMultiControlLogger.Info($"已记录手动结束回合意图: player={playerId}, round={_pendingManualEndTurnRound}, source={source}");
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
                LocalMultiControlLogger.Warn($"检测到无效战斗角色ID，回退到1号位: {currentControlledPlayerId.Value}");
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
            // 风险点：若 LocalContext 已切换但战斗UI刷新失败，会导致“逻辑 owner 与显示 owner”分离。
            // 该状态会把后续出牌入队到错误玩家队列，因此这里必须立即回滚上下文。
            LocalMultiControlLogger.Warn($"控制上下文切换回滚：战斗UI刷新失败，target={currentControlledPlayerId.Value}");
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
        LocalMultiControlLogger.Info($"控制上下文已更新: {previousNetId?.ToString() ?? "null"} -> {currentControlledPlayerId.Value}, source={source}");
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
        LocalMultiControlLogger.Warn(
            $"检测到手动出牌上下文漂移，已强制校正: {previousNetId?.ToString() ?? "null"} -> {playerId}, source={source}");
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
                LocalMultiControlLogger.Warn($"同步 {key} 的 _localPlayerId 失败: {exception.Message}");
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
            LocalMultiControlLogger.Info(
                $"跳过结束回合后自动切人：ended={endedPlayerId}, controlled={Session.CurrentControlledPlayerId?.ToString() ?? "null"}, manualMatched={matchedManualEndTurn}");
            return;
        }

        if (CombatManager.Instance.AllPlayersReadyToEndTurn())
        {
            LocalMultiControlLogger.Info("所有角色均已结束回合，跳过自动切换，等待敌方回合推进。");
            return;
        }

        LocalMultiControlLogger.Info($"检测到角色 {endedPlayerId} 结束回合，自动切换到下一位。");
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
            LocalMultiControlLogger.Info($"忽略切换请求({source})：战斗UI未就绪。");
            return false;
        }

        NPlayerHand hand = combatUi.Hand;
        if (hand.InCardPlay || hand.IsInCardSelection || (NTargetManager.Instance?.IsInSelection ?? false))
        {
            // 风险点：在拖牌、目标选择、选牌UI过程中切换，会把 NCardPlay/选择上下文中途打断，
            // 容易触发 NMouseCardPlay._ExitTree 空引用，以及动作队列进入 cancel-all 状态。
            LocalMultiControlLogger.Info($"忽略切换请求({source})：当前存在进行中的出牌/选牌操作。");
            return false;
        }

        ActionSynchronizerCombatState combatSyncState = RunManager.Instance.ActionQueueSynchronizer.CombatState;
        if (combatSyncState != ActionSynchronizerCombatState.PlayPhase)
        {
            // 风险点：非 PlayPhase 期间切换 owner，动作会被延迟/拒绝入队，造成“按牌无反应”。
            LocalMultiControlLogger.Info($"忽略切换请求({source})：战斗同步阶段={combatSyncState}。");
            return false;
        }

        CombatState? combatState = TryGetCombatState(combatUi);
        if (combatState == null || combatState.CurrentSide != CombatSide.Player)
        {
            LocalMultiControlLogger.Info($"忽略切换请求({source})：当前不在玩家出牌阶段。");
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
            // 风险点：当前实现只保证“双角色本地多控”，人数异常时继续切换会引入不可预期 owner 绑定。
            LocalMultiControlLogger.Warn($"战斗角色切换需要至少2名玩家，当前数量={combatPlayerIds.Count}");
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
            LocalMultiControlLogger.Info($"结束回合后已切换到下一个可出牌角色: {currentPlayerId} -> {targetPlayerId}");
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
            LocalMultiControlLogger.Warn($"刷新战斗UI失败：未找到玩家 {playerId}");
            return false;
        }

        try
        {
            NPlayerHand hand = combatUi.Hand;
            if (hand.InCardPlay || hand.IsInCardSelection || (NTargetManager.Instance?.IsInSelection ?? false))
            {
                // 风险点：此时重建手牌容器会销毁仍在生命周期中的 holder/cardplay 节点。
                LocalMultiControlLogger.Info($"战斗UI刷新延后：当前有进行中的出牌/选牌操作，player={playerId}");
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
                    // 防御性兜底：历史日志中该处出现过节点生命周期竞争（已释放对象被二次访问）。
                    // 这里保留最小破坏的强制移除路径，后续请谨慎改动该分支。
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
            LocalMultiControlLogger.Info($"战斗UI已刷新到当前角色 {playerId}，手牌数量={handPile.Cards.Count}");
            return true;
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"刷新战斗UI失败: {exception.Message}");
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

            // 注意：遗物刷新必须先清理旧节点再重建，避免切人后叠层。
            NRelicInventoryPatch.TryRebuildRelicInventoryToPlayer(runNode.GlobalUi.RelicInventory, runState, playerId);
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory), "UpdateNavigation")
                ?.Invoke(runNode.GlobalUi.RelicInventory, Array.Empty<object>());
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"刷新顶部栏失败: {exception.Message}");
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
            LocalMultiControlLogger.Info($"入战能量显示已刷新: player={playerId}, source={source}");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"入战能量显示刷新失败: player={playerId}, source={source}, error={exception.Message}");
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
            LocalMultiControlLogger.Warn($"战斗前药水栏刷新失败: {exception.Message}");
        }

        try
        {
            relicRefreshed = NRelicInventoryPatch.TryRebuildRelicInventoryToPrimaryPlayer(runNode.GlobalUi.RelicInventory, runState);
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"战斗前遗物栏刷新失败: {exception.Message}");
        }

        if (!potionRefreshed && !relicRefreshed)
        {
            LocalMultiControlLogger.Warn($"战斗前顶部栏刷新未生效: source={source}");
            return;
        }

        AccessTools.Method(typeof(NTopBar), "UpdateNavigation")?.Invoke(runNode.GlobalUi.TopBar, Array.Empty<object>());
        AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory), "UpdateNavigation")?.Invoke(runNode.GlobalUi.RelicInventory, Array.Empty<object>());
        LocalMultiControlLogger.Info($"战斗前顶部栏刷新完成: source={source}, potion={potionRefreshed}, relic={relicRefreshed}");
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

            LocalMultiControlLogger.Info($"卡组界面已切换到当前角色: {playerId}");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"刷新卡组界面失败: {exception.Message}");
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
            LocalMultiControlLogger.Info($"休息区UI已刷新到当前角色: {playerId}");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"刷新休息区UI失败: {exception.Message}");
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
                LocalMultiControlLogger.Warn($"重建事件房间失败：Create 返回 null，player={playerId}");
                return;
            }

            NRun.Instance?.SetCurrentRoom(refreshedRoom);
            LocalMultiControlLogger.Info($"非共享事件房间已按当前角色重建: player={playerId}, event={targetEvent.Id.Entry}");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"切换非共享事件视图失败: {exception.Message}");
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
        LocalMultiControlLogger.Warn(
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
            LocalMultiControlLogger.Warn($"刷新回合结束按钮状态失败: {exception.Message}");
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
            LocalMultiControlLogger.Info($"宝箱切人后已强制恢复鼠标可见: source={source}");
        }
        catch (Exception exception)
        {
            LocalMultiControlLogger.Warn($"宝箱切人后恢复鼠标失败: source={source}, error={exception.Message}");
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
