using System.Text.Json;
using System.Text.Json.Nodes;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

/// <summary>
/// Tools/tools.json: versions of the tools installed by the kit, e.g.
/// { "adb": "35.0.2", "buildTools": "35.0.0", "jdk": "17.0.13", "ndk": "27.2.12479018",
///   "cmake": "3.31.7", "ninja": "1.12.1", "details": { "ndk": { "url": ..., "sha256": ... } } }
/// </summary>
public sealed class ToolsManifest
{
    private readonly string _path;
    private readonly JsonObject _root;

    private ToolsManifest(string path, JsonObject root)
    {
        _path = path;
        _root = root;
    }

    public static ToolsManifest Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return new ToolsManifest(path, o);
        }
        catch
        {
            // a corrupt manifest only means versions are re-detected
        }
        return new ToolsManifest(path, new JsonObject());
    }

    public string? Version(string key) => _root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public string? Detail(string key, string field) =>
        _root["details"]?[key]?[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public void Set(string key, string version, IDictionary<string, string?>? details = null)
    {
        _root[key] = version;
        if (details != null)
        {
            var all = _root["details"] as JsonObject ?? new JsonObject();
            var d = new JsonObject();
            foreach (var kv in details.Where(kv => kv.Value != null)) d[kv.Key] = kv.Value;
            d["installedAt"] = DateTimeOffset.Now.ToString("O");
            all[key] = d;
            _root["details"] = all;
        }
        Save();
    }

    public void Remove(string key)
    {
        _root.Remove(key);
        (_root["details"] as JsonObject)?.Remove(key);
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
