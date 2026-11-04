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

    /// <summary>
    /// Combats lus, par dossier : relus seulement quand un fichier a été ajouté, retiré ou réécrit (l'appli les demande à
    /// chaque recalcul, plusieurs fois).
    /// </summary>
    static readonly Dictionary<string, (string Stamp, List<(string, CombatLog)> List)> Read = new();

    /// <summary>Empreinte d'un dossier : noms, tailles et dates des fichiers (sans les lire).</summary>
    public static string Stamp(IEnumerable<FileInfo> files) =>
        string.Join("|", files.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

    public List<(string Path, CombatLog Log)> List(string characterKey)
    {
        if (!Directory.Exists(Dir(characterKey))) return [];
        var files = new DirectoryInfo(Dir(characterKey)).GetFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        var stamp = Stamp(files);
        lock (Read)
            if (Read.TryGetValue(characterKey, out var known) && known.Stamp == stamp) return [.. known.List];
        var list = new List<(string, CombatLog)>();
        foreach (var path in files.Select(f => f.FullName))
        {
            try
            {
                if (JsonSerializer.Deserialize<CombatLog>(File.ReadAllText(path), GameDataStore.Json) is { } log)
                    list.Add((path, log with { Target = Combat.PetMeterFights.Label(log.Target) }));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        lock (Read) Read[characterKey] = (stamp, list);
        return [.. list];
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
