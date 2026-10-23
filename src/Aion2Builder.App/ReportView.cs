using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Model;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Construit les onglets d'un rapport de conseils.</summary>
sealed class ReportView(GameDatabase db, AdviceReport r)
{
    public TabControl Build(int selectedTab)
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Plan d'action", Content = Scroll(ActionPlan()) });
        tabs.Items.Add(new TabItem { Header = "Stats", Content = Scroll(Stats()) });
        tabs.Items.Add(new TabItem { Header = "Équipement", Content = Scroll(Gear()) });
        tabs.Items.Add(new TabItem { Header = "Compétences", Content = Scroll(Skills()) });
        tabs.Items.Add(new TabItem { Header = "Daevanion", Content = Scroll(Daevanion()) });
        tabs.Items.Add(new TabItem { Header = "Détails", Content = Scroll(Details()) });
        tabs.SelectedIndex = Math.Clamp(selectedTab, 0, tabs.ItemCount - 1);
        return tabs;
    }

    Control ActionPlan()
    {
        var s = r.Scores;
        var panel = Stack(8,
            Row(10, Chip($"Global {s.Overall}", ScoreBrush(s.Overall)), Chip($"Stats {s.Stats}", ScoreBrush(s.Stats)),
                Chip($"Équipement {s.Gear}", ScoreBrush(s.Gear)), Chip($"Compétences {s.Skills}", ScoreBrush(s.Skills)),
                Chip($"Daevanion {s.Daevanion}", ScoreBrush(s.Daevanion))),
            Text($"Vue {GameDatabase.ModeName(r.Mode)} — conseils classés du plus rentable au moins rentable (gain attendu / ressources).", 12, color: Muted));

        var i = 0;
        foreach (var f in r.ActionPlan.Take(15))
            panel.Children.Add(FindingCard(f, ++i));
        if (i == 0) panel.Children.Add(Card(Text("Rien d'urgent : ton build suit les repères de ta classe.")));
        return panel;
    }

    Control FindingCard(Finding f, int? rank = null)
    {
        var head = Row(8, Chip(Fmt.Severity(f.Severity), SeverityBrush(f.Severity)), Text(Fmt.Area(f.Area), 12, color: Muted));
        if (rank is not null) head.Children.Insert(0, Text($"{rank}.", 15, true));
        var body = Stack(4, head, Text(f.Title, 15, true), Text(f.Detail, 12, color: Muted));
        if (f.Action is not null) body.Children.Add(Text($"→ {f.Action}", 13));
        return Card(body, SeverityBrush(f.Severity));
    }

    Control Stats()
    {
        var sp = r.Stats;
        var cats = sp.Categories.Select(c => new Control[]
        {
            Text(GameDatabase.CategoryName(c.Category), 13, true, wrap: false),
            ShareBar(c.Share, c.Target, Math.Abs(c.Gap) <= db.Settings.Stats.CategoryTolerance ? Good : c.Gap > 0 ? Warn : Bad),
            Text($"{Fmt.Pct(c.Share)} (visé {Fmt.Pct(c.Target)})", 12, wrap: false),
        });
        var rows = sp.Stats.Where(s => s.Budget > 0 || s.ExpectedShare >= 0.03).Select(s => new Control[]
        {
            Text(s.Def.Name, 12, wrap: false),
            Text(s.Display, 12, wrap: false),
            Text(Fmt.Pct(s.Share), 12, color: s.Deficit > 0.02 ? Bad : s.Weight <= db.Settings.Stats.LowWeight && s.Share >= db.Settings.Stats.WastedShare ? Warn : null, wrap: false),
            Text(Fmt.Pct(s.ExpectedShare), 12, color: Muted, wrap: false),
            Text(s.Weight.ToString("0.00"), 12, color: Muted, wrap: false),
            Text(string.Join(", ", s.Sources.Take(3).Select(x => x.Source)), 11, color: Muted),
        });
        return Stack(10,
            Card(Stack(8,
                Text("Répartition du budget de stats", 15, true),
                Text("Ce que tu contrôles : équipement, Daevanion, titres, stats divines. Le trait blanc marque la cible de ta classe.", 12, color: Muted),
                Table("Auto,Auto,Auto", cats),
                Text($"Efficacité : {Fmt.Pct(sp.Efficiency)} du budget dans des stats utiles à ta classe en {GameDatabase.ModeName(r.Mode)}.", 13))),
            Card(Table("Auto,Auto,Auto,Auto,Auto,*", rows, ["Stat", "Total", "Budget", "Visé", "Poids", "Principales sources"])),
            Stack(0, r.Findings.Where(f => f.Area == Area.Stats && f.Severity != Severity.Info).Select(f => FindingCard(f)).ToArray()));
    }

    Control Gear()
    {
        var g = r.Gear;
        var stage = Stack(6,
            Text($"Palier actuel : {g.Stage.Name}", 15, true),
            Text(g.Stage.Goal, 12, color: Muted));
        if (g.NextStage is { } next)
        {
            stage.Children.Add(Row(10,
                new ProgressBar { Minimum = 0, Maximum = 1, Value = g.ProgressToNext(r.Character.ItemLevel), Width = 260, Height = 10 },
                Text($"Niveau d'objet {r.Character.ItemLevel} / {next.MinItemLevel} → « {next.Name} »", 12)));
            foreach (var gear in next.Gear)
                stage.Children.Add(Text($"• {db.GroupName(gear.Group)} : {gear.For(r.Mode)}", 12));
        }
        foreach (var tip in g.Stage.Tips) stage.Children.Add(Text($"Astuce : {tip}", 12, color: Muted));

        var rows = g.Slots.OrderByDescending(s => s.Priority).Select(s => new Control[]
        {
            Text(s.SlotName, 12, true, wrap: false),
            s.Item is null ? Text("—", 12, color: Muted) : Text(s.Item.Name, 12, color: Brush(db.Grade(s.Item.Grade).Color)),
            Text(s.Item is null ? "" : s.Power.ToString(), 12, wrap: false),
            Text(s.Item is null ? "" : $"+{s.Item.EnchantLevel}", 12, wrap: false),
            Chip(Fmt.Status(s.Status), StatusBrush(s.Status)),
            Text(s.Advice + (s.NextSource is not null && s.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak ? $" → {s.NextSource}" : ""), 12),
        });
        return Stack(10, Card(stage), Card(Table("Auto,2*,Auto,Auto,Auto,3*", rows, ["Emplacement", "Pièce", "Niv.", "Ench.", "Statut", "Conseil"])));
    }

    Control Skills()
    {
        var sk = r.Skills;
        var rows = sk.Skills.Select(a => new Control[]
        {
            Text(a.IsCore ? "★" : "", 13, color: Warn, wrap: false),
            Text(a.Skill.Name, 12, true, a.Skill.Acquired ? null : Muted, wrap: false),
            Text(a.Skill.IsActive ? "Active" : a.Skill.IsPassive ? "Passive" : a.Skill.Category, 12, color: Muted, wrap: false),
            Text(a.Skill.Level.ToString(), 12, wrap: false),
            Text(a.Skill.IsActive ? a.Skill.Equipped ? "équipée" : "—" : "", 12, color: Muted, wrap: false),
            a.Status == SkillStatus.Ok ? Text("") : Chip(Fmt.Status(a.Status), a.Status == SkillStatus.Wasted ? Muted : Warn),
            Text(a.Advice, 12, color: Muted),
        });
        return Stack(10,
            Card(Text($"Niveau moyen : actives équipées {Fmt.Num(sk.AverageActiveLevel)} · passives {Fmt.Num(sk.AveragePassiveLevel)}. ★ = compétence clé de la classe pour ce mode.", 12)),
            Card(Table("Auto,Auto,Auto,Auto,Auto,Auto,*", rows, ["", "Compétence", "Type", "Niv.", "Barre", "Statut", "Conseil"])));
    }

    Control Daevanion()
    {
        var d = r.Daevanion;
        var panel = Stack(10);
        var summary = d.Boards.Where(b => b.Relevant).Select(b => new Control[]
        {
            Text(b.Board.Name, 12, true, wrap: false),
            Text(db.Settings.Daevanion.PoolNames.GetValueOrDefault(b.Pool, b.Pool), 12, color: Muted, wrap: false),
            Text($"{b.Board.OpenNodes}/{b.Board.TotalNodes} ({b.Board.OpenPercent} %)", 12, wrap: false),
            Text($"{b.Reachable} accessibles", 12, wrap: false),
            Text($"rentabilité {Fmt.Num(b.ValuePerCost * 100)}", 12, color: Muted, wrap: false),
        });
        panel.Children.Add(Card(Stack(6, Text("Plateaux", 15, true), Table("Auto,Auto,Auto,Auto,Auto", summary))));

        foreach (var pool in d.ByPool)
        {
            var list = Stack(2, Text($"Prochaines cases — {db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool.Key, pool.Key)}", 15, true),
                Text("Dans l'ordre : meilleur gain par point dépensé, cases de passage comprises.", 12, color: Muted));
            foreach (var s in pool)
                list.Children.Add(Text($"{s.Step,2}. {s.Board.Name} (ligne {s.Node.Row}, col. {s.Node.Col}) — {s.Effects}{(s.IsPassage ? " · passage" : "")}",
                    12, color: s.IsPassage ? Muted : null));
            panel.Children.Add(Card(list));
        }

        var boards = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in d.Boards.Where(b => b.Relevant && b.Board.Nodes.Count > 0))
            boards.Children.Add(new Border { Margin = new Thickness(0, 0, 12, 12), Child = Card(BoardGrid(b.Board, d), bottom: 0) });
        panel.Children.Add(boards);
        return panel;
    }

    /// <summary>Plateau 15×15 : cases ouvertes en couleur de rareté, cases planifiées numérotées.</summary>
    Control BoardGrid(DaevanionBoard board, DaevanionPlan plan)
    {
        const int size = 22;
        var planned = plan.Steps.Where(s => s.Board.Id == board.Id).ToDictionary(s => s.Node.Id);
        var rows = board.Nodes.Max(n => n.Row);
        var cols = board.Nodes.Max(n => n.Col);
        var canvas = new Canvas { Width = cols * size, Height = rows * size };
        foreach (var n in board.Nodes)
        {
            var color = n.IsStart ? Brushes.White : Brush(db.Grade(n.Grade).Color);
            var step = planned.GetValueOrDefault(n.Id);
            var cell = new Border
            {
                Width = size - 3, Height = size - 3,
                CornerRadius = new CornerRadius(n.IsSkill ? 10 : 3),
                Background = n.Open || n.IsStart ? color : CardBackground,
                BorderBrush = step is not null ? Brushes.White : color,
                BorderThickness = new Thickness(step is not null ? 2 : 1),
                Opacity = n.Open || n.IsStart || step is not null ? 1 : 0.45,
                Child = step is null ? null : new TextBlock
                {
                    Text = step.Step.ToString(), FontSize = 10, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var tip = $"{(n.Name.Length > 0 ? n.Name : n.Type)}\n{string.Join("\n", n.Effects.Select(e => e.Raw))}" +
                      (n.Open ? "\n(ouverte)" : step is not null ? $"\nÉtape {step.Step} du plan" : "");
            ToolTip.SetTip(cell, tip);
            Canvas.SetLeft(cell, (n.Col - 1) * size);
            Canvas.SetTop(cell, (n.Row - 1) * size);
            canvas.Children.Add(cell);
        }
        return Stack(6, Text($"{board.Name} — {board.OpenNodes}/{board.TotalNodes}", 13, true), canvas,
            Text("Rond = niveau de compétence · carré = stat · chiffre = ordre du plan", 11, color: Muted));
    }

    Control Details()
    {
        var panel = Stack(6);
        foreach (var group in r.Findings.GroupBy(f => f.Area))
        {
            panel.Children.Add(Text(Fmt.Area(group.Key), 15, true));
            foreach (var f in group) panel.Children.Add(FindingCard(f));
        }
        return panel;
    }
}
