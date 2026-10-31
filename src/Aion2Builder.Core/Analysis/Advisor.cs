using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Point d'entrée : analyse complète d'un personnage pour un mode (JcE ou JcJ).</summary>
public sealed class Advisor(GameDatabase db)
{
    /// <param name="pets">Collection de familiers (Pet Meter via Aion 2 Todo), liée au compte et non au personnage ; null si non reliée.</param>
    /// <param name="combats">Combats gardés, les plus récents d'abord : parts de dégâts réelles, et conseils du dernier en JcE.</param>
    public AdviceReport Analyze(CharacterSnapshot c, GameMode mode, PlayerInputs? inputs = null, PetCollection? pets = null, IReadOnlyList<Combat.CombatLog>? combats = null)
    {
        inputs ??= new PlayerInputs();
        var profile = db.Resolve(c.ClassCode, mode, inputs.UsesDesigned) with { PeerBasis = inputs.Comparison, MyPeers = inputs.MyPeers };
        var petAdvisor = new PetAdvisor(db);
        var stats = new StatAnalyzer(db, petAdvisor.Sources(pets));
        var sp = stats.Profile(c, profile);

        var gearAdvisor = new GearAdvisor(db, stats);
        var gear = gearAdvisor.Plan(c, profile, sp);
        var skillAdvisor = new SkillAdvisor(db);
        var skills = skillAdvisor.Plan(c, profile, inputs, combats);
        var planner = new DaevanionPlanner(db, stats);
        var daevanion = planner.Plan(c, profile, sp, inputs);
        var engravingAdvisor = new EngravingAdvisor(db);
        var engravings = engravingAdvisor.Plan(c, profile, gear, sp);
        var petPlan = petAdvisor.Plan(pets, profile, sp);

        // Ce que chaque conseil demande (vue « par ressource » de Détails) : celui de son analyse, sauf s'il le précise.
        static IEnumerable<Finding> As(IEnumerable<Finding> list, Func<Finding, Need> need) => list.Select(f => f with { Need = f.Need ?? need(f) });
        var findings = new List<Finding>();
        findings.AddRange(As(stats.Findings(sp, profile, c), _ => Need.Rolls));
        findings.AddRange(As(gearAdvisor.Findings(gear, c, profile), f => f.Area == Area.Enchant ? Need.Enchant : Need.Farm));
        findings.AddRange(As(skillAdvisor.Findings(skills, c, profile), _ => Need.Free));
        findings.AddRange(As(planner.Findings(daevanion, c, profile), _ => Need.Points));
        findings.AddRange(As(engravingAdvisor.Findings(engravings, profile), _ => Need.Engraving));
        findings.AddRange(As(petAdvisor.Findings(petPlan, profile), _ => Need.Pets));
        if (combats?.FirstOrDefault(l => l.Kind is null or "boss" or "dummy") is { Skills.Count: > 0 } combat && mode == GameMode.PvE)
            findings.AddRange(new Combat.CombatAnalyzer(db).Analyze(combat, profile).Findings.Where(f => f.Severity <= Severity.Advice)
                .Select(f => f with { Title = $"Combat : {f.Title}", Need = Need.Combat }));
        if (profile.Class.Notes.Count > 0)
            findings.Add(new Finding(Area.General, Severity.Info, $"Repères {profile.Class.Name} ({profile.Role.Name})", string.Join(" ", profile.Class.Notes)) { Need = Need.Info });

        var ordered = findings.OrderBy(f => f.Severity).ThenByDescending(f => f.Priority).ToList();
        return new AdviceReport(c, profile, mode, sp, gear, skills, daevanion, engravings, petPlan, ordered, Scores(sp, gear, skills, daevanion, c, mode));
    }

    public static AreaScores Scores(StatProfile sp, GearPlan gear, SkillPlan skills, DaevanionPlan dae, CharacterSnapshot c, GameMode mode)
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
                         - 15 * skills.Skills.Count(a => a.Kind == SkillKind.Active && a.Priority >= 0.85 && a.Skill.Acquired && !a.InBar);

        var boards = dae.Boards.Where(b => b.Relevant && b.Board.Unlocked && b.Board.TotalNodes > 0).ToList();
        var daeScore = boards.Count == 0 ? 0 : boards.Average(b => b.Board.OpenPercent);

        static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 100));
        return new AreaScores(Clamp(statScore), Clamp(gearScore), Clamp(skillScore), Clamp(daeScore));
    }
}
