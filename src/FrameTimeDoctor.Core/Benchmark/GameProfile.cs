using System.Text.Json;
using FrameTimeDoctor.Core.Knowledge;

namespace FrameTimeDoctor.Core.Benchmark;

/// <summary>
/// A supported title and the exact, repeatable sequence to run, so before/after passes are comparable.
/// </summary>
public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Process { get; set; } = "";
    /// <summary>"built-in" benchmark mode or a "scripted" in-game route.</summary>
    public string Mode { get; set; } = "built-in";
    public int DurationSeconds { get; set; } = 60;
    public List<string> Setup { get; set; } = new();
    /// <summary>What the player does when they press the start hotkey.</summary>
    public string StartCue { get; set; } = "";
    public string AntiCheat { get; set; } = "none";
    public string? Notes { get; set; }

    public static List<GameProfile> LoadAll(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "knowledge", "games.json");
        return JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(path), KnowledgeBase.JsonOptions) ?? new();
    }
}
