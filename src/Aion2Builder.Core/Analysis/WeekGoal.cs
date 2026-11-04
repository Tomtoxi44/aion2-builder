using System.Globalization;
using System.Text.RegularExpressions;
using Aion2Builder.GameData;

namespace Aion2Builder.Analysis;

/// <summary>
/// Une action de la semaine : ce qu'il faut faire, le temps estimé (heures), ce qu'elle rapporte (points de note globale,
/// niveau d'objet, autre gain en mots) et les conseils d'où elle vient (le premier sert à ouvrir l'écran où agir).
/// </summary>
public sealed record WeekAction(string Title, string? Action, Area Area, double Hours, int Note, int ItemLevel, string? Other,
    IReadOnlyList<Finding> Findings, double Value)
{
    public Finding Finding => Findings[0];
}

/// <summary>Les actions retenues pour un temps de jeu, la note et le niveau d'objet de départ, et le palier de la feuille de route en vue.</summary>
public sealed record WeekPlan(double Budget, List<WeekAction> Actions, int NoteNow, int ItemLevelNow, GearScoreGate? Next)
{
    public double Hours => Actions.Sum(a => a.Hours);
    public int Note => Actions.Sum(a => a.Note);
    public int ItemLevel => Actions.Sum(a => a.ItemLevel);
    /// <summary>Le palier suivant de la feuille de route, s'il est atteint avec ces actions.</summary>
    public GearScoreGate? Reaches => Next is { } g && ItemLevelNow + ItemLevel >= g.Score ? g : null;
}

/// <summary>
/// Objectif de la semaine : parmi les actions du plan d'action (les plus rentables d'abord), les trois qui rapportent le plus
/// dans le temps de jeu donné. Chaque action a un temps estimé selon ce qu'elle demande (settings.json → week) ; le choix
/// garde la combinaison de trois actions au plus qui tient dans le temps et dont la valeur (priorité du plan d'action) est
/// la plus grande. Gains : note globale par le calcul des « Et si », niveau d'objet par la feuille de route.
/// </summary>
public static partial class WeekGoal
{
    [GeneratedRegex(@"≈\s*(\d+(?:[.,]\d+)?)\s*%\s*de dégâts")]
    private static partial Regex DamageRegex();

    /// <summary>Poids d'un conseil dans le plan d'action (même calcul que <see cref="AdviceReport.ActionPlan"/>).</summary>
    static double Weight(Finding f) => f.Priority * f.Severity switch { Severity.Critical => 1.5, Severity.Important => 1.2, _ => 1.0 };

    public static WeekPlan Plan(GameDatabase db, AdviceReport r, GearRoadmapPlan route, double budget, Func<Finding, bool>? done = null)
    {
        var week = db.Settings.Week;
        var now = r.Scores.Overall;
        var todo = r.ActionPlan.Where(f => done?.Invoke(f) != true).ToList();
        var candidates = new List<WeekAction>();
        var used = new HashSet<Finding>();

        // Combat : tous les points se corrigent à la même séance (mannequin ou boss) ; une seule action.
        var combat = todo.Where(f => f.Need == Need.Combat).ToList();
        // Compétences : « ajoute » et « retire » des points sont la même réinitialisation gratuite.
        var skills = todo.Where(f => f.Area == Area.Skills && f.Title.StartsWith("Compétences :", StringComparison.Ordinal)).ToList();

        foreach (var f in todo)
        {
            if (used.Contains(f)) continue;
            if (combat.Contains(f))
            {
                used.UnionWith(combat);
                var dmg = combat.Select(x => DamageRegex().Match(x.Detail)).Where(m => m.Success)
                    .Sum(m => double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
                candidates.Add(new WeekAction(
                    combat.Count == 1 ? f.Title : $"Combat : corrige ta rotation ({combat.Count} points)",
                    combat.Count == 1 ? f.Action : string.Join(" ", combat.Select(x => x.Action).Distinct().Take(3)),
                    Area.General, week.For(Need.Combat), 0, 0, dmg > 0 ? $"≈ +{Fmt.Num(dmg)} % de dégâts" : "rotation",
                    combat, combat.Max(Weight) * (1 + 0.1 * (combat.Count - 1))));
                continue;
            }
            if (skills.Contains(f))
            {
                used.UnionWith(skills);
                var note = WhatIf.SkillsFollowed(r) - now;
                candidates.Add(new WeekAction("Compétences : refais la répartition conseillée",
                    "Réinitialise tes compétences (gratuit) et place les points comme dans l'écran Compétences.",
                    Area.Skills, week.For(Need.Free), note, 0, note > 0 ? null : "niveaux visés", skills, skills.Max(Weight)));
                continue;
            }
            used.Add(f);
            var need = f.Need ?? Need.Info;
            var hours = week.For(need);
            int noteGain = 0, itemLevel = 0;
            string? other = null;
            switch (f.Area)
            {
                case Area.Gear when r.Gear.Slots.FirstOrDefault(x => f.Title.StartsWith(x.SlotName, StringComparison.Ordinal)) is { } slot:
                    noteGain = WhatIf.SlotFixed(r, slot.Slot) - now;
                    itemLevel = route.Slots.FirstOrDefault(x => x.Slot == slot.Slot)?.Gain ?? 0;
                    break;
                case Area.Enchant:
                    itemLevel = route.Enchants.Where(e => e.Verdict == EnchantVerdict.Raise).Sum(e => e.Gain);
                    other = itemLevel > 0 ? null : "enchantement";
                    break;
                case Area.Daevanion:
                    // Prochaines cases avec tes points libres, ou réorganisation : quelques clics dans le jeu.
                    hours = week.For(Need.Free);
                    var pool = r.Daevanion.ByPool.FirstOrDefault(g => f.Title.EndsWith(DaevanionPoolTitle(db, g.Key), StringComparison.Ordinal));
                    if (pool?.Where(x => x.Affordable).ToList() is { Count: > 0 } affordable) noteGain = WhatIf.NodesOpened(r, affordable) - now;
                    else other = "valeur des cases";
                    break;
                case Area.Stats:
                    other = "stats";
                    break;
                default:
                    other = Fmt.Area(f.Area).ToLowerInvariant();
                    break;
            }
            candidates.Add(new WeekAction(f.Title, f.Action, f.Area, hours, noteGain, itemLevel, noteGain > 0 || itemLevel > 0 ? null : other, [f], Weight(f)));
        }

        // Les 12 premières seulement : au-delà, des conseils de peu de valeur ; la meilleure combinaison de 3 au plus qui tient.
        var pick = Best(candidates.Take(12).ToList(), budget);
        return new WeekPlan(budget, pick.OrderByDescending(a => a.Value).ToList(), now, r.Character.ItemLevel, route.Next);
    }

    static string DaevanionPoolTitle(GameDatabase db, string pool) => db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool, pool);

    /// <summary>La combinaison de 3 actions au plus qui tient dans le temps, de plus grande valeur (à valeur égale, la plus courte).</summary>
    static List<WeekAction> Best(List<WeekAction> list, double budget)
    {
        var best = new List<WeekAction>();
        var bestValue = 0.0;
        var bestHours = 0.0;
        void Try(List<WeekAction> combo)
        {
            var hours = combo.Sum(a => a.Hours);
            if (hours > budget + 1e-9) return;
            var value = combo.Sum(a => a.Value);
            if (value > bestValue + 1e-9 || Math.Abs(value - bestValue) <= 1e-9 && hours < bestHours)
                (best, bestValue, bestHours) = (combo, value, hours);
        }
        for (var i = 0; i < list.Count; i++)
        {
            Try([list[i]]);
            for (var j = i + 1; j < list.Count; j++)
            {
                Try([list[i], list[j]]);
                for (var k = j + 1; k < list.Count; k++) Try([list[i], list[j], list[k]]);
            }
        }
        return best;
    }
}
