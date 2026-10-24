using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Combat;

/// <summary>Un sort du combat jugé : sa donnée de sort (recharge), le nombre de lancers possibles, la part simulée.</summary>
public sealed record SkillReview(CombatSkill Skill, SpellData? Spell, int? Possible, double? Simulated, string Verdict, Severity Level);

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

        // Sorts : lancers face à la recharge.
        var skills = log.Skills.Select(s =>
        {
            var spell = Find(p.Class.Code, s.Name);
            var sim = simulated.TryGetValue(GameDatabase.NormalizeLabel(s.Name), out var v) ? v : (double?)null;
            if (spell is null)
                return new SkillReview(s, null, null, sim, "Hors des données de sorts (enchaînement ou effet déclenché) : rien à comparer.", Severity.Info);
            var possible = Possible(duration, spell.Cooldown);
            if (possible is not { } max || s.Uses <= 0)
                return new SkillReview(s, spell, null, sim, spell.Kind == "Passive" ? "Effet passif déclenché par tes coups : rien à comparer."
                    : spell.Cooldown > 0 ? "Utilisations non lues." : "Sans recharge : sort de remplissage.", Severity.Info);
            if (s.Uses > max)
                return new SkillReview(s, spell, max, sim,
                    $"{Times(s.Uses)} pour {max} permises par sa recharge seule ({spell.Cooldown:0} s) : réinitialisations ou réductions de recharge bien exploitées.", Severity.Info);
            if (s.Uses >= 0.8 * max)
                return new SkillReview(s, spell, max, sim, $"{s.Uses} sur {max} possibles (recharge {spell.Cooldown:0} s) : lancé dès qu'il est prêt.", Severity.Info);
            var lost = max - s.Uses;
            var gain = log.TotalDamage > 0 ? lost * s.Damage / s.Uses / log.TotalDamage : 0;
            return new SkillReview(s, spell, max, sim,
                $"{s.Uses} sur {max} possibles (recharge {spell.Cooldown:0} s) : {lost} lancer{(lost > 1 ? "s" : "")} perdu{(lost > 1 ? "s" : "")}, " +
                $"≈ {Fmt.Pct(gain)} de dégâts en plus en le lançant dès qu'il revient.", gain >= 0.05 ? Severity.Important : Severity.Advice);
        }).ToList();
        foreach (var r in skills.Where(r => r.Level <= Severity.Advice).OrderBy(r => r.Level).ThenByDescending(r => r.Skill.Damage))
            findings.Add(new Finding(Area.Skills, r.Level, $"« {r.Skill.Name} » : {r.Skill.Uses} lancers sur {r.Possible} possibles", r.Verdict,
                $"Relance « {r.Skill.Name} » dès que sa recharge est finie.", r.Level == Severity.Important ? 70 : 50));

        // Sorts de la rotation jamais vus.
        var seen = log.Skills.Select(s => Key(s.Name)).Concat(log.Buffs.Select(b => Key(b.Name))).ToHashSet();
        var expected = RotationSimulator.Priority(p.Play).Select(step => Find(p.Class.Code, step.Replace("(stigma)", "").Trim()))
            .OfType<SpellData>().Where(s => s.Coef > 0 || s.Cooldown > 0).DistinctBy(s => s.Name).ToList();
        var unseen = expected.Where(s => !seen.Contains(Key(s.Name)) && !seen.Any(k => k.Length >= 8 && Key(s.Name).StartsWith(k))).Select(s => s.Name).ToList();
        if (unseen.Count > 0 && log.Complete)
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Jamais lancé{(unseen.Count > 1 ? "s" : "")} : {string.Join(", ", unseen)}",
                string.Join(" · ", unseen.Select(n => expected.First(s => s.Name == n)).Select(s =>
                    $"{s.Name} : dans ta rotation {mode}{(s.Coef > 0 ? $", {s.Coef:0} % d'ATQ" : "")}{(Possible(duration, s.Cooldown) is { } m ? $", jusqu'à {m} lancers en {seconds}" : "")}")),
                "Ajoute-les à ta rotation (écran Build).", 65));
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
            if (spell is { Cooldown: > 0 } && Possible(duration, spell.Cooldown) is { } max)
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
            var gaps = casts.Zip(casts.Skip(1), (a, b) => b.T - a.T).Where(g => g > 2.5).ToList();
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

    /// <summary>Parts de dégâts mesurées, par nom de sort normalisé : remplacent la simulation dans le focus dégâts.</summary>
    public static Dictionary<string, double> Shares(CombatLog log) =>
        log.Skills.GroupBy(s => GameDatabase.NormalizeLabel(s.Name)).ToDictionary(g => g.Key, g => g.Sum(s => s.Share));
}
