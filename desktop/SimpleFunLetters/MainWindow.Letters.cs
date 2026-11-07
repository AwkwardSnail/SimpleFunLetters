using System.Windows;
using System.Windows.Controls;
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

    // ---------------------------------------------------------- page fit

    /// <summary>Shrinks the page to fit beside the letters on smaller screens.</summary>
    void Desk_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double needed = 28 + Page.Width + DeskContent.Margin.Left + DeskContent.Margin.Right;
        double available = e.NewSize.Width - SystemParameters.VerticalScrollBarWidth;
        double scale = Math.Clamp(available / needed, 0.5, 1);
        DeskContent.LayoutTransform = scale >= 1 ? Transform.Identity : new ScaleTransform(scale, scale);
    }
}
