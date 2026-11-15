using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SimpleFunLetters;

// Pages. The editor is one long column of text. When the text reaches the
// bottom margin, the lines that don't fit are shown on another A4 sheet below,
// with a strip of desk between the sheets. Lines are never cut in half: each
// page shows whole lines only, the same way the letter prints.
//
// The letter itself isn't changed by any of this. Each page shows a picture
// (a VisualBrush) of its share of the editor's lines. The editor is invisible
// and is moved so the part under the mouse lines up with the page there.
public partial class MainWindow
{
    /// <summary>The strip of desk between two sheets.</summary>
    const double PageGap = 28;

    /// <summary>One page's share of the editor: where it starts in the editor, how tall it is,
    /// and how far down the editor is moved to show it in place.</summary>
    readonly record struct Slice(double Start, double Height, double Shift);

    readonly List<Slice> slices = [new Slice(0, 1, 0)];
    readonly List<Border> sheets = [];
    readonly List<Rectangle> sliceViews = [];
    readonly List<Line> pageGuides = [];
    readonly TranslateTransform hostShift = new();
    readonly RectangleGeometry hostClip = new();

    bool pagesDirty = true;
    bool scrollToCaret;
    int livePage;

    static double SheetTop(int page) => page * (Dip(PageHeightMm) + PageGap);

    // A RichTextBox keeps a little room for the caret on each side of its text.
    // The editor is made wider by that much and moved left, so the room sits in
    // the margins and the text lines up with them.
    double EditorInsetLeft => Inset(Editor.Document.PagePadding.Left);
    double EditorInsetRight => Inset(Editor.Document.PagePadding.Right);
    static double Inset(double padding) => double.IsNaN(padding) ? 5 : padding;

    void InitPages()
    {
        EditorHost.RenderTransform = hostShift;
        EditorHost.Clip = hostClip;

        // Lay the pages out as part of layout, so a line never shows cut for a moment.
        Editor.LayoutUpdated += (_, _) =>
        {
            if (!pagesDirty) return;
            pagesDirty = false;
            Paginate();
        };
        Editor.SizeChanged += (_, _) => InvalidatePages();
        // Scrolling to the caret is done here, where the pages are known.
        Editor.RequestBringIntoView += (_, e) => e.Handled = true;
        Editor.SelectionChanged += (_, _) => CaretMoved();

        Page.PreviewMouseMove += (_, e) => SetLivePage(PageAt(e.GetPosition(Page).Y));
        Page.MouseLeftButtonDown += Page_MouseLeftButtonDown;
    }

    void InvalidatePages()
    {
        pagesDirty = true;
        Page.InvalidateArrange(); // makes sure a layout pass (and so Paginate) follows
    }

    /// <summary>Works out which lines go on which page, and lays out the sheets to match.</summary>
    void Paginate()
    {
        double sheetW = Dip(PageWidthMm), sheetH = Dip(PageHeightMm);
        double left = Dip(settings.MarginLeft), top = Dip(settings.MarginTop);
        double inset = EditorInsetLeft;
        double textW = sheetW - left - Dip(settings.MarginRight) + inset + EditorInsetRight; // the editor's width
        double textH = sheetH - top - Dip(settings.MarginBottom);

        // Where each line of text starts, top to bottom.
        var tops = new List<double>();
        double end = Editor.ExtentHeight;
        var p = Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        p = p?.GetLineStartPosition(0) ?? p;
        while (p is not null && tops.Count < 100000)
        {
            var r = p.GetCharacterRect(LogicalDirection.Forward);
            if (!r.IsEmpty)
            {
                if (tops.Count == 0 || r.Top > tops[^1] + 0.01) tops.Add(r.Top);
                end = Math.Max(end, r.Bottom);
            }
            var next = p.GetLineStartPosition(1);
            if (next is null || next.CompareTo(p) <= 0) break;
            p = next;
        }

        // A sharp picture needs the text to land on whole screen pixels.
        double pixels = zoom * VisualTreeHelper.GetDpi(this).DpiScaleY;
        double Snap(double v) => Math.Round(v * pixels) / pixels;

        // Fill each page with whole lines; the first line that doesn't fit starts the next page.
        slices.Clear();
        double start = 0;
        void Add(double height) =>
            slices.Add(new Slice(start, Math.Max(height, 1), Snap(SheetTop(slices.Count) + top - start)));
        for (int k = 0; k < tops.Count; k++)
        {
            double bottom = k + 1 < tops.Count ? tops[k + 1] : end;
            if (bottom - start > textH + 0.5 && tops[k] > start + 0.5)
            {
                Add(tops[k] - start);
                start = tops[k];
            }
        }
        Add(end - start);

        int n = slices.Count;
        Page.Height = Sheets.Height = SheetTop(n - 1) + sheetH;

        // Sheets of paper.
        while (sheets.Count < n)
        {
            var sheet = new Border
            {
                Width = sheetW,
                Height = sheetH,
                Background = (Brush)FindResource("PaperBrush"),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.6, Color = Colors.Black },
            };
            Canvas.SetTop(sheet, SheetTop(sheets.Count));
            sheets.Add(sheet);
            Sheets.Children.Add(sheet);
        }
        while (sheets.Count > n)
        {
            Sheets.Children.Remove(sheets[^1]);
            sheets.RemoveAt(sheets.Count - 1);
        }

        // The picture of the text on each sheet.
        while (sliceViews.Count < n)
        {
            var view = new Rectangle
            {
                Fill = new VisualBrush(Editor) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill },
            };
            sliceViews.Add(view);
            Slices.Children.Add(view);
        }
        while (sliceViews.Count > n)
        {
            Slices.Children.Remove(sliceViews[^1]);
            sliceViews.RemoveAt(sliceViews.Count - 1);
        }
        for (int i = 0; i < n; i++)
        {
            var s = slices[i];
            double height = Math.Min(s.Height, textH); // one line taller than the page is cut off
            var view = sliceViews[i];
            view.Width = textW;
            view.Height = height;
            Canvas.SetLeft(view, left - inset);
            Canvas.SetTop(view, s.Shift + s.Start);
            ((VisualBrush)view.Fill).Viewbox = new Rect(0, s.Start, textW, height);
        }

        // Top and bottom margin lines on the pages after the first, and no lines across the gaps.
        foreach (var line in pageGuides) Guides.Children.Remove(line);
        pageGuides.Clear();
        var paper = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int i = 0; i < n; i++)
        {
            paper.Children.Add(new RectangleGeometry(new Rect(0, SheetTop(i), sheetW, sheetH)));
            if (i == 0) continue;
            foreach (var side in new[] { Side.Top, Side.Bottom })
            {
                double y = side == Side.Top ? SheetTop(i) + top : SheetTop(i) + sheetH - Dip(settings.MarginBottom);
                var line = new Line
                {
                    Stroke = GuideBrush, StrokeThickness = 1, SnapsToDevicePixels = true,
                    X1 = 0, X2 = sheetW, Y1 = y, Y2 = y,
                    Visibility = guides[side].Visibility,
                };
                pageGuides.Add(line);
                Guides.Children.Insert(0, line);
            }
        }
        Guides.Clip = paper;

        hostShift.X = left - inset;
        SetLivePage(livePage, force: true);

        if (scrollToCaret)
        {
            scrollToCaret = false;
            ScrollCaretIntoView();
        }
    }

    /// <summary>The page at a height on the paper (a point in a gap counts as the nearer page).</summary>
    int PageAt(double y) =>
        Math.Clamp((int)Math.Floor((y + PageGap / 2) / (Dip(PageHeightMm) + PageGap)), 0, slices.Count - 1);

    /// <summary>The page showing a height in the editor.</summary>
    int PageOf(double editorY)
    {
        int page = 0;
        for (int i = 1; i < slices.Count && slices[i].Start <= editorY + 0.5; i++) page = i;
        return page;
    }

    /// <summary>Moves the invisible editor so its lines for this page sit on the page.</summary>
    void SetLivePage(int page, bool force = false)
    {
        page = Math.Clamp(page, 0, slices.Count - 1);
        if (page == livePage && !force) return;
        livePage = page;
        var s = slices[page];
        hostShift.Y = s.Shift;
        hostClip.Rect = new Rect(0, s.Start, Math.Max(Editor.Width, 0), s.Height);
    }

    Rect CaretRect() => Editor.CaretPosition.GetCharacterRect(Editor.CaretPosition.LogicalDirection);

    void CaretMoved()
    {
        // With the keyboard, keep the editor where the caret is (for the input
        // method window and the like). With the mouse, it follows the mouse.
        if (Mouse.LeftButton != MouseButtonState.Pressed && !pagesDirty)
        {
            var r = CaretRect();
            if (!r.IsEmpty) SetLivePage(PageOf(r.Top));
        }

        if (pagesDirty) scrollToCaret = true;
        else ScrollCaretIntoView();
    }

    void ScrollCaretIntoView()
    {
        var r = CaretRect();
        if (r.IsEmpty) return;
        var s = slices[PageOf(r.Top)];
        const double room = 24; // keep a little space above and below the line being written
        Page.BringIntoView(new Rect(hostShift.X + r.X, s.Shift + r.Top - room, 1, r.Height + 2 * room));
    }

    /// <summary>A click on a page that missed the text (below the last line, or before the
    /// editor has moved under the mouse) puts the caret at the nearest place in the text.</summary>
    void Page_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(Page);
        int page = PageAt(at.Y);
        double inSheet = at.Y - SheetTop(page);
        if (inSheet < 0 || inSheet > Dip(PageHeightMm)) return; // between pages

        var s = slices[page];
        double y = Math.Clamp(at.Y - s.Shift, s.Start, s.Start + s.Height - 1);
        var position = Editor.GetPositionFromPoint(new Point(at.X - hostShift.X, y), snapToText: true);
        if (position is null) return;

        SetLivePage(page);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            Editor.Selection.Select(Editor.Selection.Start, position);
        else
            Editor.Selection.Select(position, position);
        Editor.Focus();
        e.Handled = true;
    }
}
