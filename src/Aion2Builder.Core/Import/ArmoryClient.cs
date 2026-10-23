using System.Net;
using System.Text.Json;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Import;

public class ArmoryException(string message) : Exception(message);

public sealed record ServerInfo(int Id, string Name);

public sealed record SearchHit(string CharacterId, string Name, int Level, int ServerId, string ServerName, string? ClassCode, string Race);

/// <summary>
/// Lecture des fiches personnage publiques du site officiel AION 2 Global (aion2.plaync.com).
/// Ce sont les adresses qu'utilise la page "Character Info" du site : non documentées, elles peuvent
/// changer sans préavis. Pour diagnostiquer, les réponses brutes peuvent être sauvegardées (<see cref="RawCharacter.SaveTo"/>).
/// </summary>
public sealed class ArmoryClient(HttpClient http) : IDisposable
{
    public static readonly string[] Regions = ["eu", "naw", "nae", "la", "as"];
    const string Lang = "fr-FR";
    const string Site = "https://aion2.plaync.com";

    public static ArmoryClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aion2Builder/0.1 (+https://github.com/Tomtoxi44/aion2-builder)");
        return new ArmoryClient(client);
    }

    public void Dispose() => http.Dispose();

    public async Task<List<ServerInfo>> Servers(string region, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Get($"{Site}/en-us/api/gameinfo/servers?lang=en-US&region={Region(region)}", ct));
        return doc.RootElement.GetProperty("serverList").EnumerateArray()
            .Select(s => new ServerInfo(s.GetProperty("serverId").GetInt32(), s.GetProperty("serverName").GetString() ?? ""))
            .OrderBy(s => s.Name)
            .ToList();
    }

    /// <summary>Recherche par nom (serveur facultatif). Les correspondances exactes du nom viennent en premier.</summary>
    public async Task<List<SearchHit>> Search(string region, string name, string? serverName = null, CancellationToken ct = default)
    {
        int? serverId = null;
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            var servers = await Servers(region, ct);
            serverId = servers.FirstOrDefault(s => s.Name.Equals(serverName.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                       ?? throw new ArmoryException($"Serveur « {serverName} » inconnu en région {region}. Serveurs : {string.Join(", ", servers.Select(s => s.Name))}");
        }
        var url = $"https://api-search.plaync.com/aion2global/search/v2/character?keyword={Uri.EscapeDataString(name.Trim())}" +
                  $"&serverId={serverId}&page=1&size=20&region={Region(region)}&localeInfo=en-US";
        using var doc = JsonDocument.Parse(await Get(url, ct));
        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Select(c => new SearchHit(
                c.GetProperty("characterId").GetString()!,
                ArmoryMapper.StripTags(c.GetProperty("name").GetString()),
                c.GetProperty("level").GetInt32(),
                c.GetProperty("serverId").GetInt32(),
                c.GetProperty("serverName").GetString() ?? "",
                ArmoryMapper.ClassFromPcId(c.GetProperty("pcId").GetInt32()),
                c.GetProperty("race").GetInt32() == 1 ? "Élyséen" : "Asmodien"))
            .OrderByDescending(h => h.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => h.Level)
            .ToList();
    }

    /// <summary>Lit tout le build : profil, équipement, fiche de chaque pièce et plateaux Daevanion.</summary>
    public async Task<RawCharacter> FetchRaw(string region, int serverId, string characterId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        region = Region(region);
        // L'identifiant est renvoyé déjà encodé par la recherche ("...%3D") : on le normalise une fois.
        var id = Uri.EscapeDataString(Uri.UnescapeDataString(characterId));
        var q = $"region={region}&lang={Lang}&characterId={id}&serverId={serverId}";

        progress?.Report("Profil et équipement…");
        var infoTask = Get($"{Site}/api/character/info?{q}", ct);
        var equipmentTask = Get($"{Site}/api/character/equipment?{q}", ct);
        var info = await infoTask;
        var equipment = await equipmentTask;

        using var eq = JsonDocument.Parse(equipment);
        using var inf = JsonDocument.Parse(info);
        var pieces = eq.RootElement.TryGetProperty("equipment", out var e) && e.TryGetProperty("equipmentList", out var l)
            ? l.EnumerateArray().Select(i => (Slot: i.GetProperty("slotPos").GetInt32(), Id: i.GetProperty("id").GetInt64(),
                Enchant: i.TryGetProperty("enchantLevel", out var en) ? en.GetInt32() : 0)).ToList()
            : [];
        var boardIds = inf.RootElement.TryGetProperty("daevanion", out var d) && d.TryGetProperty("boardList", out var bl)
            ? bl.EnumerateArray().Where(b => b.TryGetProperty("open", out var o) && o.GetInt32() == 1).Select(b => b.GetProperty("id").GetInt32()).ToList()
            : [];

        // Quelques appels en parallèle seulement : on reste raisonnable avec le site officiel.
        using var gate = new SemaphoreSlim(4);
        var done = 0;
        var total = pieces.Count + boardIds.Count;
        async Task<string?> Limited(string url)
        {
            await gate.WaitAsync(ct);
            try { return await Get(url, ct, allowNotFound: true); }
            finally
            {
                gate.Release();
                progress?.Report($"Détails {Interlocked.Increment(ref done)}/{total}…");
            }
        }

        var itemTasks = pieces.ToDictionary(p => p.Slot, p =>
            Limited($"{Site}/api/character/equipment/item?id={p.Id}&enchantLevel={p.Enchant}&characterId={id}&serverId={serverId}&slotPos={p.Slot}&region={region}&lang={Lang}"));
        var boardTasks = boardIds.ToDictionary(b => b, b => Limited($"{Site}/api/character/daevanion/detail?{q}&boardId={b}"));
        await Task.WhenAll(itemTasks.Values.Concat(boardTasks.Values));

        return new RawCharacter(region, info, equipment,
            itemTasks.Where(t => t.Value.Result is not null).ToDictionary(t => t.Key, t => t.Value.Result!),
            boardTasks.Where(t => t.Value.Result is not null).ToDictionary(t => t.Key, t => t.Value.Result!),
            DateTime.UtcNow);
    }

    public async Task<CharacterSnapshot> Fetch(GameDatabase data, string region, int serverId, string characterId,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        ArmoryMapper.Map(await FetchRaw(region, serverId, characterId, progress, ct), data);

    /// <summary>Recherche + import du premier résultat exact (nom et serveur).</summary>
    public async Task<(CharacterSnapshot Snapshot, RawCharacter Raw)> FetchByName(GameDatabase data, string region, string name,
        string? serverName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Recherche du personnage…");
        var hits = await Search(region, name, serverName, ct);
        var hit = hits.FirstOrDefault(h => h.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? hits.FirstOrDefault()
                  ?? throw new ArmoryException($"Aucun personnage « {name} » trouvé en région {region}{(serverName is null ? "" : $" sur {serverName}")}.");
        var raw = await FetchRaw(region, hit.ServerId, hit.CharacterId, progress, ct);
        return (ArmoryMapper.Map(raw, data), raw);
    }

    static string Region(string region)
    {
        var r = region.Trim().ToLowerInvariant();
        return Regions.Contains(r) ? r : throw new ArmoryException($"Région inconnue « {region} » (attendu : {string.Join(", ", Regions)}).");
    }

    async Task<string> Get(string url, CancellationToken ct) => (await Get(url, ct, allowNotFound: false))!;

    async Task<string?> Get(string url, CancellationToken ct, bool allowNotFound)
    {
        try
        {
            using var res = await http.GetAsync(url, ct);
            if (res.StatusCode == HttpStatusCode.NotFound && allowNotFound) return null;
            if (res.StatusCode == HttpStatusCode.NotFound) throw new ArmoryException("Le site officiel ne trouve pas ce personnage (404).");
            if (!res.IsSuccessStatusCode) throw new ArmoryException($"Le site officiel a répondu {(int)res.StatusCode}.");
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch (ArmoryException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new ArmoryException($"Le site officiel AION 2 ne répond pas (maintenance ?) : {ex.Message}");
        }
    }
}
