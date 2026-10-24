using System.Text.Json;
using Aion2Builder.Combat;
using Aion2Builder.GameData;

namespace Aion2Builder.Storage;

/// <summary>Combats importés, par personnage : %APPDATA%\Aion2Builder\combat\&lt;perso&gt;\&lt;date&gt;.json, le plus récent d'abord.</summary>
public sealed class CombatStore
{
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

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
