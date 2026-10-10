namespace NightClubPatcher.Core.Adb;

public enum QuestModel { Unknown, Quest1, Quest2, QuestPro, Quest3, Quest3S, NotQuest }

/// <summary>A Meta headset generation as seen through adb (ro.product.model / ro.product.device).</summary>
public sealed record QuestModelInfo(
    QuestModel Model,
    string DisplayName,
    IReadOnlyList<string> Codenames,
    IReadOnlyList<string> ModelStrings,
    IReadOnlyList<string> SupportedDevicesTokens,
    bool Supported,
    string? Note = null)
{
    public bool IsQuest => Model is not (QuestModel.Unknown or QuestModel.NotQuest);
}

public sealed record DeviceCompatibility(bool Supported, bool ListedInManifest, IReadOnlyList<string> Warnings)
{
    public string Summary => Supported
        ? Warnings.Count == 0 ? "supported" : "supported (see warnings)"
        : "not supported";
}

/// <summary>
/// Known Meta Quest headsets. Every supported model is arm64-v8a, so the same patched APK runs
/// on all of them; the model only matters for compatibility checks and storage behaviour.
/// </summary>
public static class QuestDevices
{
    public static readonly IReadOnlyList<QuestModelInfo> All = new[]
    {
        new QuestModelInfo(QuestModel.Quest1, "Meta Quest (1st gen)", new[] { "monterey" }, new[] { "Quest" }, new[] { "quest" },
            Supported: false, Note: "Quest 1 is not supported by Unity 6 / current Meta XR runtimes."),
        new QuestModelInfo(QuestModel.Quest2, "Meta Quest 2", new[] { "hollywood" }, new[] { "Quest 2" }, new[] { "quest2" },
            Supported: true),
        new QuestModelInfo(QuestModel.QuestPro, "Meta Quest Pro", new[] { "seacliff" }, new[] { "Quest Pro" }, new[] { "questpro" },
            Supported: true),
        new QuestModelInfo(QuestModel.Quest3, "Meta Quest 3", new[] { "eureka" }, new[] { "Quest 3" }, new[] { "quest3", "eureka" },
            Supported: true),
        new QuestModelInfo(QuestModel.Quest3S, "Meta Quest 3S", new[] { "panther" }, new[] { "Quest 3S", "Quest 3S Xbox Edition" },
            new[] { "quest3s", "panther" }, Supported: true),
    };

    public static readonly QuestModelInfo Unknown = new(QuestModel.Unknown, "Unknown Meta headset", Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<string>(), Supported: true, Note: "Unrecognised Quest model; treated as a future Quest (arm64).");

    public static readonly QuestModelInfo NotQuest = new(QuestModel.NotQuest, "Not a Meta Quest", Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<string>(), Supported: false, Note: "NightClub Simulator only runs on Meta Quest headsets.");

    /// <summary>Identifies a headset from ro.product.model, ro.product.device (codename) and manufacturer.</summary>
    public static QuestModelInfo Identify(string? model, string? codename, string? manufacturer = null)
    {
        var m = Normalize(model);
        var c = (codename ?? "").Trim().ToLowerInvariant();

        // Codename is the most reliable signal; exact model string next ("Quest 3" vs "Quest 3S").
        var byCodename = All.FirstOrDefault(i => i.Codenames.Contains(c));
        if (byCodename != null) return byCodename;
        var byModel = All.FirstOrDefault(i => i.ModelStrings.Any(s => Normalize(s).Equals(m, StringComparison.OrdinalIgnoreCase)));
        if (byModel != null) return byModel;

        var oculus = (manufacturer ?? "").Contains("Oculus", StringComparison.OrdinalIgnoreCase) ||
                     (manufacturer ?? "").Contains("Meta", StringComparison.OrdinalIgnoreCase) ||
                     m.Contains("Quest", StringComparison.OrdinalIgnoreCase);
        if (oculus) return Unknown with { DisplayName = string.IsNullOrEmpty(m) ? Unknown.DisplayName : "Meta " + m };
        return string.IsNullOrEmpty(m) && string.IsNullOrEmpty(c) ? Unknown : NotQuest;
    }

    private static string Normalize(string? s) => (s ?? "").Replace('_', ' ').Trim();

    /// <summary>
    /// Checks a headset against the game. <paramref name="manifestSupportedDevices"/> is the value of
    /// the com.oculus.supportedDevices meta-data (e.g. "quest2|questpro|quest3|quest3s"), if present.
    /// </summary>
    public static DeviceCompatibility Check(QuestModelInfo device, string? manifestSupportedDevices)
    {
        var warnings = new List<string>();
        if (device.Note != null) warnings.Add(device.Note);
        if (!device.Supported) return new DeviceCompatibility(false, false, warnings);

        var listed = true;
        if (!string.IsNullOrWhiteSpace(manifestSupportedDevices) && device.IsQuest)
        {
            var tokens = manifestSupportedDevices.Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant()).ToHashSet();
            listed = device.SupportedDevicesTokens.Any(tokens.Contains);
            if (!listed)
                warnings.Add($"The game manifest (com.oculus.supportedDevices = \"{manifestSupportedDevices}\") does not list {device.DisplayName}. " +
                             "A sideloaded install usually still works, but the developer has not declared support for this headset.");
        }
        return new DeviceCompatibility(true, listed, warnings);
    }
}
