using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleFunLetters;

/// <summary>
/// The squared paper a child draws one letter on. Left button draws (or rubs
/// out, when the eraser is chosen); the right button always rubs out. The
/// normal letter shows faintly behind as a guide, with the writing line marked.
/// </summary>
public sealed class DrawingGrid : FrameworkElement
{
    public const double Cell = 20;

    static readonly Brush Paper = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xFD, 0xFB)));
    static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    static readonly Brush GuideInk = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xDC)));
    static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0)), 1));
    static readonly Pen LinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0x8C, 0x48)), 2));

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    bool[,] pixels = Grid.Empty();
    bool? strokeValue; // true while drawing, false while rubbing out

    public DrawingGrid()
    {
        Width = Grid.Columns * Cell;
        Height = Grid.Rows * Cell;
        Cursor = Cursors.Pen;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    public event EventHandler? Changed;

    /// <summary>When true, the left button rubs out instead of drawing.</summary>
    public bool Erasing { get; set; }

    /// <summary>The normal letter shown faintly behind the squares.</summary>
    public char GuideCharacter { get; set; }
    public FontFamily GuideFont { get; set; } = new("Segoe UI");

    public bool[,] Pixels
    {
        get => Grid.Copy(pixels);
        set { pixels = Grid.Copy(value); InvalidateVisual(); }
    }

    public void Clear()
    {
        pixels = Grid.Empty();
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Paper, null, new Rect(0, 0, Width, Height));

        double baselineY = Grid.AscentRows * Cell;
        DrawGuideLetter(dc, baselineY);

        for (int r = 0; r < Grid.Rows; r++)
            for (int c = 0; c < Grid.Columns; c++)
                if (pixels[r, c])
                    dc.DrawRectangle(Ink, null, new Rect(c * Cell, r * Cell, Cell, Cell));

        for (int c = 0; c <= Grid.Columns; c++)
            dc.DrawLine(GridPen, new Point(c * Cell + 0.5, 0), new Point(c * Cell + 0.5, Height));
        for (int r = 0; r <= Grid.Rows; r++)
            dc.DrawLine(GridPen, new Point(0, r * Cell + 0.5), new Point(Width, r * Cell + 0.5));

        // The writing line: letters sit on it, tails hang below.
        dc.DrawLine(LinePen, new Point(0, baselineY), new Point(Width, baselineY));
    }

    void DrawGuideLetter(DrawingContext dc, double baselineY)
    {
        if (GuideCharacter == '\0') return;
        var typeface = new Typeface(GuideFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, double size) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, GuideInk, pixelsPerDip);

        // Size the guide so a capital H reaches almost to the top of the squares,
        // measuring the drawn H itself (font metrics aren't always available).
        double capRatio = Text("H", 100).BuildGeometry(new Point()).Bounds.Height / 100;
        if (!(capRatio > 0.2)) capRatio = 0.7;
        double emSize = (Grid.AscentRows - 1) * Cell / capRatio;

        var text = Text(GuideCharacter.ToString(), emSize);
        double x = (Width - text.WidthIncludingTrailingWhitespace) / 2;
        dc.DrawText(text, new Point(x, baselineY - text.Baseline));
    }

    // ------------------------------------------------------------- mouse

    (int r, int c)? last;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => StartStroke(e, !Erasing);
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => StartStroke(e, false);

    void StartStroke(MouseButtonEventArgs e, bool draw)
    {
        strokeValue = draw;
        last = null;
        CaptureMouse();
        Paint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (strokeValue is null) return;
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
        {
            EndStroke();
            return;
        }
        Paint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndStroke();
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { EndStroke(); e.Handled = true; }
    protected override void OnLostMouseCapture(MouseEventArgs e) { strokeValue = null; last = null; }

    void EndStroke()
    {
        strokeValue = null;
        last = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    void Paint(Point p)
    {
        if (strokeValue is not bool value) return;
        int r = Math.Clamp((int)(p.Y / Cell), 0, Grid.Rows - 1);
        int c = Math.Clamp((int)(p.X / Cell), 0, Grid.Columns - 1);

        bool changed = false;
        // Fill in every square between the last point and this one, so quick strokes don't skip.
        var (r0, c0) = last ?? (r, c);
        int steps = Math.Max(Math.Abs(r - r0), Math.Abs(c - c0));
        for (int i = 0; i <= steps; i++)
        {
            int rr = steps == 0 ? r : r0 + (int)Math.Round((r - r0) * (double)i / steps);
            int cc = steps == 0 ? c : c0 + (int)Math.Round((c - c0) * (double)i / steps);
            if (pixels[rr, cc] != value) { pixels[rr, cc] = value; changed = true; }
        }
        last = (r, c);

        if (changed)
        {
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
