using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Daevanion dans une simulation : rien, les prochaines cases, la réorganisation conseillée ou la route conçue.</summary>
public enum DaevanionChoice { None, Next, Respec, Route }

/// <param name="NextNodes">Nombre de prochaines cases ouvertes par réserve (choix <see cref="DaevanionChoice.Next"/>).</param>
/// <param name="Designed">Build suivi pendant la simulation (null = celui du personnage).</param>
/// <param name="GearAtStage">Pièces vides, à remplacer ou en retard comptées au niveau du palier (note seulement).</param>
/// <param name="ArcanaGrade">Grade visé pour les cartes d'arcane plus basses (null = telles quelles).</param>
/// <param name="ArcanaSlot">La seule carte changée (Arcana1…5) ; null = toutes celles sous <paramref name="ArcanaGrade"/>.</param>
public sealed record SimulationOptions(DaevanionChoice Daevanion = DaevanionChoice.None, int NextNodes = 20, bool Skills = false,
    bool? Designed = null, bool GearAtStage = false, string? ArcanaGrade = null, string? ArcanaSlot = null)
{
    public bool Any => Daevanion != DaevanionChoice.None || Skills || Designed is not null || GearAtStage || ArcanaGrade is not null;
}

/// <summary>
/// Une carte d'arcane passée à un grade plus haut : la carte avant et après, sa divinité et ses points, et les compétences
/// bonus qu'elle gagnerait (tirées au hasard : non simulées).
/// </summary>
public sealed record ArcanaChange(EquippedItem Before, EquippedItem After, string? Divinity, int DivineBefore, int DivineAfter, int ExtraSkills);

/// <summary>
/// « Et si… » : applique des changements à une copie du personnage (cases Daevanion ouvertes ou retirées, niveaux de
/// compétences, build suivi) et relance toute l'analyse. Les stats sont recalculées à partir des effets réels des cases ;
/// une pièce « au niveau du palier » ne change que la note d'équipement, ses stats futures étant inconnues. Une carte d'arcane
/// d'un grade plus haut prend le niveau d'objet et les points de stat divine de ce grade (relevés sur des joueurs), sur sa divinité.
/// </summary>
public static partial class Simulator
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<name>.+\])\s*\+?(?<value>\d+)")]
    private static partial System.Text.RegularExpressions.Regex DivineRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+(?:[.,]\d+)?(?=\s*%?\s*$)")]
    private static partial System.Text.RegularExpressions.Regex LastNumberRegex();

    /// <summary>Les cartes que <see cref="SimulationOptions.ArcanaGrade"/> change (cartes posées sous ce grade ; un emplacement vide n'est pas simulé).</summary>
    public static List<ArcanaChange> ArcanaChanges(GameDatabase db, CharacterSnapshot c, SimulationOptions o)
    {
        var rules = db.Progression.Arcana;
        if (o.ArcanaGrade is not { } target || !rules.Grades.TryGetValue(target, out var to)) return [];
        var rank = db.Grade(target).Rank;
        return c.Equipment
            .Where(i => i.Slot.StartsWith("Arcana", StringComparison.OrdinalIgnoreCase) && (o.ArcanaSlot is null || i.Slot == o.ArcanaSlot) && db.Grade(i.Grade).Rank < rank)
            .Select(i =>
            {
                // « Illusion [Kaisinel] 12 » : la divinité de la carte reste la même, ses points passent à ceux du grade.
                var divine = i.MainStats.Select(e => (Effect: e, Match: DivineRegex().Match(e.Raw))).FirstOrDefault(x => !x.Effect.IsStat && x.Match.Success);
                var name = divine.Match?.Groups["name"].Value;
                var points = divine.Effect is null ? 0 : (int)divine.Effect.Value;
                var after = i with
                {
                    Grade = target,
                    ItemLevel = to.ItemLevel,
                    MainStats = i.MainStats.Select(e => e == divine.Effect ? e with { Raw = $"{name} {to.Divine}", Value = to.Divine } : e).ToList(),
                };
                var extra = Math.Max(0, Math.Min(rules.MaxSkills, to.Skills + i.EnchantLevel) - i.SubSkills.Count);
                return new ArcanaChange(i, after, name, points, divine.Effect is null ? 0 : to.Divine, extra);
            })
            .ToList();
    }

    /// <summary>
    /// Le personnage avec ces cartes : pièces remplacées, score d'équipement relevé de leurs niveaux d'objet, et total de chaque
    /// divinité (stats principales) avec ses effets ramenés au nouveau total.
    /// </summary>
    static CharacterSnapshot WithArcana(CharacterSnapshot c, List<ArcanaChange> changes)
    {
        if (changes.Count == 0) return c;
        var bySlot = changes.ToDictionary(x => x.Before.Slot, x => x.After);
        var points = changes.Where(x => x.Divinity is not null).GroupBy(x => x.Divinity!).ToDictionary(g => g.Key, g => g.Sum(x => x.DivineAfter - x.DivineBefore));
        var primary = c.PrimaryStats.Select(p =>
        {
            if (!points.TryGetValue(p.Name, out var add) || add == 0 || p.Value <= 0) return p;
            var factor = (p.Value + add) / (double)p.Value;
            return p with
            {
                Value = p.Value + add,
                Effects = p.Effects.Select(e =>
                {
                    var value = Math.Round(e.Value * factor, 1);
                    return e with { Value = value, Raw = LastNumberRegex().Replace(e.Raw, value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)) };
                }).ToList(),
            };
        }).ToList();
        return c with
        {
            Equipment = c.Equipment.Select(i => bySlot.GetValueOrDefault(i.Slot) ?? i).ToList(),
            PrimaryStats = primary,
            ItemLevel = c.ItemLevel + changes.Sum(x => x.After.ItemLevel - x.Before.ItemLevel),
        };
    }

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
        // Une case de compétence ouverte ou retirée change le niveau total de la compétence (le niveau acheté, lui, ne bouge pas).
        var bonus = new Dictionary<string, int>();
        foreach (var n in c.Boards.SelectMany(b => b.Nodes))
        {
            var sign = !n.Open && open.Contains(n.Id) ? 1 : n.Open && close.Contains(n.Id) ? -1 : 0;
            if (sign == 0) continue;
            foreach (var e in n.Effects.Where(e => e.IsSkill))
            {
                var key = GameDatabase.NormalizeLabel(e.SkillName!);
                bonus[key] = bonus.GetValueOrDefault(key) + sign * (int)e.Value;
            }
        }
        if (bonus.Count > 0)
            skills = skills.Select(s => s.Acquired && bonus.TryGetValue(GameDatabase.NormalizeLabel(s.Name), out var d) && d != 0 ? s with { Level = s.Level + d } : s).ToList();

        var simulated = WithArcana(c, ArcanaChanges(db, c, o)) with { Boards = boards, Skills = skills };
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
