using System.Collections.Concurrent;
using System.Text.Json;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Import;

/// <summary>
/// Stats principales d'une pièce à chaque niveau d'enchantement, lues sur le site officiel (sa fiche objet se calcule pour
/// n'importe quel niveau) : seul le bonus d'enchantement change d'un niveau à l'autre. Une pièce donnée a toujours les mêmes
/// chiffres : gardés sur ce PC (%APPDATA%\Aion2Builder\cache\enchant\&lt;numéro&gt;.json) pour ne pas redemander au site.
/// Aucun coût : le site ne les donne pas.
/// </summary>
public static class EnchantTables
{
    static string Dir => Path.Combine(AppPaths.Root, "cache", "enchant");
    static readonly ConcurrentDictionary<long, Dictionary<int, string>> Memory = new();

    /// <summary>
    /// Les stats principales de la pièce pour chaque niveau de <paramref name="from"/> à <paramref name="to"/> (ceux que le site
    /// donne : il s'arrête au maximum de la pièce). Lance <see cref="ArmoryException"/> si le site ne répond pas.
    /// </summary>
    public static async Task<Dictionary<int, List<Effect>>> Load(GameDatabase db, CharacterSnapshot c, EquippedItem item, int from, int to, CancellationToken ct = default)
    {
        var known = Memory.GetOrAdd(item.Id, ReadDisk);
        List<int> missing;
        lock (known) missing = Enumerable.Range(from, Math.Max(0, to - from + 1)).Where(l => !known.ContainsKey(l)).ToList();
        if (missing.Count > 0)
        {
            using var client = ArmoryClient.Create();
            // Quelques appels en parallèle seulement : on reste raisonnable avec le site officiel.
            using var gate = new SemaphoreSlim(4);
            var answers = await Task.WhenAll(missing.Select(async level =>
            {
                await gate.WaitAsync(ct);
                try { return (Level: level, Json: await client.ItemDetail(c.Region, c.ServerId, c.CharacterId, item.Id, item.SlotPos, level, ct)); }
                finally { gate.Release(); }
            }));
            lock (known)
            {
                foreach (var (level, json) in answers)
                    // Au-delà du maximum, le site renvoie la pièce au maximum : ce niveau-là n'existe pas.
                    if (json is not null && ArmoryMapper.EnchantedMainStats(json) is { } read && read.Level == level)
                        known[level] = read.MainStats;
                WriteDisk(item.Id, known);
            }
        }
        var parser = new EffectParser(db);
        lock (known)
            return Enumerable.Range(from, Math.Max(0, to - from + 1)).Where(known.ContainsKey)
                .ToDictionary(l => l, l => ArmoryMapper.MainStatsFrom(known[l], parser));
    }

    static Dictionary<int, string> ReadDisk(long id)
    {
        try
        {
            var path = Path.Combine(Dir, $"{id}.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    static void WriteDisk(long id, Dictionary<int, string> known)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, $"{id}.json"), JsonSerializer.Serialize(known));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* cache non vital */ }
    }
}
