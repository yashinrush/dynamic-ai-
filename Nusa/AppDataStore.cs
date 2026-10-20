using System.IO;
using System.Text.Json;

namespace Nusa;

public sealed class NusaData
{
    public List<TodoItem> Tasks { get; set; } = [];
    public List<WaterEntry> WaterEntries { get; set; } = [];
    public List<ShelfItem> ShelfItems { get; set; } = [];
    public int BottleMl { get; set; } = 250;
    public int WaterGoalMl { get; set; } = 2000;
    public bool WaterRemindersEnabled { get; set; }
    public int ReminderStartHour { get; set; } = 9;
    public int ReminderEndHour { get; set; } = 21;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public DateTime? FocusEndsAt { get; set; }
    public bool IsOnBreak { get; set; }
    public string? LastTrackTitle { get; set; }
    public string? LastTrackArtist { get; set; }
    public double LastTrackDurationSeconds { get; set; }
}

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime? DueAt { get; set; }
    public bool IsDone { get; set; }
    public DateTime? RemindedAt { get; set; }
}

public sealed class WaterEntry
{
    public DateTime At { get; set; } = DateTime.Now;
    public int Millilitres { get; set; }
}

public sealed class ShelfItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

public static class AppDataStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nusa");
    private static string FilePath => Path.Combine(DataDirectory, "nusa-data.json");

    public static NusaData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<NusaData>(File.ReadAllText(FilePath)) ?? new NusaData();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        var data = new NusaData();
        data.Tasks.AddRange([
            new TodoItem { Title = "Review the report", IsDone = true },
            new TodoItem { Title = "Reply to Dina", IsDone = true },
            new TodoItem { Title = "Plan the week" }
        ]);
        return data;
    }

    public static void Save(NusaData data)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
        File.Move(temp, FilePath, true);
    }
}
