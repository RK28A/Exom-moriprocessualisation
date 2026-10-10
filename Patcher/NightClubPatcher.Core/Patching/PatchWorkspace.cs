using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Patching;

/// <summary>
/// %TEMP%\NightClubModKit\&lt;timestamp&gt;\{input,unpacked,native,patched,output}.
/// The original backup is only ever copied INTO input/; nothing is written back.
/// </summary>
public sealed class PatchWorkspace
{
    public string Root { get; }
    public string Input => Path.Combine(Root, "input");
    public string Unpacked => Path.Combine(Root, "unpacked");
    public string Native => Path.Combine(Root, "native");
    public string Patched => Path.Combine(Root, "patched");
    public string Output => Path.Combine(Root, "output");

    private PatchWorkspace(string root)
    {
        Root = root;
        foreach (var d in new[] { Input, Unpacked, Native, Patched, Output }) Directory.CreateDirectory(d);
    }

    public static PatchWorkspace Create(string label)
    {
        Cleanup(keep: 5);
        var root = Path.Combine(KitPaths.WorkspaceRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}");
        var n = 1;
        var unique = root;
        while (Directory.Exists(unique)) unique = root + "-" + n++;
        return new PatchWorkspace(unique);
    }

    private static void Cleanup(int keep)
    {
        try
        {
            if (!Directory.Exists(KitPaths.WorkspaceRoot)) return;
            foreach (var old in Directory.GetDirectories(KitPaths.WorkspaceRoot).OrderByDescending(d => d).Skip(keep))
                Directory.Delete(old, true);
        }
        catch
        {
            // temp cleanup is best effort
        }
    }
}
