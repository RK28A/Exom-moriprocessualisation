using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Cli;

internal sealed class ConsoleInteraction : IUserInteraction
{
    public int Choose(string title, string message, IReadOnlyList<string> options)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== " + title + " ===");
        Console.ResetColor();
        Console.WriteLine(message);
        for (var i = 0; i < options.Count; i++) Console.WriteLine($"  [{i}] {options[i]}");
        Console.Write("Choice (Enter = 0): ");
        var line = Console.ReadLine();
        if (line == null) return -1;
        return int.TryParse(line.Trim(), out var n) && n >= 0 && n < options.Count ? n : 0;
    }

    public void Notify(string title, string message) => Console.WriteLine($"{title}: {message}");
}
