using System.Text.Json;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Storage;

public sealed record SavedCharacter(string Key, string Name, string ServerName, string Region, string ClassCode, int Level, int ItemLevel, DateTime FetchedAtUtc, string Path);

/// <summary>Un import, pour suivre la progression : niveau, niveau d'objet, puissance de combat.</summary>
public sealed record ProgressPoint(DateTime FetchedAtUtc, int Level, int ItemLevel, int CombatPower);

/// <summary>Personnages importés, un fichier JSON chacun, pour travailler hors ligne et comparer dans le temps.</summary>
public sealed class CharacterStore(string? dir = null)
{
    public string Directory { get; } = dir ?? AppPaths.CharactersDir;

    public string Save(CharacterSnapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"{Sanitize(snapshot.FileKey)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, GameDataStore.Json));
        AddProgress(snapshot);
        return path;
    }

    string ProgressPath(CharacterSnapshot c) => Path.Combine(Directory, "history", $"{Sanitize(c.FileKey)}.progress.json");

    /// <summary>Imports successifs du personnage, du plus ancien au plus récent (un point par import qui change quelque chose).</summary>
    public List<ProgressPoint> Progress(CharacterSnapshot c)
    {
        var path = ProgressPath(c);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<ProgressPoint>>(File.ReadAllText(path), GameDataStore.Json) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    void AddProgress(CharacterSnapshot c)
    {
        var points = Progress(c);
        var point = new ProgressPoint(c.FetchedAtUtc, c.Level, c.ItemLevel, c.CombatPower);
        if (points.Count > 0 && points[^1] with { FetchedAtUtc = point.FetchedAtUtc } == point) return; // rien de neuf
        points.Add(point);
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath(c))!);
            File.WriteAllText(ProgressPath(c), JsonSerializer.Serialize(points.TakeLast(200).ToList(), GameDataStore.Json));
        }
        catch (IOException) { /* historique non vital */ }
    }

    public CharacterSnapshot Load(string path) =>
        JsonSerializer.Deserialize<CharacterSnapshot>(File.ReadAllText(path), GameDataStore.Json)
        ?? throw new InvalidDataException($"{path} est vide.");

    public List<SavedCharacter> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<SavedCharacter>();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                var c = Load(path);
                list.Add(new SavedCharacter(Path.GetFileNameWithoutExtension(path), c.Name, c.ServerName, c.Region, c.ClassCode,
                    c.Level, c.ItemLevel, c.FetchedAtUtc, path));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                // Fichier abîmé ou d'un ancien format : ignoré.
            }
        }
        return list.OrderByDescending(c => c.FetchedAtUtc).ToList();
    }

    /// <summary>Retrouve un personnage enregistré par nom (et serveur si précisé).</summary>
    public SavedCharacter? Find(string name, string? server = null) =>
        List().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                   && (server is null || c.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase)));

    string InputsPath(CharacterSnapshot c) => Path.Combine(Directory, "inputs", $"{Sanitize(c.FileKey)}.json");

    /// <summary>Points non dépensés déclarés pour ce personnage (zéro si rien n'a été saisi).</summary>
    public PlayerInputs LoadInputs(CharacterSnapshot c)
    {
        var path = InputsPath(c);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PlayerInputs>(File.ReadAllText(path), GameDataStore.Json) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }

    public void SaveInputs(CharacterSnapshot c, PlayerInputs inputs)
    {
        var path = InputsPath(c);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(inputs, GameDataStore.Json));
    }

    // Historique de consultation : dernière ouverture de chaque personnage (clé = nom du fichier).
    string ViewsPath => Path.Combine(Directory, "history", "views.json");

    Dictionary<string, DateTime> LoadViews()
    {
        try { return File.Exists(ViewsPath) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(ViewsPath)) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    public void MarkViewed(string path)
    {
        var views = LoadViews();
        views[Path.GetFileName(path)] = DateTime.UtcNow;
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ViewsPath)!);
            File.WriteAllText(ViewsPath, JsonSerializer.Serialize(views));
        }
        catch (IOException) { /* historique non vital */ }
    }

    /// <summary>Personnages consultés, du plus récent au plus ancien ; un personnage jamais rouvert date de son import.</summary>
    public List<(SavedCharacter Character, DateTime ViewedAtUtc)> History()
    {
        var views = LoadViews();
        return List()
            .Select(c => (c, views.TryGetValue(Path.GetFileName(c.Path), out var at) ? at : c.FetchedAtUtc))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    static string Sanitize(string key) => string.Concat(key.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
