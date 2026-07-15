using System.Text.Json;

namespace IpadBridgeBle;

public class Config
{
    /// <summary>Screen edge that enters iPad mode: "right", "left", "top", "bottom", or "none".</summary>
    public string EnterEdge { get; set; } = "right";

    /// <summary>Map Ctrl to Cmd (and Win to Ctrl) in iPad mode, so Ctrl+C becomes Cmd+C.</summary>
    public bool MapCtrlToCmd { get; set; } = true;

    /// <summary>Ctrl+Alt+Space/arrows/M media hotkeys (work even outside iPad mode).</summary>
    public bool MediaHotkeys { get; set; } = true;

    public bool InvertScroll { get; set; } = false;

    /// <summary>Multiplier applied to mouse movement sent to the iPad.</summary>
    public double MouseSensitivity { get; set; } = 1.0;

    /// <summary>Turn off Windows 3/4-finger touchpad gestures while in iPad mode.</summary>
    public bool DisableSwipeGesturesInIpadMode { get; set; } = true;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { /* broken file shouldn't kill the app */ }
        var config = new Config();
        config.Save();
        return config;
    }

    public void Save() =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
