using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace LocalMultiControl.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's own rewards, in a panel beside the driver's screen, claimed with the teammate's own controls.
/// Two sources:
/// <list type="bullet">
/// <item>Post-combat rewards (the fork's merged offer, untracked): claimed with <see cref="Reward.SelectUnsynchronized"/>
/// as the fork's merged screen does; the driver can't proceed until the teammate is done.</item>
/// <item>Rewards offered to the teammate by events or relics (tracked by <see cref="RewardsSetSynchronizer"/>, "synchronized"):
/// claimed and skipped through the synchronizer, exactly as when a remote player's reward messages arrive, so whatever
/// offered them continues once the teammate is done.</item>
/// </list>
/// A card reward runs the game's remote-player path and waits for the teammate's pick, which this panel answers through
/// <see cref="CouchTeammateChoices"/>.
/// Drawn like the driver's reward screen: the reward item buttons, gold text on focus, the selection reticle.
/// </summary>
internal sealed partial class CouchTeammateRewards : CouchPanel
{
    public const string PanelNodeName = "CouchTeammateRewards";

    public const string CardRewardSource = "card reward";

    private const float RightMargin = 36f;

    private const float PanelTop = 150f;

    private const float CardScale = 0.4f;

    private static readonly System.Reflection.MethodInfo? SelectRewardForPlayerMethod =
        AccessTools.Method(typeof(RewardsSetSynchronizer), "SelectRewardForPlayer", new[] { typeof(Player), typeof(int) });

    private static readonly System.Reflection.MethodInfo? SkipRewardsSetMethod =
        AccessTools.Method(typeof(RewardsSetSynchronizer), "SkipRewardsSetOnStackTopForPlayer", new[] { typeof(Player) });

    private static CouchTeammateRewards? _instance;

    private readonly List<RowView> _rows = new();

    private readonly List<NCard> _choiceCards = new();

    private readonly List<RowView> _choiceExtras = new();

    private List<CardModel> _shownChoiceCards = new();

    private CouchTeammateChoice? _shownChoice;

    private RewardsSet _set = null!;

    private Node _host = null!;

    private bool _synchronized;

    private int _cursor;

    private int _choiceCursor;

    private bool _busy;

    private bool _done;

    private CardReward? _activeCardReward;

    /// <summary>The potion reward waiting on the teammate to make room in their belt.</summary>
    private Reward? _waitingForRoom;

    /// <summary>True while the teammate still has their rewards panel open.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && !_instance._done;

    protected override float PanelWidth => 460f;

    protected override float RowIconSize => 52f;

    /// <summary>True while the teammate is picking from a card reward.</summary>
    public static bool IsChoosingCard => IsActive && _instance!._shownChoice != null;

    public static bool OwnsReward(Reward reward)
    {
        return IsActive && _instance!._set.Rewards.Contains(reward);
    }

    /// <summary>True if the driver's reward screen <paramref name="screen"/> must wait for the teammate.</summary>
    public static bool BlocksProceed(Node screen)
    {
        return IsActive && _instance!._host == screen;
    }

    /// <summary>
    /// Picks the set the teammate panel should take: the first other local character with rewards, when simultaneous
    /// mode is on. Everyone else stays in the driver's merged screen.
    /// </summary>
    public static RewardsSet? PickTeammateSet(IEnumerable<RewardsSet> sets, Player displayPlayer)
    {
        if (!CouchConfig.SimultaneousEnabled || !LocalSelfCoopContext.IsEnabled)
        {
            return null;
        }

        return sets.FirstOrDefault((RewardsSet s) =>
            s.Player != displayPlayer
            && s.Player.Creature?.IsDead != true
            && LocalSelfCoopContext.LocalPlayerIds.Contains(s.Player.NetId)
            && s.Rewards.Count > 0);
    }

    /// <summary>True if rewards offered to <paramref name="player"/> should go to their panel instead of the main screen.</summary>
    public static bool ShouldTakeRewards(Player player)
    {
        return CouchTeammate.IsSimultaneousTeammate(player) && !MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn;
    }

    /// <param name="synchronized">True for sets begun with <see cref="RewardsSetSynchronizer.BeginRewardsSet"/>.</param>
    public static void Open(Node host, RewardsSet set, bool synchronized = false)
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            _instance.QueueFree();
        }

        CouchTeammateRewards panel = new() { Name = PanelNodeName, _set = set, _host = host, _synchronized = synchronized };
        host.AddChild(panel);
        CouchLog.Info($"Teammate {set.Player.NetId} takes their {set.Rewards.Count} reward(s) in their own panel{(synchronized ? " (tracked set)" : "")}.");
    }

    public static void NotifyProceedBlocked()
    {
        if (!IsActive)
        {
            return;
        }

        string seat = CouchSeats.FindByPlayer(_instance!._set.Player.NetId)?.Label ?? "P2";
        NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create($"Waiting for {seat} to finish their rewards"));
        _instance.Flash($"{LocalLabel()} is waiting for you");
    }

    /// <summary>
    /// Called before the game waits for a remote choice: if it's the teammate's card reward, describe the cards and
    /// the extra options (Skip, Reroll...) so the panel can answer it.
    /// </summary>
    public static void PrepareChoiceRequest(Player player)
    {
        CardReward? reward = _instance != null && IsInstanceValid(_instance) ? _instance._activeCardReward : null;
        if (reward == null || reward.Player != player)
        {
            return;
        }

        List<string> extras = CardRewardAlternative.Generate(reward)
            .Select((CardRewardAlternative a) => CouchText.Plain(a.Title.GetFormattedText()))
            .ToList();
        CouchTeammateChoices.Request(new CouchTeammateChoice
        {
            Player = player,
            Kind = CouchChoiceAnswerKind.Index,
            Options = reward.Cards.ToList(),
            ExtraOptions = extras,
            MinSelect = 0,
            MaxSelect = 1,
            CanClose = true,
            IsCombatChoice = false,
            Prompt = "Choose a card",
            Source = CardRewardSource
        });
    }

    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        if (!IsActive || (playerId.HasValue && _instance!._set.Player.NetId != playerId.Value))
        {
            return false;
        }

        _instance!.LastInputFromController = playerId.HasValue;
        _instance.OnCommand(command);
        return true;
    }

    public override void _Ready()
    {
        base._Ready();
        _instance = this;
        TopLevel = true;
        ZIndex = 50;
        foreach (Reward reward in _set.Rewards)
        {
            _rows.Add(CreateRow(IconFor(reward), CouchButtonKind.Reward));
        }

        _rows.Add(CreateRow(null, CouchButtonKind.Reward));
    }

    public override void _ExitTree()
    {
        ClearChoiceView();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        if (_done)
        {
            return;
        }

        if (!_busy && _set.Rewards.All((Reward r) => r.SuccessfullySelected))
        {
            Finish(allTaken: true);
            return;
        }

        CouchTeammateChoice? choice = PendingCardChoice();
        if (choice != _shownChoice)
        {
            ShowChoice(choice);
        }

        Visible = true;
        PlaceOnSide(left: false, PanelTop, RightMargin);
        UpdatePotionDiscard();
        if (IsDiscardingPotion)
        {
            LayoutDiscard();
        }
        else if (choice == null)
        {
            LayoutRewards();
        }
        else
        {
            LayoutChoice(choice);
        }
    }

    private CouchTeammateChoice? PendingCardChoice()
    {
        return CouchTeammateChoices.Pending.FirstOrDefault((CouchTeammateChoice c) => c.Player == _set.Player && c.Source == CardRewardSource);
    }

    private void OnCommand(CouchHudCommand command)
    {
        if (IsDiscardingPotion)
        {
            OnPotionDiscardCommand(command);
            return;
        }

        CouchTeammateChoice? choice = PendingCardChoice();
        if (choice != null)
        {
            OnChoiceCommand(choice, command);
            return;
        }

        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                MoveCursor(-1);
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                MoveCursor(1);
                break;
            case CouchHudCommand.Accept:
                if (_cursor == _rows.Count - 1)
                {
                    Finish(allTaken: false);
                }
                else
                {
                    Claim(_set.Rewards[_cursor]);
                }

                break;
            case CouchHudCommand.EndTurn:
            case CouchHudCommand.SubmitOrEndTurn:
            case CouchHudCommand.Submit:
                _cursor = _rows.Count - 1;
                break;
        }
    }

    /// <summary>Taken rewards leave the list, as they do on the driver's reward screen; the Done row always stays.</summary>
    private bool IsRowShown(int index)
    {
        return index >= _set.Rewards.Count || !_set.Rewards[index].SuccessfullySelected;
    }

    /// <summary>Moves to the next row still in the list.</summary>
    private void MoveCursor(int step)
    {
        int count = _rows.Count;
        for (int i = 1; i <= count; i++)
        {
            int index = ((_cursor + step * i) % count + count) % count;
            if (IsRowShown(index))
            {
                _cursor = index;
                return;
            }
        }
    }

    private void OnChoiceCommand(CouchTeammateChoice choice, CouchHudCommand command)
    {
        int optionCount = choice.Options.Count + choice.ExtraOptions.Count;
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                _choiceCursor = (_choiceCursor - 1 + optionCount) % optionCount;
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _choiceCursor = (_choiceCursor + 1) % optionCount;
                break;
            case CouchHudCommand.Accept:
            case CouchHudCommand.Submit:
            case CouchHudCommand.SubmitOrEndTurn:
                CouchTeammateChoices.AnswerIndex(choice, _choiceCursor);
                break;
            case CouchHudCommand.Back:
                CouchTeammateChoices.AnswerIndex(choice, null);
                break;
        }
    }

    private void Claim(Reward reward)
    {
        if (_busy)
        {
            return;
        }

        if (reward.SuccessfullySelected)
        {
            Flash("Already taken");
            return;
        }

        // Belt full: let the teammate throw out a potion first, as the driver can from the top bar.
        if (reward is PotionReward && !_set.Player.HasOpenPotionSlots)
        {
            _waitingForRoom = reward;
            OpenPotionDiscard(_set.Player, () => Claim(reward));
            return;
        }

        _busy = true;
        _activeCardReward = reward as CardReward;
        TaskHelper.RunSafely(ClaimAsync(reward));
    }

    private async System.Threading.Tasks.Task ClaimAsync(Reward reward)
    {
        try
        {
            bool taken;
            if (_synchronized && SelectRewardForPlayerMethod != null)
            {
                // What the synchronizer does when a remote player's RewardSelectedMessage arrives.
                await (System.Threading.Tasks.Task)SelectRewardForPlayerMethod.Invoke(RunManager.Instance.RewardsSetSynchronizer, new object[] { _set.Player, _set.Rewards.IndexOf(reward) })!;
                taken = reward.SuccessfullySelected;
            }
            else
            {
                taken = await reward.SelectUnsynchronized();
            }

            CouchLog.Info($"Teammate {_set.Player.NetId} {(taken ? "took" : "didn't take")} reward: {Describe(reward)}.");
            if (!taken && reward is PotionReward)
            {
                Flash("Potion slots are full");
            }
        }
        catch (Exception ex)
        {
            CouchLog.Warn($"Teammate reward failed: {ex.Message}");
            Flash("Couldn't take that reward");
        }
        finally
        {
            _busy = false;
            _activeCardReward = null;
        }
    }

    private void Finish(bool allTaken)
    {
        if (_busy)
        {
            Flash("Finish the current reward first");
            return;
        }

        if (!allTaken)
        {
            if (_synchronized && SkipRewardsSetMethod != null)
            {
                // What the synchronizer does when a remote player's RewardSetSkippedMessage arrives: skips the
                // untaken rewards and completes the set, so whatever offered it can continue.
                SkipRewardsSetMethod.Invoke(RunManager.Instance.RewardsSetSynchronizer, new object[] { _set.Player });
            }
            else
            {
                foreach (Reward reward in _set.Rewards.Where((Reward r) => !r.SuccessfullySelected))
                {
                    reward.OnSkipped();
                }
            }
        }

        _done = true;
        Visible = false;
        CouchLog.Info($"Teammate {_set.Player.NetId} is done with rewards ({(allTaken ? "took everything" : "skipped the rest")}).");
        QueueFree();
    }

    private void ShowChoice(CouchTeammateChoice? choice)
    {
        ClearChoiceView();
        _shownChoice = choice;
        _choiceCursor = 0;
        foreach (RowView row in _rows)
        {
            row.Root.Visible = choice == null;
        }

        if (choice == null)
        {
            return;
        }

        _shownChoiceCards = choice.Options.ToList();
        foreach (CardModel card in _shownChoiceCards)
        {
            NCard node = CouchCards.Create(card, this);
            node.Scale = new Vector2(CardScale, CardScale);
            _choiceCards.Add(node);
        }

        foreach (string extra in choice.ExtraOptions)
        {
            RowView row = CreateRow(null, CouchButtonKind.Reward);
            row.Label.Text = extra;
            _choiceExtras.Add(row);
        }
    }

    private void ClearChoiceView()
    {
        foreach (NCard node in _choiceCards)
        {
            CouchCards.Free(node);
        }

        _choiceCards.Clear();
        FreeRows(_choiceExtras);
        _shownChoiceCards = new List<CardModel>();
    }

    private void LayoutDiscard()
    {
        foreach (RowView row in _rows)
        {
            row.Root.Visible = false;
        }

        SetTitle($"{SeatLabel()} · Potion belt full");
        float y = LayoutPotionDiscard(ContentTop);
        FinishLayout(y, _waitingForRoom != null ? $"Discard one to take {Describe(_waitingForRoom)}" : "", $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} choose · {Keys("K", "B")} back");
    }

    private void LayoutRewards()
    {
        SetTitle($"{SeatLabel()} · {_set.Player.Character.Title.GetFormattedText()} rewards    Gold {_set.Player.Gold}");
        if (!IsRowShown(_cursor))
        {
            MoveCursor(1);
        }

        List<RowView> shown = new();
        for (int i = 0; i < _rows.Count; i++)
        {
            RowView row = _rows[i];
            row.Root.Visible = IsRowShown(i);
            if (!row.Root.Visible)
            {
                continue;
            }

            bool isDoneRow = i == _rows.Count - 1;
            row.Label.Text = isDoneRow ? "Done — skip anything left" : Describe(_set.Rewards[i]);
            StyleRow(row, i == _cursor, dimmed: false);
            shown.Add(row);
        }

        float y = LayoutRows(shown, ContentTop);
        FinishLayout(y, _busy ? "…" : $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} take · {Keys("O", "Y")} jump to Done");
    }

    private void LayoutChoice(CouchTeammateChoice choice)
    {
        SetTitle($"{SeatLabel()} · Choose a card");
        Vector2 cardSize = NCard.defaultSize * CardScale;
        float spacing = cardSize.X + 10f;
        float rowWidth = spacing * Math.Max(0, _choiceCards.Count - 1);
        float firstCenterX = PanelWidth * 0.5f - rowWidth * 0.5f - 8f;
        float centerY = ContentTop + cardSize.Y * 0.5f + 8f;
        for (int i = 0; i < _choiceCards.Count; i++)
        {
            bool isCursor = i == _choiceCursor;
            NCard node = _choiceCards[i];
            CouchCards.Glide(node, new Vector2(firstCenterX + i * spacing, centerY + (isCursor ? 10f : 0f)), CardScale * (isCursor ? 1.12f : 1f));
            node.ZIndex = isCursor ? 2 : 0;
            CouchCards.SetGlow(node, isCursor ? NCardHighlight.playableColor : null);
        }

        for (int i = 0; i < _choiceExtras.Count; i++)
        {
            StyleRow(_choiceExtras[i], _choiceCursor == _choiceCards.Count + i, dimmed: false);
        }

        float y = LayoutRows(_choiceExtras, centerY + cardSize.Y * 0.5f + 26f);
        FinishLayout(y, $"{Keys("J/L", "D-pad")} move · {Keys("I", "A")} pick · {Keys("K", "B")} back");
    }

    private string SeatLabel()
    {
        return CouchSeats.FindByPlayer(_set.Player.NetId)?.Label ?? "P2";
    }

    private static string LocalLabel()
    {
        ulong? driver = MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        return driver.HasValue ? CouchSeats.FindByPlayer(driver.Value)?.Label ?? "P1" : "P1";
    }

    private static string Describe(Reward reward)
    {
        return CouchText.Plain(reward.Description.GetFormattedText());
    }

    private static Texture2D? IconFor(Reward reward)
    {
        try
        {
            if (reward is PotionReward { Potion: PotionModel potion })
            {
                return potion.Image;
            }

            if (reward is RelicReward { Relic: RelicModel relic })
            {
                return relic.Icon;
            }

            string? path = AccessTools.Property(reward.GetType(), "IconPath")?.GetValue(reward) as string;
            return string.IsNullOrEmpty(path) ? null : PreloadManager.Cache.GetCompressedTexture2D(path);
        }
        catch (Exception ex)
        {
            CouchLog.Throttled("reward-icon", $"No icon for reward {reward.GetType().Name}: {ex.Message}", 10000);
            return null;
        }
    }

}
