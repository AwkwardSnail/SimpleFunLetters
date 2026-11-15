using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleFunLetters;

// The look of the text: size, Sans or Serif, bold, italic, underline — the
// same toolbar as SimpleLetters. Drawn letters always keep their drawing;
// Sans or Serif chooses the font for the letters that aren't drawn.
//
// There is always a "current look" for what is typed next, in the child's
// current letters. Moving the caret picks up the look of the text it is in;
// the toolbar buttons change it. With text selected, the buttons (and − / +)
// change the selection, one undo step each.
public partial class MainWindow
{
    bool currentSerif, currentBold, currentItalic, currentUnderline;

    FontFamily SansFamily => (FontFamily)FindResource("PageFont");
    FontFamily SerifFamily => (FontFamily)FindResource("SerifFont");

    LetterRun CurrentLook =>
        new(null, currentSizePt, designs.Current.Id, currentSerif, currentBold, currentItalic, currentUnderline);

    void SetCurrentLook(LetterRun look)
    {
        currentSizePt = Math.Clamp(look.SizePt, MinFontPt, MaxFontPt);
        currentSerif = look.Serif;
        currentBold = look.Bold;
        currentItalic = look.Italic;
        currentUnderline = look.Underline;
        UpdateFormatControls();
    }

    void UpdateFormatControls()
    {
        FontButton.Content = currentSerif ? "Font: Serif" : "Font: Sans";
        FontButton.FontFamily = currentSerif ? SerifFamily : SansFamily;
        FontButton.ToolTip = currentSerif
            ? "Letters you haven't drawn have serifs (Literata). Press for plain letters."
            : "Letters you haven't drawn are plain (Atkinson Hyperlegible Next). Press for letters with serifs.";
        BoldButton.IsChecked = currentBold;
        ItalicButton.IsChecked = currentItalic;
        UnderlineButton.IsChecked = currentUnderline;
        UpdateSizeControls();
    }

    // -------------------------------------------------------------- size

    void UpdateSizeControls()
    {
        SizeLabel.Text = $"{currentSizePt:0.#} pt";
        SmallerButton.IsEnabled = currentSizePt > MinFontPt;
        LargerButton.IsEnabled = currentSizePt < MaxFontPt;
    }

    /// <summary>
    /// − and + change the size. With text selected, the whole selection takes
    /// the new size (one undo step); otherwise it applies to what is typed next.
    /// </summary>
    void ChangeFontSize(double delta)
    {
        currentSizePt = Math.Clamp(Math.Round(currentSizePt) + delta, MinFontPt, MaxFontPt);
        settings.FontSize = currentSizePt; // new letters start at the last size used
        settings.Save();
        ApplyToSelection(TextElement.FontSizeProperty, PtToDip(currentSizePt));
    }

    void Smaller_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    void Larger_Click(object sender, RoutedEventArgs e) => ChangeFontSize(1);

    // ------------------------------------------------- font and style

    void Font_Click(object sender, RoutedEventArgs e) => SetSerif(!currentSerif);
    void Bold_Click(object sender, RoutedEventArgs e) => SetBold(!currentBold);
    void Italic_Click(object sender, RoutedEventArgs e) => SetItalic(!currentItalic);
    void Underline_Click(object sender, RoutedEventArgs e) => SetUnderline(!currentUnderline);

    /// <summary>
    /// Sans or Serif for the letters that aren't drawn. With text selected, each
    /// piece keeps its own version of the child's letters and only swaps the
    /// font behind them, so drawn letters never change.
    /// </summary>
    void SetSerif(bool serif)
    {
        currentSerif = serif;
        settings.Serif = serif; // new letters start in the last font used
        settings.Save();

        Editor.LockCurrentUndoUnit();
        var selection = Editor.Selection;
        if (!selection.IsEmpty)
        {
            var pieces = SelectedPieces().Select(p => (p.Start, p.End, Family: designs.VersionOf(p.Run.FontFamily).FamilyFor(serif))).ToList();
            Editor.BeginChange();
            try
            {
                foreach (var (start, end, family) in pieces)
                    new TextRange(start, end).ApplyPropertyValue(TextElement.FontFamilyProperty, family);
            }
            finally
            {
                Editor.EndChange();
            }
            Editor.LockCurrentUndoUnit();
        }
        UpdateFormatControls();
        Editor.Focus();
    }

    void SetBold(bool bold)
    {
        currentBold = bold;
        ApplyToSelection(TextElement.FontWeightProperty, bold ? FontWeights.Bold : FontWeights.Normal);
    }

    void SetItalic(bool italic)
    {
        currentItalic = italic;
        ApplyToSelection(TextElement.FontStyleProperty, italic ? FontStyles.Italic : FontStyles.Normal);
    }

    void SetUnderline(bool underline)
    {
        currentUnderline = underline;
        ApplyToSelection(Inline.TextDecorationsProperty, underline ? TextDecorations.Underline : new TextDecorationCollection());
    }

    /// <summary>Changes the selected text (one undo step); with nothing selected, only what is typed next.</summary>
    void ApplyToSelection(DependencyProperty property, object value)
    {
        Editor.LockCurrentUndoUnit();
        if (!Editor.Selection.IsEmpty)
        {
            Editor.Selection.ApplyPropertyValue(property, value);
            Editor.LockCurrentUndoUnit();
        }
        UpdateFormatControls();
        Editor.Focus();
    }

    /// <summary>The parts of each piece of text that lie inside the selection.</summary>
    List<(Run Run, TextPointer Start, TextPointer End)> SelectedPieces()
    {
        var selection = Editor.Selection;
        var pieces = new List<(Run, TextPointer, TextPointer)>();
        foreach (var run in AllRuns(Editor.Document.Blocks))
        {
            var start = run.ContentStart.CompareTo(selection.Start) > 0 ? run.ContentStart : selection.Start;
            var end = run.ContentEnd.CompareTo(selection.End) < 0 ? run.ContentEnd : selection.End;
            if (start.CompareTo(end) < 0) pieces.Add((run, start, end));
        }
        return pieces;
    }

    // ------------------------------------------------------------ caret

    /// <summary>The look of the text at the caret.</summary>
    LetterRun LookAtCaret()
    {
        var here = Editor.CaretPosition.Parent as TextElement
                   ?? Editor.Document.Blocks.FirstBlock
                   ?? (TextElement?)null;
        return here is null ? CurrentLook : Letter.LookOf(here, designs);
    }

    /// <summary>
    /// Moving the caret picks up the look of the text it is in. With text
    /// selected, the buttons show what the whole selection shares.
    /// </summary>
    void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (settingText) return;
        var selection = Editor.Selection;
        if (selection.IsEmpty)
        {
            SetCurrentLook(LookAtCaret());
            return;
        }

        var size = selection.GetPropertyValue(TextElement.FontSizeProperty);
        var weight = selection.GetPropertyValue(TextElement.FontWeightProperty);
        var style = selection.GetPropertyValue(TextElement.FontStyleProperty);
        var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var pieces = SelectedPieces();
        SetCurrentLook(new LetterRun(null,
            // Mixed sizes: show (and step − / + from) the size where the selection starts.
            size is double dip ? DipToPt(dip)
                : selection.Start.Parent is TextElement first ? DipToPt(first.FontSize) : currentSizePt,
            // Serif when every selected piece is Serif, whichever version of the letters it uses.
            Serif: pieces.Count > 0 && pieces.All(p => designs.IsSerif(p.Run.FontFamily)),
            Bold: weight is FontWeight w && w.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight(),
            Italic: style is FontStyle s && (s == FontStyles.Italic || s == FontStyles.Oblique),
            Underline: decorations is TextDecorationCollection d && d.Any(x => x.Location == TextDecorationLocation.Underline)));
    }

    void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // WPF would otherwise undo all continuous typing in one go; start a
        // new undo step at each word instead.
        if (e.Text == " ") Editor.LockCurrentUndoUnit();

        // Ordinary typing into text that already looks right (in the child's
        // current letters): let WPF do it.
        var selection = Editor.Selection;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        if (selection.IsEmpty && LookAtCaret().SameLook(CurrentLook)) return;

        // Otherwise the app types this character itself, as a new piece of
        // text in the current look; the rest of the word then follows it as
        // ordinary typing. (WPF's own "format for the next character" is
        // unreliable once the caret has moved.) This first character is its
        // own undo step.
        var look = CurrentLook;
        e.Handled = true;
        Editor.BeginChange();
        try
        {
            if (!selection.IsEmpty) selection.Text = "";
            var at = selection.Start.GetInsertionPosition(LogicalDirection.Forward);
            var run = new Run(e.Text, at);
            Letter.Style(run, look, designs);
            Editor.CaretPosition = run.ContentEnd;
        }
        finally
        {
            Editor.EndChange();
        }
        SetCurrentLook(look);
    }

    void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.B or Key.I or Key.U)
        {
            if (key == Key.B) SetBold(!currentBold);
            else if (key == Key.I) SetItalic(!currentItalic);
            else SetUnderline(!currentUnderline);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !AllowedCtrlKeys.Contains(key))
        {
            e.Handled = true; // no alignment, lists, indents, etc.
            return;
        }

        // A new line, or moving the caret, starts a new undo step.
        if (key is Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            Editor.LockCurrentUndoUnit();
    }

    /// <summary>Pastes as plain text in the current look and letters, never with outside formatting.</summary>
    void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;

        var look = CurrentLook;
        Editor.BeginChange();
        try
        {
            var selection = Editor.Selection;
            selection.Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            selection.ApplyPropertyValue(TextElement.FontSizeProperty, PtToDip(look.SizePt));
            selection.ApplyPropertyValue(TextElement.FontFamilyProperty, designs.Current.FamilyFor(look.Serif));
            selection.ApplyPropertyValue(TextElement.FontWeightProperty, look.Bold ? FontWeights.Bold : FontWeights.Normal);
            selection.ApplyPropertyValue(TextElement.FontStyleProperty, look.Italic ? FontStyles.Italic : FontStyles.Normal);
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, look.Underline ? TextDecorations.Underline : new TextDecorationCollection());
            selection.Select(selection.End, selection.End);
        }
        finally
        {
            Editor.EndChange();
        }
        SetCurrentLook(look);
    }
}
