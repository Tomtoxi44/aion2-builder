using System.Text.Json;
using Aion2Builder.Combat;
using Aion2Builder.GameData;

namespace Aion2Builder.Storage;

/// <summary>Combats importés, par personnage : %APPDATA%\Aion2Builder\combat\&lt;perso&gt;\&lt;date&gt;.json, le plus récent d'abord.</summary>
public sealed class CombatStore
{
    /// <summary>Combats gardés par personnage.</summary>
    public const int Keep = 20;

    static string Dir(string characterKey) => Path.Combine(AppPaths.Root, "combat", characterKey);

    public string Save(string characterKey, CombatLog log)
    {
        Directory.CreateDirectory(Dir(characterKey));
        var path = Path.Combine(Dir(characterKey), $"{log.ImportedAtUtc:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(log, GameDataStore.Json));
        return path;
    }

    public List<(string Path, CombatLog Log)> List(string characterKey)
    {
        if (!Directory.Exists(Dir(characterKey))) return [];
        var list = new List<(string, CombatLog)>();
        foreach (var path in Directory.GetFiles(Dir(characterKey), "*.json").OrderDescending())
        {
            try
            {
                if (JsonSerializer.Deserialize<CombatLog>(File.ReadAllText(path), GameDataStore.Json) is { } log) list.Add((path, log));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }

    public CombatLog? Latest(string characterKey) => List(characterKey).Select(x => x.Log).FirstOrDefault();

    /// <summary>Remplace un combat gardé (reconversion), au même endroit.</summary>
    public void Overwrite(string path, CombatLog log) => File.WriteAllText(path, JsonSerializer.Serialize(log, GameDataStore.Json));

    /// <summary>Ne garde que les <paramref name="keep"/> combats les plus récents.</summary>
    public void Prune(string characterKey, int keep = Keep)
    {
        if (!Directory.Exists(Dir(characterKey))) return;
        foreach (var path in Directory.GetFiles(Dir(characterKey), "*.json").OrderDescending().Skip(keep)) Delete(path);
    }

    static string ImportedPath(string characterKey) => Path.Combine(Dir(characterKey), "imported.txt");

    /// <summary>Fichiers du Pet Meter déjà repris (même ceux supprimés depuis) : un combat n'est jamais importé deux fois.</summary>
    public HashSet<string> Imported(string characterKey) =>
        File.Exists(ImportedPath(characterKey)) ? File.ReadAllLines(ImportedPath(characterKey)).Where(l => l.Length > 0).ToHashSet() : [];

    public void MarkImported(string characterKey, IEnumerable<string> files)
    {
        Directory.CreateDirectory(Dir(characterKey));
        File.AppendAllLines(ImportedPath(characterKey), files);
    }

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
