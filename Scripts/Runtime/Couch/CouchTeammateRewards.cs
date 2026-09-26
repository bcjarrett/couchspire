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
/// </summary>
internal sealed partial class CouchTeammateRewards : Control
{
    public const string PanelNodeName = "CouchTeammateRewards";

    public const string CardRewardSource = "card reward";

    private const float PanelWidth = 440f;

    private const float RightMargin = 36f;

    private const float PanelTop = 150f;

    private const float RowHeight = 54f;

    private const float RowGap = 6f;

    private const float IconSize = 40f;

    private const float CardScale = 0.4f;

    private const double FlashSeconds = 3.0;

    private static readonly Color RowColor = new(0.1f, 0.14f, 0.18f, 0.92f);

    private static readonly Color RowCursorColor = new(0.28f, 0.34f, 0.4f, 0.97f);

    private static readonly Color CursorBorder = new(1f, 0.78f, 0.25f);

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

    private Panel? _background;

    private Label? _title;

    private Label? _hint;

    private int _cursor;

    private int _choiceCursor;

    private bool _busy;

    private bool _done;

    private CardReward? _activeCardReward;

    private bool _lastInputFromController;

    private string _flash = "";

    private double _flashUntil;

    /// <summary>True while the teammate still has their rewards panel open.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && !_instance._done;

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

        _instance!._lastInputFromController = playerId.HasValue;
        _instance.OnCommand(command);
        return true;
    }

    public override void _Ready()
    {
        _instance = this;
        TopLevel = true;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 50;
        _background = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _background.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.05f, 0.07f, 0.88f),
            BorderColor = new Color(0.55f, 0.48f, 0.32f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        });
        AddChild(_background);
        _title = CreateLabel(24, new Color("f3efe6"));
        _hint = CreateLabel(16, new Color("d8d2c4"));
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hint.Size = new Vector2(PanelWidth - 32f, 0f);

        foreach (Reward reward in _set.Rewards)
        {
            _rows.Add(CreateRow(IconFor(reward)));
        }

        _rows.Add(CreateRow(null));
        SetProcess(true);
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

        Vector2 viewport = GetViewportRect().Size;
        Position = new Vector2(viewport.X - PanelWidth - RightMargin, CouchTeammateRelicBar.TopBelowBar(PanelTop));
        float height = choice == null ? LayoutRows() : LayoutChoice(choice);
        _background!.Size = new Vector2(PanelWidth, height);
    }

    private CouchTeammateChoice? PendingCardChoice()
    {
        return CouchTeammateChoices.Pending.FirstOrDefault((CouchTeammateChoice c) => c.Player == _set.Player && c.Source == CardRewardSource);
    }

    private void OnCommand(CouchHudCommand command)
    {
        CouchTeammateChoice? choice = PendingCardChoice();
        if (choice != null)
        {
            OnChoiceCommand(choice, command);
            return;
        }

        int rowCount = _rows.Count;
        switch (command)
        {
            case CouchHudCommand.Left:
            case CouchHudCommand.Up:
                _cursor = (_cursor - 1 + rowCount) % rowCount;
                break;
            case CouchHudCommand.Right:
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                _cursor = (_cursor + 1) % rowCount;
                break;
            case CouchHudCommand.Accept:
                if (_cursor == rowCount - 1)
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
                _cursor = rowCount - 1;
                break;
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

        foreach (string _ in choice.ExtraOptions)
        {
            _choiceExtras.Add(CreateRow(null));
        }
    }

    private void ClearChoiceView()
    {
        foreach (NCard node in _choiceCards)
        {
            CouchCards.Free(node);
        }

        _choiceCards.Clear();
        foreach (RowView extra in _choiceExtras)
        {
            extra.Root.QueueFree();
        }

        _choiceExtras.Clear();
        _shownChoiceCards = new List<CardModel>();
    }

    private float LayoutRows()
    {
        _title!.Text = $"{SeatLabel()} · {_set.Player.Character.Title.GetFormattedText()} rewards    Gold {_set.Player.Gold}";
        _title.Position = new Vector2(16f, 12f);
        float y = 56f;
        for (int i = 0; i < _rows.Count; i++)
        {
            RowView row = _rows[i];
            bool isDoneRow = i == _rows.Count - 1;
            Reward? reward = isDoneRow ? null : _set.Rewards[i];
            bool taken = reward?.SuccessfullySelected ?? false;
            row.Label.Text = isDoneRow
                ? "Done — skip anything left"
                : (taken ? "Taken: " : "") + Describe(reward!);
            StyleRow(row, i == _cursor, dimmed: taken);
            row.Root.Position = new Vector2(12f, y);
            y += RowHeight + RowGap;
        }

        string keys = _lastInputFromController
            ? "D-pad move · A take · Y jump to Done"
            : "J/L move · I take · O jump to Done";
        SetHint(y + 4f, _busy ? "…" : keys);
        return _hint!.Position.Y + _hint.GetMinimumSize().Y + 14f;
    }

    private float LayoutChoice(CouchTeammateChoice choice)
    {
        _title!.Text = $"{SeatLabel()} · Choose a card";
        _title.Position = new Vector2(16f, 12f);
        Vector2 cardSize = NCard.defaultSize * CardScale;
        float spacing = cardSize.X + 10f;
        float rowWidth = spacing * Math.Max(0, _choiceCards.Count - 1);
        float firstCenterX = PanelWidth * 0.5f - rowWidth * 0.5f;
        float centerY = 60f + cardSize.Y * 0.5f + 8f;
        for (int i = 0; i < _choiceCards.Count; i++)
        {
            bool isCursor = i == _choiceCursor;
            NCard node = _choiceCards[i];
            node.Position = new Vector2(firstCenterX + i * spacing, centerY + (isCursor ? 10f : 0f));
            float scale = CardScale * (isCursor ? 1.1f : 1f);
            node.Scale = new Vector2(scale, scale);
            node.ZIndex = isCursor ? 2 : 0;
            node.Modulate = isCursor ? Colors.White : new Color(0.75f, 0.75f, 0.75f);
        }

        float y = centerY + cardSize.Y * 0.5f + 26f;
        for (int i = 0; i < _choiceExtras.Count; i++)
        {
            RowView row = _choiceExtras[i];
            row.Label.Text = choice.ExtraOptions[i];
            StyleRow(row, _choiceCursor == _choiceCards.Count + i, dimmed: false);
            row.Root.Position = new Vector2(12f, y);
            y += RowHeight + RowGap;
        }

        string keys = _lastInputFromController
            ? "D-pad move · A pick · B back"
            : "J/L move · I pick · K back";
        SetHint(y + 4f, keys);
        return _hint!.Position.Y + _hint.GetMinimumSize().Y + 14f;
    }

    private void SetHint(float y, string keys)
    {
        _hint!.Position = new Vector2(16f, y);
        _hint.Text = Time.GetTicksMsec() / 1000.0 < _flashUntil ? $"{_flash}\n{keys}" : keys;
    }

    private void StyleRow(RowView row, bool isCursor, bool dimmed)
    {
        row.Style.BgColor = isCursor ? RowCursorColor : RowColor;
        int border = isCursor ? 2 : 0;
        row.Style.BorderColor = CursorBorder;
        row.Style.BorderWidthLeft = border;
        row.Style.BorderWidthRight = border;
        row.Style.BorderWidthTop = border;
        row.Style.BorderWidthBottom = border;
        row.Root.Modulate = new Color(1f, 1f, 1f, dimmed ? 0.45f : 1f);
    }

    private RowView CreateRow(Texture2D? icon)
    {
        StyleBoxFlat style = new()
        {
            BgColor = RowColor,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        Panel root = new() { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(PanelWidth - 24f, RowHeight), ZIndex = 1 };
        root.AddThemeStyleboxOverride("panel", style);
        float textLeft = 14f;
        if (icon != null)
        {
            TextureRect iconRect = new()
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Size = new Vector2(IconSize, IconSize),
                Position = new Vector2(8f, (RowHeight - IconSize) * 0.5f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            root.AddChild(iconRect);
            textLeft = IconSize + 18f;
        }

        Label label = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(textLeft, 0f),
            Size = new Vector2(PanelWidth - 24f - textLeft - 8f, RowHeight),
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true
        };
        label.AddThemeFontSizeOverride("font_size", 19);
        label.AddThemeColorOverride("font_color", new Color("f3efe6"));
        root.AddChild(label);
        AddChild(root);
        return new RowView(root, style, label);
    }

    private Label CreateLabel(int fontSize, Color color)
    {
        Label label = new() { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 1 };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color("111111"));
        label.AddThemeConstantOverride("outline_size", 4);
        AddChild(label);
        return label;
    }

    /// <summary>Shows a refusal or a "still waiting" note, with the "no" sound.</summary>
    private void Flash(string message)
    {
        _flash = message;
        _flashUntil = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        CouchSfx.Deny();
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

    private static void DisableInteraction(Control control)
    {
        control.MouseFilter = MouseFilterEnum.Ignore;
        control.FocusMode = FocusModeEnum.None;
        foreach (Node child in control.GetChildren())
        {
            if (child is Control childControl)
            {
                DisableInteraction(childControl);
            }
        }
    }

    private readonly record struct RowView(Panel Root, StyleBoxFlat Style, Label Label);
}
