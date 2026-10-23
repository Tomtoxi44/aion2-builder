using System.Text.Json;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Storage;

public sealed record SavedCharacter(string Key, string Name, string ServerName, string Region, string ClassCode, int Level, int ItemLevel, DateTime FetchedAtUtc, string Path);

/// <summary>Personnages importés, un fichier JSON chacun, pour travailler hors ligne et comparer dans le temps.</summary>
public sealed class CharacterStore(string? dir = null)
{
    public string Directory { get; } = dir ?? AppPaths.CharactersDir;

    public string Save(CharacterSnapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"{Sanitize(snapshot.FileKey)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, GameDataStore.Json));
        return path;
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

    static string Sanitize(string key) => string.Concat(key.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
