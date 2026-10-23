using System.Text.RegularExpressions;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Compétences. Les points montent une compétence du niveau 1 au niveau 10 (1 point par niveau, réinitialisation
/// gratuite) ; au-delà, seuls le Daevanion et l'équipement donnent des niveaux. On répartit donc un budget :
/// d'abord ce qui compte dans la rotation de la classe (dégâts principaux, burst, débuffs), et on reprend des points
/// aux compétences nettement moins utiles dans ce mode — avec la raison à chaque fois.
/// Les stigmas sont à part : 4 emplacements, niveaux montés avec des fragments.
/// </summary>
public sealed partial class SkillAdvisor(GameDatabase db)
{
    [GeneratedRegex(@"^(?<name>.+?)\s*\+(?<n>\d+)$")]
    private static partial Regex BonusRegex();

    static string Norm(string s) => GameDatabase.NormalizeLabel(s);

    public static SkillKind KindOf(SkillInfo s) =>
        s.Category.Equals("Dp", StringComparison.OrdinalIgnoreCase) || s.Category.Equals("Stigma", StringComparison.OrdinalIgnoreCase)
            ? SkillKind.Stigma
            : s.IsPassive ? SkillKind.Passive : SkillKind.Active;

    bool CommonPvp(SkillInfo s) => db.Settings.Skills.CommonPvpSkills.Any(n => Norm(n) == Norm(s.Name));

    /// <summary>Importance de la compétence pour ce profil : 0 = inutile, 1 = indispensable.</summary>
    public double Priority(SkillInfo s, ResolvedProfile p)
    {
        if (p.Skill(s.Name) is { } k) return k.Priority.GetValueOrDefault(p.Mode);
        var set = db.Settings.Skills;
        if (p.Mode == GameMode.PvP && CommonPvp(s)) return 1.0;
        return KindOf(s) switch
        {
            SkillKind.Passive => set.UnknownPassivePriority,
            SkillKind.Stigma => s.Equipped ? set.UnknownStigmaPriority : 0.2,
            // Sans fiche de classe, la barre du joueur fait foi : une active non équipée ne sert pas.
            _ => s.Equipped ? set.UnknownActivePriority : 0.05,
        };
    }

    public HashSet<string> CoreSet(ResolvedProfile p) =>
        p.CoreSkills.Concat(p.Mode == GameMode.PvP ? db.Settings.Skills.CommonPvpSkills : []).Select(Norm).ToHashSet();

    /// <summary>Niveaux gratuits par compétence : cases Daevanion ouvertes et compétences bonus de l'équipement.</summary>
    public static Dictionary<string, int> BonusLevels(CharacterSnapshot c)
    {
        var bonus = new Dictionary<string, int>();
        void Add(string name, int n) => bonus[Norm(name)] = bonus.GetValueOrDefault(Norm(name)) + n;
        foreach (var e in c.Boards.SelectMany(b => b.Nodes).Where(n => n.Open && n.IsSkill).SelectMany(n => n.Effects).Where(e => e.IsSkill))
            Add(e.SkillName!, (int)e.Value);
        foreach (var sub in c.Equipment.SelectMany(i => i.SubSkills))
            if (BonusRegex().Match(sub) is { Success: true } m)
                Add(m.Groups["name"].Value, int.Parse(m.Groups["n"].Value));
        return bonus;
    }

    sealed class Row(SkillInfo skill, SkillKnowledge? known, int bonus, int baseLevel, double priority, int target)
    {
        public SkillInfo Skill { get; } = skill;
        public SkillKnowledge? Known { get; } = known;
        public int Bonus { get; } = bonus;
        public int Base { get; } = baseLevel;
        public double Priority { get; } = priority;
        public int Target { get; } = target;
        public int Rec { get; set; } = baseLevel;
    }

    public SkillPlan Plan(CharacterSnapshot c, ResolvedProfile p, PlayerInputs inputs)
    {
        var set = db.Settings.Skills;
        var bonus = BonusLevels(c);
        var openable = c.Boards.SelectMany(b => b.Nodes).Where(n => !n.Open && n.IsSkill)
            .SelectMany(n => n.Effects.Where(e => e.IsSkill)).GroupBy(e => Norm(e.SkillName!))
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = c.Skills.Where(s => KindOf(s) != SkillKind.Stigma).Select(s =>
        {
            var known = p.Skill(s.Name);
            var b = bonus.GetValueOrDefault(Norm(s.Name));
            var priority = Priority(s, p);
            var target = known?.Target.GetValueOrDefault(p.Mode) is > 0 and var t ? t : priority >= 0.3 ? set.BaseMax : 1;
            return new Row(s, known, b, s.Acquired ? Math.Clamp(s.Level - b, 1, set.BaseMax) : 0, priority, Math.Clamp(target, 1, set.BaseMax));
        }).ToList();

        var spent = rows.Where(r => r.Skill.Acquired).Sum(r => (r.Base - 1) * set.CostPerLevel);
        var free = inputs.UnspentSkillPoints / Math.Max(1, set.CostPerLevel);
        var acquired = rows.Where(r => r.Skill.Acquired).ToList();

        // 1. Les compétences sous leur niveau visé, de la plus importante à la moins importante, prennent les points
        //    libres, puis ceux des compétences nettement moins utiles (au-dessus de leur cible d'abord).
        foreach (var need in acquired.Where(r => r.Rec < r.Target).OrderByDescending(r => r.Priority))
            while (need.Rec < need.Target)
            {
                if (free > 0) { free--; need.Rec++; continue; }
                var donor = acquired.Where(r => r != need && r.Rec > 1 && r.Priority + set.ChangeThreshold <= need.Priority)
                    .OrderBy(r => r.Rec > r.Target ? 0 : 1).ThenBy(r => r.Priority).FirstOrDefault();
                if (donor is null) break;
                donor.Rec--;
                need.Rec++;
            }

        // 2. Points encore libres : vers les compétences utiles pas encore au maximum.
        foreach (var r in acquired.Where(r => r.Priority >= 0.3).OrderByDescending(r => r.Priority))
            while (free > 0 && r.Rec < set.BaseMax) { free--; r.Rec++; }

        var skills = rows.Select(r => new SkillAdvice(r.Skill, KindOf(r.Skill), r.Known?.Role ?? DefaultRole(r.Skill),
                r.Known?.Why ?? DefaultWhy(r.Skill, p), r.Known is not null, r.Known?.Unverified ?? false, r.Priority,
                r.Base, r.Bonus, r.Target, r.Rec, openable.GetValueOrDefault(Norm(r.Skill.Name))))
            .OrderBy(s => s.Kind).ThenByDescending(s => s.Priority).ToList();

        // Stigmas : les plus utiles occupent les emplacements, montés jusqu'au niveau visé.
        var st = db.Settings.Stigmas;
        var stigmaRows = c.Skills.Where(s => KindOf(s) == SkillKind.Stigma)
            .Select(s => (Skill: s, Known: p.Skill(s.Name), Priority: Priority(s, p)))
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.Skill.Level).ToList();
        var chosen = stigmaRows.Take(st.Slots).Select(x => x.Skill.Id).ToHashSet();
        var stigmas = stigmaRows.Select(x =>
        {
            var target = x.Known?.Target.GetValueOrDefault(p.Mode) is > 0 and var t ? t : x.Skill.Equipped ? 10 : 1;
            var recommended = chosen.Contains(x.Skill.Id);
            return new StigmaAdvice(x.Skill, x.Known?.Role ?? "Stigma", x.Known?.Why ?? DefaultWhy(x.Skill, p), x.Known is not null,
                x.Known?.Unverified ?? false, x.Priority, recommended, target,
                recommended ? st.ShardCost(x.Skill.Level, Math.Min(target, st.MaxLevel)) : 0);
        }).ToList();

        return new SkillPlan(skills, stigmas, spent, inputs.UnspentSkillPoints, p.Class.Skills.Count > 0, p.Class.SkillsSource);
    }

    static string DefaultRole(SkillInfo s) => KindOf(s) switch
    {
        SkillKind.Passive => "Passif",
        SkillKind.Stigma => "Stigma",
        _ => s.Equipped ? "Dans ta barre" : "Hors de ta barre",
    };

    string DefaultWhy(SkillInfo s, ResolvedProfile p)
    {
        if (p.Mode == GameMode.PvP && CommonPvp(s))
            return "Compétence commune qui libère des effets de contrôle : indispensable en JcJ.";
        return KindOf(s) switch
        {
            SkillKind.Passive => "Passif actif en permanence une fois appris (pas de fiche détaillée pour cette classe).",
            SkillKind.Stigma => s.Equipped ? "Stigma équipé (pas de fiche détaillée pour cette classe)." : "Stigma non équipé.",
            _ => s.Equipped
                ? "Dans ta barre : considérée comme faisant partie de ta rotation (pas de fiche détaillée pour cette classe)."
                : "Pas dans ta barre : les points investis ne servent à rien tant qu'elle n'est pas équipée.",
        };
    }

    static string Levels(SkillAdvice a) =>
        $"niveau acheté {a.Base} → {a.RecommendedBase} ({(a.Delta > 0 ? "+" : "")}{a.Delta} point{(Math.Abs(a.Delta) > 1 ? "s" : "")})";

    public List<Finding> Findings(SkillPlan plan, CharacterSnapshot c, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        var set = db.Settings.Skills;
        var mode = GameDatabase.ModeName(p.Mode);

        findings.Add(new Finding(Area.Skills, Severity.Info, $"Points de compétence : {plan.SpentPoints} investis + {plan.UnspentPoints} non dépensés",
            $"Chaque point monte une compétence d'un niveau, jusqu'au niveau {set.BaseMax} ; au-delà, seuls le Daevanion et l'équipement en donnent. " +
            $"La réinitialisation est gratuite : la répartition conseillée réutilise les {plan.Budget} points dont tu disposes. {set.PointSources}" +
            (plan.UnspentPoints == 0 ? " Si tu as des points non dépensés, indique-les (onglet Compétences ou --sp) pour qu'ils soient répartis." : "")));

        var remove = plan.ToRemove.ToList();
        var add = plan.ToAdd.ToList();
        string Line(SkillAdvice a) => $"« {a.Skill.Name} » ({a.Role}) : {Levels(a)}. Pourquoi : {a.Why}";
        if (remove.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Compétences : retire des points à {string.Join(", ", remove.Select(a => a.Skill.Name))}",
                string.Join("\n", remove.Select(Line)),
                $"Réinitialise tes compétences (gratuit) et refais la répartition {mode} de l'onglet Compétences : ces points servent davantage ailleurs.",
                60 + 5 * remove.Sum(a => -a.Delta) * remove.Max(a => 1 - a.Priority) / 3));
        if (add.Count > 0)
            findings.Add(new Finding(Area.Skills, remove.Count > 0 || add.Any(a => a.Priority >= 0.85) ? Severity.Important : Severity.Advice,
                $"Compétences : ajoute des points à {string.Join(", ", add.Select(a => a.Skill.Name))}",
                string.Join("\n", add.Select(Line)),
                plan.UnspentPoints > 0 && remove.Count == 0
                    ? "Place tes points non dépensés dans cet ordre."
                    : "Mets les points récupérés dans cet ordre (la plus importante d'abord).",
                55 + 5 * add.Sum(a => a.Delta * a.Priority) / 3));

        foreach (var a in plan.Skills.Where(a => a.Kind == SkillKind.Active && a.Priority >= 0.85 && a.Skill.Acquired && !a.Skill.Equipped))
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Équipe « {a.Skill.Name} »",
                $"{a.Role} : {a.Why}", $"Mets « {a.Skill.Name} » dans ta barre {mode}.", 75));
        foreach (var a in plan.Skills.Where(a => a.Priority >= 0.85 && !a.Skill.Acquired))
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Apprends « {a.Skill.Name} »",
                $"{a.Role} : {a.Why}", $"Apprends-la dès le niveau {a.Skill.NeedLevel}.", 70));

        // Au-delà du niveau 10 : spécialisations aux niveaux 12, 16, 20, atteignables seulement par le Daevanion et l'équipement.
        var specialties = set.SpecialtyLevels.Where(l => l > set.BaseMax).ToList();
        var nextSpecialty = plan.Skills.Where(a => a.Priority >= 0.8 && a.Skill.Acquired && a.OpenableNodes > 0)
            .Select(a => (Advice: a, Next: specialties.FirstOrDefault(l => l > a.TotalAfter)))
            .Where(x => x.Next > 0).OrderByDescending(x => x.Advice.Priority).Take(5).ToList();
        if (nextSpecialty.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Advice, "Spécialisations au-delà du niveau 10",
                $"Spécialisations aux niveaux {string.Join(", ", set.SpecialtyLevels)} (niveau total). Au-delà de {set.BaseMax}, seules les cases Daevanion et l'équipement montent une compétence : " +
                string.Join(" · ", nextSpecialty.Select(x => $"{x.Advice.Skill.Name} niv. {x.Advice.TotalAfter} → {x.Next} ({x.Advice.OpenableNodes} case(s) Daevanion encore fermée(s))")),
                "Privilégie ces cases de compétence dans ton Daevanion (le plan Daevanion en tient compte).", 35));

        // Stigmas.
        var st = db.Settings.Stigmas;
        var wanted = plan.Stigmas.Where(s => s.Recommended).ToList();
        var toUnequip = plan.Stigmas.Where(s => s.Skill.Equipped && !s.Recommended).ToList();
        var toEquip = wanted.Where(s => !s.Skill.Equipped).ToList();
        if (toEquip.Count > 0 || toUnequip.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Stigmas {mode} : {string.Join(", ", wanted.Select(s => s.Skill.Name))}",
                string.Join("\n", toUnequip.Select(s => $"Retire « {s.Skill.Name} » : {s.Why}")
                    .Concat(toEquip.Select(s => $"Mets « {s.Skill.Name} » ({s.Role}){(s.Skill.Acquired ? "" : " — à obtenir d'abord")} : {s.Why}"))),
                $"Échange tes stigmas (gratuit) : {st.Slots} emplacements.", 65));
        var shards = wanted.Sum(s => s.ShardsToTarget);
        if (shards > 0)
            findings.Add(new Finding(Area.Skills, Severity.Advice, $"Stigmas : {shards} fragments pour atteindre les niveaux visés",
                string.Join(" · ", wanted.Where(s => s.ShardsToTarget > 0).Select(s => $"{s.Skill.Name} niv. {s.Skill.Level} → {s.Target} ({s.ShardsToTarget} fragments)")) +
                $". Coût par niveau : 1 jusqu'au niveau 5, puis 2, 4 et 8 fragments. Sources : {st.ShardSources}",
                $"Monte d'abord « {wanted.Where(s => s.ShardsToTarget > 0).OrderByDescending(s => s.Priority).First().Skill.Name} ».", 40));

        if (!plan.HasKnowledge)
            findings.Add(new Finding(Area.Skills, Severity.Info, $"Pas de fiche de compétences pour {p.Class.Name}",
                "Sans fiche, l'analyse considère ta barre comme ta rotation : les actives hors barre sont jugées inutiles. " +
                "Ajoute le rôle de chaque compétence dans classes.json (« skills ») pour des conseils détaillés."));
        else if (plan.Skills.Concat<object>(plan.Stigmas).OfType<SkillAdvice>().Any(a => a.Unverified && a.Delta != 0))
            findings.Add(new Finding(Area.Skills, Severity.Info, "Certaines raisons sont « à valider »",
                $"Fiche de compétences : {plan.Source} Corrige-les dans classes.json si ton expérience de jeu dit autre chose."));
        return findings;
    }
}
