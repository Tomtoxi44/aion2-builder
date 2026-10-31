using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;

namespace Aion2Builder.Cli;

/// <summary>
/// « bench » : refait les joueurs de comparaison de chaque classe (benchmark.json). Des joueurs niveau 45 de la région, trouvés
/// par la recherche du site officiel, importés en entier et calculés par le Builder comme ton perso, en JcE puis en JcJ (seules les
/// stats qui changent sont gardées pour le JcJ) ; le fichier ne garde
/// ni nom ni serveur. Lancé chaque semaine par .github/workflows/benchmark.yml.
/// </summary>
static class BenchCommand
{
    const int Level = 45;
    // La recherche du site veut un mot : des lettres courantes, 20 joueurs par lettre et par pcId (classe × genre × race).
    static readonly string[] Keywords = ["a", "e", "i", "o"];
    // Un import à la fois et une pause entre deux : on reste raisonnable avec le site officiel.
    static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    sealed record Measured(int ItemLevel, int CombatPower, int DaevanionNodes, Dictionary<string, double> Stats, Dictionary<string, double> PvpStats);

    /// <summary>
    /// Options d'âme et pierres vues sur les pièces de tous les joueurs relevés (gear-options.json) : par type de pièce, les
    /// options qui sortent et leur valeur à 100 % de lien d'âme ; les pierres par grade. Sans nom ni serveur.
    /// </summary>
    sealed class GearCollector(EffectParser parser)
    {
        readonly Dictionary<(string Category, string Stat), List<(int ItemLevel, double Value, bool Percent, bool Max)>> soul = [];
        readonly Dictionary<(string Kind, string Stat, string Grade), List<double>> stones = [];
        public int Players { get; private set; }

        public void Add(CharacterSnapshot s)
        {
            Players++;
            foreach (var item in s.Equipment.Where(i => i.HasDetail))
            {
                if (item.SoulLineCount > 0 && item.Category is { } category)
                    foreach (var e in item.SubStats.Select(parser.Recognize).Where(e => e.IsStat))
                    {
                        if (!soul.TryGetValue((category, e.StatKey!), out var list)) soul[(category, e.StatKey!)] = list = [];
                        list.Add((item.ItemLevel, e.Value, e.IsPercent, (item.SoulBindRate ?? 0) >= 99.5));
                    }
                foreach (var stone in item.Stones)
                {
                    var e = parser.Recognize(stone.Effect);
                    if (!e.IsStat) continue;
                    if (!stones.TryGetValue((stone.IsSoulStone ? "âme" : "mana", e.StatKey!, stone.Grade), out var list))
                        stones[(stone.IsSoulStone ? "âme" : "mana", e.StatKey!, stone.Grade)] = list = [];
                    list.Add(e.Value);
                }
            }
        }

        static double Median(IEnumerable<double> values) => values.OrderBy(x => x).ToList() is { Count: > 0 } v ? v[v.Count / 2] : 0;

        public GearOptionsFile Build(string region, DateTime date) => new()
        {
            Updated = date.ToString("yyyy-MM-dd"),
            Source = $"Pièces des {Players} joueurs niveau 45 du relevé hebdomadaire (serveurs {region.ToUpperInvariant()}, {date:dd/MM/yyyy}) : " +
                     "options d'âme vues au moins 3 fois par type de pièce, valeur à 100 % de lien d'âme (médiane par niveau d'objet), pierres par grade (médiane).",
            Soul = soul.Where(x => x.Value.Count >= 3).GroupBy(x => x.Key.Category).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.OrderBy(x => x.Key.Stat, StringComparer.Ordinal).ToDictionary(x => x.Key.Stat, x =>
                {
                    // Le format le plus vu (% ou valeur plate) ; la valeur au maximum, par niveau d'objet, dans ce format.
                    var percent = x.Value.Count(l => l.Percent) * 2 > x.Value.Count;
                    var max = x.Value.Where(l => l.Max && l.Percent == percent).GroupBy(l => l.ItemLevel).OrderBy(l => l.Key)
                        .ToDictionary(l => l.Key, l => Math.Round(Median(l.Select(v => v.Value)), 1));
                    return new SoulOption(x.Value.Count, percent, max);
                })),
            Stones = stones.GroupBy(x => x.Key.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.GroupBy(x => x.Key.Stat).OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key,
                    s => s.OrderBy(x => x.Key.Grade, StringComparer.Ordinal).ToDictionary(x => x.Key.Grade, x => Math.Round(Median(x.Value), 1)))),
        };
    }

    public static async Task<int> Run(Args cli)
    {
        var output = cli.Get("out") ?? throw new InvalidOperationException("Précise le fichier : bench --out <benchmark.json> [--count 24] [--class <classe>]");
        var count = int.TryParse(cli.Get("count"), out var n) && n > 0 ? n : 24;
        var only = cli.Get("class");
        var db = GameDataStore.Load();
        var advisor = new Advisor(db);
        using var client = ArmoryClient.Create();
        var today = DateTime.UtcNow;

        var classes = new List<(string Code, ClassBenchmark? Bench)>();
        var gear = new GearCollector(new EffectParser(db));
        var refreshed = 0;
        foreach (var c in db.Classes)
        {
            if (only is not null && db.FindClass(only) != c) { classes.Add((c.Code, c.Benchmark)); continue; }
            var (players, found, skipped, error) = await Sample(client, db, advisor, gear, cli.Region, c.Code, count);
            // Trop peu de joueurs (site en maintenance, adresse changée) : on garde l'échantillon précédent de la classe.
            var keep = players.Count < Math.Max(6, count / 2);
            Console.WriteLine($"{c.Name} : {players.Count} joueurs relevés sur {found} trouvés, {skipped} écartés" +
                              (keep ? " — trop peu, l'échantillon précédent est gardé" : "") + (error is null ? "" : $" (dernière erreur : {error})"));
            if (keep) { classes.Add((c.Code, c.Benchmark)); continue; }
            classes.Add((c.Code, Build(c, players, cli.Region, today)));
            refreshed++;
        }
        if (refreshed == 0)
        {
            Console.Error.WriteLine("Aucune classe relevée : benchmark.json n'est pas modifié.");
            return 1;
        }

        // Une ligne par classe, comme les autres fichiers de données : les changements de la semaine restent lisibles.
        var compact = new JsonSerializerOptions(GameDataStore.Json)
        {
            WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var lines = classes.Where(x => x.Bench is not null).Select(x => $"    \"{x.Code}\": {JsonSerializer.Serialize(x.Bench, compact)}");
        File.WriteAllText(output, $"{{\n  \"updated\": \"{today:yyyy-MM-dd}\",\n  \"classes\": {{\n{string.Join(",\n", lines)}\n  }}\n}}\n");
        Console.WriteLine($"{refreshed}/{classes.Count} classes relevées : {Path.GetFullPath(output)}");

        // Options d'équipement : à côté de benchmark.json, seulement après un relevé complet (toutes les classes).
        if (only is null && gear.Players > 0)
        {
            var file = gear.Build(cli.Region, today);
            var gearPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, GameDataStore.GearOptionsFileName);
            var soulLines = file.Soul.Select(x => $"    \"{x.Key}\": {JsonSerializer.Serialize(x.Value, compact)}");
            var stoneLines = file.Stones.Select(x => $"    \"{x.Key}\": {JsonSerializer.Serialize(x.Value, compact)}");
            File.WriteAllText(gearPath, $"{{\n  \"updated\": \"{file.Updated}\",\n  \"source\": {JsonSerializer.Serialize(file.Source, compact)},\n" +
                                        $"  \"soul\": {{\n{string.Join(",\n", soulLines)}\n  }},\n  \"stones\": {{\n{string.Join(",\n", stoneLines)}\n  }}\n}}\n");
            Console.WriteLine($"Options d'équipement : {file.Soul.Count} types de pièces, {file.Soul.Sum(x => x.Value.Count)} options, {gear.Players} joueurs : {gearPath}");
        }
        return 0;
    }

    static async Task<(List<Measured> Players, int Found, int Skipped, string? Error)> Sample(
        ArmoryClient client, GameDatabase db, Advisor advisor, GearCollector gear, string region, string classCode, int count)
    {
        string? error = null;
        var hits = new List<SearchHit>();
        foreach (var pcId in ArmoryMapper.PcIds(classCode))
            foreach (var keyword in Keywords)
            {
                try { hits.AddRange(await client.SearchLevel(region, keyword, Level, pcId)); }
                catch (ArmoryException ex) { error = ex.Message; }
                await Task.Delay(Pause);
            }
        // Ordre fixe (empreinte de l'identifiant) : d'une semaine à l'autre, ce sont surtout les mêmes joueurs, qu'on voit progresser.
        var candidates = hits.Where(h => h.Level == Level && h.ClassCode == classCode)
            .DistinctBy(h => Uri.UnescapeDataString(h.CharacterId))
            .OrderBy(h => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(h.CharacterId)))))
            .ToList();

        var players = new List<Measured>();
        var skipped = 0;
        foreach (var hit in candidates)
        {
            if (players.Count >= count || skipped > count) break;
            try
            {
                var s = await client.Fetch(db, region, hit.ServerId, hit.CharacterId);
                // Un perso à peine équipé (ou dont les pièces ne s'ouvrent pas) fausserait la comparaison.
                if (s.Level != Level || s.Equipment.Count(i => i.HasDetail) < 10) skipped++;
                else
                {
                    players.Add(Measure(db, advisor.Analyze(s, GameMode.PvE)));
                    gear.Add(s);
                }
            }
            catch (Exception ex) when (ex is ArmoryException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                skipped++;
                error = ex.Message;
            }
            await Task.Delay(Pause);
        }
        return (players, candidates.Count, skipped, error);
    }

    /// <summary>
    /// Ce que l'appli compare : niveau d'objet, puissance, cases Daevanion et stats totales (le % pour une stat en %) en JcE, et
    /// en JcJ les seules stats qui changent (même calcul, effets réservés à chaque mode).
    /// </summary>
    static Measured Measure(GameDatabase db, AdviceReport r)
    {
        static Dictionary<string, double> Totals(StatProfile sp) => sp.Stats.Where(x => Math.Abs(x.BenchValue) >= 0.05)
            .OrderBy(x => x.Def.Key, StringComparer.Ordinal).ToDictionary(x => x.Def.Key, x => Math.Round(x.BenchValue, 1));
        var pve = Totals(r.Stats);
        var pvp = Totals(new StatAnalyzer(db).Profile(r.Character, db.Resolve(r.Character.ClassCode, GameMode.PvP)));
        var changed = pve.Keys.Union(pvp.Keys).Where(k => Math.Abs(pve.GetValueOrDefault(k) - pvp.GetValueOrDefault(k)) > 1e-9)
            .OrderBy(k => k, StringComparer.Ordinal).ToDictionary(k => k, k => pvp.GetValueOrDefault(k));
        return new(r.Character.ItemLevel, r.Character.CombatPower,
            r.Daevanion.Boards.Where(b => b.Pool == DaevanionPlanner.NormalPool).Sum(b => b.Board.OpenNodes), pve, changed);
    }

    static ClassBenchmark Build(ClassProfile c, List<Measured> players, string region, DateTime date)
    {
        var rows = players.OrderBy(p => p.ItemLevel).ToList();
        var keys = rows.SelectMany(p => p.Stats.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal);
        return new ClassBenchmark
        {
            Source = $"{rows.Count} {c.Name}s niveau {Level} des serveurs {region.ToUpperInvariant()}, trouvés par la recherche du site officiel " +
                     $"et relevés le {date:dd/MM/yyyy} (mis à jour chaque semaine)",
            ItemLevel = rows.Select(p => p.ItemLevel).Order().ToList(),
            CombatPower = rows.Select(p => p.CombatPower).Order().ToList(),
            DaevanionNodes = rows.Select(p => p.DaevanionNodes).Order().ToList(),
            Stats = keys.ToDictionary(k => k, k => rows.Select(p => p.Stats.GetValueOrDefault(k)).Order().ToList()),
            Players = rows.Select(p => new BenchPlayer(p.ItemLevel, p.Stats, p.PvpStats)).ToList(),
        };
    }
}
