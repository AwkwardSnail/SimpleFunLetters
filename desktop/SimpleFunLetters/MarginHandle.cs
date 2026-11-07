using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleFunLetters;

/// <summary>
/// One of the four margin arrows. Left and right sit above the page and point
/// down; top and bottom sit to the left of the page and point right. An outline
/// arrow means its margin line is hidden; a filled arrow means it is showing.
/// </summary>
public sealed class MarginHandle : FrameworkElement
{
    public const double Size = 20;

    static readonly Brush Bright = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xDC, 0x8A)));
    static readonly Pen Outline = Freeze(new Pen(Bright, 1.5) { LineJoin = PenLineJoin.Round });
    static readonly Pen FocusPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0x8A, 0x56)), 1) { DashStyle = DashStyles.Dash });

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    bool lineShown;

    public MarginHandle(Side side)
    {
        Side = side;
        Width = Size;
        Height = Size;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = IsHorizontal ? Cursors.SizeWE : Cursors.SizeNS;
    }

    public Side Side { get; }

    /// <summary>Left and right arrows move sideways along the top of the page.</summary>
    public bool IsHorizontal => Side is Side.Left or Side.Right;

    public bool LineShown
    {
        get => lineShown;
        set { lineShown = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole square can be clicked, not just the arrow.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            if (IsHorizontal)
            {
                g.BeginFigure(new Point(3.5, 5), true, true);
                g.LineTo(new Point(16.5, 5), true, true);
                g.LineTo(new Point(10, 17), true, true);
            }
            else
            {
                g.BeginFigure(new Point(5, 3.5), true, true);
                g.LineTo(new Point(5, 16.5), true, true);
                g.LineTo(new Point(17, 10), true, true);
            }
        }
        arrow.Freeze();
        dc.DrawGeometry(lineShown ? Bright : null, Outline, arrow);

        if (IsKeyboardFocused)
            dc.DrawRectangle(null, FocusPen, new Rect(0.5, 0.5, Size - 1, Size - 1));
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }
}
