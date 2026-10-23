using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Compétences : les actives équipées forment la rotation, les passives apprises sont toujours actives.
/// On monte en priorité les compétences clés de la classe et celles en retard sur la moyenne,
/// et on signale les niveaux investis dans des compétences non utilisées.
/// </summary>
public sealed class SkillAdvisor(GameDatabase db)
{
    static string Norm(string s) => GameDatabase.NormalizeLabel(s);

    public HashSet<string> CoreSet(ResolvedProfile p) =>
        p.CoreSkills.Concat(p.Mode == GameMode.PvP ? db.Settings.Skills.CommonPvpSkills : []).Select(Norm).ToHashSet();

    public SkillPlan Plan(CharacterSnapshot c, ResolvedProfile p)
    {
        var set = db.Settings.Skills;
        var core = CoreSet(p);
        var actives = c.Skills.Where(s => s.IsActive && s.Equipped).ToList();
        var passives = c.Skills.Where(s => s.IsPassive && s.Acquired).ToList();
        var avgActive = actives.Count == 0 ? 0 : actives.Average(s => s.Level);
        var avgPassive = passives.Count == 0 ? 0 : passives.Average(s => s.Level);

        // Cases Daevanion encore fermées qui donnent +niveau à chaque compétence.
        var openable = c.Boards.SelectMany(b => b.Nodes).Where(n => !n.Open && n.IsSkill)
            .SelectMany(n => n.Effects.Where(e => e.IsSkill)).GroupBy(e => Norm(e.SkillName!))
            .ToDictionary(g => g.Key, g => g.Count());

        var list = new List<SkillAdvice>();
        foreach (var s in c.Skills)
        {
            var isCore = core.Contains(Norm(s.Name));
            var nodes = openable.GetValueOrDefault(Norm(s.Name));
            var nodesText = nodes > 0 ? $" {nodes} case(s) Daevanion encore fermée(s) pour la monter." : "";
            SkillAdvice advice;
            if (isCore && !s.Acquired)
                advice = new(s, SkillStatus.CoreMissing, true, $"Compétence clé pas encore apprise (niveau requis {s.NeedLevel}).", nodes, 70);
            else if (isCore && s.IsActive && !s.Equipped)
                advice = new(s, SkillStatus.CoreNotEquipped, true, CommonPvp(s)
                    ? "Compétence commune indispensable en JcJ (libération des effets de contrôle) : à mettre dans ta barre."
                    : "Compétence clé de la classe absente de ta barre : à équiper.", nodes, 75);
            else if (s.IsActive && s.Equipped && avgActive - s.Level >= set.LowLevelGap
                     || s.IsPassive && s.Acquired && avgPassive - s.Level >= set.LowLevelGap)
            {
                var avg = s.IsActive ? avgActive : avgPassive;
                advice = new(s, SkillStatus.LevelUp, isCore,
                    $"Niveau {s.Level} contre {Fmt.Num(avg)} en moyenne pour tes {(s.IsActive ? "actives équipées" : "passives")}.{nodesText}",
                    nodes, 25 + Math.Min(25, 2.5 * (avg - s.Level)) + (isCore ? 15 : 0));
            }
            else if (s.IsActive && s.Acquired && !s.Equipped && s.Level >= set.WastedLevelThreshold)
                advice = new(s, SkillStatus.Wasted, isCore, $"Niveau {s.Level} mais pas dans ta barre : ressources immobilisées.", nodes, 0);
            else
                advice = new(s, SkillStatus.Ok, isCore, isCore ? $"Compétence clé.{nodesText}" : "", nodes, isCore ? 10 : 0);
            list.Add(advice);
        }
        return new SkillPlan(list.OrderByDescending(a => a.Priority).ThenBy(a => a.Skill.NeedLevel).ToList(), avgActive, avgPassive);
    }

    bool CommonPvp(SkillInfo s) => db.Settings.Skills.CommonPvpSkills.Any(n => Norm(n) == Norm(s.Name));

    public List<Finding> Findings(SkillPlan plan, CharacterSnapshot c, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        foreach (var a in plan.Skills.Where(a => a.Status is SkillStatus.CoreNotEquipped or SkillStatus.CoreMissing))
            findings.Add(new Finding(Area.Skills, Severity.Important, $"{a.Skill.Name} : {Fmt.Status(a.Status).ToLowerInvariant()}", a.Advice,
                a.Status == SkillStatus.CoreNotEquipped ? $"Équipe « {a.Skill.Name} »." : $"Apprends « {a.Skill.Name} » dès le niveau {a.Skill.NeedLevel}.",
                a.Priority));

        var levelUp = plan.Skills.Where(a => a.Status == SkillStatus.LevelUp).Take(5).ToList();
        if (levelUp.Count > 0)
            findings.Add(new Finding(Area.Skills, levelUp.Any(a => a.IsCore) ? Severity.Important : Severity.Advice,
                $"Compétences à monter : {string.Join(", ", levelUp.Select(a => a.Skill.Name))}",
                string.Join(" · ", levelUp.Select(a => $"{a.Skill.Name} niv. {a.Skill.Level}" + (a.OpenableNodes > 0 ? $" ({a.OpenableNodes} case(s) Daevanion)" : ""))),
                "Monte-les avant de pousser les compétences déjà hautes : c'est là que chaque niveau rapporte le plus.",
                levelUp.Max(a => a.Priority)));

        var wasted = plan.Skills.Where(a => a.Status == SkillStatus.Wasted).ToList();
        if (wasted.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Info, "Niveaux investis dans des compétences non équipées",
                string.Join(", ", wasted.Select(a => $"{a.Skill.Name} niv. {a.Skill.Level}")) + " : n'y mets plus de ressources sauf si tu changes de rotation."));

        var known = c.Skills.Select(s => GameDatabase.NormalizeLabel(s.Name)).ToHashSet();
        var unknown = CoreSet(p).Where(n => !known.Contains(n)).ToList();
        if (unknown.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Info, "Compétences clés introuvables",
                $"Pas trouvées sur la fiche : {string.Join(", ", unknown)}. Vérifie leur nom exact dans classes.json."));

        if (p.CoreSkills.Count == 0)
            findings.Add(new Finding(Area.Skills, Severity.Info, $"Pas de compétences clés définies pour {p.Class.Name}",
                "L'analyse se base sur ta barre actuelle. Ajoute les noms français des compétences clés dans classes.json (coreSkills) pour des conseils plus précis."));
        return findings;
    }
}
