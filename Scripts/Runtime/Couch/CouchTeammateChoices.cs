using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>How the waiting side decodes the answer (mirrors the <c>WaitForRemoteChoice(...).AsXxx()</c> call).</summary>
internal enum CouchChoiceAnswerKind
{
    /// <summary>Cards from a combat pile (hand, draw, discard...): <c>AsCombatCards()</c>.</summary>
    CombatCards,

    /// <summary>One index into the offered cards, or none to skip: <c>AsIndex()</c>.</summary>
    Index,

    /// <summary>Several indexes into the offered cards: <c>AsIndexes()</c>.</summary>
    Indexes,

    /// <summary>Cards from the player's deck (upgrade, transform, remove...): <c>AsDeckCards()</c>.</summary>
    DeckCards,

    /// <summary>A player (e.g. who to heal with Mend): <c>AsPlayerId()</c>. Options are <see cref="CouchTeammateChoice.OptionPlayerIds"/>.</summary>
    PlayerId
}

/// <summary>A choice a teammate has to make, e.g. "discard 1 card" from their Survivor.</summary>
internal sealed class CouchTeammateChoice
{
    public required Player Player { get; init; }

    public required CouchChoiceAnswerKind Kind { get; init; }

    public required IReadOnlyList<CardModel> Options { get; init; }

    public required int MinSelect { get; init; }

    public required int MaxSelect { get; init; }

    public required string Prompt { get; init; }

    public required string Source { get; init; }

    /// <summary>
    /// Non-card options after the cards (e.g. a card reward's "Skip" or "Reroll"). Answered as index
    /// <c>Options.Count + i</c>; only for <see cref="CouchChoiceAnswerKind.Index"/> choices.
    /// </summary>
    public IReadOnlyList<string> ExtraOptions { get; init; } = Array.Empty<string>();

    /// <summary>For <see cref="CouchChoiceAnswerKind.PlayerId"/> choices: the player behind each extra option.</summary>
    public IReadOnlyList<ulong> OptionPlayerIds { get; init; } = Array.Empty<ulong>();

    /// <summary>Whether the choice can be closed without picking anything (answered as no index).</summary>
    public bool CanClose { get; init; }

    /// <summary>True when the extra options are just "Skip": picking one answers with no card.</summary>
    public bool ExtraOptionsSkip { get; init; }

    /// <summary>Combat choices are dropped when combat ends; reward choices live outside combat.</summary>
    public bool IsCombatChoice { get; init; } = true;

    /// <summary>Set once the game starts waiting for the answer.</summary>
    public uint? ChoiceId { get; set; }

    /// <summary>When the request was recorded; stale requests are ignored.</summary>
    public ulong RequestedAtMs { get; } = Godot.Time.GetTicksMsec();

    public string Describe()
    {
        string options = string.Join(", ", Options.Select((CardModel c) => c.Title).Concat(ExtraOptions));
        string count = MinSelect == MaxSelect ? $"{MinSelect}" : $"{MinSelect}-{MaxSelect}";
        return $"{Prompt} ({count} of {Options.Count}: {options})";
    }
}

/// <summary>
/// Answers choices for teammates during simultaneous combat. When a teammate's card asks for a choice, the game takes
/// the online path: it reserves a choice id and waits for that player's <see cref="PlayerChoiceMessage"/>. The card
/// selection prefixes record what is being asked (<see cref="Request"/>), the WaitForRemoteChoice prefix attaches the
/// choice id (<see cref="OnWaitingForRemoteChoice"/>), and <see cref="Answer"/> sends the teammate's answer through the
/// loopback network exactly as an online client would.
/// </summary>
internal static class CouchTeammateChoices
{
    private static readonly Dictionary<ulong, CouchTeammateChoice> _requested = new();

    private static readonly List<CouchTeammateChoice> _pending = new();

    public static IReadOnlyList<CouchTeammateChoice> Pending
    {
        get
        {
            PruneIfCombatOver();
            return _pending;
        }
    }

    public static void Request(Player player, CouchChoiceAnswerKind kind, IEnumerable<CardModel> options, int minSelect, int maxSelect, string prompt, string source)
    {
        if (!CouchTeammate.IsTeammate(player))
        {
            return;
        }

        _requested[player.NetId] = new CouchTeammateChoice
        {
            Player = player,
            Kind = kind,
            Options = options.ToList(),
            MinSelect = minSelect,
            MaxSelect = maxSelect,
            Prompt = CouchText.Plain(prompt),
            Source = source
        };
    }

    /// <summary>Records a choice prepared by other couch code (e.g. the teammate's card reward), in or out of combat.</summary>
    public static void Request(CouchTeammateChoice choice)
    {
        _requested[choice.Player.NetId] = choice;
    }

    /// <summary>
    /// True if a selection the teammate can answer from their own UI was just started for <paramref name="player"/>.
    /// Selections without one stay on the main screen (the fork's behavior), so nothing waits for an answer that can't come.
    /// </summary>
    public static bool HasRequest(Player player)
    {
        return _requested.TryGetValue(player.NetId, out CouchTeammateChoice? choice) && Godot.Time.GetTicksMsec() - choice.RequestedAtMs < RequestLifetimeMs;
    }

    private const ulong RequestLifetimeMs = 5000;

    public static void OnWaitingForRemoteChoice(Player player, uint choiceId)
    {
        if (!_requested.Remove(player.NetId, out CouchTeammateChoice? choice))
        {
            if (CouchTeammate.IsTeammate(player))
            {
                CouchLog.Warn($"Teammate {player.NetId} has to make choice {choiceId}, but the kind of choice is unknown; it can't be answered yet.");
            }

            return;
        }

        choice.ChoiceId = choiceId;
        _pending.Add(choice);
        CouchLog.Info($"Teammate {player.NetId} must choose (choice {choiceId}, from {choice.Source}): {choice.Describe()}. F5 answers with the first option(s).");
    }

    /// <summary>
    /// Sends the teammate's answer. <paramref name="selectedIndexes"/> index into <see cref="CouchTeammateChoice.Options"/>;
    /// an empty list skips (only valid when <see cref="CouchTeammateChoice.MinSelect"/> is 0).
    /// </summary>
    public static bool Answer(CouchTeammateChoice choice, IReadOnlyList<int> selectedIndexes)
    {
        LocalLoopbackHostGameService? netService = LocalSelfCoopContext.NetService;
        if (netService == null || choice.ChoiceId is not uint choiceId || !_pending.Contains(choice))
        {
            CouchLog.Warn("Can't answer teammate choice: it isn't pending or there's no loopback network.");
            return false;
        }

        bool closing = selectedIndexes.Count == 0 && choice.CanClose;
        if ((!closing && selectedIndexes.Count < choice.MinSelect) || selectedIndexes.Count > choice.MaxSelect || selectedIndexes.Any((int i) => i < 0 || i >= choice.Options.Count))
        {
            CouchLog.Warn($"Invalid answer [{string.Join(",", selectedIndexes)}] for {choice.Describe()}.");
            return false;
        }

        PlayerChoiceResult result = choice.Kind switch
        {
            CouchChoiceAnswerKind.CombatCards => PlayerChoiceResult.FromMutableCombatCards(selectedIndexes.Select((int i) => choice.Options[i]).ToList()),
            CouchChoiceAnswerKind.DeckCards => PlayerChoiceResult.FromMutableDeckCards(selectedIndexes.Select((int i) => choice.Options[i]).ToList()),
            CouchChoiceAnswerKind.Index => PlayerChoiceResult.FromIndex(selectedIndexes.Count == 0 ? null : selectedIndexes[0]),
            _ => PlayerChoiceResult.FromIndexes(selectedIndexes.ToList())
        };

        _pending.Remove(choice);
        string picked = string.Join(", ", selectedIndexes.Select((int i) => choice.Options[i].Title));
        CouchLog.Info($"Teammate {choice.Player.NetId} answers choice {choiceId}: [{picked}].");
        netService.DispatchLoopback(new PlayerChoiceMessage { choiceId = choiceId, result = result.ToNetData() }, choice.Player.NetId);
        return true;
    }

    /// <summary>
    /// Answers an <see cref="CouchChoiceAnswerKind.Index"/> choice with one index into the cards followed by the extra
    /// options, or with no index to close it (only when <see cref="CouchTeammateChoice.CanClose"/>).
    /// </summary>
    public static bool AnswerIndex(CouchTeammateChoice choice, int? index)
    {
        LocalLoopbackHostGameService? netService = LocalSelfCoopContext.NetService;
        if (netService == null || choice.ChoiceId is not uint choiceId || !_pending.Contains(choice) || choice.Kind is not (CouchChoiceAnswerKind.Index or CouchChoiceAnswerKind.PlayerId))
        {
            CouchLog.Warn("Can't answer teammate choice by index: it isn't pending, isn't an index choice, or there's no loopback network.");
            return false;
        }

        int optionCount = choice.Options.Count + choice.ExtraOptions.Count;
        if (index.HasValue ? index < 0 || index >= optionCount : !choice.CanClose)
        {
            CouchLog.Warn($"Invalid answer {index?.ToString() ?? "none"} for {choice.Describe()}.");
            return false;
        }

        _pending.Remove(choice);
        string picked = index == null ? "closed" : index < choice.Options.Count ? choice.Options[index.Value].Title : choice.ExtraOptions[index.Value - choice.Options.Count];
        CouchLog.Info($"Teammate {choice.Player.NetId} answers choice {choiceId}: {picked}.");
        PlayerChoiceResult result = choice.Kind == CouchChoiceAnswerKind.PlayerId
            ? PlayerChoiceResult.FromPlayerId(index.HasValue ? choice.OptionPlayerIds[index.Value - choice.Options.Count] : null)
            : PlayerChoiceResult.FromIndex(index);
        netService.DispatchLoopback(new PlayerChoiceMessage { choiceId = choiceId, result = result.ToNetData() }, choice.Player.NetId);
        return true;
    }

    /// <summary>Spike hotkey (F5): answer the oldest pending teammate choice with the first allowed option(s).</summary>
    public static void AnswerOldestWithFirstOptions()
    {
        CouchTeammateChoice? choice = Pending.FirstOrDefault();
        if (choice == null)
        {
            CouchLog.Info("No teammate choice is pending.");
            return;
        }

        int count = Math.Min(Math.Max(choice.MinSelect, Math.Min(1, choice.MaxSelect)), choice.Options.Count);
        Answer(choice, Enumerable.Range(0, count).ToList());
    }

    private static void PruneIfCombatOver()
    {
        if ((_pending.Count > 0 || _requested.Count > 0) && !CombatManager.Instance.IsInProgress)
        {
            _pending.RemoveAll((CouchTeammateChoice c) => c.IsCombatChoice);
            foreach (ulong id in _requested.Where((KeyValuePair<ulong, CouchTeammateChoice> r) => r.Value.IsCombatChoice).Select((KeyValuePair<ulong, CouchTeammateChoice> r) => r.Key).ToList())
            {
                _requested.Remove(id);
            }
        }
    }
}
