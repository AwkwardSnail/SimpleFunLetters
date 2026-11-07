using System.ComponentModel;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;

namespace SimpleFunLetters;

public partial class MainWindow : Window
{
    // A4, in millimetres.
    const double PageWidthMm = 210;
    const double PageHeightMm = 297;
    // Margins never squeeze the writing area below this.
    const double MinTextMm = 40;

    const double MinFontPt = 6;
    const double MaxFontPt = 72;

    const string OpenFilter = "Letters (*.funletter;*.sletter;*.txt)|*.funletter;*.sletter;*.txt|SimpleFunLetters letters (*.funletter)|*.funletter|SimpleLetters letters (*.sletter)|*.sletter|Text files (*.txt)|*.txt";
    const string SaveFilter = "SimpleFunLetters letter (*.funletter)|*.funletter";

    static double Dip(double mm) => mm * 96.0 / 25.4;
    static double Mm(double dip) => dip * 25.4 / 96.0;
    static double PtToDip(double pt) => pt * 96.0 / 72.0;
    static double DipToPt(double dip) => Math.Round(dip * 72.0 / 96.0 * 2) / 2;

    static readonly Brush GuideBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0x8C, 0x48)));

    // Ctrl shortcuts the page accepts. Everything else (Ctrl+B, Ctrl+I, Ctrl+E,
    // Ctrl+] and so on) would add formatting SimpleFunLetters doesn't offer.
    static readonly HashSet<Key> AllowedCtrlKeys =
    [
        Key.A, Key.C, Key.V, Key.X, Key.Z, Key.Y, Key.O, Key.S, Key.P,
        Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown,
        Key.Back, Key.Delete, Key.Insert, Key.Tab,
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt,
    ];

    readonly Settings settings = Settings.Load();
    readonly Dictionary<Side, MarginHandle> handles = new();
    readonly Dictionary<Side, Line> guides = new();
    readonly Border valueLabel;
    readonly TextBlock valueText;

    /// <summary>The letters drawn in this document, with every earlier version.</summary>
    Designs designs = null!;

    string? filePath;
    bool dirty;
    bool settingText;

    /// <summary>The size new writing comes out in. Follows the caret, and the − and + buttons.</summary>
    double currentSizePt;

    // Dragging a margin arrow.
    Side? dragSide;
    bool dragMoved;
    Point dragStart;

    public MainWindow()
    {
        InitializeComponent();

        Page.Width = PageShadow.Width = Dip(PageWidthMm);
        Page.Height = PageShadow.Height = Dip(PageHeightMm);
        TopRuler.Width = Page.Width;
        SideRuler.Height = Page.Height;

        valueText = new TextBlock { Foreground = (Brush)FindResource("GreenBrush"), FontSize = 12 };
        valueLabel = new Border
        {
            Background = (Brush)FindResource("BarBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = valueText,
            Visibility = Visibility.Collapsed,
        };

        foreach (var side in Enum.GetValues<Side>())
        {
            var line = new Line { Stroke = GuideBrush, StrokeThickness = 1, SnapsToDevicePixels = true };
            guides[side] = line;
            Guides.Children.Add(line);

            var handle = new MarginHandle(side);
            handle.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            handle.MouseMove += Handle_MouseMove;
            handle.MouseLeftButtonUp += Handle_MouseLeftButtonUp;
            handle.LostMouseCapture += (_, _) => EndDrag();
            handle.KeyDown += Handle_KeyDown;
            handle.GotKeyboardFocus += (_, _) => UpdateLayoutForMargins();
            handle.LostKeyboardFocus += (_, _) => UpdateLayoutForMargins();
            handles[side] = handle;
            (handle.IsHorizontal ? TopRuler : SideRuler).Children.Add(handle);
        }
        Guides.Children.Add(valueLabel);

        currentSizePt = Math.Clamp(settings.FontSize, MinFontPt, MaxFontPt);
        Designs.CleanUpOldFonts();
        designs = NewDesigns();
        BuildLetterTiles();
        ShowLetter(new Letter { Paragraphs = { new LetterParagraph { SizePt = currentSizePt } } });
        UpdateLayoutForMargins();
        UpdateSizeControls();
        UpdateTitle();

        // Open a file passed on the command line (e.g. double-clicking a .funletter file).
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            LoadFile(args[1]);

        Loaded += (_, _) => Editor.Focus();
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---------------------------------------------------------------- Margins

    void UpdateLayoutForMargins()
    {
        double top = settings.MarginTop, bottom = settings.MarginBottom;
        double left = settings.MarginLeft, right = settings.MarginRight;
        double w = Page.Width, h = Page.Height;

        Editor.Margin = new Thickness(Dip(left), Dip(top), Dip(right), Dip(bottom));

        Place(Side.Left, Dip(left));
        Place(Side.Right, w - Dip(right));
        Place(Side.Top, Dip(top));
        Place(Side.Bottom, h - Dip(bottom));

        void Place(Side side, double at)
        {
            var handle = handles[side];
            handle.LineShown = settings.GetLine(side);
            handle.ToolTip = $"{side} margin: {settings.GetMargin(side):0} mm\nClick to show or hide its line. Drag to move it.";

            var line = guides[side];
            line.Visibility = settings.GetLine(side) || (dragSide == side && dragMoved) ? Visibility.Visible : Visibility.Collapsed;
            if (handle.IsHorizontal)
            {
                Canvas.SetLeft(handle, at - MarginHandle.Size / 2);
                Canvas.SetTop(handle, 8); // tip touches the top of the page (ruler is 28 high)
                line.X1 = line.X2 = at;
                line.Y1 = 0;
                line.Y2 = h;
            }
            else
            {
                Canvas.SetLeft(handle, 8);
                Canvas.SetTop(handle, at - MarginHandle.Size / 2);
                line.Y1 = line.Y2 = at;
                line.X1 = 0;
                line.X2 = w;
            }
        }

        UpdateValueLabel();
        CheckOverflow();
    }

    /// <summary>Shows "Left margin 25 mm" beside an arrow while it is being dragged or has keyboard focus.</summary>
    void UpdateValueLabel()
    {
        Side? side = dragSide is { } d && dragMoved ? d
            : handles.Values.FirstOrDefault(h => h.IsKeyboardFocused)?.Side;
        if (side is not { } s)
        {
            valueLabel.Visibility = Visibility.Collapsed;
            return;
        }

        valueText.Text = $"{s} margin {settings.GetMargin(s):0} mm";
        valueLabel.Visibility = Visibility.Visible;
        valueLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = valueLabel.DesiredSize;
        var line = guides[s];
        const double gap = 6;

        double x, y;
        switch (s)
        {
            case Side.Left: x = line.X1 + gap; y = gap; break;
            case Side.Right: x = line.X1 - gap - size.Width; y = gap; break;
            case Side.Top: x = gap; y = line.Y1 + gap; break;
            default: x = gap; y = line.Y1 - gap - size.Height; break;
        }
        Canvas.SetLeft(valueLabel, Math.Clamp(x, 0, Page.Width - size.Width));
        Canvas.SetTop(valueLabel, Math.Clamp(y, 0, Page.Height - size.Height));
    }

    /// <summary>Sets a margin, keeping it on the page and leaving room to write.</summary>
    void SetMargin(Side side, double mm)
    {
        double opposite = side switch
        {
            Side.Left => settings.MarginRight,
            Side.Right => settings.MarginLeft,
            Side.Top => settings.MarginBottom,
            _ => settings.MarginTop,
        };
        double pageMm = side is Side.Left or Side.Right ? PageWidthMm : PageHeightMm;
        mm = Math.Clamp(Math.Round(mm), 0, pageMm - opposite - MinTextMm);
        if (mm == settings.GetMargin(side)) return;

        settings.SetMargin(side, mm);
        MarkDirty(); // margins are saved with the letter
        UpdateLayoutForMargins();
    }

    void ToggleLine(Side side)
    {
        settings.SetLine(side, !settings.GetLine(side));
        settings.Save();
        UpdateLayoutForMargins();
    }

    void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var handle = (MarginHandle)sender;
        dragSide = handle.Side;
        dragMoved = false;
        dragStart = e.GetPosition(Page);
        handle.CaptureMouse();
        e.Handled = true;
    }

    void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragSide is not { } side || !((MarginHandle)sender).IsMouseCaptured) return;

        var p = e.GetPosition(Page);
        if (!dragMoved && (p - dragStart).Length < 3) return; // still a click
        if (!dragMoved)
        {
            dragMoved = true;
            UpdateLayoutForMargins(); // show the line while dragging
        }

        double mm = side switch
        {
            Side.Left => Mm(p.X),
            Side.Right => PageWidthMm - Mm(p.X),
            Side.Top => Mm(p.Y),
            _ => PageHeightMm - Mm(p.Y),
        };
        SetMargin(side, mm);
    }

    void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (dragSide is not { } side) return;
        bool wasClick = !dragMoved;
        ((MarginHandle)sender).ReleaseMouseCapture(); // calls EndDrag
        if (wasClick) ToggleLine(side);
        e.Handled = true;
    }

    void EndDrag()
    {
        if (dragSide is null) return;
        bool moved = dragMoved;
        dragSide = null;
        dragMoved = false;
        if (moved) settings.Save();
        UpdateLayoutForMargins();
    }

    /// <summary>Arrow keys nudge a focused margin arrow by 1 mm (Shift: 5 mm); Space or Enter shows its line.</summary>
    void Handle_KeyDown(object sender, KeyEventArgs e)
    {
        var handle = (MarginHandle)sender;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        double move = (handle.IsHorizontal, e.Key) switch
        {
            (true, Key.Left) => -step,
            (true, Key.Right) => step,
            (false, Key.Up) => -step,
            (false, Key.Down) => step,
            _ => 0,
        };

        if (move != 0)
        {
            // Moving an arrow right or down grows the left/top margin but shrinks the right/bottom one.
            double sign = handle.Side is Side.Left or Side.Top ? 1 : -1;
            SetMargin(handle.Side, settings.GetMargin(handle.Side) + sign * move);
            settings.Save();
            e.Handled = true;
        }
        else if (e.Key is Key.Space or Key.Enter)
        {
            ToggleLine(handle.Side);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Editor.Focus();
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------- Text size
    //
    // − and + change the size of what is typed next, from the caret onwards.
    // Text already on the page keeps its size (and its letter drawings).

    void UpdateSizeControls()
    {
        SizeLabel.Text = $"{currentSizePt:0.#} pt";
        SmallerButton.IsEnabled = currentSizePt > MinFontPt;
        LargerButton.IsEnabled = currentSizePt < MaxFontPt;
    }

    void ChangeFontSize(double delta)
    {
        currentSizePt = Math.Clamp(Math.Round(currentSizePt) + delta, MinFontPt, MaxFontPt);
        settings.FontSize = currentSizePt; // new letters start at the last size used
        settings.Save();
        UpdateSizeControls();
        Editor.LockCurrentUndoUnit();
        Editor.Focus();
    }

    void Smaller_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    void Larger_Click(object sender, RoutedEventArgs e) => ChangeFontSize(1);

    /// <summary>True when the text at the caret already has the current size and letter drawings.</summary>
    bool CaretMatchesCurrentFormat()
    {
        var here = Editor.CaretPosition.Parent as TextElement;
        double sizeHere = here?.FontSize ?? Editor.Document.FontSize;
        var familyHere = here?.FontFamily ?? Editor.Document.FontFamily;
        return Math.Abs(sizeHere - PtToDip(currentSizePt)) <= 0.01
            && familyHere.Source == designs.Current.Family.Source;
    }

    // ------------------------------------------------------------------ Editor

    void MarkDirty()
    {
        if (settingText || dirty) return;
        dirty = true;
        UpdateTitle();
    }

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkDirty();
        CheckOverflow();
    }

    /// <summary>Moving the caret picks up the size of the text it is in.</summary>
    void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (settingText) return;
        if (Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double dip)
        {
            currentSizePt = Math.Clamp(DipToPt(dip), MinFontPt, MaxFontPt);
            UpdateSizeControls();
        }
    }

    void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // WPF would otherwise undo all continuous typing in one go; start a
        // new undo step at each word instead.
        if (e.Text == " ") Editor.LockCurrentUndoUnit();

        // Ordinary typing into text that already looks right: let WPF do it.
        var selection = Editor.Selection;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        if (selection.IsEmpty && CaretMatchesCurrentFormat()) return;

        // Otherwise the app types this character itself, as a new piece of text
        // in the current size and letters; the rest of the word then follows
        // it as ordinary typing. (WPF's own "format for the next character" is
        // unreliable once the caret has moved, and it won't type into an empty
        // piece of text.) This first character is its own undo step.
        e.Handled = true;
        Editor.BeginChange();
        try
        {
            if (!selection.IsEmpty) selection.Text = "";
            var at = selection.Start.GetInsertionPosition(LogicalDirection.Forward);
            var run = new Run(e.Text, at)
            {
                FontSize = PtToDip(currentSizePt),
                FontFamily = designs.Current.Family,
            };
            Editor.CaretPosition = run.ContentEnd;
        }
        finally
        {
            Editor.EndChange();
        }
    }

    void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !AllowedCtrlKeys.Contains(key))
        {
            e.Handled = true; // no bold, italic, alignment, lists, etc.
            return;
        }

        // A new line, or moving the caret, starts a new undo step.
        if (key is Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            Editor.LockCurrentUndoUnit();
    }

    void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Editor.LockCurrentUndoUnit();

    /// <summary>Pastes as plain text in the current size and letters, never with outside formatting.</summary>
    void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;

        Editor.BeginChange();
        try
        {
            var selection = Editor.Selection;
            selection.Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            selection.ApplyPropertyValue(TextElement.FontSizeProperty, PtToDip(currentSizePt));
            selection.ApplyPropertyValue(TextElement.FontFamilyProperty, designs.Current.Family);
            selection.Select(selection.End, selection.End);
        }
        finally
        {
            Editor.EndChange();
        }
    }

    /// <summary>The page doesn't scroll on its own: the mouse wheel scrolls the desk.</summary>
    void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Desk.ScrollToVerticalOffset(Desk.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    void CheckOverflow()
    {
        // Wait for layout so the text's height is up to date.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            bool over = Editor.ExtentHeight > Editor.ViewportHeight + 0.5;
            OverflowNote.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ------------------------------------------------------------------- Files

    string DocumentName => filePath is null ? "Untitled" : System.IO.Path.GetFileNameWithoutExtension(filePath);

    void UpdateTitle() => Title = $"{(dirty ? "*" : "")}{DocumentName} – SimpleFunLetters";

    Letter CurrentLetter()
    {
        var letter = Letter.FromFlowDocument(Editor.Document, designs);
        letter.MarginTop = settings.MarginTop;
        letter.MarginBottom = settings.MarginBottom;
        letter.MarginLeft = settings.MarginLeft;
        letter.MarginRight = settings.MarginRight;
        return letter;
    }

    /// <summary>Puts a letter on the page, with a fresh undo history.</summary>
    void ShowLetter(Letter letter)
    {
        settingText = true;
        try
        {
            Editor.Document = letter.ToFlowDocument(designs, Editor.Foreground, currentSizePt);
            Editor.CaretPosition = Editor.Document.ContentStart;
        }
        finally
        {
            settingText = false;
        }
        Editor_SelectionChanged(Editor, new RoutedEventArgs());
        CheckOverflow();
    }

    void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = OpenFilter };
        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    void LoadFile(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        bool isFunLetter = string.Equals(extension, Letter.Extension, StringComparison.OrdinalIgnoreCase);
        bool isLetter = isFunLetter || string.Equals(extension, Letter.PlainLetterExtension, StringComparison.OrdinalIgnoreCase);
        Letter letter;
        try
        {
            letter = isLetter ? Letter.Load(path) : Letter.FromPlainText(File.ReadAllText(path), currentSizePt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            MessageBox.Show(this, $"SimpleFunLetters couldn't open that file.\n\n{ex.Message}", "SimpleFunLetters",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (isLetter)
        {
            settings.MarginTop = letter.MarginTop;
            settings.MarginBottom = letter.MarginBottom;
            settings.MarginLeft = letter.MarginLeft;
            settings.MarginRight = letter.MarginRight;
            settings.Save();
        }

        // A .funletter brings its own drawings. Anything else starts with the
        // child's latest letters for new typing; its existing text stays normal.
        designs = isFunLetter ? new Designs(NormalFont) : NewDesigns();
        if (isFunLetter)
            designs.Restore(letter.Glyphs, letter.Fonts.Select(f => (f.Id, f.GlyphIds.AsEnumerable())), letter.CurrentFont);
        RefreshLetterTiles();
        CloseDrawing();

        ShowLetter(letter);
        UpdateLayoutForMargins();
        Desk.ScrollToHome();

        // Other files are brought in as a new letter: saving asks where to put the .funletter.
        filePath = isFunLetter ? path : null;
        dirty = !isFunLetter;
        UpdateTitle();
    }

    void Save_Executed(object sender, ExecutedRoutedEventArgs e) => Save();

    /// <summary>Saves to the current file, asking where the first time. Returns false if not saved.</summary>
    bool Save()
    {
        string? path = filePath;
        if (path is null)
        {
            var dialog = new SaveFileDialog { Filter = SaveFilter, DefaultExt = Letter.Extension, FileName = DocumentName + Letter.Extension };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        try
        {
            CurrentLetter().Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"SimpleFunLetters couldn't save the file.\n\n{ex.Message}", "SimpleFunLetters",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        filePath = path;
        dirty = false;
        UpdateTitle();
        return true;
    }

    /// <summary>If there are unsaved changes, offers to save them. Returns false to stay put.</summary>
    bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, $"Save changes to {DocumentName}?", "SimpleFunLetters",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------- Print

    void Print_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
        if (dialog.ShowDialog() != true) return;

        // Print a copy built from the same model that gets saved, so the page
        // prints exactly as it would reopen.
        var document = CurrentLetter().ToFlowDocument(designs, Brushes.Black, currentSizePt);
        document.PageWidth = Dip(PageWidthMm);
        document.PageHeight = Dip(PageHeightMm);
        document.PagePadding = new Thickness(Dip(settings.MarginLeft), Dip(settings.MarginTop),
                                             Dip(settings.MarginRight), Dip(settings.MarginBottom));
        document.ColumnWidth = double.PositiveInfinity;

        try
        {
            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, DocumentName);
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            MessageBox.Show(this, $"SimpleFunLetters couldn't print.\n\n{ex.Message}", "SimpleFunLetters",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
