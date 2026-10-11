using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightClubPatcher.Core.Mods;

/// <summary>One Mods/&lt;Name&gt;/ folder, read with the same rules as Native/CMakeLists.txt.</summary>
public sealed record ModFolder(string Name, string Path, string Library, bool Disabled, int SourceCount, string? Error)
{
    /// <summary>Built by "Build Native Loader".</summary>
    public bool WillBuild => !Disabled && Error == null && SourceCount > 0;

    public string Describe() =>
        Error != null ? $"Mods/{Name}: ERROR {Error}"
        : Disabled ? $"Mods/{Name}: disabled (.disabled file)"
        : SourceCount == 0 ? $"Mods/{Name}: no .cpp file - skipped"
        : $"Mods/{Name} -> {Library} ({SourceCount} source file(s))";
}

/// <summary>
/// A mod is one folder Mods/&lt;Name&gt;/ with a mod.json and its .cpp files. Output name: the mod.json
/// "library" field, else libncmkmod_&lt;folder in lower case&gt;.so. A ".disabled" file skips it.
/// Mirrors Native/CMakeLists.txt so the app can report problems before CMake runs.
/// </summary>
public static partial class ModCatalog
{
    [GeneratedRegex("^libncmkmod_[A-Za-z0-9_]+\\.so$")]
    private static partial Regex LibraryName();

    public static List<ModFolder> Discover(string modsRoot)
    {
        var result = new List<ModFolder>();
        if (!Directory.Exists(modsRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(modsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifest = System.IO.Path.Combine(dir, "mod.json");
            if (!File.Exists(manifest)) continue;
            var name = System.IO.Path.GetFileName(dir);
            var library = DefaultLibrary(name);
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) error = "mod.json is not a JSON object";
                else if (doc.RootElement.TryGetProperty("library", out var lib) && lib.ValueKind == JsonValueKind.String)
                    library = lib.GetString()!;
            }
            catch (JsonException ex) { error = "mod.json is not valid JSON: " + ex.Message; }
            if (error == null && !LibraryName().IsMatch(library))
                error = $"\"library\" is \"{library}\"; it must look like libncmkmod_<name>.so";
            var sources = Directory.EnumerateFiles(dir, "*.cpp", SearchOption.AllDirectories).Count();
            result.Add(new ModFolder(name, dir, library, File.Exists(System.IO.Path.Combine(dir, ".disabled")), sources, error));
        }

        foreach (var group in result.Where(m => m.WillBuild).GroupBy(m => m.Library).Where(g => g.Count() > 1))
            foreach (var m in group.ToList())
            {
                var others = string.Join(", ", group.Where(o => o != m).Select(o => "Mods/" + o.Name));
                result[result.IndexOf(m)] = m with { Error = $"same output {m.Library} as {others}" };
            }
        return result;
    }

    public static string DefaultLibrary(string folderName) =>
        "libncmkmod_" + Regex.Replace(folderName.ToLowerInvariant(), "[^a-z0-9_]", "_") + ".so";
}
