using System.Globalization;
using System.Text.Json;

namespace Aion2Builder.Combat;

/// <summary>Une ressource récoltée : numéro du jeu, noms (fr, en, ru) s'ils sont connus du Pet Meter, rang (1 à 4), quantité.</summary>
public sealed record GatherItem(int? Id, List<string>? Names, int Rank, int Qty)
{
    public string Name => Names is { Count: > 0 } n ? n[0] : Id is { } id ? $"objet {id}" : "objet inconnu";
    /// <summary>Rang 2 des plantes : « fraîche » ; des autres ressources : « d'une grande pureté ».</summary>
    public bool Fresh => Names is { Count: > 1 } n && n[1].StartsWith("Fresh ", StringComparison.Ordinal);
}

/// <summary>Récolte d'un serveur : le compteur du jeu (récoltes commencées, ratées comprises), les ratées et celles au rang supérieur
/// vues par le Pet Meter, les persos et les ressources.</summary>
public sealed record GatherServer(int Count, int Failed, int Better, List<string>? Chars, List<GatherItem>? Items);

/// <summary>Un jour de jeu (reset à 07:00 UTC) : récolte par serveur (« #numéro »), Kinah de monstres par perso ; Clients : combien
/// de PC l'ont vu (récolte du compte, lue sur Aion 2 Todo), 0 pour un fichier de ce PC.</summary>
public sealed record GatherDay(string Day, Dictionary<string, GatherServer>? Servers, Dictionary<string, long>? Kinah, int Cap = 1000, int Clients = 0)
{
    public DateOnly Date => DateOnly.ParseExact(Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Récolte de chaque jour écrite par le Pet Meter (v0.17.0 et plus) : %LOCALAPPDATA%\AION2-pet-meter\gathering\AAAA-MM-JJ.json,
/// 60 jours gardés. Lue seulement : le Builder n'y écrit rien.
/// </summary>
public static class PetMeterGathering
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2-pet-meter", "gathering");

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Les jours enregistrés, du plus récent au plus ancien (un fichier illisible est sauté). Relus seulement quand un fichier a
    /// changé (le Pet Meter réécrit celui du jour pendant que tu récoltes) : l'écran Récolte les demande à chaque affichage.
    /// </summary>
    public static List<GatherDay> Load()
    {
        var days = new List<GatherDay>();
        if (!Directory.Exists(Dir)) return days;
        var files = new DirectoryInfo(Dir).GetFiles("????-??-??.json");
        var stamp = (files.Length, files.Length == 0 ? DateTime.MinValue : files.Max(f => f.LastWriteTimeUtc));
        if (cache is { } c && c.Stamp == stamp) return [.. c.Days];
        foreach (var file in files.Select(f => f.FullName))
        {
            try
            {
                if (JsonSerializer.Deserialize<GatherDay>(File.ReadAllText(file), Json) is { Day.Length: 10 } d) days.Add(d);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException) { }
        }
        days = days.OrderByDescending(d => d.Day, StringComparer.Ordinal).ToList();
        cache = (stamp, days);
        return [.. days];
    }

    static ((int, DateTime) Stamp, List<GatherDay> Days)? cache;

    /// <summary>
    /// Les jours du compte (tous tes PC, lus sur Aion 2 Todo) d'abord ; ceux de ce PC qu'il n'a pas (avant le Pet Meter v0.18.0,
    /// ou pas encore envoyés), en plus. Du plus récent au plus ancien.
    /// </summary>
    public static List<GatherDay> Merge(IReadOnlyList<GatherDay> account, IReadOnlyList<GatherDay> local) =>
        account.Concat(local.Where(l => account.All(a => a.Day != l.Day))).OrderByDescending(d => d.Day, StringComparer.Ordinal).ToList();

    /// <summary>Plusieurs jours d'un serveur additionnés (ressources regroupées par numéro, ou par nom pour un numéro inconnu).</summary>
    public static GatherServer Sum(IEnumerable<GatherServer> servers)
    {
        var list = servers.ToList();
        var items = list.SelectMany(s => s.Items ?? []).GroupBy(i => i.Id?.ToString(CultureInfo.InvariantCulture) ?? i.Name)
            .Select(g => g.First() with { Qty = g.Sum(i => i.Qty) }).OrderByDescending(i => i.Qty).ToList();
        return new GatherServer(list.Sum(s => s.Count), list.Sum(s => s.Failed), list.Sum(s => s.Better),
            list.SelectMany(s => s.Chars ?? []).Distinct().ToList(), items);
    }

    /// <summary>Nom du rang (le jeu le met dans le nom de la ressource) ; le 4 (éclatant) est de rareté Unique, en jaune dans le jeu.</summary>
    public static string RankName(int rank, bool fresh = false) => rank switch
    {
        4 => "éclatant",
        3 => "pur",
        2 => fresh ? "fraîche" : "grande pureté",
        _ => "normal",
    };
}
