using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Model;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Un endroit où farmer l'âme d'un familier : le monstre (niveau, rang), sa chance de la donner, la zone, la carte, ses points d'apparition, le kibélisque le plus proche.</summary>
sealed record PetSpot(string Monster, string? MonsterSlug, int Level, string Rank, double Chance, string Zone, string Map, int Spawns, string? Kisk, int? KiskMeters);

/// <summary>Un familier de la base : son type, sa page, ses autres sources (donjons, coffres…) et ses monstres du monde ouvert.</summary>
sealed record PetFarmEntry(string Name, string Type, string Slug, string? Other, List<PetSpot> Spots);

/// <summary>
/// Où farmer les âmes des familiers : base du site (/api/content/pet-farm, relevée sur les pages des familiers de MetaBot.GG), gardée
/// un jour dans %APPDATA%\Aion2Builder\pet-farm.json. Lue une fois, au premier affichage de l'écran Familiers.
/// </summary>
static class PetFarm
{
    public const string Source = "https://metabot.gg/fr_FR/aion-2/mounts";
    static Dictionary<string, PetFarmEntry> byName = [];
    /// <summary>Race de chaque carte : 1 Élyséens, 2 Asmodiens ; une carte absente (Reshanta) est ouverte aux deux.</summary>
    public static Dictionary<string, int> Maps { get; private set; } = [];
    public static bool Loaded { get; private set; }
    static bool asked;

    public static PetFarmEntry? Of(string name) => byName.GetValueOrDefault(Market.Fold(name));

    /// <summary>Lit la base au premier appel, puis prévient (sur le fil de l'interface) pour redessiner.</summary>
    public static void Ensure(Action onLoaded)
    {
        if (asked) return;
        asked = true;
        _ = Task.Run(async () =>
        {
            if (await Load()) Avalonia.Threading.Dispatcher.UIThread.Post(onLoaded);
        });
    }

    static async Task<bool> Load()
    {
        var file = Path.Combine(AppPaths.Root, "pet-farm.json");
        string? json = null;
        try { if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1)) json = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (json is null)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(Aion2Builder.Import.PetSyncClient.Site), Timeout = TimeSpan.FromSeconds(90) };
                json = await http.GetStringAsync("/api/content/pet-farm");
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(file, json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                try { json = File.Exists(file) ? File.ReadAllText(file) : null; } catch (IOException) { json = null; }
                if (json is null) return false;
            }
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var maps = new Dictionary<string, int>();
            if (doc.RootElement.TryGetProperty("maps", out var m))
                foreach (var p in m.EnumerateObject()) maps[p.Name] = p.Value.GetInt32();
            var pets = new Dictionary<string, PetFarmEntry>();
            foreach (var e in doc.RootElement.GetProperty("pets").EnumerateArray())
            {
                string? Str(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                int Int(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                var spots = e.TryGetProperty("f", out var f)
                    ? f.EnumerateArray().Select(x => new PetSpot(Str(x, "m") ?? "", Str(x, "ms"), Int(x, "l"), Str(x, "r") ?? "Normal",
                        x.TryGetProperty("c", out var c) ? c.GetDouble() : 0, Str(x, "z") ?? "", Str(x, "p") ?? "", Int(x, "sp"), Str(x, "k"),
                        x.TryGetProperty("km", out var km) && km.ValueKind == JsonValueKind.Number ? km.GetInt32() : null)).ToList()
                    : [];
                var name = Str(e, "n") ?? "";
                pets[Market.Fold(name)] = new PetFarmEntry(name, Str(e, "t") ?? "", Str(e, "s") ?? "", Str(e, "o"), spots);
            }
            (byName, Maps, Loaded) = (pets, maps, true);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}

sealed partial class ScreenViews
{
    /// <summary>
    /// « Où farmer » de l'écran Familiers : pour chaque familier pas encore au niveau visé, le meilleur monstre du monde ouvert de
    /// ta faction qui donne son âme (la plus forte chance, un monstre normal à ton niveau de préférence, le plus de points
    /// d'apparition), rangés en route : carte par carte, zone par zone, avec le nombre de monstres à tuer. Null tant que la base
    /// n'est pas lue (elle l'est au premier affichage).
    /// </summary>
    Control? PetFarmCard(PetCollection c, GenusPlan? best)
    {
        PetFarm.Ensure(() => update(_ => { }));
        if (!PetFarm.Loaded) return null;
        var race = RaceOf(r.Character.Race);
        var level = r.Character.Level;
        bool Mine(PetSpot x) => race == 0 || !PetFarm.Maps.TryGetValue(x.Map, out var mr) || mr == 0 || mr == race;
        var all = s.PetFarmAll || best is null;
        var toMax = s.PetFarmMax;

        // Chaque familier pas encore au niveau visé, et son meilleur endroit.
        var lines = new List<(PetInfo Pet, int Level, int Missing, PetSpot Spot, long Kills)>();
        var elsewhere = new List<string>();
        foreach (var pet in c.Catalog.Where(p => all || p.Genus.Equals(best!.Key, StringComparison.OrdinalIgnoreCase)))
        {
            var lv = c.Level(pet.Id);
            if (lv >= c.Tiers.Count) continue;
            var missing = (toMax ? c.Tiers[^1] : c.Tiers[lv]) - c.SoulsOf(pet.Id);
            if (missing <= 0) continue;
            var spot = PetFarm.Of(pet.Name)?.Spots.Where(x => x.Chance > 0 && Mine(x))
                .OrderByDescending(x => x.Chance).ThenBy(x => x.Rank == "Normal" ? 0 : 1).ThenBy(x => x.Level > level + 2 ? 1 : 0).ThenByDescending(x => x.Spawns).FirstOrDefault();
            if (spot is null) { elsewhere.Add(pet.Name); continue; }
            lines.Add((pet, lv, missing, spot, (long)Math.Ceiling(missing * 100 / spot.Chance)));
        }

        var raceName = race == 1 ? "Élyséens" : race == 2 ? "Asmodiens" : "toutes factions";
        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                Choice([(best is null ? "espèce" : best.Name, !all, () => update(st => st.PetFarmAll = false)), ("toutes les espèces", all, () => update(st => st.PetFarmAll = true))]),
                Choice([("niveau suivant", !toMax, () => update(st => st.PetFarmMax = false)), ("jusqu'au niveau 3", toMax, () => update(st => st.PetFarmMax = true))]),
            },
        };
        const string cols = "*,90,60,320,70,70,90";
        var rows = new List<Control>();
        // La route : les cartes dans l'ordre de la base (Poéta, Verteron, Eltnen…), puis les zones par niveau des monstres.
        var mapOrder = PetFarm.Maps.Keys.ToList();
        int MapRank(string map) => mapOrder.IndexOf(map) is >= 0 and var i ? i : int.MaxValue;
        foreach (var zone in lines.GroupBy(x => (x.Spot.Map, x.Spot.Zone)).OrderBy(g => MapRank(g.Key.Map)).ThenBy(g => g.Min(x => x.Spot.Level)).ThenBy(g => g.Key.Zone, French))
        {
            var kisk = zone.Where(x => x.Spot.Kisk is not null).OrderBy(x => x.Spot.KiskMeters ?? int.MaxValue).Select(x => x.Spot).FirstOrDefault();
            var head = new Border
            {
                Background = Tok.Frame, BorderBrush = Tok.RowLine, BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(20, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        T($"{zone.Key.Map} · {zone.Key.Zone}", 12, Accent.Main, FontWeight.SemiBold),
                        T($"niv. {zone.Min(x => x.Spot.Level)}{(zone.Max(x => x.Spot.Level) > zone.Min(x => x.Spot.Level) ? $" à {zone.Max(x => x.Spot.Level)}" : "")}" +
                          $" · {Fmt.Plural(zone.Count(), "familier")} · {Fmt.Big(zone.Sum(x => x.Kills))} monstres à tuer" +
                          (kisk is null ? "" : $" · kibélisque : {kisk.Kisk}{(kisk.KiskMeters is { } km ? $" ({Fmt.Big(km)} m)" : "")}"), 11, Tok.Muted, mono: true).WithMargin(0, 1, 0, 0),
                    },
                },
            };
            rows.Add(head);
            foreach (var x in zone.OrderBy(x => x.Spot.Level).ThenBy(x => x.Pet.Name, French))
            {
                var spot = x.Spot;
                var monster = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        T(spot.Monster, 13),
                        T($"niv. {spot.Level}{(spot.Rank == "Normal" ? "" : $" · {spot.Rank}")}", 11, spot.Level > level + 2 || spot.Rank != "Normal" ? Tok.Warn : Tok.Muted, mono: true).WithMargin(0, 2, 0, 0),
                    },
                };
                var url = spot.MonsterSlug is { } ms ? $"https://metabot.gg/fr_FR/aion-2/monsters/{ms}" : PetFarm.Source;
                rows.Add(Tip(TableRow(Columns(cols, T(x.Pet.Name, 13),
                        T(x.Level == 0 ? "à obtenir" : toMax ? $"{x.Level} → 3" : $"{x.Level} → {x.Level + 1}", 12, x.Level == 0 ? Tok.Warn : Tok.Text2, mono: true),
                        T(x.Missing.ToString(), 13, Tok.Text, FontWeight.SemiBold, mono: true),
                        monster,
                        T($"{spot.Chance:0.#} %", 12, spot.Chance >= 100 ? Tok.Good : Tok.Text2, mono: true),
                        T(Fmt.Big(x.Kills), 13, Tok.Text, FontWeight.SemiBold, mono: true),
                        T(spot.Spawns.ToString(), 12, spot.Spawns >= 20 ? Tok.Text2 : Tok.Muted, mono: true)),
                    false, () => OpenLink(url)),
                    $"{spot.Monster} donne l'âme de {x.Pet.Name} à {spot.Chance:0.#} % par monstre tué : {Fmt.Big(x.Kills)} à tuer pour les {x.Missing} âmes. " +
                    $"{spot.Spawns} points d'apparition à {spot.Zone} ({spot.Map})" + (spot.Kisk is null ? "" : $", kibélisque le plus proche : {spot.Kisk}") +
                    (spot.Level > level + 2 ? $". Niveau {spot.Level} : au-dessus du tien ({level})." : ".") + " Clic : sa page sur MetaBot.GG (carte des apparitions)."));
            }
        }
        if (rows.Count == 0)
            rows.Add(T(elsewhere.Count > 0 ? "Aucun de ces familiers ne se farme dans le monde ouvert de ta faction." : "Rien à farmer ici : tous ces familiers sont au niveau visé.", 12, Tok.Muted)
                .WithMargin(20, 10, 20, 10));
        if (elsewhere.Count > 0)
            rows.Add(T($"Pas dans le monde ouvert de ta faction (donjons, coffres, boutiques, autre faction) : {string.Join(", ", elsewhere.Take(10))}" +
                       (elsewhere.Count > 10 ? $" et {elsewhere.Count - 10} autres." : "."), 11, Tok.Muted, wrap: true).WithMargin(20, 8, 20, 8));
        rows.Add(new Border
        {
            Padding = new Thickness(20, 6, 20, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    T($"{Fmt.Plural(lines.Count, "familier")} · {Fmt.Big(lines.Sum(x => x.Kills))} monstres à tuer en tout · monstres, zones et chances :", 11, Tok.Muted),
                    Clickable(T("MetaBot.GG ↗", 11, Accent.Main, mono: true), () => OpenLink(PetFarm.Source), null, Tok.Hover, new Thickness(4, 0)),
                },
            },
        });
        return TableCard($"Où farmer · {(all ? "toutes les espèces" : best!.Name)} · {raceName}", choices,
            TableHeader(cols, "Familier", "Niveau", "Âmes", "Monstre à tuer", "Chance", "À tuer", "Apparitions"), rows);
    }
}
