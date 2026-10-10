namespace NightClubPatcher.Core.Infrastructure;

/// <summary>
/// Abstraction over user prompts so the CLI and the GUI can share every workflow.
/// Every destructive step (uninstall, deleting files) must go through this interface.
/// </summary>
public interface IUserInteraction
{
    /// <summary>Returns the chosen option index, or -1 when cancelled.</summary>
    int Choose(string title, string message, IReadOnlyList<string> options);

    void Notify(string title, string message);
}

public static class UserInteractionExtensions
{
    public static bool Confirm(this IUserInteraction ui, string title, string message, string yes = "Yes", string no = "Cancel") =>
        ui.Choose(title, message, new[] { no, yes }) == 1;
}

/// <summary>Never confirms anything destructive. Used for unattended runs.</summary>
public sealed class NonInteractive : IUserInteraction
{
    private readonly KitLog _log;
    public NonInteractive(KitLog log) => _log = log;

    public int Choose(string title, string message, IReadOnlyList<string> options)
    {
        _log.Warn($"{title}: {message.Replace(Environment.NewLine, " ")} -> no interactive user, cancelling.");
        return -1;
    }

    public void Notify(string title, string message) => _log.Info($"{title}: {message}");
}
