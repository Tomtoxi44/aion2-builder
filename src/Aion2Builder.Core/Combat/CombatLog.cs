namespace Aion2Builder.Combat;

/// <summary>
/// Un combat relevé par un meter (NotMeter : fenêtre « Détails du combat »), lu sur des captures d'écran.
/// Les captures ne montrent que ce que la fenêtre affiche : une liste de sorts défilée sur plusieurs captures est fusionnée.
/// </summary>
public sealed record CombatLog
{
    public string Source { get; init; } = "NotMeter";
    public DateTime ImportedAtUtc { get; init; }
    public string Character { get; init; } = "";
    public string Target { get; init; } = "";
    /// <summary>Durée du combat, en secondes.</summary>
    public double Duration { get; init; }
    public double TotalDamage { get; init; }
    public double Dps { get; init; }
    public int Hits { get; init; }
    /// <summary>Nombre de sorts annoncé par le meter (« Compétences 24 ») ; 0 s'il n'a pas été lu.</summary>
    public int SkillCount { get; init; }
    public List<CombatSkill> Skills { get; init; } = [];
    public List<CombatBuff> Buffs { get; init; } = [];
    /// <summary>Captures dont le combat a été lu.</summary>
    public int Pages { get; init; }

    // Combats du Pet Meter seulement (lus dans le trafic du jeu, pas sur la fenêtre NotMeter) :
    /// <summary>Fichier du Pet Meter d'où vient le combat (pour ne pas l'importer deux fois).</summary>
    public string? SourceFile { get; init; }
    /// <summary>Version de la conversion des fichiers du Pet Meter : un combat converti par une version plus ancienne est reconverti.</summary>
    public int ImportVersion { get; init; }
    /// <summary>Coups reçus pendant le combat : total, nombre, le plus gros ; soins sur toi.</summary>
    public double? DamageTaken { get; init; }
    public int? HitsTaken { get; init; }
    public double? MaxTaken { get; init; }
    public double? Heals { get; init; }
    /// <summary>Tes lancers, dans l'ordre : secondes depuis le début du combat et sort.</summary>
    public List<CombatCast>? Casts { get; init; }
    /// <summary>Carte du jeu où le combat a eu lieu (Pet Meter) : un donjon garde la même d'un boss à l'autre.</summary>
    public long? Map { get; init; }
    /// <summary>Global de plusieurs combats : durée de chacun (lancers possibles et pauses comptés combat par combat).</summary>
    public List<double>? Segments { get; init; }

    /// <summary>
    /// Global de plusieurs combats (un donjon) : dégâts, coups et lancers additionnés par sort, critique et coups de dos
    /// pondérés par les coups, lancers mis bout à bout (chaque combat décalé de la durée des précédents).
    /// </summary>
    public static CombatLog Merge(IReadOnlyList<CombatLog> logs)
    {
        var ordered = logs.OrderBy(l => l.ImportedAtUtc).ToList();
        var duration = ordered.Sum(l => l.Duration);
        var total = ordered.Sum(l => l.TotalDamage);
        var skills = ordered.SelectMany(l => l.Skills).GroupBy(s => NotMeterReader.Key(s.Name)).Select(g =>
        {
            double? Weighted(Func<CombatSkill, double?> pick)
            {
                var known = g.Where(s => pick(s) is not null && s.Hits > 0).ToList();
                return known.Count == 0 ? null : known.Sum(s => pick(s)!.Value * s.Hits) / known.Sum(s => s.Hits);
            }
            var damage = g.Sum(s => s.Damage);
            return new CombatSkill(g.First().Name, g.Max(s => s.Level), damage, total > 0 ? damage / total : 0, g.Sum(s => s.Uses), g.Sum(s => s.Hits),
                Weighted(s => s.Crit), Weighted(s => s.Back), null);
        }).OrderByDescending(s => s.Damage).ToList();
        List<CombatCast>? casts = null;
        var offset = 0.0;
        foreach (var l in ordered)
        {
            if (l.Casts is { } c) (casts ??= []).AddRange(c.Select(x => x with { T = x.T + offset }));
            offset += l.Duration;
        }
        var buffs = ordered.SelectMany(l => l.Buffs.Select(b => (Buff: b, l.Duration))).GroupBy(x => NotMeterReader.Key(x.Buff.Name))
            .Select(g => new CombatBuff(g.First().Buff.Name, g.All(x => x.Buff.Uptime is null) ? null
                : g.Where(x => x.Buff.Uptime is not null).Sum(x => x.Buff.Uptime!.Value * x.Duration) / Math.Max(1, g.Where(x => x.Buff.Uptime is not null).Sum(x => x.Duration)),
                g.Sum(x => x.Buff.Count), g.Max(x => x.Buff.Level))).ToList();
        double? Sum(Func<CombatLog, double?> pick) => ordered.Any(l => pick(l) is not null) ? ordered.Sum(l => pick(l) ?? 0) : null;
        return new CombatLog
        {
            Source = ordered.Select(l => l.Source).Distinct().Count() == 1 ? ordered[0].Source : "Pet Meter, NotMeter",
            ImportedAtUtc = ordered[0].ImportedAtUtc, Character = ordered[0].Character, Target = $"{ordered.Count} combats",
            Duration = duration, TotalDamage = total, Dps = duration > 0 ? total / duration : 0, Hits = ordered.Sum(l => l.Hits),
            SkillCount = skills.Count, Skills = skills, Buffs = buffs, Pages = ordered.Sum(l => l.Pages),
            DamageTaken = Sum(l => l.DamageTaken), HitsTaken = (int?)Sum(l => l.HitsTaken), MaxTaken = ordered.Max(l => l.MaxTaken),
            Heals = Sum(l => l.Heals), Casts = casts, Map = ordered[0].Map, Segments = ordered.Select(l => l.Duration).ToList(),
        };
    }

    /// <summary>
    /// Combats regroupés en séances (un donjon) : même carte quand elle est connue, et moins de 10 min entre deux combats.
    /// Les plus récents d'abord.
    /// </summary>
    public static List<List<int>> Sessions(IReadOnlyList<CombatLog> newestFirst)
    {
        var groups = new List<List<int>>();
        for (var i = 0; i < newestFirst.Count; i++)
        {
            var log = newestFirst[i];
            if (groups.Count > 0 && newestFirst[groups[^1][^1]] is var later
                && later.ImportedAtUtc - log.ImportedAtUtc.AddSeconds(log.Duration) < TimeSpan.FromMinutes(10)
                && (later.Map is null || log.Map is null || later.Map == log.Map))
                groups[^1].Add(i);
            else groups.Add([i]);
        }
        return groups;
    }

    /// <summary>Tous les sorts du combat ont été lus (sinon : faire défiler la liste et ajouter une capture).</summary>
    public bool Complete => SkillCount > 0 && Skills.Count >= SkillCount;
}

/// <summary>Un sort du combat : dégâts, part des dégâts (0 à 1), utilisations, coups, critique et coups de dos (0 à 1).</summary>
public sealed record CombatSkill(string Name, int? Level, double Damage, double Share, int Uses, int Hits, double? Crit, double? Back, double? IntervalMs);

/// <summary>Un buff du combat : temps actif (0 à 1 ; null si l'OCR ne l'a pas lu), nombre d'applications, niveau.</summary>
public sealed record CombatBuff(string Name, double? Uptime, int Count, int? Level);

/// <summary>Un lancer du combat : secondes depuis le début, code et nom du sort.</summary>
public sealed record CombatCast(double T, long Code, string Name);

/// <summary>Mot reconnu sur une capture, avec sa position en pixels.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);
