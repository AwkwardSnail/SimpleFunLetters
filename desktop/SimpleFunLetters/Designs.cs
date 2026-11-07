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
        Current = AddVersion(0, new Dictionary<char, Glyph>());
    }

    /// <summary>The version new typing uses.</summary>
    public FontVersion Current { get; private set; }
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
        Current = AddVersion(NextVersionId(), map);
        return true;
    }

    /// <summary>
    /// For "Apply to existing text": the version that looks like
    /// <paramref name="old"/> except that character <paramref name="c"/> uses
    /// its current drawing (or the normal letter, if it has none). Returns
    /// <paramref name="old"/> itself when it already does.
    /// </summary>
    public FontVersion WithCurrentDrawingOf(FontVersion old, char c)
    {
        Current.Glyphs.TryGetValue(c, out var wanted);
        old.Glyphs.TryGetValue(c, out var has);
        if (wanted == has) return old;

        var key = (old.Id, c, wanted?.Id ?? 0);
        if (derived.TryGetValue(key, out var existing)) return existing;

        var map = new Dictionary<char, Glyph>(old.Glyphs);
        if (wanted is null) map.Remove(c); else map[c] = wanted;
        // Reuse a version with exactly these drawings if there is one (often the current one).
        var same = versions.FirstOrDefault(v => v.Glyphs.Count == map.Count && map.All(kv => v.Glyphs.TryGetValue(kv.Key, out var g) && g == kv.Value));
        var version = same ?? AddVersion(NextVersionId(), map);
        derived[key] = version;
        return version;
    }

    readonly Dictionary<(int, char, int), FontVersion> derived = new();

    int NextVersionId() => versions.Max(v => v.Id) + 1;

    /// <summary>Starts a new document's font from the child's saved letters.</summary>
    public void StartFrom(IReadOnlyDictionary<char, bool[,]> drawings)
    {
        foreach (var (c, pixels) in drawings)
            if (AllCharacters.Contains(c) && !Grid.IsEmpty(pixels))
                glyphs.Add(new Glyph(glyphs.Count + 1, c, Grid.Copy(pixels)));
        if (glyphs.Count > 0)
            Current = AddVersion(1, glyphs.ToDictionary(g => g.Char));
    }

    /// <summary>Rebuilds the designs saved in a letter.</summary>
    public void Restore(IEnumerable<Glyph> savedGlyphs, IEnumerable<(int Id, IEnumerable<int> GlyphIds)> savedVersions, int? currentId)
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
        // Older files don't say which version is current: it is the newest.
        Current = currentId is int cid && versions.FirstOrDefault(v => v.Id == cid) is { } v2 ? v2 : versions.MaxBy(v => v.Id)!;
    }

    public FontVersion VersionOf(FontFamily family) =>
        versions.LastOrDefault(v => v.Family.Source == family.Source) ?? versions[0];

    public FontVersion VersionById(int id) => versions.FirstOrDefault(v => v.Id == id) ?? versions[0];

    FontVersion AddVersion(int id, Dictionary<char, Glyph> map)
    {
        var version = new FontVersion { Id = id, Glyphs = map, Family = MakeFamily(id, map) };
        versions.Add(version);
        return version;
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

    static string FontsRoot => System.IO.Path.Combine(AppFolder, "fonts");

    /// <summary>
    /// This copy of the app's own name: its process number and start time.
    /// Two copies of the app open at once each keep their fonts apart.
    /// </summary>
    static readonly string SessionName = SessionNameOf(System.Diagnostics.Process.GetCurrentProcess());

    static string SessionNameOf(System.Diagnostics.Process p) => $"{p.Id}-{p.StartTime.ToUniversalTime().Ticks}";

    /// <summary>Where the fonts built while this copy of the app runs are kept.</summary>
    public static string FontFolder => System.IO.Path.Combine(FontsRoot, SessionName);

    /// <summary>
    /// Removes fonts left by copies of the app that have closed. Fonts of a copy
    /// that is still open are left alone (it may still be showing them).
    /// </summary>
    public static void CleanUpOldFonts()
    {
        try
        {
            if (!Directory.Exists(FontsRoot)) return;
            // Fonts from the first version sat loose in the folder.
            foreach (var file in Directory.GetFiles(FontsRoot, "*.ttf"))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var folder in Directory.GetDirectories(FontsRoot))
            {
                if (StillRunning(System.IO.Path.GetFileName(folder))) continue;
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool StillRunning(string sessionName)
    {
        if (sessionName == SessionName) return true;
        var dash = sessionName.IndexOf('-');
        if (dash < 0 || !int.TryParse(sessionName[..dash], out int pid)) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return SessionNameOf(p) == sessionName; // same number, but is it the same copy?
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // no such process any more
        }
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
        new XAttribute("columns", Grid.Columns),
        new XAttribute("rows", Grid.Rows),
        Enumerable.Range(0, Grid.Rows).Select(r => new XElement("row",
            new string(Enumerable.Range(0, Grid.Columns).Select(c => g.Pixels[r, c] ? '#' : '.').ToArray()))));

    /// <summary>Reads a drawing, scaling it to today's grid if it was drawn on another size.</summary>
    public static Glyph? Read(XElement e)
    {
        var ch = (string?)e.Attribute("char");
        if (string.IsNullOrEmpty(ch) || ch.Length != 1) return null;

        var rows = e.Elements("row").Select(r => r.Value.Trim()).ToList();
        // Drawings saved before the grid size was recorded are 12 × 16.
        int rowCount = (int?)e.Attribute("rows") ?? (rows.Count > 0 ? rows.Count : 16);
        int colCount = (int?)e.Attribute("columns") ?? (rows.Count > 0 ? rows.Max(r => r.Length) : 12);
        rowCount = Math.Clamp(rowCount, 1, 256);
        colCount = Math.Clamp(colCount, 1, 256);

        var source = new bool[rowCount, colCount];
        for (int r = 0; r < Math.Min(rows.Count, rowCount); r++)
            for (int c = 0; c < Math.Min(rows[r].Length, colCount); c++)
                source[r, c] = rows[r][c] == '#';
        return new Glyph((int?)e.Attribute("id") ?? 0, ch[0], Grid.Resample(source));
    }
}
