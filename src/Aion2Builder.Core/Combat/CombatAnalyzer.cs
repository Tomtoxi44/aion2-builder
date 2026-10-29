using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Combat;

/// <summary>Un sort du combat jugé : sa donnée de sort (recharge), le nombre de lancers possibles, la part simulée.</summary>
public sealed record SkillReview(CombatSkill Skill, SpellData? Spell, int? Possible, double? Simulated, string Verdict, Severity Level)
{
    /// <summary>Sort de situation (provocation, protection, soin, déplacement) : lancé au moment voulu, pas jugé sur ses lancers.</summary>
    public bool Situational { get; init; }
}

/// <summary>Un buff du combat jugé : lancers possibles s'il a une recharge, ou ce qui le déclenche.</summary>
public sealed record BuffReview(CombatBuff Buff, SpellData? Spell, int? Possible, string Verdict, Severity Level);

public sealed record CombatReport(CombatLog Log, List<SkillReview> Skills, List<BuffReview> Buffs, List<Finding> Findings, double? Crit, double? Back,
    List<string> Unseen);

/// <summary>
/// Analyse d'un combat relevé (NotMeter) : lancers face à la recharge de chaque sort (spells.json), sorts de la rotation
/// jamais lancés, buffs relancés moins souvent que leur recharge le permet, placement (coups de dos), et parts de dégâts
/// mesurées face à la simulation. Ce qui ne se déduit pas des chiffres (esquives, dégâts évitables) n'est pas inventé.
/// </summary>
public sealed class CombatAnalyzer(GameDatabase db)
{
    static string Key(string s) => NotMeterReader.Key(s);

    static int? Possible(double duration, double cooldown) => cooldown > 0 && duration > 0 ? (int)Math.Floor(duration / cooldown) + 1 : null;

    static string Times(int n) => $"{n} fois";

    /// <summary>
    /// Rôles (fiches de classe) d'un sort de situation : il sert au moment voulu (prendre l'aggro, protéger, soigner, se
    /// déplacer, se libérer), pas dès que sa recharge est finie. Ni « lancers perdus » ni « jamais lancé » pour eux.
    /// </summary>
    static readonly string[] SituationalRoles =
    [
        "Menace", "Protection", "Bouclier", "Soin", "Gros soin", "Sanctuaire", "Résurrection", "Immunité", "Fuite", "Repositionnement",
        "Rapprochement", "Engagement", "Libération des contrôles", "Furtivité", "Peur", "Repoussement", "Entrave", "Silence",
        "Retrait de buffs", "Réduction de dégâts", "Réduction des soins", "Défense", "Survie", "Esquive garantie", "Partage des dégâts",
        "PV plancher", "Contrôle aérien", "Invocation (provocation",
    ];

    /// <summary>Le rôle du sort pour ta classe s'il en fait un sort de situation ; null sinon.</summary>
    static string? SituationalRole(ResolvedProfile p, string name) =>
        p.Skill(name)?.Role is { Length: > 0 } role && SituationalRoles.Any(k => role.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ? role : null;

    /// <summary>Sort de la classe par nom ; un nom coupé par NotMeter (« Position d'attaque sur... ») se reconnaît par son début.</summary>
    SpellData? Find(string classCode, string name)
    {
        var spells = db.Spells.Values.Where(s => s.Class == classCode).ToList();
        var key = Key(name);
        if (spells.FirstOrDefault(s => Key(s.Name) == key) is { } exact) return exact;
        var truncated = name.TrimEnd().EndsWith("...") || name.TrimEnd().EndsWith('…');
        if (truncated && key.Length >= 8) return spells.FirstOrDefault(s => Key(s.Name).StartsWith(key));
        // Une ou deux lettres mal lues par l'OCR (« Coup au cæur ») : le nom le plus proche, s'il est seul à cette distance.
        if (key.Length < 8) return null;
        var close = spells.Select(s => (Spell: s, Distance: Distance(Key(s.Name), key))).Where(x => x.Distance <= 2).OrderBy(x => x.Distance).ToList();
        return close.Count > 0 && (close.Count == 1 || close[1].Distance > close[0].Distance) ? close[0].Spell : null;
    }

    /// <summary>
    /// Lancers possibles d'un sort qui ne part qu'après un autre : chaque lancer d'un sort qui l'ouvre est une occasion,
    /// à condition que sa recharge soit finie (deux ouvertures rapprochées ne valent qu'un lancer).
    /// </summary>
    static int Openings(SpellData spell, List<CombatCast> casts)
    {
        var openers = spell.Requires!.Select(Key).ToHashSet();
        var count = 0;
        var ready = double.MinValue;
        foreach (var c in casts.Where(c => openers.Contains(Key(c.Name))).OrderBy(c => c.T))
        {
            if (c.T < ready) continue;
            count++;
            ready = c.T + spell.Cooldown;
        }
        return count;
    }

    /// <summary>Distance d'édition (insertions, suppressions, remplacements).</summary>
    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }

    public CombatReport Analyze(CombatLog log, ResolvedProfile p)
    {
        var mode = p.Mode == GameMode.PvP ? "JcJ" : "JcE";
        var findings = new List<Finding>();
        var simulated = new SkillAdvisor(db).DamageShares(p);
        var duration = log.Duration;
        var seconds = $"{duration:0} s";
        // Global de plusieurs combats : lancers possibles comptés combat par combat (chacun commence recharge prête).
        int? PossibleFor(double cooldown) => log.Segments is { Count: > 1 } segments
            ? cooldown > 0 ? segments.Sum(d => (int)Math.Floor(d / cooldown) + 1) : null
            : Possible(duration, cooldown);
        var boundaries = log.Segments is { Count: > 1 } s2 ? s2.Select((_, i) => s2.Take(i + 1).Sum()).ToList() : [];

        // Sorts : lancers face à la recharge.
        var skills = log.Skills.Select(s =>
        {
            var spell = Find(p.Class.Code, s.Name);
            var sim = simulated.TryGetValue(GameDatabase.NormalizeLabel(s.Name), out var v) ? v : (double?)null;
            if (spell is null)
                return new SkillReview(s, null, null, sim, "Hors des données de sorts (enchaînement ou effet déclenché) : rien à comparer.", Severity.Info);
            var possible = PossibleFor(spell.Cooldown);
            // Sort qui ne part qu'après un autre (Jugement après un coup de bouclier) : ses occasions réelles, pas sa recharge.
            var why = $"recharge {spell.Cooldown:0} s";
            if (spell.Requires is { Count: > 0 } && spell.Within > 0 && possible is not null)
            {
                if (log.Casts is not { Count: > 0 } timed)
                    return new SkillReview(s, spell, null, sim,
                        $"Ne part que dans les {spell.Within:0} s après {string.Join(" ou ", spell.Requires)} : sans lancers chronométrés (Pet Meter), pas comparable à sa recharge.", Severity.Info);
                possible = Openings(spell, timed);
                why = $"après {string.Join(" ou ", spell.Requires)}, recharge {spell.Cooldown:0} s";
            }
            if (possible is not { } max || s.Uses <= 0)
                return new SkillReview(s, spell, null, sim, spell.Kind == "Passive" ? "Effet passif déclenché par tes coups : rien à comparer."
                    : spell.Cooldown > 0 ? "Utilisations non lues." : "Sans recharge : sort de remplissage.", Severity.Info);
            if (s.Uses < 0.8 * max && SituationalRole(p, s.Name) is { } role)
                return new SkillReview(s, spell, max, sim,
                    $"{s.Uses} sur {max} possibles ({why}) : sort de situation ({role.ToLowerInvariant()}), lancé au moment voulu : pas jugé sur ses lancers.",
                    Severity.Info) { Situational = true };
            if (s.Uses > max)
                return new SkillReview(s, spell, max, sim,
                    $"{Times(s.Uses)} pour {max} permises ({why}) : réinitialisations ou réductions de recharge bien exploitées.", Severity.Info);
            if (s.Uses >= 0.8 * max)
                return new SkillReview(s, spell, max, sim, $"{s.Uses} sur {max} possibles ({why}) : lancé dès qu'il est prêt.", Severity.Info);
            var lost = max - s.Uses;
            var gain = log.TotalDamage > 0 ? lost * s.Damage / s.Uses / log.TotalDamage : 0;
            return new SkillReview(s, spell, max, sim,
                $"{s.Uses} sur {max} possibles ({why}) : {lost} lancer{(lost > 1 ? "s" : "")} perdu{(lost > 1 ? "s" : "")}, " +
                $"≈ {Fmt.Pct(gain)} de dégâts en plus en le lançant dès qu'il revient.", gain >= 0.05 ? Severity.Important : Severity.Advice);
        }).ToList();
        foreach (var r in skills.Where(r => r.Level <= Severity.Advice).OrderBy(r => r.Level).ThenByDescending(r => r.Skill.Damage))
            findings.Add(new Finding(Area.Skills, r.Level, $"« {r.Skill.Name} » : {r.Skill.Uses} lancer{(r.Skill.Uses > 1 ? "s" : "")} sur {r.Possible} possibles", r.Verdict,
                r.Spell?.Requires is { Count: > 0 } req ? $"Enchaîne « {r.Skill.Name} » après chaque {string.Join(" ou ", req)}." : $"Relance « {r.Skill.Name} » dès que sa recharge est finie.",
                r.Level == Severity.Important ? 70 : 50));

        // Sorts de la rotation jamais vus.
        var seen = log.Skills.Select(s => Key(s.Name)).Concat(log.Buffs.Select(b => Key(b.Name))).ToHashSet();
        // Un stigma que l'écran Stigmas ne met pas dans les emplacements (les plus prioritaires du mode) n'est pas attendu ici :
        // sinon le combat dirait de lancer un stigma que les conseils disent de retirer.
        var slotted = p.Skills.Where(k => k.Kind == "Stigma").OrderByDescending(k => k.Priority.GetValueOrDefault(p.Mode))
            .Take(db.Settings.Stigmas.Slots).Select(k => Key(k.Name)).ToHashSet();
        bool Expected(string name) => p.Skill(name, "Stigma") is null || slotted.Contains(Key(name));
        var expected = RotationSimulator.Priority(p.Play).Select(step => Find(p.Class.Code, step.Replace("(stigma)", "").Trim()))
            .OfType<SpellData>().Where(s => (s.Coef > 0 || s.Cooldown > 0) && Expected(s.Name) && SituationalRole(p, s.Name) is null).DistinctBy(s => s.Name).ToList();
        var unseen = expected.Where(s => !seen.Contains(Key(s.Name)) && !seen.Any(k => k.Length >= 8 && Key(s.Name).StartsWith(k))).Select(s => s.Name).ToList();
        if (unseen.Count > 0 && log.Complete)
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Jamais lancé{(unseen.Count > 1 ? "s" : "")} : {string.Join(", ", unseen)}",
                string.Join(" · ", unseen.Select(n => expected.First(s => s.Name == n)).Select(s =>
                    $"{s.Name} : dans ta rotation {mode}{(s.Coef > 0 ? $", {s.Coef:0} % d'ATQ" : "")}{(PossibleFor(s.Cooldown) is { } m ? $", jusqu'à {m} lancer{(m > 1 ? "s" : "")} en {seconds}" : "")}")),
                $"{(unseen.Count > 1 ? "Ajoute-les" : "Ajoute-le")} à ta rotation (écran Build).", 65));
        else if (!log.Complete && log.SkillCount > 0)
            findings.Add(new Finding(Area.Skills, Severity.Info, $"Liste partielle : {log.Skills.Count} sorts lus sur {log.SkillCount}",
                (unseen.Count > 0 ? $"Pas vus sur les captures : {string.Join(", ", unseen)}. " : "") +
                "Dans NotMeter, fais défiler la liste des compétences et importe une capture par écran (toutes ensemble)."));

        // Buffs.
        var buffs = log.Buffs.Select(b =>
        {
            var uptime = b.Uptime is { } u ? Fmt.Pct(u) : "?";
            if (b.Uptime is null && Key(b.Name).StartsWith("parchemin"))
                return new BuffReview(b, null, null, "Consommable ; temps actif non lu sur la capture.", Severity.Info);
            if (Key(b.Name).StartsWith("parchemin"))
                return b.Uptime is < 0.9
                    ? new BuffReview(b, null, null, $"Actif {uptime} du combat : relance-le juste avant.", Severity.Advice)
                    : new BuffReview(b, null, null, "Consommable actif tout le combat.", Severity.Info);
            var spell = Find(p.Class.Code, b.Name);
            if (spell is { Cooldown: > 0 } && PossibleFor(spell.Cooldown) is { } max)
                return b.Count < max
                    ? new BuffReview(b, spell, max, $"Lancé {Times(b.Count)} sur {max} possibles en {seconds} (recharge {spell.Cooldown:0} s), actif {uptime} : relance-le dès qu'il revient.", Severity.Advice)
                    : new BuffReview(b, spell, max, $"Lancé à chaque recharge ({spell.Cooldown:0} s), actif {uptime}.", Severity.Info);
            if (spell?.Trigger is { } trigger && b.Uptime is < 0.35)
                return new BuffReview(b, spell, null, $"Actif seulement {uptime} : {trigger} (d'après la description du sort, à confirmer en jeu).", Severity.Advice);
            return new BuffReview(b, spell, null, $"Actif {uptime} du combat.", Severity.Info);
        }).ToList();
        foreach (var r in buffs.Where(r => r.Level <= Severity.Advice))
            findings.Add(new Finding(Area.Skills, r.Level, $"Buff « {r.Buff.Name} » : {(r.Buff.Uptime is { } u ? Fmt.Pct(u) : "?")} du temps", r.Verdict, null, 45));

        // Combats du Pet Meter : lancers chronométrés → combos ratés et temps morts ; coups reçus face aux soins.
        if (log.Casts is { Count: > 2 } casts)
        {
            var rotation = RotationSimulator.Priority(p.Play).Select(Key).ToHashSet();
            foreach (var spell in db.Spells.Values.Where(s => s.Class == p.Class.Code && s.Requires is { Count: > 0 } && s.Within > 0))
            {
                var openers = spell.Requires!.Select(Key).ToHashSet();
                var openings = casts.Where(c => openers.Contains(Key(c.Name))).ToList();
                var follow = casts.Where(c => Key(c.Name) == Key(spell.Name)).ToList();
                if (openings.Count < 3 || follow.Count == 0 && !rotation.Contains(Key(spell.Name))) continue;
                var done = openings.Count(o => follow.Any(f => f.T > o.T && f.T - o.T <= spell.Within));
                if (done >= 0.6 * openings.Count) continue;
                findings.Add(new Finding(Area.Skills, Severity.Advice, $"Combo « {spell.Name} » : {done} fois sur {openings.Count} occasions",
                    $"« {spell.Name} » ne part que dans les {spell.Within:0} s après {string.Join(" ou ", spell.Requires!)} : " +
                    $"{openings.Count - done} occasion{(openings.Count - done > 1 ? "s" : "")} perdue{(openings.Count - done > 1 ? "s" : "")}" +
                    (spell.Coef > 0 ? $" ({spell.Coef:0} % d'ATQ chacune)." : "."),
                    $"Enchaîne « {spell.Name} » juste après « {spell.Requires![0]} ».", 55));
            }
            // Pauses entre deux lancers (un lancer dure environ 1 s ; au-delà de 2,5 s, la rotation s'arrête).
            var gaps = casts.Zip(casts.Skip(1), (a, b) => (Gap: b.T - a.T, Across: boundaries.Any(x => x > a.T && x <= b.T)))
                .Where(g => g.Gap > 2.5 && !g.Across).Select(g => g.Gap).ToList();
            var idle = gaps.Sum(g => g - 1);
            if (duration > 0 && idle / duration > 0.1)
                findings.Add(new Finding(Area.Skills, Severity.Advice, $"Temps morts : {gaps.Count} pause{(gaps.Count > 1 ? "s" : "")}, {idle:0} s sur {seconds}",
                    $"{gaps.Count} fois plus de 2,5 s sans lancer ({Fmt.Pct(idle / duration)} du combat) : la rotation s'arrête (déplacement, attente d'une recharge, cible perdue).",
                    "Garde un sort sans recharge entre deux sorts à recharge.", 50));
        }
        if (log.DamageTaken is > 0 and var taken)
            findings.Add(new Finding(Area.Skills, Severity.Info, $"Dégâts subis : {Fmt.Big(taken)} ({log.HitsTaken} coups, le plus gros {Fmt.Big(log.MaxTaken ?? 0)})",
                $"Soins sur toi pendant le combat : {Fmt.Big(log.Heals ?? 0)}" + (log.Heals is { } h && h < 0.3 * taken ? " : bien moins que les dégâts subis (potions, sorts de soin ou esquives)." : ".")));

        // Placement et critique, pondérés par les coups.
        double? Weighted(Func<CombatSkill, double?> pick)
        {
            var known = log.Skills.Where(s => pick(s) is not null && s.Hits > 0).ToList();
            return known.Count == 0 ? null : known.Sum(s => pick(s)!.Value * s.Hits) / known.Sum(s => s.Hits);
        }
        var crit = Weighted(s => s.Crit);
        var back = Weighted(s => s.Back);
        if (back is { } b && p.Class.Code == "ASSASSIN")
            findings.Add(b >= 0.8
                ? new Finding(Area.Skills, Severity.Info, $"Placement : {Fmt.Pct(b)} des coups de dos", "Les attaques de dos de l'Assassin sont bien exploitées.")
                : new Finding(Area.Skills, Severity.Advice, $"Placement : {Fmt.Pct(b)} des coups de dos seulement",
                    "L'Assassin gagne des dégâts de dos (Attaque surprise +30 %).", "Reste dans le dos de la cible, Embuscade t'y replace.", 55));

        // Parts mesurées face à la simulation, et part des enchaînements que la simulation ne compte pas.
        var top = skills.OrderByDescending(r => r.Skill.Share).Take(3).ToList();
        var outside = skills.Where(r => r.Spell is null).Sum(r => r.Skill.Share);
        findings.Add(new Finding(Area.Skills, Severity.Info, $"DPS {Fmt.Big(log.Dps)} sur {seconds}{(log.Target.Length > 0 ? $" ({log.Target})" : "")}",
            $"Sorts qui font les dégâts : {string.Join(", ", top.Select(r => $"{r.Skill.Name} {Fmt.Pct(r.Skill.Share)}" + (r.Simulated is { } s ? $" (simulé {Fmt.Pct(s)})" : "")))}." +
            (outside > 0.05 ? $" Les enchaînements et effets hors données de sorts font {Fmt.Pct(outside)} des dégâts : la simulation ne les voit pas, d'où l'intérêt des parts mesurées." : "") +
            (crit is { } c ? $" Critique {Fmt.Pct(c)} des coups." : "")));

        return new CombatReport(log, skills, buffs, findings.OrderBy(f => f.Severity).ThenByDescending(f => f.Priority).ToList(), crit, back, unseen);
    }

    /// <summary>
    /// Parts de dégâts mesurées sur un ou plusieurs combats (dégâts additionnés), par nom de sort normalisé : elles remplacent
    /// la simulation dans le focus dégâts. Une suite d'enchaînement compte avec le sort lancé (<see cref="Chains"/>).
    /// </summary>
    public static Dictionary<string, double> Shares(IReadOnlyList<CombatLog> logs, IReadOnlyList<ChainLink> chains)
    {
        var parent = chains.ToDictionary(c => Norm(c.Skill), c => Norm(c.Parent));
        var damage = logs.SelectMany(l => l.Skills.Select(s => (Name: Norm(s.Name), Damage: s.Damage > 0 ? s.Damage : s.Share * l.TotalDamage)))
            .GroupBy(s => parent.GetValueOrDefault(s.Name) ?? s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Damage));
        var total = damage.Values.Sum();
        return total <= 0 ? [] : damage.ToDictionary(kv => kv.Key, kv => kv.Value / total);
    }

    static string Norm(string s) => GameDatabase.NormalizeLabel(s);

    /// <summary>
    /// Suites d'enchaînement : un sort mesuré qui n'est pas une compétence du perso (Coup punitif, qui suit Frappes successives)
    /// est rattaché au sort qui le précède avec un délai régulier. Pour chaque sort candidat : délai médian entre lui et la
    /// suite, part des utilisations de la suite qu'il précède à ce délai (± 0,35 s), moins la même part prise à n'importe
    /// quel lancer (un sort lancé sans arrêt précède tout, par hasard). Le meilleur doit dépasser ce hasard de 0,3 et le
    /// suivant de 0,15, et être lancé au moins aussi souvent que sa suite ; une suite de suite remonte au sort lancé
    /// (Coup désespéré → Coup opportun → Frappe féroce). Même calcul pour toutes les classes : il ne lit que l'ordre des lancers.
    /// </summary>
    public static List<ChainLink> Chains(IReadOnlyList<CombatLog> logs, ISet<string> learnable)
    {
        const double Window = 3, Tolerance = 0.35;
        // Utilisations par combat et par sort (le Pet Meter les a déjà regroupées : une par appui).
        var fights = logs.Where(l => l.Casts is { Count: > 0 }).Select(l => (
            All: l.Casts!.Select(c => c.T).OrderBy(t => t).ToList(),
            By: l.Casts!.GroupBy(c => Norm(c.Name)).ToDictionary(g => g.Key, g => g.Select(c => c.T).OrderBy(t => t).ToList()))).ToList();
        if (fights.Count == 0) return [];
        int Uses(string name) => fights.Sum(f => f.By.GetValueOrDefault(name)?.Count ?? 0);
        static bool Hit(List<double> times, double from, double to)
        {
            var i = times.BinarySearch(from);
            if (i < 0) i = ~i;
            return i < times.Count && times[i] <= to;
        }

        var damaging = logs.SelectMany(l => l.Skills).Where(s => s.Damage > 0 || s.Share > 0).Select(s => Norm(s.Name)).ToHashSet();
        var display = logs.SelectMany(l => l.Casts ?? []).GroupBy(c => Norm(c.Name)).ToDictionary(g => g.Key, g => g.First().Name);
        var names = fights.SelectMany(f => f.By.Keys).Distinct().ToList();
        var direct = new Dictionary<string, (string Parent, double Lag, double Regular)>();
        foreach (var x in names.Where(n => !learnable.Contains(n) && damaging.Contains(n) && Uses(n) >= 5))
        {
            var uses = Uses(x);
            var scored = new List<(string Parent, double Lag, double Regular, double Score)>();
            foreach (var y in names.Where(n => n != x && Uses(n) >= uses * 0.75))
            {
                var lags = new List<double>();
                foreach (var f in fights)
                    if (f.By.TryGetValue(y, out var ys) && f.By.TryGetValue(x, out var xs))
                        foreach (var t in xs)
                            if (ys.LastOrDefault(u => u < t - 0.05, double.NaN) is var v && !double.IsNaN(v) && t - v <= Window) lags.Add(t - v);
                if (lags.Count < 3) continue;
                lags.Sort();
                var lag = lags[lags.Count / 2];
                int Near(List<double> times, List<double> ys) => times.Count(t => Hit(ys, t - lag - Tolerance, t - lag + Tolerance));
                var regular = (double)fights.Sum(f => f.By.TryGetValue(y, out var ys) && f.By.TryGetValue(x, out var xs) ? Near(xs, ys) : 0) / uses;
                var chance = (double)fights.Sum(f => f.By.TryGetValue(y, out var ys) ? Near(f.All, ys) : 0) / fights.Sum(f => f.All.Count);
                scored.Add((y, lag, regular, regular - chance));
            }
            var ranked = scored.OrderByDescending(s => s.Score).ToList();
            if (ranked.Count > 0 && ranked[0].Score >= 0.3 && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 0.15))
                direct[x] = (ranked[0].Parent, ranked[0].Lag, ranked[0].Regular);
        }

        var links = new List<ChainLink>();
        foreach (var (x, d) in direct)
        {
            var parent = d.Parent;
            for (var hops = 0; !learnable.Contains(parent) && direct.TryGetValue(parent, out var up) && hops < 4; hops++) parent = up.Parent;
            if (learnable.Contains(parent))
                links.Add(new ChainLink(display[x], display[parent], d.Parent == parent ? null : display[d.Parent], d.Lag, d.Regular, Uses(x)));
        }
        return links;
    }
}

/// <summary>
/// Une suite d'enchaînement rattachée au sort lancé (noms tels que relevés) : <paramref name="Via"/> est la suite qui la précède
/// quand elle en suit une autre ; délai typique en secondes et part de ses utilisations qui suivent ce délai.
/// </summary>
public sealed record ChainLink(string Skill, string Parent, string? Via, double Lag, double Regular, int Uses)
{
    /// <summary>Part des dégâts de la suite elle-même (déjà comptée dans la part de <see cref="Parent"/>).</summary>
    public double Share { get; init; }

    /// <summary>« Coup désespéré (après Coup opportun) → Frappe féroce : 92 % de ses lancers, 0,7 s après ».</summary>
    public string Text => $"{Skill}{(Via is null ? "" : $" (après {Via})")} → {Parent} : {Fmt.Pct(Regular)} de ses lancers, {Fmt.Num(Math.Round(Lag, 1))} s après";
}
