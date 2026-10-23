using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aion2Builder.Model;

namespace Aion2Builder.Import;

public sealed class PetSyncException(string message) : ArmoryException(message);

/// <summary>
/// Lit la collection de familiers sur Aion 2 Todo, où le Pet Meter (lecture du trafic du jeu) l'envoie avec une clé
/// personnelle. Le catalogue (espèce de chaque familier) est public ; les âmes demandent la clé
/// (Aion 2 Todo, onglet Familiers → Pet Meter → Créer ma clé).
/// </summary>
public sealed class PetSyncClient(HttpClient http) : IDisposable
{
    public static string Site { get; set; } =
        Environment.GetEnvironmentVariable("AION2_TODO_URL") ?? "https://aion2-todo-7275.azurewebsites.net";

    /// <summary>Délai large : le site (offre gratuite) se met en veille et met jusqu'à une minute à se réveiller.</summary>
    public static PetSyncClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aion2Builder/0.1 (+https://github.com/Tomtoxi44/aion2-builder)");
        return new PetSyncClient(client);
    }

    public void Dispose() => http.Dispose();

    public async Task<PetCollection> Fetch(string key, CancellationToken ct = default)
    {
        var catalogTask = Get("/api/content/pets", null, ct);
        var soulsTask = Get("/api/sync/pets", key.Trim(), ct);
        using var catalog = JsonDocument.Parse(await catalogTask);
        using var souls = JsonDocument.Parse(await soulsTask);

        var pets = catalog.RootElement.GetProperty("pets").EnumerateArray()
            .Select(p => new PetInfo(p.GetProperty("id").GetInt32(), p.GetProperty("name").GetString() ?? "",
                p.TryGetProperty("genus", out var g) ? g.GetString() ?? "" : ""))
            .ToList();
        var root = souls.RootElement;
        var tiers = root.TryGetProperty("tiers", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetInt32()).ToList()
            : catalog.RootElement.TryGetProperty("tiers", out var ct2) ? ct2.EnumerateArray().Select(x => x.GetInt32()).ToList() : [5, 30, 105];
        var map = root.TryGetProperty("souls", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().Where(x => int.TryParse(x.Name, out _)).ToDictionary(x => int.Parse(x.Name), x => x.Value.GetInt32())
            : [];
        return new PetCollection { Souls = map, Tiers = tiers, Catalog = pets, FetchedAtUtc = DateTime.UtcNow };
    }

    async Task<string> Get(string path, string? key, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Site + path);
            if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new PetSyncException("Clé refusée par Aion 2 Todo : elle a été remplacée ou désactivée. Crée une nouvelle clé (onglet Familiers → Pet Meter) et recolle-la.");
            if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new PetSyncException("Aion 2 Todo limite les demandes : réessaie dans une minute.");
            if (!res.IsSuccessStatusCode) throw new PetSyncException($"Aion 2 Todo a répondu {(int)res.StatusCode}.");
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch (PetSyncException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new PetSyncException($"Aion 2 Todo ne répond pas : {ex.Message}");
        }
    }
}
