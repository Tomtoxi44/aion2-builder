using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Daevanion dans une simulation : rien, les prochaines cases, la réorganisation conseillée ou la route conçue.</summary>
public enum DaevanionChoice { None, Next, Respec, Route }

/// <param name="NextNodes">Nombre de prochaines cases ouvertes par réserve (choix <see cref="DaevanionChoice.Next"/>).</param>
/// <param name="Designed">Build suivi pendant la simulation (null = celui du personnage).</param>
/// <param name="GearAtStage">Pièces vides, à remplacer ou en retard comptées au niveau du palier (note seulement).</param>
public sealed record SimulationOptions(DaevanionChoice Daevanion = DaevanionChoice.None, int NextNodes = 20, bool Skills = false,
    bool? Designed = null, bool GearAtStage = false)
{
    public bool Any => Daevanion != DaevanionChoice.None || Skills || Designed is not null || GearAtStage;
}

/// <summary>
/// « Et si… » : applique des changements à une copie du personnage (cases Daevanion ouvertes ou retirées, niveaux de
/// compétences, build suivi) et relance toute l'analyse. Les stats sont recalculées à partir des effets réels des cases ;
/// une pièce « au niveau du palier » ne change que la note d'équipement, ses stats futures étant inconnues.
/// </summary>
public static class Simulator
{
    public static AdviceReport Run(GameDatabase db, AdviceReport before, PlayerInputs inputs, PetCollection? pets, SimulationOptions o)
    {
        var c = before.Character;
        var open = new HashSet<long>();
        var close = new HashSet<long>();
        var unspent = new Dictionary<string, int>();
        foreach (var b in before.Daevanion.Budgets) unspent[b.Pool] = b.Unspent;

        switch (o.Daevanion)
        {
            case DaevanionChoice.Next:
                foreach (var pool in before.Daevanion.ByPool)
                {
                    var taken = pool.Take(o.NextNodes).ToList();
                    open.UnionWith(taken.Select(s => s.Node.Id));
                    unspent[pool.Key] = Math.Max(0, unspent.GetValueOrDefault(pool.Key) - (int)Math.Round(taken.Sum(s => s.Cost)));
                }
                break;
            case DaevanionChoice.Respec:
                foreach (var r in before.Daevanion.Respecs.Where(r => r.Remove.Count > 0))
                {
                    close.UnionWith(r.Remove.Select(x => x.Node.Id));
                    open.UnionWith(r.Add.Select(x => x.Node.Id));
                }
                break;
            case DaevanionChoice.Route:
                foreach (var route in before.Daevanion.Routes.Where(r => r.Nodes.Count > 0))
                {
                    var keep = route.InBudget.Select(x => x.Node.Id).ToHashSet();
                    open.UnionWith(keep);
                    close.UnionWith(before.Daevanion.Boards.Where(b => b.Pool == route.Pool)
                        .SelectMany(b => b.Board.Nodes.Where(n => n.Open && !n.IsStart && !keep.Contains(n.Id)).Select(n => n.Id)));
                    unspent[route.Pool] = 0; // la route utilise les points dépensés et non dépensés
                }
                break;
        }

        var boards = c.Boards.Select(b =>
        {
            var opened = b.Nodes.Count(n => !n.Open && open.Contains(n.Id));
            var closed = b.Nodes.Count(n => n.Open && close.Contains(n.Id));
            if (opened == 0 && closed == 0) return b;
            var nodes = b.Nodes.Select(n => !n.Open && open.Contains(n.Id) ? n with { Open = true } : n.Open && close.Contains(n.Id) ? n with { Open = false } : n).ToList();
            return b with { Nodes = nodes, OpenNodes = b.OpenNodes + opened - closed };
        }).ToList();

        var skills = c.Skills;
        if (o.Skills)
        {
            var levels = before.Skills.Skills.Where(a => a.Skill.Acquired).ToDictionary(a => a.Skill.Id, a => a.RecommendedBase + a.Bonus);
            skills = c.Skills.Select(s => levels.TryGetValue(s.Id, out var level) ? s with { Level = level } : s).ToList();
        }

        var simulated = c with { Boards = boards, Skills = skills };
        var simInputs = inputs with
        {
            UnspentSkillPoints = o.Skills ? 0 : inputs.UnspentSkillPoints,
            UnspentDaevanion = unspent.GetValueOrDefault(DaevanionPlanner.NormalPool, inputs.UnspentDaevanion),
            UnspentAriel = unspent.GetValueOrDefault("PvE", inputs.UnspentAriel),
            UnspentAzphel = unspent.GetValueOrDefault("PvP", inputs.UnspentAzphel),
            Build = o.Designed is { } designed ? (designed ? "designed" : "players") : inputs.Build,
        };
        var after = new Advisor(db).Analyze(simulated, before.Mode, simInputs, pets);
        if (!o.GearAtStage) return after;

        static bool Late(SlotPlan s) => s.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak;
        var gear = after.Gear with { Slots = after.Gear.Slots.Select(s => Late(s) ? s with { Status = SlotStatus.Ok } : s).ToList() };
        return after with { Scores = Advisor.Scores(after.Stats, gear, after.Skills, after.Daevanion, after.Character, after.Mode) };
    }

    /// <summary>Stats qui changent entre deux rapports (valeurs plates et en %), la plus pondérée d'abord.</summary>
    public static List<(StatTotal Before, StatTotal After)> StatChanges(AdviceReport before, AdviceReport after) =>
        before.Stats.Stats.Join(after.Stats.Stats, b => b.Def.Key, a => a.Def.Key, (b, a) => (Before: b, After: a))
            .Where(x => Math.Abs(x.Before.Flat - x.After.Flat) > 1e-6 || Math.Abs(x.Before.Percent - x.After.Percent) > 1e-6)
            .OrderByDescending(x => x.After.Weight).ThenByDescending(x => Math.Abs(x.After.Budget - x.Before.Budget)).ToList();
}
