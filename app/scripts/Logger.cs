using Godot;

namespace SLNG.App;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public static class Logger
{
    public static LogLevel CurrentLevel { get; set; } = LogLevel.Info;

    public static void Debug(string message)
    {
        if (CurrentLevel <= LogLevel.Debug)
            GD.Print("[DEBUG] " + message);
    }

    public static void Info(string message)
    {
        if (CurrentLevel <= LogLevel.Info)
            GD.Print("[INFO] " + message);
    }

    public static void Warn(string message)
    {
        if (CurrentLevel <= LogLevel.Warning)
            GD.PrintErr("[WARN] " + message);
    }

    public static void Error(string message)
    {
        if (CurrentLevel <= LogLevel.Error)
            GD.PrintErr("[ERROR] " + message);
    }
}
