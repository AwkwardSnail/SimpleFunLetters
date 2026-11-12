using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace SimpleFunLetters;

/// <summary>
/// A piece of text in one size and one version of the child's letters, or a
/// line break (Text is null). Font 0 means the normal letters.
/// </summary>
public sealed record LetterRun(string? Text, double SizePt, int Font = 0);

public sealed class LetterParagraph
{
    /// <summary>The paragraph's own size, which sets the height of an empty line.</summary>
    public double SizePt { get; set; } = 12;
    public List<LetterRun> Runs { get; } = new();
}

/// <summary>
/// A SimpleLetters Fun document, saved as XML with the .funletter extension:
/// the page margins, every letter drawing, every version of the child's font,
/// and the text with its sizes and font versions.
/// <code>
/// &lt;funletter version="1"&gt;
///   &lt;page size="A4" units="mm" top="25" bottom="25" left="25" right="25"/&gt;
///   &lt;glyphs&gt;
///     &lt;glyph id="1" char="a" columns="18" rows="24"&gt;&lt;row&gt;......##..........&lt;/row&gt; … 24 rows&lt;/glyph&gt;
///   &lt;/glyphs&gt;
///   &lt;fonts current="1"&gt;
///     &lt;font id="1"&gt;&lt;use glyph="1"/&gt;&lt;/font&gt;
///   &lt;/fonts&gt;
///   &lt;text&gt;
///     &lt;p size="12"&gt;&lt;run size="12" font="1"&gt;a cat&lt;/run&gt;&lt;/p&gt;
///   &lt;/text&gt;
/// &lt;/funletter&gt;
/// </code>
/// SimpleLetters' .sletter files open too, in the normal letters.
/// </summary>
public sealed class Letter
{
    public const string Extension = ".funletter";
    public const string PlainLetterExtension = ".sletter";
    const int FormatVersion = 1;

    public double MarginTop { get; set; } = 25;
    public double MarginBottom { get; set; } = 25;
    public double MarginLeft { get; set; } = 25;
    public double MarginRight { get; set; } = 25;
    public List<LetterParagraph> Paragraphs { get; } = new();
    public List<Glyph> Glyphs { get; } = new();
    public List<(int Id, List<int> GlyphIds)> Fonts { get; } = new();
    /// <summary>The font version new typing uses. Older files leave it out.</summary>
    public int? CurrentFont { get; set; }

    static double DipToPt(double dip) => Math.Round(dip * 72.0 / 96.0 * 2) / 2; // nearest half point
    static double PtToDip(double pt) => pt * 96.0 / 72.0;
    static double ClampSize(double pt) => Math.Clamp(pt, 1, 400);
    static double ClampMargin(double mm) => Math.Clamp(mm, 0, 150);

    // ------------------------------------------------------- FlowDocument

    /// <summary>Reads the text, sizes and font versions out of the editor's document.</summary>
    public static Letter FromFlowDocument(FlowDocument document, Designs designs)
    {
        var letter = new Letter();
        foreach (var block in document.Blocks)
        {
            var paragraph = new LetterParagraph();
            if (block is Paragraph p)
            {
                paragraph.SizePt = DipToPt(p.FontSize);
                AddInlines(paragraph, p.Inlines, designs);
            }
            else
            {
                // Shouldn't happen (pasting is plain text only), but keep any text.
                paragraph.SizePt = DipToPt(block.FontSize);
                var text = new TextRange(block.ContentStart, block.ContentEnd).Text;
                if (text.Length > 0) paragraph.Runs.Add(new LetterRun(text, paragraph.SizePt));
            }
            letter.Paragraphs.Add(paragraph);
        }

        // Keep only the font versions (and drawings) the text actually uses,
        // plus the current one so the child's latest letters come back.
        var used = letter.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Font).ToHashSet();
        used.Add(designs.Current.Id);
        letter.CurrentFont = designs.Current.Id;
        foreach (var version in designs.Versions.Where(v => v.Id != 0 && used.Contains(v.Id)))
            letter.Fonts.Add((version.Id, version.Glyphs.Values.Select(g => g.Id).OrderBy(id => id).ToList()));
        var usedGlyphs = letter.Fonts.SelectMany(f => f.GlyphIds).ToHashSet();
        letter.Glyphs.AddRange(designs.Glyphs.Where(g => usedGlyphs.Contains(g.Id)));
        return letter;
    }

    static void AddInlines(LetterParagraph paragraph, InlineCollection inlines, Designs designs)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run when run.Text.Length > 0:
                    double size = DipToPt(run.FontSize);
                    int font = designs.VersionOf(run.FontFamily).Id;
                    // Merge neighbouring runs that look the same to keep the file tidy.
                    if (paragraph.Runs.Count > 0 && paragraph.Runs[^1] is { Text: { } prev } last
                        && last.SizePt == size && last.Font == font)
                        paragraph.Runs[^1] = last with { Text = prev + run.Text };
                    else
                        paragraph.Runs.Add(new LetterRun(run.Text, size, font));
                    break;
                case LineBreak lineBreak:
                    paragraph.Runs.Add(new LetterRun(null, DipToPt(lineBreak.FontSize)));
                    break;
                case Span span:
                    AddInlines(paragraph, span.Inlines, designs);
                    break;
            }
        }
    }

    /// <summary>Builds a document for the editor (or for printing) from this letter.</summary>
    public FlowDocument ToFlowDocument(Designs designs, Brush ink, double defaultSizePt)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = designs.Current.Family,
            Foreground = ink,
            FontSize = PtToDip(Paragraphs.Count > 0 ? Paragraphs[0].SizePt : defaultSizePt),
            TextAlignment = TextAlignment.Left,
        };

        foreach (var lp in Paragraphs)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), FontSize = PtToDip(lp.SizePt) };
            foreach (var run in lp.Runs)
            {
                if (run.Text is null)
                    paragraph.Inlines.Add(new LineBreak { FontSize = PtToDip(run.SizePt) });
                else
                    paragraph.Inlines.Add(new Run(run.Text)
                    {
                        FontSize = PtToDip(run.SizePt),
                        FontFamily = designs.VersionById(run.Font).Family,
                    });
            }
            document.Blocks.Add(paragraph);
        }

        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0), FontSize = PtToDip(defaultSizePt) });
        return document;
    }

    /// <summary>Turns a plain text file into a letter, one paragraph per line.</summary>
    public static Letter FromPlainText(string text, double sizePt)
    {
        var letter = new Letter();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new LetterParagraph { SizePt = sizePt };
            if (line.Length > 0) paragraph.Runs.Add(new LetterRun(line, sizePt));
            letter.Paragraphs.Add(paragraph);
        }
        return letter;
    }

    // ---------------------------------------------------------------- XML

    public void Save(string path)
    {
        var xml = new XDocument(
            new XElement("funletter",
                new XAttribute("version", FormatVersion),
                new XElement("page",
                    new XAttribute("size", "A4"),
                    new XAttribute("units", "mm"),
                    new XAttribute("top", MarginTop),
                    new XAttribute("bottom", MarginBottom),
                    new XAttribute("left", MarginLeft),
                    new XAttribute("right", MarginRight)),
                new XElement("glyphs", Glyphs.OrderBy(g => g.Id).Select(GlyphXml.Write)),
                new XElement("fonts",
                    CurrentFont is int current ? new XAttribute("current", current) : null,
                    Fonts.OrderBy(f => f.Id).Select(f => new XElement("font",
                    new XAttribute("id", f.Id),
                    f.GlyphIds.Select(id => new XElement("use", new XAttribute("glyph", id)))))),
                new XElement("text",
                    Paragraphs.Select(p => new XElement("p",
                        new XAttribute("size", p.SizePt),
                        p.Runs.Select(r => r.Text is null
                            ? new XElement("br", new XAttribute("size", r.SizePt))
                            : new XElement("run",
                                new XAttribute("size", r.SizePt),
                                r.Font != 0 ? new XAttribute("font", r.Font) : null,
                                XmlSafe(r.Text))))))));

        // Write to a temporary file first so a failed save never damages the old file.
        string temp = path + ".saving";
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(temp, settings))
            xml.Save(writer);
        File.Move(temp, path, overwrite: true);
    }

    public static Letter Load(string path)
    {
        var xml = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = xml.Root;
        if (root is null || (root.Name != "funletter" && root.Name != "sletter"))
            throw new FormatException("This isn't a SimpleLetters Fun or SimpleLetters document.");

        var letter = new Letter();
        if (root.Element("page") is { } page)
        {
            letter.MarginTop = ClampMargin((double?)page.Attribute("top") ?? 25);
            letter.MarginBottom = ClampMargin((double?)page.Attribute("bottom") ?? 25);
            letter.MarginLeft = ClampMargin((double?)page.Attribute("left") ?? 25);
            letter.MarginRight = ClampMargin((double?)page.Attribute("right") ?? 25);
        }

        foreach (var g in root.Element("glyphs")?.Elements("glyph") ?? [])
            if (GlyphXml.Read(g) is { Id: > 0 } glyph && Designs.AllCharacters.Contains(glyph.Char))
                letter.Glyphs.Add(glyph);

        letter.CurrentFont = (int?)root.Element("fonts")?.Attribute("current");
        foreach (var f in root.Element("fonts")?.Elements("font") ?? [])
            if ((int?)f.Attribute("id") is int id and > 0)
                letter.Fonts.Add((id, f.Elements("use").Select(u => (int?)u.Attribute("glyph") ?? 0).ToList()));

        var fontIds = letter.Fonts.Select(f => f.Id).ToHashSet();
        foreach (var p in root.Element("text")?.Elements("p") ?? [])
        {
            var paragraph = new LetterParagraph { SizePt = ClampSize((double?)p.Attribute("size") ?? 12) };
            foreach (var e in p.Elements())
            {
                double size = ClampSize((double?)e.Attribute("size") ?? paragraph.SizePt);
                int font = (int?)e.Attribute("font") ?? 0;
                if (!fontIds.Contains(font)) font = 0;
                if (e.Name == "run") paragraph.Runs.Add(new LetterRun(e.Value, size, font));
                else if (e.Name == "br") paragraph.Runs.Add(new LetterRun(null, size));
            }
            letter.Paragraphs.Add(paragraph);
        }
        return letter;
    }

    /// <summary>Drops the rare control characters XML can't hold.</summary>
    static string XmlSafe(string text)
    {
        var safe = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                safe.Append(c).Append(text[++i]); // emoji and other characters beyond U+FFFF
            else if (XmlConvert.IsXmlChar(c))
                safe.Append(c);
        }
        return safe.ToString();
    }
}
