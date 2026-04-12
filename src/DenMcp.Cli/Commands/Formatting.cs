using DenMcp.Core.Models;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Cli.Commands;

public static class Fmt
{
    public static ConsoleColor StatusColor(TaskStatus status) => status switch
    {
        TaskStatus.Planned => ConsoleColor.Gray,
        TaskStatus.InProgress => ConsoleColor.Cyan,
        TaskStatus.Review => ConsoleColor.Yellow,
        TaskStatus.Blocked => ConsoleColor.Red,
        TaskStatus.Done => ConsoleColor.Green,
        TaskStatus.Cancelled => ConsoleColor.DarkGray,
        _ => ConsoleColor.White
    };

    public static string StatusIcon(TaskStatus status) => status switch
    {
        TaskStatus.Planned => " ",
        TaskStatus.InProgress => ">",
        TaskStatus.Review => "?",
        TaskStatus.Blocked => "!",
        TaskStatus.Done => "x",
        TaskStatus.Cancelled => "-",
        _ => " "
    };

    public static ConsoleColor PriorityColor(int priority) => priority switch
    {
        1 => ConsoleColor.Red,
        2 => ConsoleColor.Yellow,
        3 => ConsoleColor.White,
        4 => ConsoleColor.Gray,
        5 => ConsoleColor.DarkGray,
        _ => ConsoleColor.White
    };

    public static ConsoleColor DispatchStatusColor(DispatchStatus status) => status switch
    {
        DispatchStatus.Pending => ConsoleColor.Yellow,
        DispatchStatus.Approved => ConsoleColor.Cyan,
        DispatchStatus.Rejected => ConsoleColor.Red,
        DispatchStatus.Completed => ConsoleColor.Green,
        DispatchStatus.Expired => ConsoleColor.DarkGray,
        _ => ConsoleColor.White
    };

    public static string DispatchStatusIcon(DispatchStatus status) => status switch
    {
        DispatchStatus.Pending => "?",
        DispatchStatus.Approved => ">",
        DispatchStatus.Rejected => "x",
        DispatchStatus.Completed => "v",
        DispatchStatus.Expired => "-",
        _ => " "
    };

    public static void WriteColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    public static void WriteLineColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static void WriteHeader(string text)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(text);
        Console.WriteLine(new string('─', Math.Min(text.Length, Console.WindowWidth - 1)));
        Console.ResetColor();
    }

    public static void WriteRow(params (string text, int width, ConsoleColor color)[] cols)
    {
        foreach (var (text, width, color) in cols)
        {
            Console.ForegroundColor = color;
            Console.Write(Truncate(text, width).PadRight(width));
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    public static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return maxLen > 3 ? text[..(maxLen - 3)] + "..." : text[..maxLen];
    }

    public static string FormatTime(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}d ago";
        return dt.ToString("yyyy-MM-dd");
    }
}
