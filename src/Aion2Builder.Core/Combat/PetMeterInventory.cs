using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aion2Builder.GameData;

namespace Aion2Builder.Combat;

/// <summary>Une pile d'objets : numéro du jeu (les <c>id</c> du site officiel), quantité, conteneur (sac, coffre…) et emplacement.</summary>
public sealed record InventoryStack(long Item, long Qty, int Cont, int Slot);

/// <summary>L'inventaire d'un perso tel que le Pet Meter l'a noté (heure, version du Pet Meter, piles).</summary>
public sealed record CharacterInventory(string Character, DateTime At, string? Version, List<InventoryStack> Items);

/// <summary>Le fichier : la légende des conteneurs (« 1 » → « sac ») et l'inventaire de chaque perso.</summary>
public sealed record InventoryFile(Dictionary<int, string> Containers, Dictionary<string, CharacterInventory> Chars)
{
    public string ContainerName(int cont) => Containers.GetValueOrDefault(cont, $"conteneur {cont}");

    public CharacterInventory? For(string character) =>
        Chars.TryGetValue(character, out var c) ? c
            : Chars.FirstOrDefault(kv => string.Equals(kv.Key, character, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// Inventaire écrit par le Pet Meter (v0.21.0 et plus) dans %LOCALAPPDATA%\AION2-pet-meter\inventory.json : la liste complète
/// envoyée par le jeu à l'entrée en jeu, puis chaque changement, gardée par perso (hors combat, au plus toutes les 10 s). Lu
/// seulement ; null sans fichier.
/// </summary>
public static partial class PetMeterInventory
{
    public static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2-pet-meter", "inventory.json");

    static (DateTime Stamp, InventoryFile? Data)? cache;
    static readonly object Lock = new();

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetRegex();

    /// <summary>Le fichier, relu seulement quand il a changé ; null s'il manque ou s'il est illisible.</summary>
    public static InventoryFile? Load()
    {
        var stamp = System.IO.File.Exists(File) ? System.IO.File.GetLastWriteTimeUtc(File) : DateTime.MinValue;
        lock (Lock)
        {
            if (cache is { } c && c.Stamp == stamp) return c.Data;
            InventoryFile? data = null;
            try
            {
                if (stamp != DateTime.MinValue)
                {
                    using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
                    var root = doc.RootElement;
                    var conts = new Dictionary<int, string>();
                    if (root.TryGetProperty("conts", out var cs) && cs.ValueKind == JsonValueKind.Object)
                        foreach (var kv in cs.EnumerateObject())
                            if (int.TryParse(kv.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && kv.Value.ValueKind == JsonValueKind.String)
                                conts[id] = kv.Value.GetString()!;
                    var chars = new Dictionary<string, CharacterInventory>();
                    if (root.TryGetProperty("chars", out var ch) && ch.ValueKind == JsonValueKind.Object)
                        foreach (var kv in ch.EnumerateObject())
                        {
                            var items = new List<InventoryStack>();
                            if (kv.Value.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
                                foreach (var it in list.EnumerateArray())
                                    if (Long(it, "item") is { } item && Long(it, "qty") is { } qty)
                                        items.Add(new InventoryStack(item, qty, (int)(Long(it, "cont") ?? 0), (int)(Long(it, "slot") ?? 0)));
                            var at = kv.Value.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String
                                     && DateTimeOffset.TryParse(OffsetRegex().Replace(a.GetString()!, "$1:$2"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                                ? t.UtcDateTime : stamp;
                            var version = kv.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                            chars[kv.Name] = new CharacterInventory(kv.Name, at, version, items);
                        }
                    data = new InventoryFile(conts, chars);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                // fichier en cours d'écriture ou d'un autre format : rien de repris
            }
            cache = (stamp, data);
            return data;
        }
    }

    static long? Long(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}

/// <summary>
/// Les ressources de récolte (numéros 6106xxxxx) : noms (fr, en) et rang. Les noms viennent des résumés de récolte du Pet Meter
/// quand la ressource y figure, sinon de la même table que le Pet Meter (numéros d'aion2hub.com, noms du jeu de metabot.gg,
/// 30/10/2026). Le rang se lit sur le nom anglais : « Radiant » 4 (éclatant), « Pure » 3, « Fine » / « Fresh » 2, sinon 1.
/// </summary>
public static class GatherCatalog
{
    public static bool IsResource(long item) => item / 100_000 == 6106;

    static readonly Dictionary<long, (string Fr, string En)> Known = new()
    {
        [610610013] = ("Od", "Odyle"), [610610014] = ("Od d'une grande pureté", "Fine Odyle"),
        [610610015] = ("Od pur", "Pure Odyle"), [610610016] = ("Od éclatant", "Radiant Odyle"),
        [610620013] = ("Minerai d'orichalque", "Orichalcum Ore"), [610620014] = ("Minerai d'orichalque d'une grande pureté", "Fine Orichalcum Ore"),
        [610620015] = ("Minerai d'orichalque pur", "Pure Orichalcum Ore"), [610620016] = ("Minerai d'orichalque éclatant", "Radiant Orichalcum Ore"),
        [610650011] = ("Bois d'Yggdrasil", "Yggdrasil Log"), [610650012] = ("Bois d'Asvata", "Asvata Log"),
        [610650013] = ("Bois d'Yggdrasil d'une grande pureté", "Fine Yggdrasil Log"), [610650014] = ("Bois d'Asvata d'une grande pureté", "Fine Asvata Log"),
        [610650015] = ("Bois d'Yggdrasil pur", "Pure Yggdrasil Log"), [610650016] = ("Bois d'Asvata pur", "Pure Asvata Log"),
        [610650017] = ("Bois d'Yggdrasil éclatant", "Radiant Yggdrasil Log"), [610650018] = ("Bois d'Asvata éclatant", "Radiant Asvata Log"),
        [610660025] = ("Saphir brut", "Sapphire Gemstone"), [610660026] = ("Diamant brut", "Diamond Gemstone"), [610660027] = ("Rubis brut", "Ruby Gemstone"),
        [610660028] = ("Saphir brut d'une grande pureté", "Fine Sapphire Gemstone"), [610660029] = ("Diamant brut d'une grande pureté", "Fine Diamond Gemstone"),
        [610660030] = ("Rubis brut d'une grande pureté", "Fine Ruby Gemstone"),
        [610660031] = ("Saphir brut pur", "Pure Sapphire Gemstone"), [610660032] = ("Diamant brut pur", "Pure Diamond Gemstone"), [610660033] = ("Rubis brut pur", "Pure Ruby Gemstone"),
        [610660034] = ("Saphir brut éclatant", "Radiant Sapphire Gemstone"), [610660035] = ("Diamant brut éclatant", "Radiant Diamond Gemstone"),
        [610660036] = ("Rubis brut éclatant", "Radiant Ruby Gemstone"),
        [610670041] = ("Aria", "Aria"), [610670042] = ("Targena", "Targena"), [610670043] = ("Azpha", "Azpha"), [610670044] = ("Calendula", "Calendula"),
        [610670045] = ("Aria fraîche", "Fresh Aria"), [610670046] = ("Targena fraîche", "Fresh Targena"), [610670047] = ("Azpha fraîche", "Fresh Azpha"),
        [610670048] = ("Calendula fraîche", "Fresh Calendula"),
        [610680043] = ("Coriolus", "Coriolus"), [610680044] = ("Kukuru", "Kukuru"), [610680045] = ("Méla", "Mela"), [610680047] = ("Inina", "Inina"),
        [610680048] = ("Cypri", "Cypri"), [610680049] = ("Morfa", "Morfa"), [610680050] = ("Ohkra", "Ohkra"), [610680051] = ("Raydam", "Raydam"),
        [610680053] = ("Conide", "Conide"), [610680054] = ("Pujery", "Pujery"),
    };

    /// <summary>La ressource comme dans les résumés de récolte (noms des jours de récolte d'abord, sinon la table) ; quantité donnée.</summary>
    public static GatherItem Item(long id, long qty, IEnumerable<GatherDay> days)
    {
        var names = days.SelectMany(d => d.Servers?.Values ?? Enumerable.Empty<GatherServer>()).SelectMany(s => s.Items ?? [])
            .FirstOrDefault(i => i.Id == id && i.Names is { Count: > 1 })?.Names
            ?? (Known.TryGetValue(id, out var k) ? [k.Fr, k.En] : null);
        var en = names is { Count: > 1 } n ? n[1] : "";
        var rank = en.StartsWith("Radiant ", StringComparison.Ordinal) ? 4 : en.StartsWith("Pure ", StringComparison.Ordinal) ? 3
            : en.StartsWith("Fine ", StringComparison.Ordinal) || en.StartsWith("Fresh ", StringComparison.Ordinal) ? 2 : 1;
        return new GatherItem((int)id, names, rank, (int)Math.Min(int.MaxValue, qty));
    }
}

/// <summary>
/// Historique local du stock de récolte, pour son évolution : par perso et par jour de jeu (reset à 07:00 UTC), la quantité de
/// chaque ressource (sac, coffre et rangements additionnés) au dernier relevé du jour, dans %APPDATA%\Aion2Builder\gather-stock.json.
/// 90 jours gardés ; rien n'est envoyé.
/// </summary>
public sealed class GatherStockStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.Root, "gather-stock.json");
    const int KeepDays = 90;

    /// <summary>Perso → jour (AAAA-MM-JJ) → numéro de ressource → quantité.</summary>
    public Dictionary<string, SortedDictionary<string, Dictionary<long, long>>> Load()
    {
        try
        {
            return System.IO.File.Exists(Path)
                ? JsonSerializer.Deserialize<Dictionary<string, SortedDictionary<string, Dictionary<long, long>>>>(System.IO.File.ReadAllText(Path)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Le jour de jeu d'un relevé (le jeu remet à zéro à 07:00 UTC).</summary>
    public static string GameDay(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(-7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Note le stock du jour de ce relevé (s'il a changé) et rend l'historique du perso, du plus ancien au plus récent.</summary>
    public SortedDictionary<string, Dictionary<long, long>> Record(string character, DateTime atUtc, Dictionary<long, long> stock)
    {
        var all = Load();
        if (!all.TryGetValue(character, out var days)) all[character] = days = [];
        var day = GameDay(atUtc);
        if (days.TryGetValue(day, out var known) && known.Count == stock.Count && known.All(kv => stock.GetValueOrDefault(kv.Key) == kv.Value)) return days;
        days[day] = stock;
        foreach (var old in days.Keys.Take(Math.Max(0, days.Count - KeepDays)).ToList()) days.Remove(old);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.WriteAllText(Path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* historique non vital */ }
        return days;
    }
}
