using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SimpleFunLetters;

// The left-hand panel: the grid of letters, and the pane for drawing one.
public partial class MainWindow
{
    readonly Dictionary<char, Button> tiles = new();
    char? drawingChar;

    FontFamily NormalFont => (FontFamily)FindResource("PageFont");

    /// <summary>A fresh set of designs that starts with the child's latest letters.</summary>
    Designs NewDesigns()
    {
        var d = new Designs(NormalFont);
        d.StartFrom(Designs.LoadMyLetters());
        return d;
    }

    void BuildLetterTiles()
    {
        AddTiles(CapitalsPanel, Designs.Capitals);
        AddTiles(SmallsPanel, Designs.Smalls);
        AddTiles(PunctuationPanel, Designs.Punctuation);
        AddTiles(NumbersPanel, Designs.Numbers);
        RefreshLetterTiles();

        void AddTiles(Panel panel, string characters)
        {
            foreach (char c in characters)
            {
                var tile = new Button { Style = (Style)FindResource("LetterTile"), Tag = c };
                tile.Click += (_, _) => OpenDrawing(c);
                tiles[c] = tile;
                panel.Children.Add(tile);
            }
        }
    }

    /// <summary>Shows each letter as the child drew it, or as the normal letter if not drawn yet.</summary>
    void RefreshLetterTiles()
    {
        foreach (var (c, tile) in tiles) RefreshTile(c, tile);
    }

    void RefreshTile(char c, Button tile)
    {
        var drawing = designs.CurrentDrawing(c);
        tile.ToolTip = drawing is null ? $"Draw your own {Describe(c)}" : $"Change your {Describe(c)}";
        if (drawing is null)
        {
            tile.Content = new TextBlock { Text = c.ToString(), FontFamily = NormalFont };
            return;
        }
        tile.Content = new Image
        {
            Source = PixelImage(drawing, (Brush)FindResource("GreenBrush")),
            Stretch = Stretch.Uniform,
        };
    }

    /// <summary>A small picture of a drawing, keeping the whole grid so letters line up.</summary>
    static DrawingImage PixelImage(bool[,] pixels, Brush ink)
    {
        var shapes = new GeometryGroup();
        for (int r = 0; r < Grid.Rows; r++)
            for (int c = 0; c < Grid.Columns; c++)
                if (pixels[r, c]) shapes.Children.Add(new RectangleGeometry(new Rect(c, r, 1, 1)));

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Grid.Columns, Grid.Rows))));
        group.Children.Add(new GeometryDrawing(ink, null, shapes));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    static string Describe(char c) => c switch
    {
        '.' => "full stop",
        ',' => "comma",
        '!' => "exclamation mark",
        '?' => "question mark",
        '\'' => "apostrophe",
        '"' => "quotation mark",
        ':' => "colon",
        ';' => "semicolon",
        '-' => "dash",
        '(' => "opening bracket",
        ')' => "closing bracket",
        '&' => "ampersand",
        '/' => "slash",
        _ => c.ToString(),
    };

    // ------------------------------------------------------------ drawing

    void OpenDrawing(char c)
    {
        drawingChar = c;
        DrawTitle.Text = $"Draw your {Describe(c)}";
        Drawing.GuideCharacter = c;
        Drawing.GuideFont = NormalFont;
        Drawing.Pixels = designs.CurrentDrawing(c) ?? Grid.Empty();
        PenTool.IsChecked = true;
        LettersView.Visibility = Visibility.Collapsed;
        DrawView.Visibility = Visibility.Visible;
    }

    void CloseDrawing()
    {
        drawingChar = null;
        DrawView.Visibility = Visibility.Collapsed;
        LettersView.Visibility = Visibility.Visible;
    }

    void Tool_Checked(object sender, RoutedEventArgs e)
    {
        // Also fires while the window is still loading, before every control exists.
        if (Drawing is null || EraserTool is null) return;
        Drawing.Erasing = EraserTool.IsChecked == true;
    }

    void ClearDrawing_Click(object sender, RoutedEventArgs e) => Drawing.Clear();

    void CancelDrawing_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawing();
        Editor.Focus();
    }

    /// <summary>
    /// Uses the drawing for everything typed from now on. Text already on the
    /// page keeps the drawing it was typed with.
    /// </summary>
    void DoneDrawing_Click(object sender, RoutedEventArgs e)
    {
        if (drawingChar is char c && designs.Redesign(c, Drawing.Pixels))
        {
            designs.SaveMyLetters();
            RefreshTile(c, tiles[c]);
            MarkDirty();
            Editor.LockCurrentUndoUnit();
        }
        CloseDrawing();
        Editor.Focus();
    }

    /// <summary>
    /// Like Done, and also changes this character everywhere it has already
    /// been written. Only this character changes: other letters in that text
    /// keep their drawings. One Undo puts the old text back.
    /// </summary>
    void ApplyToExisting_Click(object sender, RoutedEventArgs e)
    {
        if (drawingChar is not char c) return;

        if (designs.Redesign(c, Drawing.Pixels))
        {
            designs.SaveMyLetters();
            RefreshTile(c, tiles[c]);
        }

        bool changed = false;
        Editor.LockCurrentUndoUnit();
        Editor.BeginChange();
        try
        {
            foreach (var run in AllRuns(Editor.Document.Blocks))
            {
                if (!run.Text.Contains(c)) continue;
                var old = designs.VersionOf(run.FontFamily);
                var updated = designs.WithCurrentDrawingOf(old, c);
                if (updated == old) continue;
                run.FontFamily = updated.Family;
                changed = true;
            }
        }
        finally
        {
            Editor.EndChange();
        }
        Editor.LockCurrentUndoUnit();
        if (changed) MarkDirty();

        CloseDrawing();
        Editor.Focus();
    }

    static IEnumerable<Run> AllRuns(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
                foreach (var run in AllRuns(p.Inlines)) yield return run;
        }
    }

    static IEnumerable<Run> AllRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines.ToList())
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span)
                foreach (var inner in AllRuns(span.Inlines)) yield return inner;
        }
    }

    // --------------------------------------------------------------- zoom

    static readonly double[] ZoomSteps = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    double zoom = 1;

    /// <summary>Until the child picks a zoom, the page fits the window (never larger than actual size).</summary>
    void Desk_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (settings.Zoom > 0)
        {
            SetZoom(settings.Zoom, remember: false);
            return;
        }
        double needed = 28 + Page.Width + DeskContent.Margin.Left + DeskContent.Margin.Right;
        double available = e.NewSize.Width - SystemParameters.VerticalScrollBarWidth;
        double fit = available / needed;
        SetZoom(ZoomSteps.LastOrDefault(z => z <= fit, ZoomSteps[0]) is var z && z > 1 ? 1 : z, remember: false);
    }

    void SetZoom(double value, bool remember)
    {
        zoom = Math.Clamp(value, ZoomSteps[0], ZoomSteps[^1]);
        DeskContent.LayoutTransform = Math.Abs(zoom - 1) < 0.001 ? Transform.Identity : new ScaleTransform(zoom, zoom);
        ZoomLabel.Text = $"{zoom * 100:0}%";
        ZoomOutButton.IsEnabled = zoom > ZoomSteps[0] + 0.001;
        ZoomInButton.IsEnabled = zoom < ZoomSteps[^1] - 0.001;
        if (remember)
        {
            settings.Zoom = zoom;
            settings.Save();
        }
    }

    void StepZoom(int direction)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < zoom - 0.001, ZoomSteps[0]);
        SetZoom(next, remember: true);
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(1);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    /// <summary>Ctrl + mouse wheel zooms the page.</summary>
    void Desk_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
        StepZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }
}
