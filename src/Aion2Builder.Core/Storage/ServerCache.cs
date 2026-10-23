using System.Text.Json;
using Aion2Builder.GameData;
using Aion2Builder.Import;

namespace Aion2Builder.Storage;

/// <summary>
/// Liste des serveurs par région, gardée sur disque : elle s'affiche tout de suite (et hors ligne)
/// et n'est relue sur le site officiel que lorsqu'elle a plus d'une semaine.
/// </summary>
public sealed class ServerCache(string? dir = null)
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public string Directory { get; } = dir ?? Path.Combine(AppPaths.Root, "servers");

    string PathOf(string region) => Path.Combine(Directory, $"{region.Trim().ToLowerInvariant()}.json");

    /// <summary>Liste en cache, même ancienne ; vide si jamais lue.</summary>
    public List<ServerInfo> Load(string region)
    {
        var path = PathOf(region);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<ServerInfo>>(File.ReadAllText(path), GameDataStore.Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public bool IsStale(string region) =>
        !File.Exists(PathOf(region)) || DateTime.UtcNow - File.GetLastWriteTimeUtc(PathOf(region)) > MaxAge;

    /// <summary>Liste à jour : cache si récent, sinon site officiel (et retour au cache si le site ne répond pas).</summary>
    public async Task<List<ServerInfo>> Get(ArmoryClient client, string region, bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && !IsStale(region) && Load(region) is { Count: > 0 } cached) return cached;
        try
        {
            var servers = await client.Servers(region, ct);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(PathOf(region), JsonSerializer.Serialize(servers, GameDataStore.Json));
            return servers;
        }
        catch (ArmoryException) when (Load(region) is { Count: > 0 } fallback)
        {
            return fallback;
        }
    }
}
