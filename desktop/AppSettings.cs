using System.Text.Json;

namespace LoopSolver;

/// <summary>Stored in %AppData%\LoopSolver\settings.json.</summary>
internal sealed class AppSettings
{
    /// <summary>"Show" puts the answer over the board; "Draw" clicks it in.</summary>
    public string Mode { get; set; } = "Show";

    /// <summary>"Click" clicks the middle of each edge (puzzle-loop.com, Simon Tatham's Loopy, most sites).
    /// "Drag" drags along the dots (puzz.link and other sites where you draw by dragging).</summary>
    public string DrawMethod { get; set; } = "Click";

    /// <summary>Pause between clicks. Raise it if a site misses clicks.</summary>
    public int ClickDelayMs { get; set; } = 30;

    public string LineColor { get; set; } = "#1E90FF";

    /// <summary>Last board area picked, in screen pixels: x, y, width, height.</summary>
    public int[]? Area { get; set; }

    public int[]? WindowPosition { get; set; }

    /// <summary>Never written to disk (used when taking a picture of the window).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; init; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoopSolver");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public bool DrawMode => Mode.Equals("Draw", StringComparison.OrdinalIgnoreCase);
    public bool DragToDraw => DrawMethod.Equals("Drag", StringComparison.OrdinalIgnoreCase);
    public Rectangle? AreaRect => Area is { Length: 4 } a ? new Rectangle(a[0], a[1], a[2], a[3]) : null;

    public Color LineColorValue
    {
        get
        {
            try { return ColorTranslator.FromHtml(LineColor); }
            catch (Exception) { return Color.DodgerBlue; }
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A broken settings file just means defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Not being able to save settings shouldn't stop the tool.
        }
    }
}
