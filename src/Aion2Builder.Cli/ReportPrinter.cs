using Aion2Builder.Analysis;
using Aion2Builder.GameData;

namespace Aion2Builder.Cli;

/// <summary>Affichage console d'un rapport de conseils.</summary>
sealed class ReportPrinter(GameDatabase db)
{
    public void Print(AdviceReport r)
    {
        var c = r.Character;
        Title($"{c.Name} — {r.Profile.Class.Name} niv. {c.Level} · {c.ServerName} ({c.Region}) · niveau d'objet {c.ItemLevel} · PC {c.CombatPower}");
        Console.WriteLine($"Vue {GameDatabase.ModeName(r.Mode)} · rôle {r.Profile.Role.Name} · lu le {c.FetchedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        Console.WriteLine($"Score {r.Scores.Overall}/100  (stats {r.Scores.Stats} · équipement {r.Scores.Gear} · compétences {r.Scores.Skills} · Daevanion {r.Scores.Daevanion})");

        Section("Plan d'action — du plus rentable au moins rentable");
        var i = 0;
        foreach (var f in r.ActionPlan.Take(10))
        {
            Colored(Color(f.Severity), $"{++i,2}. [{Fmt.Severity(f.Severity)}] {f.Title}");
            foreach (var line in f.Detail.Split('\n')) Console.WriteLine($"    {line}");
            Console.WriteLine($"    → {f.Action}");
        }

        Section("Répartition des stats ajustables (stats secondaires, Daevanion, titres, stats divines)");
        foreach (var cat in r.Stats.Categories)
        {
            var bar = Bar(cat.Share, 24);
            var marker = Math.Abs(cat.Gap) <= db.Settings.Stats.CategoryTolerance ? "ok" : cat.Gap > 0 ? "trop" : "manque";
            Console.WriteLine($"  {GameDatabase.CategoryName(cat.Category),-9} {bar} {Fmt.Pct(cat.Share),5}  visé {Fmt.Pct(cat.Target),5}  {marker}");
        }
        Console.WriteLine($"  Efficacité : {Fmt.Pct(r.Stats.Efficiency)} de tes stats ajustables dans des stats utiles à ta classe (stats fixes des pièces hors calcul).");
        Console.WriteLine();
        Console.WriteLine($"  {"Stat",-30} {"Total",-16} {"Ajustable",9} {"Repère",8} {"Poids",6}");
        foreach (var s in r.Stats.Stats.Where(s => s.Budget > 0 || s.ExpectedShare >= 0.03).Take(18))
            Console.WriteLine($"  {s.Def.Name,-30} {s.Display,-16} {Fmt.Pct(s.Share),9} {s.TargetDisplay,8} {s.Weight,6:0.00}");

        Section($"Équipement — palier « {r.Gear.Stage.Name} »" + (r.Gear.NextStage is null ? "" : $" → « {r.Gear.NextStage.Name} » à {r.Gear.NextStage.MinItemLevel}"));
        foreach (var s in r.Gear.Slots.OrderByDescending(s => s.Priority))
        {
            var item = s.Item is null ? "—" : $"{s.Item.Name} — {db.Grade(s.Item.Grade).Name}, niv. {s.Power}, +{s.Item.EnchantLevel}";
            Colored(SlotColor(s.Status), $"  {s.SlotName,-19} {Fmt.Status(s.Status),-12} {item}");
            if (s.Status != SlotStatus.Ok) Console.WriteLine($"  {"",-19} {s.Advice}");
            if (s.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak && s.NextSource is not null)
                Console.WriteLine($"  {"",-19} → {s.NextSource}");
        }

        if (r.Engravings.Items.Count > 0)
        {
            Section("Gravures d'âme et pierres — ✓ utile · ~ moyen · ✕ à remplacer");
            foreach (var e in r.Engravings.Items.OrderByDescending(e => e.NeedsWork).ThenByDescending(e => e.Priority))
            {
                var rate = e.HasSoulBinding ? $"lien d'âme {Fmt.Num(e.SoulBindRate ?? 0)} %" : "";
                var stones = e.StoneSlots > 0 ? $"{e.StoneKind} {e.Stones.Count}/{e.StoneSlots}" : "";
                Colored(e.NeedsWork ? ConsoleColor.Yellow : ConsoleColor.Gray, $"  {e.SlotName,-19} {rate,-20} {stones}");
                if (e.SoulLines.Count > 0) Console.WriteLine($"  {"",-19} {(e.HasSoulBinding ? "" : "options fixes : ")}{string.Join(" · ", e.SoulLines.Select(EngravingAdvisor.Describe))}");
                if (e.Stones.Count > 0) Console.WriteLine($"  {"",-19} {string.Join(" · ", e.Stones.Select(EngravingAdvisor.Describe))}");
                if (e.Action != SoulAction.None) Console.WriteLine($"  {"",-19} → {e.SoulAdvice}");
                if (e.StoneAdvice is not null) Console.WriteLine($"  {"",-19} → {e.StoneAdvice}");
                if (e.StoneUpgrade is not null) Console.WriteLine($"  {"",-19}   {e.StoneUpgrade}");
            }
        }

        Section($"Compétences — {r.Skills.SpentPoints} points investis + {r.Skills.UnspentPoints} non dépensés (niveau acheté max {db.Settings.Skills.BaseMax})");
        Console.WriteLine($"  {"Compétence",-36} {"Rôle",-30} {"Acheté",6} {"Bonus",6} {"Conseil",8}");
        foreach (var a in r.Skills.Skills)
        {
            var delta = a.Delta == 0 ? "=" : a.Delta > 0 ? $"+{a.Delta}" : a.Delta.ToString();
            Colored(a.Delta < 0 ? ConsoleColor.Red : a.Delta > 0 ? ConsoleColor.Green : a.Skill.Acquired ? ConsoleColor.Gray : ConsoleColor.DarkGray,
                $"  {(a.Kind == SkillKind.Passive ? "(P) " : "")}{a.Skill.Name,-32} {a.Role,-30} {a.Base,6} {"+" + a.Bonus,6} {delta,8}");
            if (a.Delta != 0) Console.WriteLine($"      {a.Why}");
        }
        if (r.Skills.Moves.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Maintenant (réinitialisation gratuite) :");
            foreach (var g in r.Skills.Moves.GroupBy(m => m.To))
                Console.WriteLine($"   +{g.Sum(m => m.Points)} {g.Key.Skill.Name} ({g.Key.Role}, {g.Key.Priority:0.00}) ← " +
                    string.Join(", ", g.Select(m => m.From is null ? $"non dépensés −{m.Points}" : $"{m.From.Skill.Name} ({m.From.Priority:0.00}) −{m.Points}")));
        }
        Console.WriteLine();
        Console.WriteLine("  Prochains points, dans l'ordre :");
        var n = 0;
        foreach (var x in r.Skills.NextPoints)
            Console.WriteLine($"   {++n,2}. {x.Skill.Skill.Name} {x.From} → {x.To}" + (x.Specialty is { } spec ? $"  (spécialisation du niveau {spec})" : "") +
                              (x.BeyondTarget ? "  (au-delà du visé)" : ""));
        if (n == 0) Console.WriteLine($"   compétences utiles au maximum achetable ({db.Settings.Skills.BaseMax}) : la suite passe par le Daevanion et l'équipement");
        Console.WriteLine();
        Console.WriteLine($"  Stigmas ({db.Settings.Stigmas.Slots} emplacements, fragments) :");
        foreach (var s in r.Skills.Stigmas.Where(s => s.Recommended || s.Skill.Equipped))
            Colored(s.Recommended == s.Skill.Equipped ? ConsoleColor.Gray : s.Recommended ? ConsoleColor.Green : ConsoleColor.Red,
                $"   {(s.Recommended ? (s.Skill.Equipped ? "garder " : "mettre ") : "retirer")} {s.Skill.Name,-36} niv. {s.Skill.Level,2} → {s.Target,-2} {s.Role}");
        if (r.Skills.NextShards.Count > 0)
            Console.WriteLine("  Prochains fragments : " + string.Join(" · ", r.Skills.NextShards.Select(x => $"{x.Stigma.Skill.Name} {x.From} → {x.To} ({x.Shards})")));

        if (r.Profile.Play is { } play)
        {
            var designed = r.Profile.UsesDesigned ? r.Profile.Class.Designed : null;
            Section($"Rotation et façon de jouer — {(designed is null ? "build des joueurs" : "build conçu")} ({GameDatabase.ModeName(r.Mode)})");
            if (designed is not null) Console.WriteLine($"  {designed.Summary}");
            foreach (var phase in play.Rotation)
            {
                Console.WriteLine($"  {phase.Name} : {string.Join(" → ", phase.Steps)}");
                if (phase.Note.Length > 0) Console.WriteLine($"      {phase.Note}");
            }
            foreach (var tip in play.Tips) Console.WriteLine($"  • {tip}");
            if (designed is not null && designed.Differences.Count > 0)
            {
                Console.WriteLine("  Différences avec le build des joueurs :");
                foreach (var d in designed.Differences) Console.WriteLine($"   - {d}");
            }
            else if (designed is null && r.Profile.Class.Designed is not null)
                Console.WriteLine("  Un build conçu à partir des données des sorts existe aussi : advise <nom> --build concu");
        }

        Section("Daevanion");
        foreach (var b in r.Daevanion.Budgets.Where(b => DaevanionPlanner.Relevant(b.Pool, r.Mode)))
            Console.WriteLine($"  {db.Settings.Daevanion.PoolNames.GetValueOrDefault(b.Pool, b.Pool)} : {b.Spent} dépensés + {b.Unspent} non dépensés (tout remplir : {b.FillCost})");
        foreach (var b in r.Daevanion.Boards.Where(b => b.Relevant))
            Console.WriteLine($"  {b.Board.Name,-10} {b.Board.OpenNodes,3}/{b.Board.TotalNodes,-3} cases · {b.Spent,3}/{b.Total} points · {b.Reachable} accessibles");
        foreach (var respec in r.Daevanion.Respecs.Where(x => x.Remove.Count > 0 && x.Gain >= db.Settings.Daevanion.RespecMinGain))
        {
            Console.WriteLine();
            Colored(ConsoleColor.Yellow, $"  Réorganisation {db.Settings.Daevanion.PoolNames.GetValueOrDefault(respec.Pool, respec.Pool)} : +{Fmt.Pct(respec.Gain)} de valeur avec les mêmes {respec.Budget} points");
            foreach (var x in respec.Remove)
                Colored(ConsoleColor.Red, $"   retirer  {x.Board.Name,-9} ligne {x.Node.Row,2} col. {x.Node.Col,2}  {x.Effects}");
            foreach (var x in respec.Add)
                Colored(ConsoleColor.Green, $"   {x.Step,2}. prendre {x.Board.Name,-9} ligne {x.Node.Row,2} col. {x.Node.Col,2}  {x.Effects}");
        }
        foreach (var pool in r.Daevanion.ByPool)
        {
            Console.WriteLine();
            Console.WriteLine($"  Prochaines cases — {db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool.Key, pool.Key)} :");
            foreach (var s in pool.Take(15))
                Colored(s.IsPassage ? ConsoleColor.DarkGray : ConsoleColor.Gray,
                    $"   {s.Step,2}. {s.Board.Name,-9} ligne {s.Node.Row,2} col. {s.Node.Col,2}  {s.Effects}{(s.IsPassage ? "  (passage)" : "")}{(s.Affordable ? "  [faisable maintenant]" : "")}");
        }

        Section("Familiers — effets de possession (Pet Meter via Aion 2 Todo)");
        if (!r.Pets.Linked) Console.WriteLine("  Non reliés : « pets --key <clé> » (clé : Aion 2 Todo, onglet Familiers → Pet Meter).");
        else
        {
            var best = r.Pets.Best;
            foreach (var g in r.Pets.Genera.Where(g => g.Total > 0))
            {
                var now = string.Join(", ", g.Stats.Where(s => s.Now > 0).Select(s => $"{s.Name} +{Fmt.Num(s.Now)}"));
                Colored(g == best ? ConsoleColor.Green : ConsoleColor.Gray,
                    $"  {g.Name,-8} {g.Owned,3}/{g.Total,-4} niv. 3 : {g.Maxed,-3} intérêt {g.PetValue,5:0.00}  {now}{(g == best ? "  ← à farmer" : "")}");
            }
            foreach (var f in r.Findings.Where(f => f.Area == Area.Pets && f.Severity == Severity.Advice))
            {
                foreach (var line in f.Detail.Split('\n')) Console.WriteLine($"  {line}");
                Console.WriteLine($"  → {f.Action}");
            }
        }

        Section("Détails et repères");
        foreach (var f in r.Findings.Where(f => f.Action is null || f.Severity == Severity.Info))
        {
            Colored(Color(f.Severity), $"  • {f.Title}");
            Console.WriteLine("    " + f.Detail.Replace("\n", "\n    "));
        }
        Console.WriteLine();
    }

    static string Bar(double ratio, int width)
    {
        var n = (int)Math.Round(Math.Clamp(ratio, 0, 1) * width);
        return new string('█', n) + new string('░', width - n);
    }

    static ConsoleColor Color(Severity s) => s switch
    {
        Severity.Critical => ConsoleColor.Red,
        Severity.Important => ConsoleColor.Yellow,
        Severity.Advice => ConsoleColor.Cyan,
        _ => ConsoleColor.Gray,
    };

    static ConsoleColor SlotColor(SlotStatus s) => s switch
    {
        SlotStatus.Empty => ConsoleColor.Red,
        SlotStatus.Replace or SlotStatus.Weak => ConsoleColor.Yellow,
        SlotStatus.Enchant => ConsoleColor.Cyan,
        _ => ConsoleColor.Gray,
    };

    static void Title(string text)
    {
        Console.WriteLine();
        Colored(ConsoleColor.White, $"══ {text} ══");
    }

    static void Section(string text)
    {
        Console.WriteLine();
        Colored(ConsoleColor.White, $"▌ {text}");
    }

    static void Colored(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
