using System.Text.Json;

namespace WinKeyGuard;

static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinKeyGuard");

    public static string Config => Path.Combine(Dir, "config.json");
    public static string Backup => Path.Combine(Dir, "keymap-backup.json");
    public static string LogFile => Path.Combine(Dir, "log.txt");

    static AppPaths()
    {
        Directory.CreateDirectory(Dir);
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Init()
    {
        try
        {
            var fi = new FileInfo(AppPaths.LogFile);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
        }
        catch { }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(AppPaths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}

sealed class Config
{
    // Exe names (case-insensitive) that count as "a game is focused".
    public List<string> Targets { get; set; } = new() { "TslGame.exe" };

    // Delay before disabling the Win keys after a target gains focus.
    public int DisableDelayMs { get; set; } = 150;

    // Delay before restoring the Win keys after a target loses focus.
    // Absorbs the brief focus flicker through explorer.exe when alt-tabbing.
    public int RestoreDelayMs { get; set; } = 600;

    // Only used when creating a keymap backup from the live keyboard.
    // 2144 = 0x0860 = Keychron Q6 Max over USB.
    public int ProductId { get; set; } = 2144;
    public int Rows { get; set; } = 6;
    public int Cols { get; set; } = 21;

    // Which Win keys get disabled (KC_LGUI / KC_RGUI keymap entries).
    public bool DisableLeftWin { get; set; } = true;
    public bool DisableRightWin { get; set; } = true;

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Config Clone() => new()
    {
        Targets = new List<string>(Targets),
        DisableDelayMs = DisableDelayMs,
        RestoreDelayMs = RestoreDelayMs,
        ProductId = ProductId,
        Rows = Rows,
        Cols = Cols,
        DisableLeftWin = DisableLeftWin,
        DisableRightWin = DisableRightWin
    };

    public void Save()
    {
        Sanitize();
        File.WriteAllText(AppPaths.Config, JsonSerializer.Serialize(this, Opts));
    }

    public static Config Load()
    {
        if (!File.Exists(AppPaths.Config))
        {
            var fresh = new Config();
            try { File.WriteAllText(AppPaths.Config, JsonSerializer.Serialize(fresh, Opts)); } catch { }
            return fresh;
        }

        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(AppPaths.Config), Opts);
            if (c != null)
            {
                c.Sanitize();
                return c;
            }
        }
        catch (Exception ex)
        {
            // Never overwrite a config the user has edited - just fall back to defaults in memory.
            Log.Write("Config load failed, using defaults: " + ex.Message);
        }

        return new Config();
    }

    void Sanitize()
    {
        Targets = Targets
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Targets.Count == 0) Targets.Add("TslGame.exe");
        DisableDelayMs = Math.Clamp(DisableDelayMs, 0, 60000);
        RestoreDelayMs = Math.Clamp(RestoreDelayMs, 0, 60000);
    }

    public bool IsTarget(string? exe) =>
        exe != null && Targets.Any(t => string.Equals(t, exe, StringComparison.OrdinalIgnoreCase));
}

static class Assets
{
    // Loads an icon embedded in the exe (see EmbeddedResource entries in the csproj).
    public static Icon Load(string name, Size? size = null)
    {
        using var stream = typeof(Assets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded icon: " + name);
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }
}
