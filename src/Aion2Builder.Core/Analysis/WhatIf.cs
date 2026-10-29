namespace Aion2Builder.Analysis;

/// <summary>
/// « Et si… » : le score global si l'on suivait un conseil, avec exactement le calcul des notes (rien d'autre ne change).
/// Les stats ne sont pas simulées : une pièce remplacée apporte des stats qu'on ne connaît pas encore.
/// </summary>
public static class WhatIf
{
    public sealed record Gain(string Label, string Detail, int Overall, int Delta, Area Area);

    static AreaScores Score(AdviceReport r, GearPlan? gear = null, SkillPlan? skills = null, DaevanionPlan? dae = null) =>
        Advisor.Scores(r.Stats, gear ?? r.Gear, skills ?? r.Skills, dae ?? r.Daevanion, r.Character, r.Mode);

    static bool Todo(SlotPlan s) => s.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak;

    /// <summary>Les gains possibles, du plus grand au plus petit.</summary>
    public static List<Gain> Gains(AdviceReport r, int nextNodes = 20)
    {
        var now = r.Scores.Overall;
        var gains = new List<Gain>();

        var todo = r.Gear.Slots.Where(Todo).ToList();
        if (todo.Count > 0)
        {
            var overall = Score(r, gear: r.Gear with { Slots = r.Gear.Slots.Select(s => Todo(s) ? s with { Status = SlotStatus.Ok } : s).ToList() }).Overall;
            gains.Add(new Gain(Fmt.Plural(todo.Count, "pièce remise à niveau", "pièces remises à niveau"), string.Join(", ", todo.OrderByDescending(s => s.Priority).Select(s => s.SlotName)),
                overall, overall - now, Area.Gear));
        }

        var moved = r.Skills.Skills.Count(a => a.RecommendedBase != a.Base);
        if (moved > 0)
        {
            var overall = Score(r, skills: r.Skills with { Skills = r.Skills.Skills.Select(a => a with { Base = a.RecommendedBase }).ToList() }).Overall;
            gains.Add(new Gain("les compétences conseillées", $"{Fmt.Plural(moved, "compétence change", "compétences changent")} de niveau", overall, overall - now, Area.Skills));
        }

        var steps = r.Daevanion.Steps.Take(nextNodes).ToList();
        if (steps.Count > 0)
        {
            var added = steps.GroupBy(s => s.Board.Id).ToDictionary(g => g.Key, g => g.Count());
            var boards = r.Daevanion.Boards.Select(b => added.TryGetValue(b.Board.Id, out var n) ? b with { Board = b.Board with { OpenNodes = b.Board.OpenNodes + n } } : b).ToList();
            var overall = Score(r, dae: r.Daevanion with { Boards = boards }).Overall;
            gains.Add(new Gain($"les {steps.Count} prochaines cases Daevanion", $"{Fmt.Num(steps[^1].Cumulative)} points", overall, overall - now, Area.Daevanion));
        }
        return gains.OrderByDescending(g => g.Delta).ToList();
    }

    /// <summary>Score global si cette seule pièce passait au niveau du palier.</summary>
    public static int SlotFixed(AdviceReport r, string slot) =>
        Score(r, gear: r.Gear with { Slots = r.Gear.Slots.Select(s => s.Slot == slot && Todo(s) ? s with { Status = SlotStatus.Ok } : s).ToList() }).Overall;
}
