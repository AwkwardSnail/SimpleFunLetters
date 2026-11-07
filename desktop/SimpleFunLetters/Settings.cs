using System.IO;
using System.Text.Json;

namespace SimpleFunLetters;

public enum Side { Top, Bottom, Left, Right }

/// <summary>
/// The page settings: margins in millimetres, text size in points, and which
/// margin lines are showing. Kept in a small file on this computer only.
/// </summary>
public sealed class Settings
{
    public double MarginTop { get; set; } = 25;
    public double MarginBottom { get; set; } = 25;
    public double MarginLeft { get; set; } = 25;
    public double MarginRight { get; set; } = 25;
    public double FontSize { get; set; } = 12;

    public bool LineTop { get; set; }
    public bool LineBottom { get; set; }
    public bool LineLeft { get; set; }
    public bool LineRight { get; set; }

    public double GetMargin(Side side) => side switch
    {
        Side.Top => MarginTop,
        Side.Bottom => MarginBottom,
        Side.Left => MarginLeft,
        _ => MarginRight,
    };

    public void SetMargin(Side side, double mm)
    {
        switch (side)
        {
            case Side.Top: MarginTop = mm; break;
            case Side.Bottom: MarginBottom = mm; break;
            case Side.Left: MarginLeft = mm; break;
            default: MarginRight = mm; break;
        }
    }

    public bool GetLine(Side side) => side switch
    {
        Side.Top => LineTop,
        Side.Bottom => LineBottom,
        Side.Left => LineLeft,
        _ => LineRight,
    };

    public void SetLine(Side side, bool shown)
    {
        switch (side)
        {
            case Side.Top: LineTop = shown; break;
            case Side.Bottom: LineBottom = shown; break;
            case Side.Left: LineLeft = shown; break;
            default: LineRight = shown; break;
        }
    }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleFunLetters", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings: start from the defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember settings shouldn't interrupt writing.
        }
    }
}
