using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's Crystal Sphere minigame ("divination"), in a panel beside the driver's screen. The game only opens the
/// grid for the local player (<c>CrystalSphereMinigame.PlayMinigame</c> checks <c>LocalContext.IsMe</c>; online, each
/// player runs it on their own machine), so without this the teammate paid for the event and got nothing. The panel
/// drives the game's own <see cref="CrystalSphereMinigame"/> (tool, cell clearing, reveals, the Doubt curse) with the
/// teammate's controls, draws the items with the game's <see cref="NCrystalSphereItem"/> nodes under a fog grid, and
/// once the divinations are spent offers the revealed rewards the way a remote player's are offered, so they land in
/// <see cref="CouchTeammateRewards"/>. The teammate's event panel hides while this is up, and the driver can't leave the
/// room until it's done.
/// </summary>
internal sealed partial class CouchTeammateCrystalSphere : CouchPanel
{
    public const string PanelNodeName = "CouchTeammateCrystalSphere";

    /// <summary>The game's grid is always 11×11 (<c>CrystalSphereMinigame._defaultWidth</c>).</summary>
    private const int GridCells = 11;

    private const float CellSize = 32f;

    /// <summary><c>NCrystalSphereScreen</c>'s cell pitch; item nodes are sized in it, so the item layer is scaled down.</summary>
    private const float GameCellSize = 57f;

    /// <summary>The game's item scene (and its glints) draws above its siblings, so the fog is lifted well over it.</summary>
    private const int FogZIndex = 500;

    private const float SideMargin = 36f;

    private const float PanelTop = 130f;

    /// <summary>How long the finished grid stays up before the rewards are offered (the game waits 0.75 s).</summary>
    private const float FinishedLingerSeconds = 1.25f;

    private static readonly Color FogColor = new(0.10f, 0.07f, 0.18f, 0.97f);

    private static readonly Color FogHighlightColor = new(0.42f, 0.32f, 0.68f, 0.97f);

    private static readonly Color BackdropColor = new(0.03f, 0.02f, 0.06f, 0.85f);

    private static readonly AccessTools.FieldRef<CrystalSphereMinigame, TaskCompletionSource> CompletionSourceRef =
        AccessTools.FieldRefAccess<CrystalSphereMinigame, TaskCompletionSource>("_completionSource");

    private static readonly AccessTools.FieldRef<CrystalSphereMinigame, List<CrystalSphereItem>> RevealedRef =
        AccessTools.FieldRefAccess<CrystalSphereMinigame, List<CrystalSphereItem>>("_revealed");

    private static readonly MethodInfo? OfferRewardsMethod =
        AccessTools.Method(typeof(OneOffSynchronizer), "OfferCrystalSphereRewards");

    private static CouchTeammateCrystalSphere? _instance;

    private CrystalSphereMinigame? _entity;

    private Player? _owner;

    private Control? _grid;

    private Control? _items;

    /// <summary>Each item's node, shown once any of its cells is uncovered (see <see cref="UpdateItemVisibility"/>).</summary>
    private readonly List<(CrystalSphereItem Item, Control Node)> _itemNodes = new();

    private ColorRect[,]? _fog;

    private Panel? _cursorFrame;

    private Label? _status;

    private int _cursorX = GridCells / 2;

    private int _cursorY = GridCells / 2;

    private bool _busy;

    /// <summary>True while the teammate's minigame is up.</summary>
    public static bool IsActive => _instance != null && IsInstanceValid(_instance) && _instance.Visible;

    /// <summary>Divinations the teammate has left while the panel is up; null otherwise.</summary>
    public static int? DivinationsLeft => IsActive ? _instance!._entity?.DivinationCount : null;

    protected override float PanelWidth => CouchFrame.PadLeft + GridCells * CellSize + CouchFrame.PadRight;

    public static void Attach(NEventRoom room)
    {
        if (room.GetNodeOrNull(PanelNodeName) == null)
        {
            room.AddChild(new CouchTeammateCrystalSphere { Name = PanelNodeName });
        }
    }

    public static bool Handle(ulong? playerId, CouchHudCommand command)
    {
        if (!IsActive || (playerId.HasValue && _instance!._owner?.NetId != playerId.Value))
        {
            return false;
        }

        _instance!.LastInputFromController = playerId.HasValue;
        _instance.OnCommand(command);
        return true;
    }

    /// <summary>
    /// Stands in for <c>CrystalSphereMinigame.PlayMinigame</c> when the minigame's owner is the teammate: runs it in
    /// the panel, then offers the revealed rewards. Like the game's version, it throws if the minigame is cancelled
    /// (the room closed under it).
    /// </summary>
    public static async Task PlayForTeammate(CrystalSphereMinigame minigame, Player owner)
    {
        CouchTeammateCrystalSphere? panel = _instance != null && IsInstanceValid(_instance) ? _instance : null;
        if (panel == null)
        {
            CouchLog.Warn($"Teammate {owner.NetId} started the Crystal Sphere minigame, but there is no panel to play it in; skipping it.");
            return;
        }

        CouchLog.Info($"Teammate {owner.NetId} starts the Crystal Sphere minigame ({minigame.DivinationCount} divinations).");
        panel.Open(minigame, owner);
        try
        {
            await CompletionSourceRef(minigame).Task;
            await Cmd.Wait(FinishedLingerSeconds);
        }
        finally
        {
            if (IsInstanceValid(panel))
            {
                panel.Close();
            }
        }

        List<CrystalSphereItem> revealed = RevealedRef(minigame).ToList();
        CouchLog.Info($"Teammate {owner.NetId} finished the Crystal Sphere minigame; offering {revealed.Count} revealed item(s): {string.Join(", ", revealed.Select((CrystalSphereItem i) => i.GetType().Name))}.");

        // OneOffSynchronizer.DoLocalCrystalSphereRewards only accepts the local player; the private offer it ends with is
        // what a remote player's rewards message runs on every other machine.
        if (OfferRewardsMethod?.Invoke(RunManager.Instance.OneOffSynchronizer, new object[] { owner, revealed, minigame.Rng }) is Task offer)
        {
            await offer;
        }
        else
        {
            CouchLog.Warn("Could not offer the teammate's Crystal Sphere rewards: OneOffSynchronizer.OfferCrystalSphereRewards not found.");
        }
    }

    public override void _Ready()
    {
        base._Ready();
        _instance = this;
        TopLevel = true;
        ZIndex = 50;
        _status = CouchStyle.CreateLabel(this, 19, wrapWidth: PanelWidth - CouchFrame.PadLeft - CouchFrame.PadRight);
    }

    public override void _ExitTree()
    {
        // The room is closing under the minigame (save and quit): end it as the game's screen does, so
        // PlayForTeammate's wait is cancelled instead of hanging.
        CrystalSphereMinigame? entity = _entity;
        Close();
        entity?.ForceMinigameEnd();
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible || _entity == null || _owner == null)
        {
            return;
        }

        PlaceOnSide(CouchConfig.EventPanelOnLeft, PanelTop, SideMargin);
        SetTitle($"{SeatLabel(_owner)} · Crystal Sphere");

        _items!.Visible = Modulate.A >= 0.99f;
        UpdateItemVisibility(_entity);
        Rect2I? area = null;
        for (int x = 0; x < GridCells; x++)
        {
            for (int y = 0; y < GridCells; y++)
            {
                CrystalSphereCell cell = _entity.cells[x, y];
                ColorRect fog = _fog![x, y];
                fog.Visible = cell.IsHidden;
                fog.Color = cell.IsHighlighted ? FogHighlightColor : FogColor;
                if (cell.IsHighlighted)
                {
                    Rect2I cellRect = new(x, y, 1, 1);
                    area = area?.Merge(cellRect) ?? cellRect;
                }
            }
        }

        _cursorFrame!.Visible = area.HasValue;
        if (area.HasValue)
        {
            _cursorFrame.Position = new Vector2(area.Value.Position.X, area.Value.Position.Y) * CellSize;
            _cursorFrame.Size = new Vector2(area.Value.Size.X, area.Value.Size.Y) * CellSize;
        }

        float top = ContentTop;
        _grid!.Position = new Vector2(CouchFrame.PadLeft, top);
        float y2 = top + GridCells * CellSize + 10f;
        bool big = _entity.CrystalSphereTool == CrystalSphereMinigame.CrystalSphereToolType.Big;
        _status!.Text = _entity.IsFinished
            ? "All divinations used. Your rewards are next."
            : $"Divinations left: {_entity.DivinationCount} · Tool: {(big ? "big (3×3)" : "small (1 cell)")}";
        _status.Position = new Vector2(CouchFrame.PadLeft, y2);
        _status.Size = _status.GetMinimumSize();
        y2 += _status.Size.Y + 6f;

        string hint = _entity.IsFinished
            ? ""
            : $"{Keys("J/L/U/K", "D-pad")} move · {Keys("I", "A")} divine · {Keys("O", "X")} {(big ? "small" : "big")} tool";
        FinishLayout(y2, hint);
    }

    private void Open(CrystalSphereMinigame minigame, Player owner)
    {
        Close();
        _entity = minigame;
        _owner = owner;
        _busy = false;
        _cursorX = GridCells / 2;
        _cursorY = GridCells / 2;
        BuildGrid(minigame);
        foreach (CrystalSphereItem item in minigame.Items)
        {
            item.Revealed += OnItemRevealed;
        }

        Visible = true;
        UpdateHover();
    }

    private void Close()
    {
        if (_entity != null)
        {
            foreach (CrystalSphereItem item in _entity.Items)
            {
                item.Revealed -= OnItemRevealed;
            }

            _entity.UnsetHoveredCell();
        }

        _entity = null;
        _owner = null;
        _grid?.QueueFree();
        _grid = null;
        _items = null;
        _itemNodes.Clear();
        _fog = null;
        _cursorFrame = null;
        Visible = false;
    }

    /// <summary>
    /// The game's item scene has glint effects that draw over any fog, so an item stays hidden until at least one of
    /// its cells is uncovered; from then on the fog over its other cells hides the rest, as on the game's screen.
    /// </summary>
    private void UpdateItemVisibility(CrystalSphereMinigame entity)
    {
        foreach ((CrystalSphereItem item, Control node) in _itemNodes)
        {
            if (node.Visible)
            {
                continue;
            }

            for (int x = item.Position.X; x < item.Position.X + item.Size.X && !node.Visible; x++)
            {
                for (int y = item.Position.Y; y < item.Position.Y + item.Size.Y; y++)
                {
                    if (!entity.cells[x, y].IsHidden)
                    {
                        node.Visible = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>A backdrop, the game's item nodes (scaled from its 57 px cells), the fog on top, then the cursor frame.</summary>
    private void BuildGrid(CrystalSphereMinigame minigame)
    {
        float side = GridCells * CellSize;
        _grid = new Control { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(side, side) };
        AddChild(_grid);
        _grid.AddChild(new ColorRect { Color = BackdropColor, Size = new Vector2(side, side), MouseFilter = MouseFilterEnum.Ignore });

        // Hidden until the panel has faded in: while it is see-through, so is the fog, and the items would show.
        Control items = new() { MouseFilter = MouseFilterEnum.Ignore, Scale = Vector2.One * (CellSize / GameCellSize), Visible = false };
        _grid.AddChild(items);
        _items = items;
        foreach (CrystalSphereItem item in minigame.Items)
        {
            Vector2 position = new Vector2(item.Position.X, item.Position.Y) * GameCellSize;
            Vector2 size = new Vector2(item.Size.X, item.Size.Y) * GameCellSize;
            Control? node = NCrystalSphereItem.Create(item);
            node ??= new TextureRect
            {
                Texture = item.Texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
            };
            node.MouseFilter = MouseFilterEnum.Ignore;
            node.Size = size;
            node.Visible = false;
            items.AddChild(node);
            node.Position = position;
            _itemNodes.Add((item, node));
        }

        _fog = new ColorRect[GridCells, GridCells];
        for (int x = 0; x < GridCells; x++)
        {
            for (int y = 0; y < GridCells; y++)
            {
                ColorRect fog = new()
                {
                    Color = FogColor,
                    MouseFilter = MouseFilterEnum.Ignore,
                    ZIndex = FogZIndex,
                    Position = new Vector2(x * CellSize + 1f, y * CellSize + 1f),
                    Size = new Vector2(CellSize - 2f, CellSize - 2f)
                };
                _grid.AddChild(fog);
                _fog[x, y] = fog;
            }
        }

        _cursorFrame = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = FogZIndex + 1 };
        _cursorFrame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = CouchStyle.Gold,
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3
        });
        _grid.AddChild(_cursorFrame);
    }

    private void OnCommand(CouchHudCommand command)
    {
        if (_entity == null || _entity.IsFinished)
        {
            return;
        }

        switch (command)
        {
            case CouchHudCommand.Left:
                Move(-1, 0);
                break;
            case CouchHudCommand.Right:
                Move(1, 0);
                break;
            case CouchHudCommand.Up:
                Move(0, -1);
                break;
            case CouchHudCommand.Down:
            case CouchHudCommand.ToggleRow:
                Move(0, 1);
                break;
            case CouchHudCommand.Back:
                // The keyboard has no up/down keys: U moves down and K (Back) moves up. B on a controller does nothing.
                if (!LastInputFromController)
                {
                    Move(0, -1);
                }

                break;
            case CouchHudCommand.Submit:
                bool big = _entity.CrystalSphereTool == CrystalSphereMinigame.CrystalSphereToolType.Big;
                _entity.SetTool(big ? CrystalSphereMinigame.CrystalSphereToolType.Small : CrystalSphereMinigame.CrystalSphereToolType.Big);
                break;
            case CouchHudCommand.Accept:
                Divine();
                break;
        }
    }

    private void Move(int dx, int dy)
    {
        _cursorX = Mathf.Clamp(_cursorX + dx, 0, GridCells - 1);
        _cursorY = Mathf.Clamp(_cursorY + dy, 0, GridCells - 1);
        UpdateHover();
    }

    private void UpdateHover()
    {
        if (_entity == null || _entity.IsFinished)
        {
            _entity?.UnsetHoveredCell();
            return;
        }

        _entity.SetHoveredCell(_entity.cells[_cursorX, _cursorY]);
    }

    private void Divine()
    {
        CrystalSphereMinigame entity = _entity!;
        if (_busy)
        {
            return;
        }

        bool anyHidden = false;
        foreach (CrystalSphereCell cell in entity.cells)
        {
            anyHidden |= cell.IsHighlighted && cell.IsHidden;
        }

        if (!anyHidden)
        {
            Flash("Nothing hidden there");
            return;
        }

        _busy = true;
        CrystalSphereCell target = entity.cells[_cursorX, _cursorY];
        CouchLog.Info($"Teammate {_owner?.NetId} divines at ({target.X},{target.Y}) with the {entity.CrystalSphereTool} tool; {entity.DivinationCount - 1} left.");
        TaskHelper.RunSafely(DivineAsync(entity, target));
    }

    private async Task DivineAsync(CrystalSphereMinigame entity, CrystalSphereCell target)
    {
        try
        {
            await entity.CellClicked(target);
        }
        finally
        {
            _busy = false;
            if (_entity == entity)
            {
                UpdateHover();
            }
        }
    }

    private void OnItemRevealed(CrystalSphereItem item)
    {
        string found = item switch
        {
            CrystalSphereRelic => "a relic",
            CrystalSpherePotion => "a potion",
            CrystalSphereCardReward => "a card reward",
            CrystalSphereGold => "gold",
            CrystalSphereCurse => "a curse",
            _ => "something"
        };
        Flash($"Found {found}!", denied: !item.IsGood);
    }
}
