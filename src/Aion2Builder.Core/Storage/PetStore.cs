using System.Text.Json;
using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;

namespace Aion2Builder.Storage;

/// <summary>
/// Clé Aion 2 Todo et dernière collection de familiers lue, dans %APPDATA%\Aion2Builder\pets.json.
/// La clé y est en clair, comme dans le fichier d'état du Pet Meter : elle ne donne accès qu'à la synchro des familiers.
/// </summary>
public sealed class PetStore(string? path = null)
{
    public sealed record Saved(string? Key, PetCollection? Collection)
    {
        /// <summary>Fin de la clé, pour la reconnaître sans l'afficher.</summary>
        public string? KeyHint => Key is { Length: > 4 } k ? $"…{k[^4..]}" : null;
    }

    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.Root, "pets.json");

    public Saved Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(Path), GameDataStore.Json) ?? new(null, null) : new(null, null); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(null, null); }
    }

    void Save(Saved saved)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(saved, GameDataStore.Json));
    }

    /// <summary>Relit la collection sur Aion 2 Todo (avec la clé donnée, sinon celle enregistrée) et la garde.</summary>
    public async Task<PetCollection> Refresh(string? key = null, CancellationToken ct = default)
    {
        key = string.IsNullOrWhiteSpace(key) ? Load().Key : key.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new PetSyncException("Aucune clé Aion 2 Todo : crée-la dans Aion 2 Todo (onglet Familiers → Pet Meter) et colle-la ici.");
        using var client = PetSyncClient.Create();
        var collection = await client.Fetch(key, ct);
        Save(new Saved(key, collection));
        return collection;
    }

    /// <summary>
    /// Clé déjà collée dans le Pet Meter sur ce PC : son fichier d'état est dans %LOCALAPPDATA%\AION2-pet-meter, ou à côté
    /// de son exe quand il peut y écrire (le Pet Meter v0.20.3 et plus note ce dossier dans data_dir.txt).
    /// </summary>
    public static string? PetMeterKey()
    {
        var local = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2-pet-meter");
        var file = System.IO.Path.Combine(local, "aion2_pet_meter_state.json");
        try
        {
            if (!File.Exists(file) && File.Exists(System.IO.Path.Combine(local, "data_dir.txt"))
                && File.ReadAllText(System.IO.Path.Combine(local, "data_dir.txt")).Trim() is { Length: > 0 } dir)
                file = System.IO.Path.Combine(dir, "aion2_pet_meter_state.json");
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            // L'état du Pet Meter peut être gros et il le réécrit pendant le jeu : relu seulement quand il a changé (l'appli
            // demande la clé à chaque affichage).
            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (meterKeyCache is { } c && c.Stamp == stamp) return c.Key;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var found = doc.RootElement.TryGetProperty("sync_key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 } key ? key : null;
            meterKeyCache = (stamp, found);
            return found;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    static ((DateTime, long) Stamp, string? Key)? meterKeyCache;

    /// <summary>Oublie la clé et la collection.</summary>
    public void Forget()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
