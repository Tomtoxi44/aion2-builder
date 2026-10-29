using System.Text;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Le rapport en Markdown, pour le garder ou le partager (Discord, forum, notes) : l'essentiel de chaque écran,
/// sans les tableaux complets.
/// </summary>
public static class ReportMarkdown
{
    public static string Write(AdviceReport r, GameDatabase db, PlayerInputs inputs)
    {
        var c = r.Character;
        var md = new StringBuilder();
        void Line(string text = "") => md.AppendLine(text);

        Line($"# {c.Name} — {r.Profile.Class.Name} niv. {c.Level} · {c.ServerName} ({c.Region})");
        Line();
        Line($"Vue {GameDatabase.ModeName(r.Mode)} · build {(r.Profile.UsesDesigned ? "conçu" : "des joueurs")} · niveau d'objet {c.ItemLevel} · PC {c.CombatPower} · " +
             $"importé le {c.FetchedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        Line();
        Line($"**Score {r.Scores.Overall}/100** — stats {r.Scores.Stats} · équipement {r.Scores.Gear} · compétences {r.Scores.Skills} · Daevanion {r.Scores.Daevanion}");

        Line();
        Line("## Plan d'action");
        Line();
        var i = 0;
        foreach (var f in r.ActionPlan.Take(15))
        {
            var done = inputs.IsDone(f.Title, c.FetchedAtUtc);
            Line($"{++i}. {(done ? "~~" : "")}**[{Fmt.Severity(f.Severity)}] {f.Title}**{(done ? "~~ (fait)" : "")} — {f.Action}");
        }

        Line();
        Line("## Stats ajustables");
        Line();
        Line("| Famille | Part | Visé |");
        Line("|---|---|---|");
        foreach (var cat in r.Stats.Categories)
            Line($"| {GameDatabase.CategoryName(cat.Category)} | {Fmt.Pct(cat.Share)} | {Fmt.Pct(cat.Target)} |");
        Line();
        Line($"Efficacité : {Fmt.Pct(r.Stats.Efficiency)} des stats ajustables dans des stats utiles à la classe.");

        Line();
        Line($"## Équipement — étape « {r.Gear.Stage.Name} »");
        Line();
        var gear = r.Gear.Slots.Where(s => s.Status != SlotStatus.Ok).OrderByDescending(s => s.Priority).ToList();
        if (gear.Count == 0) Line("Toutes les pièces sont au niveau de l'étape.");
        foreach (var s in gear)
            Line($"- **{s.SlotName}** ({Fmt.Status(s.Status)}) : {s.Advice}{(s.NextSource is null ? "" : $" → {s.NextSource}")}");
        foreach (var e in r.Engravings.Items.Where(e => e.NeedsWork && e.Action != SoulAction.None))
            Line($"- Gravures **{e.SlotName}** : {e.SoulAdvice}");

        Line();
        Line($"## Compétences — {r.Skills.SpentPoints} points investis + {r.Skills.UnspentPoints} non dépensés");
        Line();
        if (r.Skills.Focus.Count > 0)
        {
            Line($"Focus dégâts ({r.Skills.DamageSource}) :");
            Line();
            foreach (var a in r.Skills.Focus.Take(6))
                Line($"- {a.Skill.Name} : {Fmt.Pct(a.DamageShare)} des dégâts, niveau {a.TotalAfter} (acheté {a.RecommendedBase}, Daevanion +{a.BonusDaevanion}, arcanes +{a.BonusArcana}, équipement +{a.BonusGear})");
            Line();
            if (r.Skills.Chains.Count > 0)
            {
                Line($"Suites d'enchaînement comptées avec le sort lancé : {string.Join(" ; ", r.Skills.Chains.Select(c => c.Text))}.");
                Line();
            }
        }
        if (r.Skills.Moves.Count == 0) Line("Points bien placés : rien à déplacer.");
        else
        {
            Line("Maintenant (réinitialisation gratuite) :");
            Line();
            foreach (var g in r.Skills.Moves.GroupBy(m => m.To))
                Line($"- +{g.Sum(m => m.Points)} {g.Key.Skill.Name} ← {string.Join(", ", g.Select(m => m.From is null ? $"non dépensés −{m.Points}" : $"{m.From.Skill.Name} −{m.Points}"))}. {g.Key.Decision}");
            foreach (var d in r.Skills.ToRemove) Line($"- −{-d.Delta} {d.Skill.Name}. {d.Decision}");
        }
        var lost = r.Skills.GearBonuses.Where(b => !b.Useful).ToList();
        if (lost.Count > 0 || r.Skills.BonusWanted.Count > 0)
        {
            Line();
            if (lost.Count > 0) Line($"Bonus de compétence perdus : {string.Join(", ", lost.Select(b => $"{b.SkillName} +{b.Levels} ({(b.Arcana ? $"arcane {b.Item}" : db.SlotName(b.Slot))})"))}.");
            if (r.Skills.BonusWanted.Count > 0) Line($"À chercher sur les arcanes et l'équipement : {string.Join(", ", r.Skills.BonusWanted.Select(a => a.Skill.Name))}.");
        }
        Line();
        Line("Prochains points, dans l'ordre :");
        Line();
        var total = 0;
        foreach (var x in r.Skills.NextPoints.Take(12))
        {
            var first = total + 1;
            total += x.Points;
            Line($"- {(x.Points == 1 ? $"{first}" : $"{first}–{total}")} : {x.Skill.Skill.Name} {x.From} → {x.To}{(x.Specialty is { } spec ? $" (spécialisation {spec})" : "")}");
        }
        Line();
        Line($"Stigmas : {string.Join(", ", r.Skills.Stigmas.Where(s => s.Recommended).Select(s => $"{s.Skill.Name} niv. {s.Skill.Level} → {s.Target}"))}");
        var off = r.Skills.Stigmas.Where(s => s.Skill.Equipped && !s.Recommended).ToList();
        if (off.Count > 0) Line($"À retirer : {string.Join(", ", off.Select(s => s.Skill.Name))}");

        if (r.Profile.Play is { } play)
        {
            Line();
            Line($"## Rotation ({(r.Profile.UsesDesigned ? "build conçu" : "build des joueurs")})");
            Line();
            foreach (var phase in play.Rotation)
                Line($"- **{phase.Name}** : {string.Join(" → ", phase.Steps)}{(phase.Note.Length > 0 ? $" — {phase.Note}" : "")}");
            foreach (var tip in play.Tips) Line($"- {tip}");
        }

        Line();
        Line("## Daevanion");
        Line();
        foreach (var b in r.Daevanion.Budgets.Where(b => DaevanionPlanner.Relevant(b.Pool, r.Mode)))
            Line($"- {db.Settings.Daevanion.PoolNames.GetValueOrDefault(b.Pool, b.Pool)} : {b.Spent} dépensés + {b.Unspent} non dépensés (tout remplir : {b.FillCost})");
        foreach (var respec in r.Daevanion.Respecs.Where(x => x.Remove.Count > 0 && x.Gain >= db.Settings.Daevanion.RespecMinGain))
            Line($"- Réorganisation conseillée : +{Fmt.Pct(respec.Gain)} de valeur, {Fmt.Plural(respec.Remove.Count, "case")} à retirer");
        foreach (var route in r.Daevanion.Routes.Where(x => x.Nodes.Count > 0))
            Line($"- Route conçue depuis le centre : {route.InBudget.Count()} cases pour {route.Budget} points, {(route.Gain >= 0 ? "+" : "")}{Fmt.Pct(route.Gain)} par rapport au placement actuel");
        foreach (var pool in r.Daevanion.ByPool)
        {
            Line();
            Line($"Prochaines cases ({db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool.Key, pool.Key)}) :");
            Line();
            foreach (var s in pool.Take(15))
                Line($"{s.Step}. {s.Board.Name} ligne {s.Node.Row} col. {s.Node.Col} — {s.Effects} ({Fmt.Num(s.Cumulative)} pts cumulés){(s.IsPassage ? " · passage" : "")}");
        }

        if (r.Pets.Linked && r.Pets.Best is { } best)
        {
            Line();
            Line("## Familiers");
            Line();
            Line($"Espèce à farmer : **{best.Name}** ({best.Owned}/{best.Total} obtenus, {best.Maxed} au niveau 3).");
        }

        Line();
        Line($"_Aion 2 Builder · {DateTime.Now:dd/MM/yyyy HH:mm}_");
        return md.ToString();
    }
}
