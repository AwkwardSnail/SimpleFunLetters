using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SimpleFunLetters;

/// <summary>
/// The drawing grid for one character. Rows run top to bottom; the bottom
/// <see cref="DescentRows"/> rows hang below the line, for the tails of g, j, p, q and y.
/// </summary>
public static class Grid
{
    public const int Columns = 18;
    public const int Rows = 24;
    public const int DescentRows = 6;
    public const int AscentRows = Rows - DescentRows; // rows sitting on the line

    public static bool[,] Empty() => new bool[Rows, Columns];

    /// <summary>
    /// Fits a drawing made on a grid of another size (such as the 12 × 16 grid
    /// of the first version) onto this one, square by square. Both grids put
    /// the writing line three quarters of the way down, so letters stay on it.
    /// </summary>
    public static bool[,] Resample(bool[,] source)
    {
        int rows = source.GetLength(0), cols = source.GetLength(1);
        if (rows == Rows && cols == Columns) return Copy(source);
        var result = Empty();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                result[r, c] = source[r * rows / Rows, c * cols / Columns];
        return result;
    }

    public static bool IsEmpty(bool[,] pixels)
    {
        foreach (var p in pixels) if (p) return false;
        return true;
    }

    public static bool SameAs(bool[,] a, bool[,] b)
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                if (a[r, c] != b[r, c]) return false;
        return true;
    }

    public static bool[,] Copy(bool[,] pixels) => (bool[,])pixels.Clone();
}

/// <summary>
/// Builds a small TrueType font from pixel drawings. Each lit pixel becomes a
/// square, so the letters keep their blocky, hand-drawn look at every size.
/// Characters that aren't in the font fall back to the normal font.
/// </summary>
public static class PixelFont
{
    const int Pixel = 50;                         // one square, in font units
    const int UnitsPerEm = Grid.Rows * Pixel;     // the whole grid is one em (1200)
    const int Ascender = UnitsPerEm * 950 / 1024; // line spacing close to Atkinson Hyperlegible
    const int Descender = UnitsPerEm * 300 / 1024;

    sealed record GlyphData(char Char, byte[] Outline, int Advance, int XMin, int YMin, int XMax, int YMax, int Points, int Contours);

    public static byte[] Build(string familyName, IReadOnlyDictionary<char, bool[,]> glyphs)
    {
        var data = glyphs.Where(g => !Grid.IsEmpty(g.Value))
                         .OrderBy(g => g.Key)
                         .Select(g => MakeGlyph(g.Key, g.Value))
                         .ToList();
        if (data.Count == 0) throw new ArgumentException("A font needs at least one drawn character.");

        int numGlyphs = data.Count + 1; // glyph 0 is the empty .notdef

        // glyf + loca
        var glyf = new MemoryStream();
        // loca holds numGlyphs + 1 offsets: where each glyph starts, then the end.
        // .notdef has no outline, so it starts and ends at 0.
        var loca = new List<uint> { 0, 0 };
        foreach (var g in data)
        {
            glyf.Write(g.Outline);
            loca.Add((uint)glyf.Length);
        }
        var locaBytes = new byte[loca.Count * 4];
        for (int i = 0; i < loca.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(i * 4), loca[i]);

        int xMin = data.Min(g => g.XMin), yMin = data.Min(g => g.YMin);
        int xMax = data.Max(g => g.XMax), yMax = data.Max(g => g.YMax);
        int advanceMax = data.Max(g => g.Advance);
        int avgWidth = (int)data.Average(g => g.Advance);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = Os2(avgWidth, data[0].Char, data[^1].Char),
            ["cmap"] = Cmap(data.Select((g, i) => (g.Char, (ushort)(i + 1))).ToList()),
            ["glyf"] = glyf.ToArray(),
            ["head"] = Head(xMin, yMin, xMax, yMax),
            ["hhea"] = Hhea(advanceMax, xMin, numGlyphs, data),
            ["hmtx"] = Hmtx(data),
            ["loca"] = locaBytes,
            ["maxp"] = Maxp(numGlyphs, data.Max(g => g.Points), data.Max(g => g.Contours)),
            ["name"] = Name(familyName),
            ["post"] = Post(),
        };
        return Assemble(tables);
    }

    /// <summary>Turns lit pixels into rectangles, one per run of pixels in a row.</summary>
    static GlyphData MakeGlyph(char ch, bool[,] pixels)
    {
        int minCol = int.MaxValue, maxCol = -1;
        for (int r = 0; r < Grid.Rows; r++)
            for (int c = 0; c < Grid.Columns; c++)
                if (pixels[r, c]) { minCol = Math.Min(minCol, c); maxCol = Math.Max(maxCol, c); }

        // Half a pixel of space each side of the drawing.
        const int side = Pixel / 2;
        int advance = (maxCol - minCol + 1) * Pixel + 2 * side;

        var rects = new List<(int x0, int y0, int x1, int y1)>();
        for (int r = 0; r < Grid.Rows; r++)
        {
            int c = 0;
            while (c < Grid.Columns)
            {
                if (!pixels[r, c]) { c++; continue; }
                int start = c;
                while (c < Grid.Columns && pixels[r, c]) c++;
                int x0 = side + (start - minCol) * Pixel;
                int x1 = side + (c - minCol) * Pixel;
                int yTop = (Grid.AscentRows - r) * Pixel;
                int yBottom = yTop - Pixel;
                // Overlap the row below by one unit so no hairline gap shows between rows.
                rects.Add((x0, yBottom - 1, x1, yTop));
            }
        }

        var outline = new MemoryStream();
        int gxMin = rects.Min(q => q.x0), gyMin = rects.Min(q => q.y0);
        int gxMax = rects.Max(q => q.x1), gyMax = rects.Max(q => q.y1);
        WriteI16(outline, rects.Count);
        WriteI16(outline, gxMin); WriteI16(outline, gyMin); WriteI16(outline, gxMax); WriteI16(outline, gyMax);
        for (int i = 0; i < rects.Count; i++) WriteU16(outline, i * 4 + 3); // endPtsOfContours
        WriteU16(outline, 0);                                                 // no instructions

        int points = rects.Count * 4;
        for (int i = 0; i < points; i++) outline.WriteByte(0x01);            // all on-curve, 16-bit deltas

        // Clockwise: up the left side, along the top, down the right side.
        var xs = new List<int>();
        var ys = new List<int>();
        foreach (var (x0, y0, x1, y1) in rects)
        {
            xs.AddRange([x0, x0, x1, x1]);
            ys.AddRange([y0, y1, y1, y0]);
        }
        int prev = 0;
        foreach (var x in xs) { WriteI16(outline, x - prev); prev = x; }
        prev = 0;
        foreach (var y in ys) { WriteI16(outline, y - prev); prev = y; }
        while (outline.Length % 4 != 0) outline.WriteByte(0);

        return new GlyphData(ch, outline.ToArray(), advance, gxMin, gyMin, gxMax, gyMax, points, rects.Count);
    }

    // ------------------------------------------------------------- tables

    static byte[] Head(int xMin, int yMin, int xMax, int yMax)
    {
        var s = new MemoryStream();
        WriteU32(s, 0x00010000); WriteU32(s, 0x00010000);
        WriteU32(s, 0);                    // checkSumAdjustment, filled in at the end
        WriteU32(s, 0x5F0F3CF5);
        WriteU16(s, 0x000B);               // baseline at y=0, lsb at x=0, integer scaling
        WriteU16(s, UnitsPerEm);
        WriteU32(s, 0); WriteU32(s, 0);    // created
        WriteU32(s, 0); WriteU32(s, 0);    // modified
        WriteI16(s, xMin); WriteI16(s, yMin); WriteI16(s, xMax); WriteI16(s, yMax);
        WriteU16(s, 0);                    // macStyle
        WriteU16(s, 8);                    // lowestRecPPEM
        WriteI16(s, 2);                    // fontDirectionHint
        WriteI16(s, 1);                    // long loca offsets
        WriteI16(s, 0);
        return s.ToArray();
    }

    static byte[] Hhea(int advanceMax, int minLsb, int numGlyphs, List<GlyphData> data)
    {
        var s = new MemoryStream();
        WriteU32(s, 0x00010000);
        WriteI16(s, Ascender); WriteI16(s, -Descender); WriteI16(s, 0);
        WriteU16(s, advanceMax);
        WriteI16(s, minLsb);
        WriteI16(s, data.Min(g => g.Advance - g.XMax));  // minRightSideBearing
        WriteI16(s, data.Max(g => g.XMax));              // xMaxExtent
        WriteI16(s, 1); WriteI16(s, 0); WriteI16(s, 0);  // caret slope rise/run, offset
        for (int i = 0; i < 4; i++) WriteI16(s, 0);
        WriteI16(s, 0);
        WriteU16(s, numGlyphs);
        return s.ToArray();
    }

    static byte[] Hmtx(List<GlyphData> data)
    {
        var s = new MemoryStream();
        WriteU16(s, Pixel * Grid.Columns / 2); WriteI16(s, 0); // .notdef
        foreach (var g in data) { WriteU16(s, g.Advance); WriteI16(s, g.XMin); }
        return s.ToArray();
    }

    static byte[] Maxp(int numGlyphs, int maxPoints, int maxContours)
    {
        var s = new MemoryStream();
        WriteU32(s, 0x00010000);
        WriteU16(s, numGlyphs);
        WriteU16(s, maxPoints);
        WriteU16(s, maxContours);
        WriteU16(s, 0); WriteU16(s, 0);  // composite points/contours
        WriteU16(s, 2);                  // maxZones
        for (int i = 0; i < 8; i++) WriteU16(s, 0);
        return s.ToArray();
    }

    static byte[] Os2(int avgWidth, char first, char last)
    {
        var s = new MemoryStream();
        WriteU16(s, 4);
        WriteI16(s, avgWidth);
        WriteU16(s, 400); WriteU16(s, 5); WriteU16(s, 0);       // weight, width, fsType (installable)
        WriteI16(s, 650); WriteI16(s, 600); WriteI16(s, 0); WriteI16(s, 75);    // subscript
        WriteI16(s, 650); WriteI16(s, 600); WriteI16(s, 0); WriteI16(s, 350);   // superscript
        WriteI16(s, 50); WriteI16(s, 300);                                      // strikeout
        WriteI16(s, 0);                                                         // family class
        s.Write(new byte[10]);                                                  // panose
        WriteU32(s, 1); WriteU32(s, 0); WriteU32(s, 0); WriteU32(s, 0);         // Basic Latin
        s.Write("AWSN"u8);
        WriteU16(s, 0x0040);                                                    // regular
        WriteU16(s, first); WriteU16(s, last);
        WriteI16(s, Ascender); WriteI16(s, -Descender); WriteI16(s, 0);         // typo metrics
        WriteU16(s, Ascender); WriteU16(s, Descender);                          // win metrics
        WriteU32(s, 1); WriteU32(s, 0);                                         // Latin 1 code page
        WriteI16(s, Pixel * Grid.Rows / 2); WriteI16(s, Pixel * Grid.AscentRows);   // x-height, cap height
        WriteU16(s, 0); WriteU16(s, 32); WriteU16(s, 1);
        return s.ToArray();
    }

    static byte[] Cmap(List<(char Char, ushort Glyph)> map)
    {
        // Format 4, one segment per character plus the closing 0xFFFF segment.
        int segCount = map.Count + 1;
        int searchRange = 2 * (1 << (int)Math.Floor(Math.Log2(segCount)));
        int entrySelector = (int)Math.Log2(searchRange / 2);

        var sub = new MemoryStream();
        WriteU16(sub, 4);
        WriteU16(sub, 0); // length, patched below
        WriteU16(sub, 0);
        WriteU16(sub, segCount * 2);
        WriteU16(sub, searchRange);
        WriteU16(sub, entrySelector);
        WriteU16(sub, segCount * 2 - searchRange);
        foreach (var m in map) WriteU16(sub, m.Char);
        WriteU16(sub, 0xFFFF);
        WriteU16(sub, 0);
        foreach (var m in map) WriteU16(sub, m.Char);
        WriteU16(sub, 0xFFFF);
        foreach (var m in map) WriteU16(sub, (ushort)(m.Glyph - m.Char));
        WriteU16(sub, 1);
        for (int i = 0; i < segCount; i++) WriteU16(sub, 0);
        var subBytes = sub.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(subBytes.AsSpan(2), (ushort)subBytes.Length);

        var s = new MemoryStream();
        WriteU16(s, 0); WriteU16(s, 1);
        WriteU16(s, 3); WriteU16(s, 1); WriteU32(s, 12); // Windows, Unicode BMP
        s.Write(subBytes);
        return s.ToArray();
    }

    static byte[] Name(string family)
    {
        var records = new (int Id, string Text)[]
        {
            (1, family), (2, "Regular"), (3, family + " Regular"), (4, family),
            (6, new string(family.Where(char.IsLetterOrDigit).ToArray()) + "-Regular"),
        };
        var strings = new MemoryStream();
        var s = new MemoryStream();
        WriteU16(s, 0);
        WriteU16(s, records.Length);
        WriteU16(s, 6 + records.Length * 12);
        foreach (var (id, text) in records)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            WriteU16(s, 3); WriteU16(s, 1); WriteU16(s, 0x0409); WriteU16(s, id);
            WriteU16(s, bytes.Length); WriteU16(s, (int)strings.Length);
            strings.Write(bytes);
        }
        s.Write(strings.ToArray());
        return s.ToArray();
    }

    static byte[] Post()
    {
        var s = new MemoryStream();
        WriteU32(s, 0x00030000);
        WriteU32(s, 0);
        WriteI16(s, -100); WriteI16(s, 50);
        for (int i = 0; i < 5; i++) WriteU32(s, 0);
        return s.ToArray();
    }

    static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int numTables = tables.Count;
        int searchRange = 16 * (1 << (int)Math.Floor(Math.Log2(numTables)));
        var font = new MemoryStream();
        WriteU32(font, 0x00010000);
        WriteU16(font, numTables);
        WriteU16(font, searchRange);
        WriteU16(font, (int)Math.Log2(searchRange / 16));
        WriteU16(font, numTables * 16 - searchRange);

        int offset = 12 + numTables * 16;
        var layout = new List<(string Tag, int Offset, byte[] Data)>();
        foreach (var (tag, data) in tables)
        {
            layout.Add((tag, offset, data));
            offset += (data.Length + 3) & ~3;
        }
        int headOffset = 0;
        foreach (var (tag, off, data) in layout)
        {
            font.Write(Encoding.ASCII.GetBytes(tag));
            WriteU32(font, Checksum(data));
            WriteU32(font, (uint)off);
            WriteU32(font, (uint)data.Length);
            if (tag == "head") headOffset = off;
        }
        foreach (var (_, _, data) in layout)
        {
            font.Write(data);
            while (font.Length % 4 != 0) font.WriteByte(0);
        }

        var bytes = font.ToArray();
        uint adjustment = unchecked(0xB1B0AFBA - Checksum(bytes));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(headOffset + 8), adjustment);
        return bytes;
    }

    static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int j = 0; j < 4; j++)
                word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    static void WriteU16(Stream s, int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); s.Write(b); }
    static void WriteI16(Stream s, int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); s.Write(b); }
    static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
}
