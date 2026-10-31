using System.Text.Json;
using Aion2Builder.GameData;

namespace Aion2Builder.Storage;

/// <summary>Un joueur de ta liste : qui il est (pour l'actualiser) et ce que le Builder en a mesuré.</summary>
public sealed record SavedPeer(string Name, string ServerName, string Region, int ServerId, string CharacterId, string ClassCode,
    DateTime FetchedAtUtc, BenchPlayer Player);

/// <summary>
/// Ta liste de joueurs de comparaison, dans %APPDATA%\Aion2Builder\comparison.json : sur ton PC seulement, jamais publiée.
/// Chaque joueur sert à comparer les persos de sa classe quand tu choisis « ma liste ».
/// </summary>
public sealed class PeerStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.Root, "comparison.json");

    public List<SavedPeer> Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<List<SavedPeer>>(File.ReadAllText(Path), GameDataStore.Json) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    void Save(List<SavedPeer> peers)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(peers, GameDataStore.Json));
    }

    /// <summary>Ajoute (ou remplace, s'il y est déjà) un joueur.</summary>
    public void Put(SavedPeer peer)
    {
        var peers = Load().Where(p => p.CharacterId != peer.CharacterId).Append(peer).OrderBy(p => p.ClassCode).ThenBy(p => p.Name).ToList();
        Save(peers);
    }

    public void Remove(string characterId) => Save(Load().Where(p => p.CharacterId != characterId).ToList());

    /// <summary>Les joueurs d'une classe, prêts à comparer (avec leur nom).</summary>
    public List<BenchPlayer> For(string classCode) => Load().Where(p => p.ClassCode == classCode).Select(p => p.Player with { Name = p.Name }).ToList();
}
