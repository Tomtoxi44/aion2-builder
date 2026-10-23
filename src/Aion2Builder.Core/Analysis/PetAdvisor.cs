using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Familier pas encore au niveau 3 : âmes manquantes pour le niveau suivant (niveau 0 = pas encore obtenu).</summary>
public sealed record PetProgress(PetInfo Pet, int Level, int Souls, int Missing);

/// <summary>Stat d'une espèce : total donné par ta collection et total si tous ses familiers étaient au niveau 3.</summary>
public sealed record GenusStat(string Key, string Name, double PerPet, double Now, double Max, bool Counted);

/// <summary>
/// Une espèce de familiers : nombre obtenu par niveau (<see cref="Counts"/>[0..3]), stats données, intérêt d'un familier
/// au niveau 3 pour ta classe (<see cref="PetValue"/>) et ce qu'il reste à gagner (<see cref="Remaining"/>).
/// </summary>
public sealed record GenusPlan(string Key, string Name, int Total, int[] Counts, List<GenusStat> Stats, string? Other,
    double PetValue, double Remaining, List<PetProgress> Next)
{
    public int Owned => Counts[1] + Counts[2] + Counts[3];
    public int Maxed => Counts[3];
}

public sealed record PetPlan(PetCollection? Collection, List<GenusPlan> Genera)
{
    public bool Linked => Collection is not null;
    public GenusPlan? For(string key) => Genera.FirstOrDefault(g => g.Key == key);
    /// <summary>Espèce à farmer en premier : la plus utile à ta classe parmi celles qui ont encore quelque chose à gagner.</summary>
    public GenusPlan? Best => Genera.Where(g => g.Remaining > 0).OrderByDescending(g => g.PetValue).FirstOrDefault();
    /// <summary>Avancement de la collection (niveaux atteints / niveaux possibles), pour le rail ; null si non reliée.</summary>
    public int? Progress => Linked ? (int)Math.Round(100.0 * Genera.Sum(g => g.Counts[1] + 2 * g.Counts[2] + 3 * g.Counts[3]) / Math.Max(1, 3 * Genera.Sum(g => g.Total))) : null;
}

/// <summary>
/// Familiers : chaque familier obtenu donne des stats selon son espèce et son niveau (« effets de possession »,
/// <c>progression.json</c> → <c>pets</c>), cumulées sur la collection. La collection vient du Pet Meter via Aion 2 Todo.
/// Les stats tirées par l'analyse de perception d'espèce ne sont lisibles ni sur le site officiel ni par le Pet Meter.
/// </summary>
public sealed class PetAdvisor(GameDatabase db)
{
    PetRules Rules => db.Progression.Pets;

    IEnumerable<PetInfo> PetsOf(PetCollection c, string genus) => c.Catalog.Where(p => p.Genus.Equals(genus, StringComparison.OrdinalIgnoreCase));

    static double At(PetEffect e, int level) => level <= 0 ? 0 : e.Levels.ElementAtOrDefault(Math.Min(level, e.Levels.Count) - 1);

    /// <summary>Stats secondaires données par la collection (les stats principales sont déjà dans les totaux du site officiel).</summary>
    public List<StatSource> Sources(PetCollection? c)
    {
        var sources = new List<StatSource>();
        if (c is null) return sources;
        foreach (var (key, genus) in Rules.Genera)
            foreach (var effect in genus.Effects.Where(e => db.Stat(e.Stat) is { Category: not StatCategory.Primary }))
            {
                var total = PetsOf(c, key).Sum(p => At(effect, c.Level(p.Id)));
                if (total > 0)
                    sources.Add(new StatSource($"Familiers · {genus.Name}",
                        new Effect($"{db.StatName(effect.Stat)} +{Fmt.Num(total)}", effect.Stat, StatScope.All, total, false)));
            }
        return sources;
    }

    /// <summary>Sans profil (classe inconnue), l'intérêt des espèces reste à 0 : seul le relevé de la collection est fait.</summary>
    public PetPlan Plan(PetCollection? c, ResolvedProfile? p = null, StatProfile? sp = null)
    {
        if (c is null) return new PetPlan(null, []);
        var engraving = new EngravingAdvisor(db);
        var gaps = (sp?.Categories ?? []).ToDictionary(x => x.Category, x => x.Gap);
        // Valeur d'un point de stat pour la classe, en unités de budget, relevée si sa famille manque dans tes stats.
        double Value(string stat, double amount)
        {
            if (p is null || db.Stat(stat) is not { Category: not StatCategory.Primary, Unit: > 0 } def) return 0;
            var need = gaps.TryGetValue(def.Category, out var gap) ? Math.Clamp(1 - 2 * gap, 0.6, 1.5) : 1;
            return engraving.Weight(stat, p) * need * amount / def.Unit;
        }

        var genera = new List<GenusPlan>();
        foreach (var (key, genus) in Rules.Genera)
        {
            var pets = PetsOf(c, key).ToList();
            var counts = new int[4];
            foreach (var pet in pets) counts[Math.Clamp(c.Level(pet.Id), 0, 3)]++;
            var stats = genus.Effects.Select(e => new GenusStat(e.Stat, db.StatName(e.Stat), At(e, 3),
                pets.Sum(pet => At(e, c.Level(pet.Id))), At(e, 3) * pets.Count, db.Stat(e.Stat)?.Category != StatCategory.Primary)).ToList();
            var petValue = genus.Effects.Sum(e => Value(e.Stat, At(e, 3)));
            var remaining = pets.Sum(pet => genus.Effects.Sum(e => Value(e.Stat, At(e, 3) - At(e, c.Level(pet.Id)))));
            var next = pets.Select(pet => (Pet: pet, Level: c.Level(pet.Id), Souls: c.SoulsOf(pet.Id)))
                .Where(x => x.Level < c.Tiers.Count)
                .Select(x => new PetProgress(x.Pet, x.Level, x.Souls, c.Tiers[x.Level] - x.Souls))
                .OrderBy(x => x.Missing).ThenBy(x => x.Pet.Name).ToList();
            genera.Add(new GenusPlan(key, genus.Name, pets.Count, counts, stats, genus.Other, petValue, remaining, next));
        }
        return new PetPlan(c, genera.OrderByDescending(g => g.PetValue).ToList());
    }

    static string StatLine(IEnumerable<GenusStat> stats, Func<GenusStat, double> value) =>
        string.Join(", ", stats.Where(s => value(s) > 0).Select(s => $"{s.Name} +{Fmt.Num(value(s))}"));

    public static string Missing(PetProgress x) =>
        x.Level == 0 ? $"{x.Pet.Name} (à obtenir : {x.Missing} âme(s))" : $"{x.Pet.Name} (niv. {x.Level} → {x.Level + 1} : {x.Missing} âme(s))";

    public List<Finding> Findings(PetPlan plan, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        if (plan.Collection is not { } c)
        {
            findings.Add(new Finding(Area.Pets, Severity.Info, "Familiers non comptés",
                "Les stats données par tes familiers manquent à l'analyse. Relie la collection du Pet Meter avec ta clé Aion 2 Todo " +
                "(écran Familiers, ou « pets --key » en ligne de commande)."));
            return findings;
        }

        var all = plan.Genera.Where(g => g.Total > 0).ToList();
        var counted = StatLine(all.SelectMany(g => g.Stats).Where(s => s.Counted).GroupBy(s => s.Name)
            .Select(g => g.First() with { Now = g.Sum(s => s.Now) }), s => s.Now);
        findings.Add(new Finding(Area.Pets, Severity.Info,
            $"Familiers : {all.Sum(g => g.Owned)} / {all.Sum(g => g.Total)} obtenus, {all.Sum(g => g.Maxed)} au niveau 3",
            string.Join(" · ", all.Select(g => $"{g.Name} {g.Owned}/{g.Total} (niv. 3 : {g.Maxed})")) +
            (counted.Length > 0 ? $"\nComptés dans tes stats : {counted}." : "")));

        if (plan.Best is { } best)
        {
            var second = plan.Genera.Where(g => g != best && g.Remaining > 0).OrderByDescending(g => g.PetValue).FirstOrDefault();
            var closest = best.Next.Take(4).Select(Missing).ToList();
            var toGet = best.Counts[0];
            findings.Add(new Finding(Area.Pets, Severity.Advice, $"Familiers : farme d'abord les {best.Name}",
                $"Un {best.Name} au niveau 3 donne {StatLine(best.Stats, s => s.PerPet)} : le plus utile pour un {p.Class.Name} en " +
                $"{GameDatabase.ModeName(p.Mode)}. Il t'en reste {toGet} à obtenir et {best.Total - toGet - best.Maxed} à monter." +
                (closest.Count > 0 ? $"\nLes plus proches : {string.Join(" · ", closest)}." : ""),
                $"Farme les monstres de ces familiers (le Pet Meter affiche les âmes à chaque kill)" +
                (second is null ? "." : $", puis passe aux {second.Name}.") +
                " Chaque familier obtenu fait aussi monter la perception de son espèce.",
                12 + Math.Min(15, best.Remaining / 4)));
        }

        var age = DateTime.UtcNow - c.FetchedAtUtc;
        if (age > TimeSpan.FromDays(7))
            findings.Add(new Finding(Area.Pets, Severity.Info, $"Collection de familiers lue il y a {(int)age.TotalDays} jours",
                "Actualise-la depuis l'écran Familiers après tes sessions de farm."));
        return findings;
    }
}
