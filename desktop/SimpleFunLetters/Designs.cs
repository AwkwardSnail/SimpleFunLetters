using System.IO;
using System.Windows.Media;
using System.Xml.Linq;

namespace SimpleFunLetters;

/// <summary>One drawing of one character. Never changed once made.</summary>
public sealed record Glyph(int Id, char Char, bool[,] Pixels);

/// <summary>
/// A version of the child's font: which drawing each character used at one
/// moment. Text keeps the version it was typed with, so redrawing a letter
/// only changes what is typed afterwards.
/// </summary>
public sealed class FontVersion
{
    public required int Id { get; init; }
    public required IReadOnlyDictionary<char, Glyph> Glyphs { get; init; }
    /// <summary>The drawn letters, falling back to the normal font for the rest.</summary>
    public required FontFamily Family { get; init; }
}

/// <summary>All the letters designed in one document, with every earlier version.</summary>
public sealed class Designs
{
    public const string Capitals = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Smalls = "abcdefghijklmnopqrstuvwxyz";
    public const string Punctuation = ".,!?'\":;-()&/";
    public const string Numbers = "0123456789";
    public const string AllCharacters = Capitals + Smalls + Punctuation + Numbers;

    readonly FontFamily normal;
    readonly List<Glyph> glyphs = new();
    readonly List<FontVersion> versions = new();
    // Font files and names are unique to this session so Windows never mixes
    // up two documents' letters.
    readonly string token = Guid.NewGuid().ToString("N")[..10];

    public Designs(FontFamily normal)
    {
        this.normal = normal;
        AddVersion(0, new Dictionary<char, Glyph>());
    }

    public FontVersion Current => versions[^1];
    public IReadOnlyList<FontVersion> Versions => versions;
    public IReadOnlyList<Glyph> Glyphs => glyphs;

    public bool[,]? CurrentDrawing(char c) => Current.Glyphs.TryGetValue(c, out var g) ? g.Pixels : null;

    /// <summary>
    /// Records a new drawing for a character (an empty drawing means "use the
    /// normal letter"). Returns false if nothing changed.
    /// </summary>
    public bool Redesign(char c, bool[,] pixels)
    {
        var existing = CurrentDrawing(c);
        bool empty = Grid.IsEmpty(pixels);
        if (existing is null ? empty : !empty && Grid.SameAs(existing, pixels))
            return false;

        var map = new Dictionary<char, Glyph>(Current.Glyphs);
        if (empty)
        {
            map.Remove(c);
        }
        else
        {
            var glyph = new Glyph(glyphs.Count == 0 ? 1 : glyphs.Max(g => g.Id) + 1, c, Grid.Copy(pixels));
            glyphs.Add(glyph);
            map[c] = glyph;
        }
        AddVersion(versions.Max(v => v.Id) + 1, map);
        return true;
    }

    /// <summary>Starts a new document's font from the child's saved letters.</summary>
    public void StartFrom(IReadOnlyDictionary<char, bool[,]> drawings)
    {
        foreach (var (c, pixels) in drawings)
            if (AllCharacters.Contains(c) && !Grid.IsEmpty(pixels))
                glyphs.Add(new Glyph(glyphs.Count + 1, c, Grid.Copy(pixels)));
        if (glyphs.Count > 0)
            AddVersion(1, glyphs.ToDictionary(g => g.Char));
    }

    /// <summary>Rebuilds the designs saved in a letter.</summary>
    public void Restore(IEnumerable<Glyph> savedGlyphs, IEnumerable<(int Id, IEnumerable<int> GlyphIds)> savedVersions)
    {
        glyphs.AddRange(savedGlyphs);
        var byId = glyphs.ToDictionary(g => g.Id);
        foreach (var (id, glyphIds) in savedVersions.OrderBy(v => v.Id))
        {
            if (id == 0) continue; // version 0 (no drawings) always exists
            var map = new Dictionary<char, Glyph>();
            foreach (var gid in glyphIds)
                if (byId.TryGetValue(gid, out var g)) map[g.Char] = g;
            AddVersion(id, map);
        }
    }

    public FontVersion VersionOf(FontFamily family) =>
        versions.LastOrDefault(v => v.Family.Source == family.Source) ?? versions[0];

    public FontVersion VersionById(int id) => versions.FirstOrDefault(v => v.Id == id) ?? versions[0];

    void AddVersion(int id, Dictionary<char, Glyph> map)
    {
        versions.Add(new FontVersion { Id = id, Glyphs = map, Family = MakeFamily(id, map) });
    }

    FontFamily MakeFamily(int id, IReadOnlyDictionary<char, Glyph> map)
    {
        if (map.Count == 0) return normal;

        string name = $"Fun Letters {token} {id}";
        string file = $"{token}-{id}.ttf";
        Directory.CreateDirectory(FontFolder);
        File.WriteAllBytes(System.IO.Path.Combine(FontFolder, file),
            PixelFont.Build(name, map.ToDictionary(g => g.Key, g => g.Value.Pixels)));
        // The drawn font first; anything it doesn't have comes from the normal font.
        return new FontFamily(new Uri(FontFolder + System.IO.Path.DirectorySeparatorChar), $"./{file}#{name}, {normal.Source}");
    }

    // ------------------------------------------------------------ storage

    static string AppFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleFunLetters");

    /// <summary>Where the fonts built while the app runs are kept.</summary>
    public static string FontFolder => System.IO.Path.Combine(AppFolder, "fonts");

    /// <summary>Removes fonts left from earlier runs. Fonts still in use can't be deleted and are skipped.</summary>
    public static void CleanUpOldFonts()
    {
        try
        {
            if (!Directory.Exists(FontFolder)) return;
            foreach (var file in Directory.GetFiles(FontFolder, "*.ttf"))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string MyLettersPath => System.IO.Path.Combine(AppFolder, "my-letters.xml");

    /// <summary>The child's latest letters, so a new document starts with them.</summary>
    public static Dictionary<char, bool[,]> LoadMyLetters()
    {
        var result = new Dictionary<char, bool[,]>();
        try
        {
            if (!File.Exists(MyLettersPath)) return result;
            foreach (var e in XDocument.Load(MyLettersPath).Root?.Elements("glyph") ?? [])
                if (GlyphXml.Read(e) is { } g) result[g.Char] = g.Pixels;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        return result;
    }

    public void SaveMyLetters()
    {
        try
        {
            Directory.CreateDirectory(AppFolder);
            new XDocument(new XElement("myletters",
                Current.Glyphs.Values.OrderBy(g => g.Char).Select(GlyphXml.Write))).Save(MyLettersPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Writes and reads one drawing as rows of '#' (drawn) and '.' (empty).</summary>
public static class GlyphXml
{
    public static XElement Write(Glyph g) => new("glyph",
        new XAttribute("id", g.Id),
        new XAttribute("char", g.Char.ToString()),
        Enumerable.Range(0, Grid.Rows).Select(r => new XElement("row",
            new string(Enumerable.Range(0, Grid.Columns).Select(c => g.Pixels[r, c] ? '#' : '.').ToArray()))));

    public static Glyph? Read(XElement e)
    {
        var ch = (string?)e.Attribute("char");
        if (string.IsNullOrEmpty(ch) || ch.Length != 1) return null;

        var pixels = Grid.Empty();
        var rows = e.Elements("row").Select(r => r.Value.Trim()).ToList();
        for (int r = 0; r < Math.Min(rows.Count, Grid.Rows); r++)
            for (int c = 0; c < Math.Min(rows[r].Length, Grid.Columns); c++)
                pixels[r, c] = rows[r][c] == '#';
        return new Glyph((int?)e.Attribute("id") ?? 0, ch[0], pixels);
    }
}
