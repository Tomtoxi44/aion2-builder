using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Additionne les stats que le joueur contrôle (équipement, Daevanion, titres, stats principales et divines)
/// (plus les sources ajoutées hors fiche : familiers)
/// et les ramène à un "budget" commun pour comparer la répartition à celle visée par la classe.
/// Les stats de base de la classe ne sont pas exposées par le site officiel : elles sont hors calcul.
/// </summary>
public sealed class StatAnalyzer(GameDatabase db, IReadOnlyList<StatSource>? extra = null)
{
    public static bool Applies(Effect e, GameMode mode) =>
        e.Scope == StatScope.All || (e.Scope == StatScope.PvE) == (mode == GameMode.PvE);

    public static IEnumerable<StatSource> Sources(CharacterSnapshot c, GameDatabase db)
    {
        foreach (var item in c.Equipment)
        {
            foreach (var e in item.MainStats)
                yield return new StatSource($"{db.SlotName(item.Slot)} · {item.Name}", e, Fixed: true);
            foreach (var e in item.SubStats)
                yield return new StatSource($"{db.SlotName(item.Slot)} · {item.Name}", e);
            foreach (var stone in item.Stones)
                yield return new StatSource($"{db.SlotName(item.Slot)} · pierre {stone.Effect.Raw}", stone.Effect);
        }
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
        var all = Sources(c, db).Concat(extra ?? []).ToList();
        var unrecognized = all.Where(s => !s.Effect.IsStat && !s.Effect.IsSkill && s.Effect.Raw.Length > 0)
            .Select(s => s.Effect.Raw).Distinct().ToList();
        var byKey = all.Where(s => s.Effect.IsStat && Applies(s.Effect, profile.Mode))
            .GroupBy(s => s.Effect.StatKey!).ToDictionary(g => g.Key, g => g.ToList());

        var defs = db.Stats.Where(s => s.Category != StatCategory.Primary).ToList();
        static double Sum(IEnumerable<StatSource> s, bool percent) => s.Where(x => x.Effect.IsPercent == percent).Sum(x => x.Effect.Value);
        var raw = defs.Select(d =>
        {
            var sources = byKey.GetValueOrDefault(d.Key) ?? [];
            var flat = Sum(sources, false);
            var pct = Sum(sources, true);
            // Part ajustable : sans les stats fixes des pièces ; ses % s'appliquent quand même au total plat.
            var choice = sources.Where(s => !s.Fixed).ToList();
            var choiceBudget = (d.Unit > 0 ? Sum(choice, false) / d.Unit : 0) + Budget(d, flat, Sum(choice, true)) - Budget(d, flat, 0);
            return (Def: d, Flat: flat, Pct: pct, Budget: Budget(d, flat, pct), Choice: choiceBudget, Sources: sources);
        }).ToList();

        var total = raw.Sum(r => r.Budget);
        var choiceTotal = raw.Sum(r => r.Choice);
        var weightByCategory = defs.GroupBy(d => d.Category).ToDictionary(g => g.Key, g => g.Sum(d => profile.Weight(d.Key)));
        var stats = raw.Select(r =>
        {
            var w = profile.Weight(r.Def.Key);
            var catWeight = weightByCategory.GetValueOrDefault(r.Def.Category);
            var expected = catWeight > 0 ? profile.Target(r.Def.Category) * w / catWeight : 0;
            return new StatTotal(r.Def, r.Flat, r.Pct, r.Budget, r.Choice, w, choiceTotal > 0 ? r.Choice / choiceTotal : 0, expected,
                r.Sources.OrderByDescending(s => s.Effect.Value).ToList());
        }).OrderByDescending(s => s.Budget).ToList();

        var categories = new[] { StatCategory.Offense, StatCategory.Defense, StatCategory.Sustain, StatCategory.Utility }
            .Select(cat =>
            {
                var b = stats.Where(s => s.Def.Category == cat).Sum(s => s.ChoiceBudget);
                return new CategoryShare(cat, b, choiceTotal > 0 ? b / choiceTotal : 0, profile.Target(cat));
            }).ToList();

        var maxWeight = Math.Max(0.01, profile.Weights.Values.DefaultIfEmpty(1).Max());
        var efficiency = choiceTotal > 0 ? stats.Sum(s => s.ChoiceBudget * s.Weight) / choiceTotal / maxWeight : 0;

        var otherMode = all.Where(s => s.Effect.IsStat && !Applies(s.Effect, profile.Mode))
            .Sum(s => db.Stat(s.Effect.StatKey!) is { } d ? Budget(d, s.Effect.IsPercent ? 0 : s.Effect.Value, s.Effect.IsPercent ? s.Effect.Value : 0) : 0);

        return new StatProfile(profile.Mode, stats, categories, total, choiceTotal, efficiency, otherMode, unrecognized)
        {
            Comparison = Compare(stats, categories, c, profile),
        };
    }

    /// <summary>
    /// Famille par famille : ta part face à la part conseillée ; chaque stat qui compte face aux valeurs conseillées par les
    /// guides (de la classe, sinon communes) et à la moyenne des joueurs de ta classe au niveau d'objet le plus proche (JcE
    /// seulement : l'échantillon est en JcE). À monter : les stats utiles sous une valeur conseillée ou sous la moyenne (sous
    /// la part visée, sans joueurs), encore trouvables, celles des guides d'abord. Où reprendre de la place : les stats
    /// au-delà de leur plafond, et, si la famille dépasse la part conseillée, ses stats au-dessus de la moyenne ou peu utiles.
    /// </summary>
    StatComparison Compare(List<StatTotal> stats, List<CategoryShare> categories, CharacterSnapshot c, ResolvedProfile p)
    {
        var tolerance = db.Settings.Stats.CategoryTolerance;
        var peers = p.Mode == GameMode.PvE ? Peers(p, c) : null;
        var obtainable = Obtainable(c, p);
        var comparison = new StatComparison(peers, peers is null ? null : PeersText(p, c, peers), []) { Targets = Targets(p) };
        static string Value(StatTotal x, double v) => x.Def.Kind == StatKind.Percent ? $"{Fmt.Num(v)} %" : Fmt.Num(Math.Round(v));
        var families = categories.Select(cat =>
        {
            var own = stats.Where(x => x.Def.Category == cat.Category && (x.Weight >= 0.5 || x.Share >= 0.02 || x.ExpectedShare >= 0.03))
                .OrderByDescending(x => x.Weight).ThenByDescending(x => x.Share).ToList();
            var vs = peers is null ? [] : own.Select(x => new StatVsPlayers(x, ClassBenchmark.Sample(peers, x.Def.Key))).ToList();
            int Verdict(StatTotal x) => vs.FirstOrDefault(v => v.Stat == x)?.Verdict ?? (x.Deficit > 0.01 ? -1 : 0);
            var raise = own.Where(x => x.Weight >= 0.6 && obtainable.ContainsKey(x.Def.Key) && (comparison.NextTarget(x) is not null || Verdict(x) < 0))
                .OrderByDescending(x => comparison.NextTarget(x) is not null).ThenByDescending(x => x.Weight).Take(3)
                .Select(x => new StatToRaise(x,
                    comparison.NextTarget(x) is { } t ? $"il manque {Value(x, t.Value - x.BenchValue)} pour {Value(x, t.Value)}, {t.Label}"
                    : peers is null ? "sous la part visée" : "en dessous de la moyenne",
                    WhereToFind(x, obtainable)))
                .ToList();
            var capped = own.Select(x => (Stat: x, Cap: comparison.CapExceeded(x))).Where(z => z.Cap is not null)
                .Select(z => new StatToFree(z.Stat, $"au-delà du plafond de {Value(z.Stat, z.Cap!.Value)} ({z.Cap.Label}) : le surplus ne sert plus"));
            var over = cat.Gap > tolerance
                ? own.Where(x => x.Share >= 0.01 && (Verdict(x) > 0 || x.Weight < 0.6) && !(x.Weight >= 0.6 && Verdict(x) < 0) && comparison.CapExceeded(x) is null)
                    .OrderByDescending(x => Verdict(x) > 0).ThenBy(x => x.Weight).ThenByDescending(x => x.Share).Take(3)
                    .Select(x => new StatToFree(x, Verdict(x) > 0 ? "au-dessus de la moyenne" : "peu utile pour ta classe"))
                : [];
            return new FamilyReview(cat, own, vs, raise, capped.Concat(over).ToList());
        }).ToList();
        return comparison with { Families = families };
    }

    /// <summary>Valeurs conseillées pour le mode : celles de la classe remplacent, stat par stat, les valeurs communes.</summary>
    Dictionary<string, List<StatTarget>> Targets(ResolvedProfile p)
    {
        var own = p.Class.StatTargets.GetValueOrDefault(p.Mode) ?? [];
        var replaced = own.Select(t => t.Stat).ToHashSet();
        return (db.Settings.Stats.Targets.GetValueOrDefault(p.Mode) ?? []).Where(t => !replaced.Contains(t.Stat)).Concat(own)
            .GroupBy(t => t.Stat).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Value).ToList());
    }

    /// <summary>Où ce personnage peut encore trouver la stat ; à défaut, où elle se trouve en général.</summary>
    static string WhereToFind(StatTotal t, Dictionary<string, Availability> obtainable)
    {
        if (!obtainable.TryGetValue(t.Def.Key, out var a))
            return t.Def.Where.Count > 0 ? string.Join(", ", t.Def.Where.Take(2)) : "stats secondaires";
        var parts = new List<string>();
        if (a.Nodes > 0) parts.Add($"{a.Nodes} case(s) Daevanion encore fermée(s)");
        if (a.Slots.Count > 0) parts.Add($"stat secondaire possible sur : {string.Join(", ", a.Slots.Take(4))}");
        if (a.Title) parts.Add("titres");
        return string.Join(" ; ", parts);
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

    /// <summary>Joueurs de comparaison : ceux de la classe au niveau d'objet le plus proche (niveau 45) ; null sans échantillon.</summary>
    public static List<BenchPlayer>? Peers(ResolvedProfile p, CharacterSnapshot c) =>
        c.Level >= 45 && p.Class.Benchmark is { Players.Count: >= 6 } bench ? bench.Peers(c.ItemLevel) : null;

    /// <summary>Nombre de ces joueurs sous ta valeur de la stat.</summary>
    public static int Ahead(List<BenchPlayer> peers, StatTotal x) => peers.Count(o => o.Stats.GetValueOrDefault(x.Def.Key) < x.BenchValue - 1e-9);

    /// <summary>Position parmi ces joueurs, de 0 à 1 : ceux sous ta valeur, plus la moitié de ceux à égalité (10 % comme la plupart n'est pas un retard).</summary>
    public static double Rank(List<BenchPlayer> peers, StatTotal x) =>
        (Ahead(peers, x) + 0.5 * peers.Count(o => Math.Abs(o.Stats.GetValueOrDefault(x.Def.Key) - x.BenchValue) <= 1e-9)) / Math.Max(1, peers.Count);

    /// <summary>« les 8 Templiers niveau 45 au niveau d'objet le plus proche du tien (1860 à 2631 ; toi 2182) ».</summary>
    public static string PeersText(ResolvedProfile p, CharacterSnapshot c, List<BenchPlayer> peers) =>
        $"les {peers.Count} {p.Class.Name}s niveau 45 au niveau d'objet le plus proche du tien ({peers.Min(o => o.ItemLevel)} à {peers.Max(o => o.ItemLevel)} ; toi {c.ItemLevel})";

    /// <summary>Où ce personnage peut encore trouver chaque stat : cases Daevanion fermées, stats secondaires de ses pièces, titres.</summary>
    sealed class Availability
    {
        public int Nodes;
        public SortedSet<string> Slots = [];
        public bool Title;
    }

    Dictionary<string, Availability> Obtainable(CharacterSnapshot c, ResolvedProfile p)
    {
        var result = new Dictionary<string, Availability>();
        Availability Get(string key) => result.TryGetValue(key, out var a) ? a : result[key] = new Availability();
        foreach (var board in c.Boards.Where(b => DaevanionPlanner.Relevant(db.Settings.Daevanion.BoardPools.GetValueOrDefault(b.Name, "Normal"), p.Mode)))
            foreach (var e in board.Nodes.Where(n => !n.Open).SelectMany(n => n.Effects).Where(e => e.IsStat && Applies(e, p.Mode)))
                Get(e.StatKey!).Nodes++;
        foreach (var item in c.Equipment)
            foreach (var e in item.SubStats.Where(e => e.IsStat && Applies(e, p.Mode)))
                Get(e.StatKey!).Slots.Add(db.SlotName(item.Slot));
        foreach (var e in c.Titles.SelectMany(t => t.Effects).Where(e => e.IsStat && Applies(e, p.Mode)))
            Get(e.StatKey!).Title = true;
        return result;
    }

    public List<Finding> Findings(StatProfile sp, ResolvedProfile p, CharacterSnapshot c)
    {
        var findings = new List<Finding>();
        var s = db.Settings.Stats;
        var who = $"un {p.Class.Name} en {GameDatabase.ModeName(p.Mode)}";
        if (sp.ChoiceBudget <= 0)
        {
            findings.Add(new Finding(Area.Stats, Severity.Info, "Pas de stats lisibles",
                "Le site officiel n'a renvoyé aucune stat d'équipement ou de Daevanion pour ce personnage."));
            return findings;
        }

        // On ne conseille que des stats que ce personnage peut réellement aller chercher.
        var obtainable = Obtainable(c, p);
        bool Can(StatTotal t) => obtainable.ContainsKey(t.Def.Key);
        string Where(StatTotal t) => WhereToFind(t, obtainable);
        StatTotal? Wanted(StatCategory? category) =>
            sp.Stats.Where(x => (category is null || x.Def.Category == category) && x.Weight > s.LowWeight && Can(x))
                .OrderByDescending(x => x.Deficit * x.Weight).FirstOrDefault();

        findings.Add(new Finding(Area.Stats, Severity.Info, $"Efficacité des stats : {Fmt.Pct(sp.Efficiency)}",
            $"Part de tes stats ajustables (stats secondaires, Daevanion, titres, stats divines) placée dans des stats utiles pour {who}. Les stats fixes des pièces (DÉF d'une armure, ATQ d'une arme) sont hors calcul : elles viennent avec la pièce."));

        // 1. Répartition Attaque / Défense / Survie / Utilité.
        var over = sp.Categories.Where(x => x.Gap > s.CategoryTolerance).OrderByDescending(x => x.Gap).ToList();
        // Une famille sous sa part visée mais dont les stats utiles sont au niveau des joueurs comparés ne manque de rien :
        // ce qu'elle a vient des stats fixes des pièces, hors part.
        bool FineVsPlayers(StatCategory cat) => sp.Comparison.Families.FirstOrDefault(f => f.Share.Category == cat)?.AtPlayers == true;
        var under = sp.Categories.Where(x => x.Gap < -s.CategoryTolerance && !FineVsPlayers(x.Category)).OrderBy(x => x.Gap).ToList();
        string Line(CategoryShare x) => $"{GameDatabase.CategoryWithExamples(x.Category)} : {Fmt.Pct(x.Share)} de tes stats ajustables, pour environ {Fmt.Pct(x.Target)} conseillé";
        if (over.Count > 0 && under.Count > 0)
        {
            var o = over[0];
            var u = under[0];
            var give = sp.Stats.Where(x => x.Def.Category == o.Category).OrderByDescending(x => x.ChoiceBudget * (1 - x.Weight)).First();
            var take = Wanted(u.Category);
            var big = Math.Max(o.Gap, -u.Gap) > 2 * s.CategoryTolerance;
            findings.Add(new Finding(Area.Stats, big ? Severity.Important : Severity.Advice,
                $"Répartition : plus {GameDatabase.Of(o.Category)} que conseillé, moins {GameDatabase.Of(u.Category)}",
                $"{Line(o)}. {Line(u)}. (Conseillé : la répartition visée pour {who}, tirée des poids de stats de la classe ; une base de réglage, pas une moyenne de joueurs. « Stats ajustables » = stats secondaires, Daevanion, titres et stats divines, hors stats fixes des pièces.)",
                take is null
                    ? $"Lors des relances de stats secondaires et au prochain changement de pièce, abandonne « {give.Def.Name} »."
                    : $"Lors des relances de stats secondaires, des choix Daevanion et des changements de pièce, remplace « {give.Def.Name} » par « {take.Def.Name} » ({Where(take)}).",
                big ? 70 : 45));
        }
        else
        {
            foreach (var o in over)
                findings.Add(new Finding(Area.Stats, Severity.Advice, $"Plus {GameDatabase.Of(o.Category)} que conseillé", $"{Line(o)}.",
                    Wanted(null) is { } best ? $"Réoriente une partie vers « {best.Def.Name} » ({Where(best)})." : null, 35));
            foreach (var u in under)
                if (Wanted(u.Category) is { } take)
                    findings.Add(new Finding(Area.Stats, Severity.Advice, $"Moins {GameDatabase.Of(u.Category)} que conseillé",
                        $"{Line(u)}.", $"Cherche « {take.Def.Name} » ({Where(take)}).", 40));
        }

        // 2. Stats qui prennent de la place sans servir la classe.
        foreach (var w in sp.Stats.Where(x => x.Weight <= s.LowWeight && x.Share >= s.WastedShare).Take(3))
        {
            var from = string.Join(", ", w.Sources.Where(x => !x.Fixed).Select(x => x.Source).Distinct().Take(3));
            findings.Add(new Finding(Area.Stats, Severity.Advice, $"{w.Def.Name} peu utile ({Fmt.Pct(w.Share)} de tes stats ajustables)",
                $"Pour {who}, « {w.Def.Name} » rapporte peu. Elle vient surtout de : {from}.",
                "Remplace-la en priorité lors des relances de stats secondaires ou au prochain changement de pièce.",
                25 + 100 * w.Share));
        }

        // 2 bis. Stats importantes sous la moyenne des joueurs de la classe à ton niveau d'objet (même calcul, JcE seulement :
        // l'échantillon est en JcE). Pas tout l'échantillon : les stats montent avec le niveau d'objet.
        if (sp.Comparison is { Peers: not null, PeersText: { } peersText })
        {
            var behind = sp.Comparison.Families.SelectMany(f => f.Players).Where(v => v.Verdict < 0 && v.Stat.Weight >= 0.6)
                .OrderByDescending(v => v.Stat.Weight).Take(3).ToList();
            string Value(StatTotal x, double v) => x.Def.Kind == StatKind.Percent ? $"{Fmt.Num(v)} %" : Fmt.Num(v);
            if (behind.Count > 0)
                findings.Add(new Finding(Area.Stats, Severity.Advice,
                    $"{string.Join(", ", behind.Select(v => v.Stat.Def.Name))} : sous la moyenne des joueurs de ton niveau d'objet",
                    string.Join(" · ", behind.Select(v => $"{v.Stat.Def.Name} : {Value(v.Stat, v.Mine)}, moyenne {Value(v.Stat, v.Average)}")) +
                    $". Joueurs comparés : {peersText}, stats totales calculées comme les tiennes.",
                    $"Vise au moins la moyenne : {string.Join(" ; ", behind.Select(v => $"{v.Stat.Def.Name} ({Where(v.Stat)})"))}.",
                    45 + 10 * behind.Count));
        }

        // 3. Stats prioritaires en retard sur la répartition idéale, et réellement disponibles.
        var wanted = sp.Stats.Where(x => x.Weight >= 0.6 && x.Deficit > 0.01 && Can(x))
            .OrderByDescending(x => x.Deficit * x.Weight).Take(3).ToList();
        if (wanted.Count > 0)
            findings.Add(new Finding(Area.Stats, Severity.Advice, $"Stats à aller chercher : {string.Join(", ", wanted.Select(x => x.Def.Name))}",
                string.Join(" · ", wanted.Select(x => $"{x.Def.Name} : {Fmt.Pct(x.Share)} des stats ajustables (≈ {Fmt.Pct(x.ExpectedShare)} visé) — {Where(x)}")),
                "Priorise-les dans tes choix Daevanion et tes relances de stats secondaires.",
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
}
