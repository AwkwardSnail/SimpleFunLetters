using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleFunLetters;

public enum DrawingTool { Pen, Eraser, Line, Arc }

/// <summary>
/// The squared paper a child draws one letter on. The paper is always the same
/// size on screen; finer grids just have smaller squares.
/// <list type="bullet">
/// <item>Pen / Eraser: click and drag. The right button always rubs out.</item>
/// <item>Line: press, drag and let go for a straight line (right button: rub one out).</item>
/// <item>Arc: click the centre; a dashed circle follows the mouse to set the size,
/// click to keep it; the mouse then snaps to the circle: click a yellow cross where
/// the arc starts, move round the circle the way it should go (all the way round
/// for a full circle), and click where it ends. Right button or Esc starts again.</item>
/// <item>Undo (or Ctrl+Z) takes back one stroke, line, arc, Clear or change of squares.</item>
/// </list>
/// The normal letter shows faintly behind as a guide, with the writing line marked.
/// </summary>
public sealed class DrawingGrid : FrameworkElement
{
    /// <summary>The paper's size on screen, whatever the grid.</summary>
    public const double PaperWidth = 360, PaperHeight = 480;

    static readonly Brush Paper = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xFD, 0xFB)));
    static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    static readonly Brush GuideInk = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xDC)));
    static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE0)), 1));
    static readonly Pen LinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0x8C, 0x48)), 2));
    // Helpers for lines and arcs: a dashed circle, and yellow crosses with a dark edge so they show on white.
    static readonly Pen CirclePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x8F, 0x71)), 1.5) { DashStyle = DashStyles.Dash });
    static readonly Pen YellowPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x00)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen EdgePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x48, 0x00)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen PreviewPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xF2, 0xC2, 0x00)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    bool[,] pixels = Grid.Empty();
    DrawingTool tool = DrawingTool.Pen;

    public DrawingGrid()
    {
        Width = PaperWidth;
        Height = PaperHeight;
        Cursor = Cursors.Pen;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = true; // for Esc while drawing an arc
        FocusVisualStyle = null;
    }

    public event EventHandler? Changed;
    /// <summary>The instructions changed (the tool, or the next step of an arc).</summary>
    public event EventHandler? HintChanged;

    int Rows => Grid.RowsOf(pixels);
    int Columns => Grid.ColumnsOf(pixels);
    /// <summary>One square, on screen.</summary>
    double Cell => PaperHeight / Rows;

    public DrawingTool Tool
    {
        get => tool;
        set
        {
            tool = value;
            CancelShape();
            Cursor = value is DrawingTool.Pen or DrawingTool.Eraser ? Cursors.Pen : Cursors.Cross;
            HintChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The normal letter shown faintly behind the squares.</summary>
    public char GuideCharacter { get; set; }
    public FontFamily GuideFont { get; set; } = new("Segoe UI");

    /// <summary>The drawing. Its size (columns × rows) is the grid shown.</summary>
    public bool[,] Pixels
    {
        get => Grid.Copy(pixels);
        set
        {
            pixels = Grid.Copy(value);
            history.Clear(); // a new drawing to work on: nothing to undo yet
            before = null;
            CancelShape();
            InvalidateVisual();
        }
    }

    // ------------------------------------------------------------- undo
    //
    // Each stroke, line, arc, Clear or change of squares is one step back.

    readonly Stack<bool[,]> history = new();
    bool[,]? before;

    /// <summary>Remembers the drawing as it is, before a step that may change it.</summary>
    void BeginStep() => before ??= Grid.Copy(pixels);

    /// <summary>Keeps the step for Undo, if it changed anything.</summary>
    void EndStep()
    {
        if (before is null) return;
        if (!Grid.SameAs(before, pixels)) history.Push(before);
        before = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool CanUndo => history.Count > 0;

    /// <summary>Takes back the last step (including a change of squares).</summary>
    public void Undo()
    {
        if (!CanUndo) return;
        CancelShape();
        before = null;
        pixels = history.Pop();
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the size of the squares, fitting the drawing onto the new grid.</summary>
    public void SetSize(int columns, int rows)
    {
        if (columns == Columns && rows == Rows) return;
        CancelShape();
        BeginStep();
        pixels = Grid.Resample(pixels, columns, rows);
        InvalidateVisual();
        EndStep();
    }

    public void Clear()
    {
        CancelShape();
        BeginStep();
        pixels = Grid.Empty(Columns, Rows);
        InvalidateVisual();
        EndStep();
    }

    /// <summary>What to do next, for the line of instructions above the paper.</summary>
    public string Hint => tool switch
    {
        DrawingTool.Pen => "Click and drag to draw. The right button rubs out. Letters sit on the green line.",
        DrawingTool.Eraser => "Click and drag to rub squares out.",
        DrawingTool.Line => "Press where the line starts, drag to where it ends, and let go.",
        _ => arcStep switch
        {
            ArcStep.Centre => "Click where the middle of the circle goes.",
            ArcStep.Radius => "Move to make the circle the size you want, then click.",
            ArcStep.FirstCross => "Click on the circle where the arc starts.",
            _ => "Move round the circle the way the arc goes, then click. All the way round makes a circle.",
        },
    };

    // ---------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        double cell = Cell;
        dc.DrawRectangle(Paper, null, new Rect(0, 0, Width, Height));

        double baselineY = Grid.AscentRowsFor(Rows) * cell;
        DrawGuideLetter(dc, baselineY);

        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                if (pixels[r, c])
                    dc.DrawRectangle(Ink, null, new Rect(c * cell, r * cell, cell, cell));

        for (int c = 0; c <= Columns; c++)
            dc.DrawLine(GridPen, new Point(c * cell + 0.5, 0), new Point(c * cell + 0.5, Height));
        for (int r = 0; r <= Rows; r++)
            dc.DrawLine(GridPen, new Point(0, r * cell + 0.5), new Point(Width, r * cell + 0.5));

        // The writing line: letters sit on it, tails hang below.
        dc.DrawLine(LinePen, new Point(0, baselineY), new Point(Width, baselineY));

        DrawHelpers(dc);
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
        double emSize = baselineY * (Grid.AscentRows - 1) / Grid.AscentRows / capRatio;

        var text = Text(GuideCharacter.ToString(), emSize);
        double x = (Width - text.WidthIncludingTrailingWhitespace) / 2;
        dc.DrawText(text, new Point(x, baselineY - text.Baseline));
    }

    /// <summary>The line being dragged, or the arc's circle and crosses.</summary>
    void DrawHelpers(DrawingContext dc)
    {
        double cell = Cell;
        Point Screen(Point g) => new(g.X * cell, g.Y * cell);

        if (lineStart is Point a && lineEnd is Point b)
            dc.DrawLine(PreviewPen, Screen(a), Screen(b));

        if (tool != DrawingTool.Arc || arcStep == ArcStep.Centre) return;
        var centre = Screen(arcCentre);
        DrawCross(dc, centre, 4, CirclePen, null); // the centre, small and quiet
        double radius = arcRadius * cell;
        if (radius > 0.5) dc.DrawEllipse(null, CirclePen, centre, radius, radius);

        if (arcStep == ArcStep.SecondCross && Math.Abs(arcSweep) > 1e-6)
            dc.DrawGeometry(null, PreviewPen, ArcGeometry(arcStartAngle, arcSweep));
        if (arcStep >= ArcStep.SecondCross) DrawCross(dc, Screen(arcFirst), 7, EdgePen, YellowPen);
        if (arcStep >= ArcStep.FirstCross && hover is Point h) DrawCross(dc, Screen(h), 7, EdgePen, YellowPen);
    }

    static void DrawCross(DrawingContext dc, Point p, double size, Pen first, Pen? second)
    {
        Pen[] pens = second is null ? [first] : [first, second];
        foreach (var pen in pens)
        {
            dc.DrawLine(pen, new Point(p.X - size, p.Y - size), new Point(p.X + size, p.Y + size));
            dc.DrawLine(pen, new Point(p.X - size, p.Y + size), new Point(p.X + size, p.Y - size));
        }
    }

    /// <summary>The arc so far, as a smooth line on screen (it can go all the way round).</summary>
    Geometry ArcGeometry(double start, double sweep)
    {
        double cell = Cell;
        Point At(double a) => new((arcCentre.X + arcRadius * Math.Cos(a)) * cell, (arcCentre.Y + arcRadius * Math.Sin(a)) * cell);
        int steps = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) * arcRadius * cell / 3));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(start), false, false);
            for (int i = 1; i <= steps; i++) ctx.LineTo(At(start + sweep * i / steps), true, true);
        }
        g.Freeze();
        return g;
    }

    double AngleOf(Point g) => Math.Atan2(g.Y - arcCentre.Y, g.X - arcCentre.X);

    /// <summary>
    /// Follows the mouse round the circle: the arc grows (or shrinks) by however
    /// far the mouse has turned, in whichever direction, up to a full circle.
    /// </summary>
    void FollowRound(Point g)
    {
        if ((g - arcCentre).Length < 1e-6) return; // right on the centre: no direction
        double angle = AngleOf(g);
        double turn = angle - arcLastAngle;
        while (turn > Math.PI) turn -= 2 * Math.PI;
        while (turn <= -Math.PI) turn += 2 * Math.PI;
        arcSweep = Math.Clamp(arcSweep + turn, -2 * Math.PI, 2 * Math.PI);
        arcLastAngle = angle;
    }

    // ------------------------------------------------------------- mouse
    //
    // Positions are in grid units: square (c, r) covers c..c+1 across and r..r+1 down.

    enum ArcStep { Centre, Radius, FirstCross, SecondCross }

    bool? strokeValue;          // pen/eraser: true while drawing, false while rubbing out
    (int r, int c)? last;       // pen/eraser: the last square painted
    Point? lineStart, lineEnd;  // line: from the middle of one square to another
    bool lineValue;
    ArcStep arcStep = ArcStep.Centre;
    Point arcCentre, arcFirst;
    double arcRadius;
    double arcStartAngle, arcSweep, arcLastAngle; // the arc so far follows the mouse round
    Point? hover;               // arc: where the mouse is (on the circle, once it has a size)

    Point ToGrid(Point screen) => new(Math.Clamp(screen.X / Cell, 0, Columns), Math.Clamp(screen.Y / Cell, 0, Rows));
    (int r, int c) SquareAt(Point g) => (Math.Clamp((int)g.Y, 0, Rows - 1), Math.Clamp((int)g.X, 0, Columns - 1));
    static Point MiddleOf((int r, int c) sq) => new(sq.c + 0.5, sq.r + 0.5);
    /// <summary>The nearest corner or middle of a square, so circles can be centred neatly.</summary>
    static Point SnapHalf(Point g) => new(Math.Round(g.X * 2) / 2, Math.Round(g.Y * 2) / 2);

    Point OnCircle(Point g)
    {
        var d = g - arcCentre;
        if (d.Length < 1e-6) d = new Vector(1, 0);
        d.Normalize();
        return arcCentre + d * arcRadius;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var g = ToGrid(e.GetPosition(this));
        switch (tool)
        {
            case DrawingTool.Pen: StartStroke(g, true); break;
            case DrawingTool.Eraser: StartStroke(g, false); break;
            case DrawingTool.Line: StartLine(g, true); break;
            case DrawingTool.Arc: ArcClick(g); break;
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var g = ToGrid(e.GetPosition(this));
        switch (tool)
        {
            case DrawingTool.Line: StartLine(g, false); break;
            case DrawingTool.Arc: CancelShape(); break;
            default: StartStroke(g, false); break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var g = ToGrid(e.GetPosition(this));
        if (strokeValue is not null)
        {
            if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released) { EndStroke(); return; }
            Paint(g);
        }
        else if (lineStart is not null)
        {
            lineEnd = MiddleOf(SquareAt(g));
            InvalidateVisual();
        }
        else if (tool == DrawingTool.Arc && arcStep != ArcStep.Centre) ArcMove(g);
    }

    /// <summary>The mouse moving while an arc is being placed.</summary>
    void ArcMove(Point g)
    {
        if (arcStep == ArcStep.Radius)
        {
            arcRadius = (g - arcCentre).Length;
            hover = null;
        }
        else
        {
            if (arcStep == ArcStep.SecondCross) FollowRound(g);
            hover = OnCircle(g); // the mouse snaps to the circle
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { EndStroke(); EndLine(e); }
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { EndStroke(); EndLine(e); e.Handled = true; }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        strokeValue = null;
        last = null;
        EndStep();
        if (lineStart is not null) { lineStart = lineEnd = null; InvalidateVisual(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (tool == DrawingTool.Arc && arcStep != ArcStep.Centre || lineStart is not null))
        {
            CancelShape();
            e.Handled = true;
        }
    }

    /// <summary>Forgets a line or arc that isn't finished.</summary>
    void CancelShape()
    {
        bool hadArc = arcStep != ArcStep.Centre;
        arcStep = ArcStep.Centre;
        arcRadius = 0;
        arcSweep = 0;
        hover = null;
        lineStart = lineEnd = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (hadArc) HintChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- pen and eraser

    void StartStroke(Point g, bool draw)
    {
        BeginStep();
        strokeValue = draw;
        last = null;
        CaptureMouse();
        Paint(g);
    }

    void EndStroke()
    {
        if (strokeValue is null) return;
        strokeValue = null;
        last = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        EndStep();
    }

    void Paint(Point g)
    {
        if (strokeValue is not bool value) return;
        var (r, c) = SquareAt(g);
        // Fill in every square between the last point and this one, so quick strokes don't skip.
        var from = last is { } l ? MiddleOf(l) : MiddleOf((r, c));
        bool changed = SetAlong(from, MiddleOf((r, c)), value);
        last = (r, c);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- straight line

    void StartLine(Point g, bool draw)
    {
        lineValue = draw;
        lineStart = lineEnd = MiddleOf(SquareAt(g));
        CaptureMouse();
        InvalidateVisual();
    }

    void EndLine(MouseButtonEventArgs e)
    {
        if (lineStart is not Point a) return;
        var b = MiddleOf(SquareAt(ToGrid(e.GetPosition(this))));
        lineStart = lineEnd = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        BeginStep();
        SetAlong(a, b, lineValue);
        InvalidateVisual();
        EndStep();
    }

    /// <summary>
    /// Sets the squares along a straight line, one step of a square at a time,
    /// so the line is one square thin (squares touch at their corners on a
    /// slant). Returns true if any changed.
    /// </summary>
    bool SetAlong(Point a, Point b, bool value)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
        bool changed = false;
        for (int i = 0; i <= steps; i++)
            changed |= Set(a + (b - a) * ((double)i / steps), value);
        if (changed) InvalidateVisual();
        return changed;
    }

    bool Set(Point g, bool value)
    {
        // Parts of a circle that run off the paper are simply not drawn.
        if (g.X < 0 || g.Y < 0 || g.X > Columns || g.Y > Rows) return false;
        var (r, c) = SquareAt(g);
        if (pixels[r, c] == value) return false;
        pixels[r, c] = value;
        return true;
    }

    // ---- arc

    void ArcClick(Point g)
    {
        switch (arcStep)
        {
            case ArcStep.Centre:
                arcCentre = SnapHalf(g);
                arcRadius = 0;
                arcStep = ArcStep.Radius;
                break;
            case ArcStep.Radius:
                arcRadius = (g - arcCentre).Length;
                if (arcRadius < 0.5) return; // too small to see: keep choosing
                arcStep = ArcStep.FirstCross;
                hover = OnCircle(g);
                break;
            case ArcStep.FirstCross:
                arcFirst = OnCircle(g);
                arcStartAngle = arcLastAngle = AngleOf(arcFirst);
                arcSweep = 0;
                arcStep = ArcStep.SecondCross;
                break;
            default:
                FollowRound(g); // include any last bit of movement
                DrawArc(arcStartAngle, arcSweep);
                arcStep = ArcStep.Centre;
                arcRadius = 0;
                arcSweep = 0;
                hover = null;
                break;
        }
        InvalidateVisual();
        HintChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Inks the squares along the arc: from an angle, turning by sweep (up to a full circle).</summary>
    void DrawArc(double start, double sweep)
    {
        BeginStep();
        // About one step per square along the arc, so it stays one square thin.
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * arcRadius));
        for (int i = 0; i <= steps; i++)
        {
            double a = start + sweep * i / steps;
            Set(new Point(arcCentre.X + arcRadius * Math.Cos(a), arcCentre.Y + arcRadius * Math.Sin(a)), true);
        }
        InvalidateVisual();
        EndStep();
    }
}
