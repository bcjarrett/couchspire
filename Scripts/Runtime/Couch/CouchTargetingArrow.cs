using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;

namespace CouchSpire.Scripts.Runtime.Couch;

/// <summary>
/// The teammate's targeting arrow: the game's arrow (<c>NTargetingArrow</c>: same art, curve, colors and head
/// animation), drawn at a smaller size. The driver's arrow is sized for a card held up from the bottom of the screen; the
/// teammate's cards sit in a band near the top, close to their targets. Positions are global, so the node is top level.
/// </summary>
internal sealed partial class CouchTargetingArrow : Node2D
{
    private const int SegmentCount = 19;

    private static readonly string HeadPath = ImageHelper.GetImagePath("ui/combat/targeting_arrow_head.png");

    private static readonly string SegmentPath = ImageHelper.GetImagePath("ui/combat/targeting_arrow_segment.png");

    private readonly Sprite2D[] _segments = new Sprite2D[SegmentCount];

    private Sprite2D? _head;

    private Tween? _headTween;

    private Control? _from;

    private Vector2 _to;

    private Vector2? _current;

    /// <summary>Size relative to the driver's arrow.</summary>
    public float SizeScale { get; set; } = 0.55f;

    private Vector2 HeadDefaultScale => Vector2.One * 0.95f * SizeScale;

    private Vector2 HeadHoverScale => Vector2.One * 1.05f * SizeScale;

    public override void _Ready()
    {
        TopLevel = true;
        Texture2D? segment = CouchStyle.Load<Texture2D>(SegmentPath) ?? PreloadManager.Cache.GetTexture2D(SegmentPath);
        Texture2D? head = CouchStyle.Load<Texture2D>(HeadPath) ?? PreloadManager.Cache.GetTexture2D(HeadPath);
        for (int i = 0; i < SegmentCount; i++)
        {
            _segments[i] = new Sprite2D { Texture = segment };
            AddChild(_segments[i]);
        }

        _head = new Sprite2D { Texture = head, Scale = HeadDefaultScale };
        AddChild(_head);
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!Visible || _from == null || !IsInstanceValid(_from) || _current == null)
        {
            return;
        }

        // NTargetingArrow with a controller: the head eases toward the target.
        _current = _current.Value.Lerp(_to, CouchStyle.Smooth(delta, 14f));
        UpdateArrow(_current.Value);
    }

    public void StartDrawingFrom(Control from)
    {
        _from = from;
        ZIndex = from.ZIndex + 1;
        _current = from.GlobalPosition;
        _to = from.GlobalPosition;
        Visible = true;
    }

    public void UpdateDrawingTo(Vector2 position)
    {
        _to = position;
    }

    public void SetHighlightingOn(bool isEnemy)
    {
        _headTween?.Kill();
        _headTween = CreateTween();
        _headTween.TweenProperty(_head!, "scale", HeadHoverScale, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Elastic);
        Modulate = isEnemy ? StsColors.targetingArrowEnemy : StsColors.targetingArrowAlly;
    }

    public void StopDrawing()
    {
        _from = null;
        _current = null;
        Visible = false;
        _headTween?.Kill();
        if (_head != null)
        {
            _head.Scale = HeadDefaultScale;
        }

        Modulate = Colors.White;
    }

    private void UpdateArrow(Vector2 target)
    {
        Vector2 from = _from!.GlobalPosition;
        Sprite2D head = _head!;
        head.Position = target + new Vector2(0f, 88f * SizeScale).Rotated(head.Rotation);
        Vector2 end = target + new Vector2(0f, 40f * SizeScale).Rotated(head.Rotation);
        Vector2 control = new(from.X - (head.Position.X - from.X) * 0.25f, from.Y > 540f
            ? head.Position.Y + (head.Position.Y - from.Y) * 0.5f
            : head.Position.Y * 0.75f + from.Y * 0.25f);
        head.Rotation = (target - control).Angle() + (float)Math.PI / 2f;
        for (int i = 0; i < SegmentCount; i++)
        {
            _segments[i].Scale = Vector2.One * Mathf.Lerp(0.28f, 0.42f, i * 2f / SegmentCount) * SizeScale;
            _segments[i].Position = MathHelper.BezierCurve(from, end, control, i / 20f);
            if (i > 0)
            {
                _segments[i].Rotation = (_segments[i].Position - _segments[i - 1].Position).Angle() + (float)Math.PI / 2f;
            }
        }

        _segments[0].Rotation = (_segments[0].Position - _segments[1].Position).Angle() - (float)Math.PI / 2f;
    }
}
