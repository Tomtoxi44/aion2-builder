using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Point d'entrée : analyse complète d'un personnage pour un mode (JcE ou JcJ).</summary>
public sealed class Advisor(GameDatabase db)
{
    public AdviceReport Analyze(CharacterSnapshot c, GameMode mode, PlayerInputs? inputs = null)
    {
        inputs ??= new PlayerInputs();
        var profile = db.Resolve(c.ClassCode, mode);
        var stats = new StatAnalyzer(db);
        var sp = stats.Profile(c, profile);

        var gearAdvisor = new GearAdvisor(db, stats);
        var gear = gearAdvisor.Plan(c, profile, sp);
        var skillAdvisor = new SkillAdvisor(db);
        var skills = skillAdvisor.Plan(c, profile, inputs);
        var planner = new DaevanionPlanner(db, stats);
        var daevanion = planner.Plan(c, profile, sp, inputs);

        var findings = new List<Finding>();
        findings.AddRange(stats.Findings(sp, profile, c));
        findings.AddRange(gearAdvisor.Findings(gear, c, profile));
        findings.AddRange(skillAdvisor.Findings(skills, c, profile));
        findings.AddRange(planner.Findings(daevanion, c, profile));
        if (profile.Class.Notes.Count > 0)
            findings.Add(new Finding(Area.General, Severity.Info, $"Repères {profile.Class.Name} ({profile.Role.Name})", string.Join(" ", profile.Class.Notes)));

        var ordered = findings.OrderBy(f => f.Severity).ThenByDescending(f => f.Priority).ToList();
        return new AdviceReport(c, profile, mode, sp, gear, skills, daevanion, ordered, Scores(sp, gear, skills, daevanion, c, mode));
    }

    static AreaScores Scores(StatProfile sp, GearPlan gear, SkillPlan skills, DaevanionPlan dae, CharacterSnapshot c, GameMode mode)
    {
        var balance = 1 - sp.Categories.Sum(x => Math.Abs(x.Gap)) / 2;
        var statScore = sp.TotalBudget > 0 ? 100 * (0.6 * sp.Efficiency + 0.4 * balance) : 0;

        var required = gear.Slots.Where(s => !(s.Status == SlotStatus.Empty && s.Priority <= 1)).ToList();
        var gearScore = required.Count == 0 ? 0 : 100 * required.Average(s => s.Status switch
        {
            SlotStatus.Ok or SlotStatus.Enchant => 1.0,
            SlotStatus.Weak => 0.5,
            SlotStatus.Replace => 0.25,
            _ => 0.0,
        });

        // Part des niveaux visés atteints, pondérée par l'importance de chaque compétence.
        var useful = skills.Skills.Where(a => a.Skill.Acquired && a.Priority > 0).ToList();
        var skillScore = useful.Count == 0 ? 0 : 100 * useful.Sum(a => Math.Min(a.Base, a.Target) * a.Priority) / useful.Sum(a => a.Target * a.Priority)
                         - 15 * skills.Skills.Count(a => a.Kind == SkillKind.Active && a.Priority >= 0.85 && a.Skill.Acquired && !a.Skill.Equipped);

        var boards = dae.Boards.Where(b => b.Relevant && b.Board.Unlocked && b.Board.TotalNodes > 0).ToList();
        var daeScore = boards.Count == 0 ? 0 : boards.Average(b => b.Board.OpenPercent);

        static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 100));
        return new AreaScores(Clamp(statScore), Clamp(gearScore), Clamp(skillScore), Clamp(daeScore));
    }
}
