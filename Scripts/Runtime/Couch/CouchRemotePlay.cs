using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// Plays for the teammate who isn't driving, without taking the screen away from the driver.
/// The action is sent through the in-process loopback network as a <see cref="RequestEnqueueActionMessage"/> from the
/// teammate, which is exactly how an online teammate's card play reaches the host: the game queues it under the
/// teammate, animates it from their character, and runs it alongside the driver's own plays.
///
/// Used by the teammate HUD (<see cref="CouchTeammateHud"/>) and by the spike hotkeys: F7 = teammate plays a playable
/// card (starter Strike/Defend first, since those never ask for a choice); Shift+F7 = first playable card in hand
/// order; F6 = teammate ends (or un-ends) their turn; F5 = answer the teammate's pending choice
/// (<see cref="CouchTeammateChoices"/>).
/// </summary>
internal static class CouchRemotePlay
{
    private const double FollowUpDelaySeconds = 2.0;

    private static readonly HashSet<PotionModel> _queuedPotions = new();

    /// <summary>
    /// The first living local character, in session order, who isn't the driver. Null outside combat.
    /// </summary>
    public static Player? FindTeammate()
    {
        if (!LocalSelfCoopContext.IsEnabled || !RunManager.Instance.IsInProgress || !CombatManager.Instance.IsInProgress)
        {
            return null;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        return LocalControlRuntime.SessionState.OrderedPlayerIds
            .Where((ulong id) => id != LocalContext.NetId)
            .Select((ulong id) => runState?.GetPlayer(id))
            .FirstOrDefault((Player? p) => p != null && p.Creature.IsAlive && p.PlayerCombatState != null);
    }

    /// <summary>
    /// Whether a teammate action can be sent now. Like a real client, play-phase actions aren't sent during the enemy turn.
    /// </summary>
    public static bool CanActNow(out string reason)
    {
        if (LocalSelfCoopContext.NetService == null || !CombatManager.Instance.IsInProgress)
        {
            reason = "not in combat";
            return false;
        }

        if (RunManager.Instance.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase)
        {
            reason = "enemy turn";
            return false;
        }

        reason = "";
        return true;
    }

    public static bool NeedsTarget(CardModel card)
    {
        return card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;
    }

    public static IReadOnlyList<Creature> ValidTargets(CardModel card, Player owner)
    {
        ICombatState? combat = owner.Creature.CombatState;
        if (combat == null)
        {
            return new List<Creature>();
        }

        IEnumerable<Creature> candidates = card.TargetType switch
        {
            TargetType.AnyEnemy => combat.HittableEnemies,
            TargetType.AnyAlly => combat.PlayerCreatures.Where((Creature c) => c != owner.Creature),
            _ => Enumerable.Empty<Creature>()
        };
        return candidates.Where((Creature c) => c.IsAlive && card.IsValidTarget(c)).ToList();
    }

    public static bool TryPlay(Player teammate, CardModel card, Creature? target, out string reason)
    {
        if (!CanActNow(out reason))
        {
            return false;
        }

        if (!card.CanPlayTargeting(target))
        {
            reason = card.CanPlay() ? "invalid target" : "can't play that card now";
            return false;
        }

        PlayerCombatState combatState = teammate.PlayerCombatState!;
        int energyBefore = combatState.Energy;
        int handBefore = combatState.Hand.Cards.Count;
        string targetText = target == null ? "no target" : $"target {target.LogName}";
        CouchLog.Info($"[RemotePlay] Teammate {teammate.NetId} plays {card.Id.Entry} ({targetText}); driver stays {LocalContext.NetId}. Energy {energyBefore}, hand {handBefore}.");
        Send(teammate, new PlayCardAction(card, target));
        FollowUp(teammate, card, energyBefore, handBefore);
        return true;
    }

    public static bool ToggleEndTurn(Player teammate, out string reason)
    {
        if (!CanActNow(out reason))
        {
            return false;
        }

        int turnNumber = teammate.PlayerCombatState!.TurnNumber;
        bool alreadyReady = CombatManager.Instance.IsPlayerReadyToEndTurn(teammate);
        GameAction action = alreadyReady
            ? new UndoEndPlayerTurnAction(teammate, turnNumber)
            : new EndPlayerTurnAction(teammate, turnNumber);
        CouchLog.Info($"[RemotePlay] Teammate {teammate.NetId} {(alreadyReady ? "un-ends" : "ends")} turn {turnNumber}; driver stays {LocalContext.NetId}.");
        Send(teammate, action);
        return true;
    }

    /// <summary>
    /// Whether the teammate may drink <paramref name="potion"/> now; mirrors the rules of the game's potion popup
    /// (<c>NPotionPopup.RefreshButtons</c>).
    /// </summary>
    public static bool CanUsePotion(PotionModel potion, out string reason)
    {
        Player owner = potion.Owner;
        Creature creature = owner.Creature;
        PruneQueuedPotions(owner);
        if (potion.IsQueued || _queuedPotions.Contains(potion) || creature.IsDead)
        {
            reason = "already being used";
            return false;
        }

        if (!owner.CanUseOrRemovePotions)
        {
            reason = "potions are locked right now";
            return false;
        }

        switch (potion.Usage)
        {
            case PotionUsage.Automatic:
            case PotionUsage.None:
                reason = "this potion triggers by itself";
                return false;
            case PotionUsage.CombatOnly:
                // PlayerActionsDisabled is the driver's lock (set when the driver ends their turn), so it doesn't apply
                // to the teammate; their own ended turn does.
                if (!CanActNow(out reason) || creature.CombatState?.CurrentSide != creature.Side)
                {
                    reason = reason.Length > 0 ? reason : "not now";
                    return false;
                }

                if (CombatManager.Instance.IsPlayerReadyToEndTurn(owner))
                {
                    reason = "you ended your turn";
                    return false;
                }

                break;
        }

        if (!potion.PassesCustomUsabilityCheck)
        {
            reason = "can't use it now";
            return false;
        }

        // Like a real client, don't send anything during the enemy turn.
        if (CombatManager.Instance.IsInProgress && !CanActNow(out reason))
        {
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Potions whose target the teammate has to pick (Self and untargeted potions don't need it).</summary>
    public static bool PotionNeedsTargetChoice(PotionModel potion)
    {
        return potion.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer;
    }

    public static IReadOnlyList<Creature> ValidPotionTargets(PotionModel potion, Player owner)
    {
        ICombatState? combat = owner.Creature.CombatState;
        if (combat == null)
        {
            return new List<Creature>();
        }

        IEnumerable<Creature> candidates = potion.TargetType == TargetType.AnyEnemy ? combat.HittableEnemies : combat.PlayerCreatures;
        return candidates.Where((Creature c) => potion.IsValidTarget(c)).ToList();
    }

    public static bool TryUsePotion(Player teammate, PotionModel potion, Creature? target, out string reason)
    {
        if (!CanUsePotion(potion, out reason))
        {
            return false;
        }

        // Same default as PotionModel.EnqueueManualUse: self-targeted potions target the owner.
        if (target == null && potion.IsValidTarget(teammate.Creature))
        {
            target = teammate.Creature;
        }

        if (!potion.IsValidTarget(target))
        {
            reason = "invalid target";
            return false;
        }

        _queuedPotions.Add(potion);
        string targetText = target == null ? "no target" : $"target {target.LogName}";
        CouchLog.Info($"[RemotePlay] Teammate {teammate.NetId} uses potion {potion.Id.Entry} ({targetText}); driver stays {LocalContext.NetId}.");
        Send(teammate, new UsePotionAction(potion, target, CombatManager.Instance.IsInProgress));
        return true;
    }

    public static bool TryDiscardPotion(Player teammate, PotionModel potion, out string reason)
    {
        PruneQueuedPotions(teammate);
        int slot = teammate.PotionSlots.ToList().IndexOf(potion);
        if (slot < 0 || _queuedPotions.Contains(potion) || potion.IsQueued)
        {
            reason = "that potion is gone or being used";
            return false;
        }

        if (!teammate.CanUseOrRemovePotions)
        {
            reason = "potions are locked right now";
            return false;
        }

        _queuedPotions.Add(potion);
        CouchLog.Info($"[RemotePlay] Teammate {teammate.NetId} discards potion {potion.Id.Entry} (slot {slot}).");
        Send(teammate, new DiscardPotionGameAction(teammate, (uint)slot, CombatManager.Instance.IsInProgress));
        reason = "";
        return true;
    }

    /// <summary>
    /// Potions sent for use or discard stay in their slot until the action runs; remember them so they can't be
    /// sent twice (the game's own IsQueued flag is only set for the local player's potions).
    /// </summary>
    private static void PruneQueuedPotions(Player owner)
    {
        _queuedPotions.RemoveWhere((PotionModel p) => p.Owner == owner && !owner.PotionSlots.Contains(p));
    }

    /// <summary>Spike hotkey (F7 / Shift+F7).</summary>
    public static void PlayCardAsTeammate(bool preferStarterCards)
    {
        Player? teammate = FindTeammate();
        if (teammate == null)
        {
            CouchLog.Info("[RemotePlay] No teammate to play for (not in a local multi-character combat).");
            return;
        }

        IEnumerable<CardModel> candidates = preferStarterCards
            ? teammate.PlayerCombatState!.Hand.Cards.OrderBy((CardModel c) => IsStarterCard(c) ? 0 : 1)
            : teammate.PlayerCombatState!.Hand.Cards;
        foreach (CardModel card in candidates.ToList())
        {
            Creature? target = NeedsTarget(card) ? ValidTargets(card, teammate).FirstOrDefault() : null;
            if (card.CanPlayTargeting(target))
            {
                if (!TryPlay(teammate, card, target, out string reason))
                {
                    CouchLog.Info($"[RemotePlay] Can't play for teammate: {reason}.");
                }

                return;
            }
        }

        string hand = string.Join(", ", teammate.PlayerCombatState!.Hand.Cards.Select((CardModel c) => c.Id.Entry));
        CouchLog.Info($"[RemotePlay] Teammate {teammate.NetId} has no playable card (energy {teammate.PlayerCombatState.Energy}, hand: {hand}).");
    }

    /// <summary>Spike hotkey (F6).</summary>
    public static void ToggleEndTurnAsTeammate()
    {
        Player? teammate = FindTeammate();
        if (teammate == null)
        {
            CouchLog.Info("[RemotePlay] No teammate to end the turn for.");
            return;
        }

        if (!ToggleEndTurn(teammate, out string reason))
        {
            CouchLog.Info($"[RemotePlay] Can't end the teammate's turn: {reason}.");
        }
    }

    private static bool IsStarterCard(CardModel card)
    {
        string id = card.Id.Entry;
        return id.Contains("STRIKE", System.StringComparison.OrdinalIgnoreCase) || id.Contains("DEFEND", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sends <paramref name="action"/> as the teammate's own request, as an online client would.</summary>
    public static void SendAs(Player teammate, GameAction action)
    {
        Send(teammate, action);
    }

    /// <summary>Delivers <paramref name="message"/> through the loopback network as if the teammate had sent it.</summary>
    public static void DispatchAs<T>(Player teammate, T message) where T : MegaCrit.Sts2.Core.Multiplayer.Serialization.INetMessage
    {
        LocalSelfCoopContext.NetService?.DispatchLoopback(message, teammate.NetId);
    }

    private static void Send(Player teammate, GameAction action)
    {
        RequestEnqueueActionMessage message = new()
        {
            location = RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation,
            action = action.ToNetAction()
        };
        LocalSelfCoopContext.NetService!.DispatchLoopback(message, teammate.NetId);
    }

    /// <summary>
    /// Logs the teammate's state a moment later so the log shows whether the play resolved.
    /// </summary>
    private static void FollowUp(Player teammate, CardModel card, int energyBefore, int handBefore)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        tree.CreateTimer(FollowUpDelaySeconds).Timeout += () =>
        {
            PlayerCombatState? state = teammate.PlayerCombatState;
            if (state == null)
            {
                CouchLog.Info("[RemotePlay] Follow-up: combat ended.");
                return;
            }

            bool stillInHand = state.Hand.Cards.Contains(card);
            CouchLog.Info(
                $"[RemotePlay] Follow-up after {FollowUpDelaySeconds}s: {card.Id.Entry} {(stillInHand ? "STILL IN HAND (not played)" : "left the hand")}, " +
                $"energy {energyBefore} -> {state.Energy}, hand {handBefore} -> {state.Hand.Cards.Count}, driver {LocalContext.NetId}.");
        };
    }
}
