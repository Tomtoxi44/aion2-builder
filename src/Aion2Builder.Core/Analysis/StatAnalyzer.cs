using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Additionne les stats que le joueur contrôle (équipement, Daevanion, titres, stats principales et divines)
/// et les ramène à un "budget" commun pour comparer la répartition à celle visée par la classe.
/// Les stats de base de la classe ne sont pas exposées par le site officiel : elles sont hors calcul.
/// </summary>
public sealed class StatAnalyzer(GameDatabase db)
{
    public static bool Applies(Effect e, GameMode mode) =>
        e.Scope == StatScope.All || (e.Scope == StatScope.PvE) == (mode == GameMode.PvE);

    public static IEnumerable<StatSource> Sources(CharacterSnapshot c, GameDatabase db)
    {
        foreach (var item in c.Equipment)
            foreach (var e in item.MainStats.Concat(item.SubStats))
                yield return new StatSource($"{db.SlotName(item.Slot)} · {item.Name}", e);
        foreach (var board in c.Boards)
            foreach (var node in board.Nodes.Where(n => n.Open))
                foreach (var e in node.Effects.Where(e => e.IsStat))
                    yield return new StatSource($"Daevanion {board.Name}", e);
        foreach (var t in c.Titles)
            foreach (var e in t.Effects)
                yield return new StatSource($"Titre {t.Name}", e);
        foreach (var p in c.PrimaryStats)
            foreach (var e in p.Effects)
                yield return new StatSource(p.Name, e);
    }

    public StatProfile Profile(CharacterSnapshot c, ResolvedProfile profile)
    {
        var all = Sources(c, db).ToList();
        var unrecognized = all.Where(s => !s.Effect.IsStat && !s.Effect.IsSkill && s.Effect.Raw.Length > 0)
            .Select(s => s.Effect.Raw).Distinct().ToList();
        var byKey = all.Where(s => s.Effect.IsStat && Applies(s.Effect, profile.Mode))
            .GroupBy(s => s.Effect.StatKey!).ToDictionary(g => g.Key, g => g.ToList());

        var defs = db.Stats.Where(s => s.Category != StatCategory.Primary).ToList();
        var raw = defs.Select(d =>
        {
            var sources = byKey.GetValueOrDefault(d.Key) ?? [];
            var flat = sources.Where(s => !s.Effect.IsPercent).Sum(s => s.Effect.Value);
            var pct = sources.Where(s => s.Effect.IsPercent).Sum(s => s.Effect.Value);
            return (Def: d, Flat: flat, Pct: pct, Budget: Budget(d, flat, pct), Sources: sources);
        }).ToList();

        var total = raw.Sum(r => r.Budget);
        var weightByCategory = defs.GroupBy(d => d.Category).ToDictionary(g => g.Key, g => g.Sum(d => profile.Weight(d.Key)));
        var stats = raw.Select(r =>
        {
            var w = profile.Weight(r.Def.Key);
            var catWeight = weightByCategory.GetValueOrDefault(r.Def.Category);
            var expected = catWeight > 0 ? profile.Target(r.Def.Category) * w / catWeight : 0;
            return new StatTotal(r.Def, r.Flat, r.Pct, r.Budget, w, total > 0 ? r.Budget / total : 0, expected,
                r.Sources.OrderByDescending(s => s.Effect.Value).ToList());
        }).OrderByDescending(s => s.Budget).ToList();

        var categories = new[] { StatCategory.Offense, StatCategory.Defense, StatCategory.Sustain, StatCategory.Utility }
            .Select(cat =>
            {
                var b = stats.Where(s => s.Def.Category == cat).Sum(s => s.Budget);
                return new CategoryShare(cat, b, total > 0 ? b / total : 0, profile.Target(cat));
            }).ToList();

        var maxWeight = Math.Max(0.01, profile.Weights.Values.DefaultIfEmpty(1).Max());
        var efficiency = total > 0 ? stats.Sum(s => s.Budget * s.Weight) / total / maxWeight : 0;

        var otherMode = all.Where(s => s.Effect.IsStat && !Applies(s.Effect, profile.Mode))
            .Sum(s => db.Stat(s.Effect.StatKey!) is { } d ? Budget(d, s.Effect.IsPercent ? 0 : s.Effect.Value, s.Effect.IsPercent ? s.Effect.Value : 0) : 0);

        return new StatProfile(profile.Mode, stats, categories, total, efficiency, otherMode, unrecognized);
    }

    /// <summary>Points de budget d'une stat : un % d'une stat plate s'applique au total plat déjà acquis.</summary>
    public static double Budget(StatDef d, double flat, double pct)
    {
        if (d.Category == StatCategory.Primary) return 0;
        var flatPart = d.Unit > 0 ? flat / d.Unit : 0;
        double pctPart;
        if (d.Kind == StatKind.Flat && flat > 0 && d.Unit > 0) pctPart = flat * pct / 100 / d.Unit;
        else pctPart = d.PercentUnit > 0 ? pct / d.PercentUnit : 0;
        return flatPart + pctPart;
    }

    /// <summary>Valeur pondérée d'un effet isolé (case Daevanion, stat d'objet) pour ce profil.</summary>
    public double EffectValue(Effect e, StatProfile current, ResolvedProfile profile) =>
        e.IsStat && Applies(e, profile.Mode) ? EffectBudget(e, current) * profile.Weight(e.StatKey!) : 0;

    /// <summary>Points de budget d'un effet isolé, sans pondération.</summary>
    public double EffectBudget(Effect e, StatProfile current)
    {
        if (!e.IsStat || db.Stat(e.StatKey!) is not { } d) return 0;
        if (!e.IsPercent) return d.Unit > 0 ? e.Value / d.Unit : 0;
        var flatTotal = current.Get(d.Key)?.Flat ?? 0;
        return Budget(d, flatTotal, e.Value) - Budget(d, flatTotal, 0);
    }

    public List<Finding> Findings(StatProfile sp, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        var s = db.Settings.Stats;
        var who = $"un {p.Class.Name} en {GameDatabase.ModeName(p.Mode)}";
        if (sp.TotalBudget <= 0)
        {
            findings.Add(new Finding(Area.Stats, Severity.Info, "Pas de stats lisibles",
                "Le site officiel n'a renvoyé aucune stat d'équipement ou de Daevanion pour ce personnage."));
            return findings;
        }

        findings.Add(new Finding(Area.Stats, Severity.Info, $"Efficacité des stats : {Fmt.Pct(sp.Efficiency)}",
            $"Part de ton budget de stats (équipement, Daevanion, titres, stats divines) placée dans des stats utiles pour {who}."));

        // 1. Répartition Attaque / Défense / Survie / Utilité.
        var over = sp.Categories.Where(c => c.Gap > s.CategoryTolerance).OrderByDescending(c => c.Gap).ToList();
        var under = sp.Categories.Where(c => c.Gap < -s.CategoryTolerance).OrderBy(c => c.Gap).ToList();
        string Line(CategoryShare c) => $"{GameDatabase.CategoryName(c.Category)} : {Fmt.Pct(c.Share)} du budget (≈ {Fmt.Pct(c.Target)} visé)";
        if (over.Count > 0 && under.Count > 0)
        {
            var o = over[0];
            var u = under[0];
            var give = sp.Stats.Where(x => x.Def.Category == o.Category).OrderByDescending(x => x.Budget * (1 - x.Weight)).First();
            var take = sp.Stats.Where(x => x.Def.Category == u.Category).OrderByDescending(x => x.Deficit * x.Weight).First();
            var big = Math.Max(o.Gap, -u.Gap) > 2 * s.CategoryTolerance;
            findings.Add(new Finding(Area.Stats, big ? Severity.Important : Severity.Advice,
                $"Trop de {GameDatabase.CategoryName(o.Category)}, pas assez de {GameDatabase.CategoryName(u.Category)}",
                $"{Line(o)}. {Line(u)}. Repères pour {who}.",
                $"Sur tes prochaines pièces, relances de stats secondaires et pierres de mana, remplace « {give.Def.Name} » par « {take.Def.Name} » ({Where(take)}).",
                big ? 70 : 45));
        }
        else
        {
            foreach (var o in over)
                findings.Add(new Finding(Area.Stats, Severity.Advice, $"Beaucoup de {GameDatabase.CategoryName(o.Category)}",
                    $"{Line(o)}.", $"Réoriente une partie vers « {Best(sp).Def.Name} ».", 35));
            foreach (var u in under)
            {
                var take = sp.Stats.Where(x => x.Def.Category == u.Category).OrderByDescending(x => x.Deficit * x.Weight).First();
                findings.Add(new Finding(Area.Stats, Severity.Advice, $"Pas assez de {GameDatabase.CategoryName(u.Category)}",
                    $"{Line(u)}.", $"Cherche « {take.Def.Name} » ({Where(take)}).", 40));
            }
        }

        // 2. Stats qui prennent de la place sans servir la classe.
        foreach (var w in sp.Stats.Where(x => x.Weight <= s.LowWeight && x.Share >= s.WastedShare).Take(3))
        {
            var from = string.Join(", ", w.Sources.Take(3).Select(x => x.Source));
            findings.Add(new Finding(Area.Stats, Severity.Advice, $"{w.Def.Name} peu utile ({Fmt.Pct(w.Share)} du budget)",
                $"Pour {who}, « {w.Def.Name} » rapporte peu. Elle vient surtout de : {from}.",
                "Remplace-la en priorité lors des relances de stats secondaires ou au prochain changement de pièce.",
                25 + 100 * w.Share));
        }

        // 3. Stats prioritaires en retard sur la répartition idéale.
        var wanted = sp.Stats.Where(x => x.Weight >= 0.6 && x.Deficit > 0.01)
            .OrderByDescending(x => x.Deficit * x.Weight).Take(3).ToList();
        if (wanted.Count > 0)
            findings.Add(new Finding(Area.Stats, Severity.Advice, $"Stats à aller chercher : {string.Join(", ", wanted.Select(x => x.Def.Name))}",
                string.Join(" · ", wanted.Select(x => $"{x.Def.Name} : {Fmt.Pct(x.Share)} du budget (≈ {Fmt.Pct(x.ExpectedShare)} visé)")),
                $"Vise-les sur tes cases Daevanion, tes stats secondaires et tes pierres de mana — {Where(wanted[0])}.",
                50 + 100 * wanted.Sum(x => x.Deficit * x.Weight)));

        if (sp.OtherModeBudget > 0.05 * sp.TotalBudget)
        {
            var other = p.Mode == GameMode.PvE ? GameMode.PvP : GameMode.PvE;
            findings.Add(new Finding(Area.Stats, Severity.Info, $"Stats {GameDatabase.ModeName(other)} non comptées",
                $"≈ {Fmt.Pct(sp.OtherModeBudget / (sp.TotalBudget + sp.OtherModeBudget))} de tes stats ne servent qu'en {GameDatabase.ModeName(other)} : bascule de vue pour les voir."));
        }

        if (sp.Unrecognized.Count > 0)
            findings.Add(new Finding(Area.General, Severity.Info, $"{sp.Unrecognized.Count} libellé(s) non reconnu(s)",
                "Ajoute-les dans stats.json pour qu'ils comptent : " + string.Join(" | ", sp.Unrecognized.Take(8))));

        return findings;
    }

    static StatTotal Best(StatProfile sp) =>
        sp.Stats.OrderByDescending(x => x.Deficit * x.Weight).First();

    static string Where(StatTotal t) => t.Def.Where.Count > 0 ? string.Join(", ", t.Def.Where.Take(3)) : "stats secondaires";
}
