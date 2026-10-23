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
/// <param name="onInputs">Appelé quand le joueur saisit ses points non dépensés (recalcul du rapport).</param>
sealed class ReportView(GameDatabase db, AdviceReport r, PlayerInputs inputs, Action<PlayerInputs> onInputs)
{
    public TabControl Build(int selectedTab)
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { FontSize = 15, Header = "Plan d'action", Content = Scroll(ActionPlan()) });
        tabs.Items.Add(new TabItem { FontSize = 15, Header = "Stats", Content = Scroll(Stats()) });
        tabs.Items.Add(new TabItem { FontSize = 15, Header = "Équipement", Content = Scroll(Gear()) });
        tabs.Items.Add(new TabItem { FontSize = 15, Header = "Compétences", Content = Scroll(Skills()) });
        tabs.Items.Add(new TabItem { FontSize = 15, Header = "Daevanion", Content = Scroll(Daevanion()) });
        tabs.Items.Add(new TabItem { FontSize = 15, Header = "Détails", Content = Scroll(Details()) });
        tabs.SelectedIndex = Math.Clamp(selectedTab, 0, tabs.ItemCount - 1);
        return tabs;
    }

    Control ActionPlan()
    {
        var s = r.Scores;
        var panel = Stack(8,
            Row(10, Tip(Chip($"Global {s.Overall}", ScoreBrush(s.Overall)), "Moyenne pondérée des quatre notes ci-contre."),
                Tip(Chip($"Stats {s.Stats}", ScoreBrush(s.Stats)), "Part de tes stats ajustables placée dans des stats utiles à ta classe, et équilibre Attaque / Défense / Survie / Utilité."),
                Tip(Chip($"Équipement {s.Gear}", ScoreBrush(s.Gear)), "Pièces au niveau de ton palier (les pièces vides, en retard ou à remplacer font baisser la note)."),
                Tip(Chip($"Compétences {s.Skills}", ScoreBrush(s.Skills)), "Niveaux visés atteints, pondérés par l'importance de chaque compétence pour ta classe."),
                Tip(Chip($"Daevanion {s.Daevanion}", ScoreBrush(s.Daevanion)), "Part moyenne des plateaux utiles déjà ouverte.")),
            Text($"Vue {GameDatabase.ModeName(r.Mode)} — conseils classés du plus rentable au moins rentable (gain attendu / ressources). Survole un élément pour son explication.", 12, color: Muted));

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
        var body = Stack(4, head, Text(f.Title, 15, true));
        foreach (var line in f.Detail.Split('\n')) body.Children.Add(Text(line, 12, color: Muted));
        if (f.Action is not null) body.Children.Add(Text($"→ {f.Action}", 13));
        return Card(body, SeverityBrush(f.Severity));
    }

    Control Stats()
    {
        var sp = r.Stats;
        var cats = sp.Categories.Select(c => new Control[]
        {
            Tip(Text(GameDatabase.CategoryName(c.Category), 13, true, wrap: false), GameDatabase.CategoryDescription(c.Category)),
            Tip(ShareBar(c.Share, c.Target, Math.Abs(c.Gap) <= db.Settings.Stats.CategoryTolerance ? Good : c.Gap > 0 ? Warn : Bad),
                $"{GameDatabase.CategoryDescription(c.Category)}\nTu y mets {Fmt.Pct(c.Share)} de tes stats ajustables ; repère pour ta classe : {Fmt.Pct(c.Target)} (trait blanc)."),
            Text($"{Fmt.Pct(c.Share)} (conseillé ≈ {Fmt.Pct(c.Target)})", 12, wrap: false),
        });
        var rows = sp.Stats.Where(s => s.Budget > 0 || s.ExpectedShare >= 0.03).Select(s => new Control[]
        {
            Tip(Text(s.Def.Name, 12, wrap: false), $"{s.Def.Name} — {s.Def.Desc}\nFamille : {GameDatabase.CategoryName(s.Def.Category)}." +
                (s.Def.Where.Count > 0 ? $"\nOù la trouver : {string.Join(", ", s.Def.Where)}." : "")),
            Text(s.Display, 12, wrap: false),
            Text(Fmt.Pct(s.Share), 12, color: s.Deficit > 0.02 ? Bad : s.Weight <= db.Settings.Stats.LowWeight && s.Share >= db.Settings.Stats.WastedShare ? Warn : null, wrap: false),
            Text(s.TargetDisplay, 12, color: Muted, wrap: false),
            Tip(Text(s.Weight.ToString("0.00"), 12, color: Muted, wrap: false), "Importance de cette stat pour ta classe dans ce mode (0 = inutile, 1 = prioritaire)."),
            Text(string.Join(", ", s.Sources.Take(3).Select(x => x.Source)), 11, color: Muted),
        });
        return Stack(10,
            Card(Stack(8,
                Text("Répartition des stats ajustables", 15, true),
                Text("« Stats ajustables » = ce que tu peux changer : stats secondaires des pièces, cases Daevanion, titres, stats divines. " +
                     "Les stats fixes d'une pièce (la DÉF d'une armure, l'ATQ d'une arme) viennent avec elle et restent hors répartition. " +
                     "Le trait blanc marque la répartition conseillée pour ta classe. Survole une famille ou une stat pour savoir à quoi elle sert.", 12, color: Muted),
                Table("Auto,Auto,Auto", cats),
                Text($"Efficacité : {Fmt.Pct(sp.Efficiency)} de tes stats ajustables vont dans des stats utiles à ta classe en {GameDatabase.ModeName(r.Mode)}.", 13))),
            Card(Table("Auto,Auto,Auto,Auto,Auto,*", rows, ["Stat", "Total", "Part ajustable", "Repère", "Poids", "Principales sources"])),
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
        foreach (var rule in db.Progression.SlotRules)
            stage.Children.Add(Text($"• {db.SlotName(rule.Key)} : {rule.Value.Source}", 12));
        foreach (var tip in g.Stage.Tips) stage.Children.Add(Text($"Astuce : {tip}", 12, color: Muted));

        var rows = g.Slots.OrderByDescending(s => s.Priority).Select(s => new Control[]
        {
            Text(s.SlotName, 12, true, wrap: false),
            s.Item is null ? Text("—", 12, color: Muted) : Tip(Text(s.Item.Name, 12, color: Brush(db.Grade(s.Item.Grade).Color)),
                $"{db.Grade(s.Item.Grade).Name} · niveau d'objet {s.Power} · +{s.Item.EnchantLevel}/{s.Item.MaxEnchantLevel}" +
                (s.Item.Sources.Count > 0 ? $"\nSource : {string.Join(", ", s.Item.Sources)}" : "") +
                $"\n{string.Join("\n", s.Item.MainStats.Concat(s.Item.SubStats).Select(e => e.Raw))}"),
            Text(s.Item is null ? "" : s.Power.ToString(), 12, wrap: false),
            Text(s.Item is null ? "" : $"+{s.Item.EnchantLevel}", 12, wrap: false),
            Chip(Fmt.Status(s.Status), StatusBrush(s.Status)),
            Text(s.Advice + (s.NextSource is not null && s.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak ? $" → {s.NextSource}" : ""), 12),
        });
        return Stack(10, Card(stage), Card(Table("Auto,2*,Auto,Auto,Auto,3*", rows, ["Emplacement", "Pièce", "Niv.", "Ench.", "Statut", "Conseil"])));
    }

    /// <summary>Champ numérique pour un nombre de points relevé en jeu.</summary>
    static NumericUpDown PointsBox(int value) => new()
    {
        Value = value, Minimum = 0, Maximum = 9999, Increment = 1, FormatString = "0", Width = 130,
    };

    Control Skills()
    {
        var sk = r.Skills;
        var set = db.Settings.Skills;
        var unspent = PointsBox(sk.UnspentPoints);
        var apply = new Button { Content = "Recalculer" };
        apply.Click += (_, _) => onInputs(inputs with { UnspentSkillPoints = (int)(unspent.Value ?? 0) });

        var budget = Card(Stack(6,
            Text($"Points de compétence : {sk.SpentPoints} investis + {sk.UnspentPoints} non dépensés = {sk.Budget}", 15, true),
            Text($"1 point = +1 niveau, jusqu'au niveau {set.BaseMax}. Au-delà, seuls le Daevanion et l'équipement donnent des niveaux (colonne « Bonus »). " +
                 $"Spécialisations aux niveaux {string.Join(", ", set.SpecialtyLevels)}. La réinitialisation est gratuite : la colonne « Conseil » redistribue tes {sk.Budget} points " +
                 $"pour la vue {GameDatabase.ModeName(r.Mode)} (garde un préréglage par mode). {set.PointSources}", 12, color: Muted),
            Row(8, Text("Points non dépensés (compteur en jeu) :", 12), unspent, apply),
            Text(sk.HasKnowledge ? $"Fiche de compétences : {sk.Source}" : $"Pas de fiche détaillée pour {r.Profile.Class.Name} : ta barre est considérée comme ta rotation.", 11, color: Muted)));

        Control Delta(SkillAdvice a) => a.Delta == 0
            ? Text(a.Skill.Acquired ? "=" : "non apprise", 12, color: Muted, wrap: false)
            : Chip(a.Delta > 0 ? $"+{a.Delta} → {a.RecommendedBase}" : $"{a.Delta} → {a.RecommendedBase}", a.Delta > 0 ? Good : Bad);
        var rows = sk.Skills.Select(a => new Control[]
        {
            Text(a.Kind == SkillKind.Passive ? "Passive" : a.InBar ? "Active ✓" : "Active", 12, color: Muted, wrap: false),
            Tip(Text(a.Skill.Name, 12, true, a.Skill.Acquired ? null : Muted, wrap: false),
                $"Priorité {GameDatabase.ModeName(r.Mode)} : {a.Priority:0.00} (0 = inutile, 1 = indispensable)\nNiveau visé : {a.Target}"),
            Text(a.Role, 12, wrap: false),
            Text(a.Base.ToString(), 12, wrap: false),
            Tip(Text($"+{a.Bonus}", 12, color: Muted, wrap: false), "Niveaux gratuits : cases Daevanion ouvertes et compétences bonus de l'équipement."),
            Text(a.Total.ToString(), 12, wrap: false),
            Delta(a),
            Text(a.Why, 11, color: a.Unverified ? Warn : Muted),
        });

        var stigmaRows = sk.Stigmas.Where(s => s.Recommended || s.Skill.Equipped || s.Skill.Level > 0).Select(s => new Control[]
        {
            Chip(s.Recommended ? (s.Skill.Equipped ? "Garder" : "Mettre") : "Retirer",
                s.Recommended ? (s.Skill.Equipped ? Good : Accent) : Bad),
            Text(s.Skill.Name, 12, true, s.Skill.Acquired ? null : Muted, wrap: false),
            Text(s.Role, 12, wrap: false),
            Text($"{s.Skill.Level} → {s.Target}", 12, wrap: false),
            Text(s.ShardsToTarget > 0 ? $"{s.ShardsToTarget} fragments" : "", 12, color: Muted, wrap: false),
            Text(s.Why, 11, color: s.Unverified ? Warn : Muted),
        });

        return Stack(10, budget,
            Card(Table("Auto,Auto,Auto,Auto,Auto,Auto,Auto,*", rows, ["Type", "Compétence", "Rôle", "Acheté", "Bonus", "Total", "Conseil", "Pourquoi"])),
            Card(Stack(6,
                Text($"Stigmas — {db.Settings.Stigmas.Slots} emplacements, montés avec des fragments (1 par niveau jusqu'au 5, puis 2, 4 et 8)", 15, true),
                Text($"L'échange de stigmas est gratuit : équipe ceux marqués « Garder » ou « Mettre » pour la vue {GameDatabase.ModeName(r.Mode)}. {db.Settings.Stigmas.ShardSources}", 12, color: Muted),
                Table("Auto,Auto,Auto,Auto,Auto,*", stigmaRows, ["", "Stigma", "Rôle", "Niveau", "Coût", "Pourquoi"]))),
            Stack(0, r.Findings.Where(f => f.Area == Area.Skills && f.Severity != Severity.Info).Select(f => FindingCard(f)).ToArray()));
    }

    Control Daevanion()
    {
        var d = r.Daevanion;
        var panel = Stack(10);
        string PoolName(string pool) => db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool, pool);

        // Points : dépensés (calculés) et non dépensés (saisis).
        var boxes = new Dictionary<string, NumericUpDown>();
        var budgetRows = d.Budgets.Where(b => DaevanionPlanner.Relevant(b.Pool, r.Mode)).Select(b =>
        {
            var box = boxes[b.Pool] = PointsBox(b.Unspent);
            return new Control[] { Text(PoolName(b.Pool), 12, true, wrap: false), Text($"{b.Spent} dépensés", 12, wrap: false), box,
                Text($"non dépensés · tout remplir : {b.FillCost}", 12, color: Muted, wrap: false) };
        }).ToList();
        var apply = new Button { Content = "Recalculer" };
        apply.Click += (_, _) => onInputs(inputs with
        {
            UnspentDaevanion = Value(boxes, DaevanionPlanner.NormalPool, inputs.UnspentDaevanion),
            UnspentAriel = Value(boxes, "PvE", inputs.UnspentAriel),
            UnspentAzphel = Value(boxes, "PvP", inputs.UnspentAzphel),
        });
        panel.Children.Add(Card(Stack(6,
            Text("Points Daevanion", 15, true),
            Text("Une case coûte 1 (gris), 2 (vert : passif +1), 3 (bleu : active +1) ou 4 points (orange : gros bonus en %). " +
                 "Les points dépensés sont calculés depuis ta fiche ; indique tes points non dépensés (visibles en jeu) pour un plan exact. " +
                 db.Settings.Daevanion.PointSources, 12, color: Muted),
            Table("Auto,Auto,Auto,*", budgetRows), apply)));

        var summary = d.Boards.Where(b => b.Relevant).Select(b => new Control[]
        {
            Text(b.Board.Name, 12, true, wrap: false),
            Text(PoolName(b.Pool), 12, color: Muted, wrap: false),
            Text($"{b.Board.OpenNodes}/{b.Board.TotalNodes} cases · {b.Spent}/{b.Total} points", 12, wrap: false),
            Text($"{b.Reachable} accessibles", 12, wrap: false),
        });
        panel.Children.Add(Card(Stack(6, Text("Plateaux", 15, true), Table("Auto,Auto,Auto,Auto", summary))));

        foreach (var respec in d.Respecs)
        {
            var worth = respec.Remove.Count > 0 && respec.Gain >= db.Settings.Daevanion.RespecMinGain;
            var card = Stack(4, Text($"Réorganisation — {PoolName(respec.Pool)}", 15, true));
            if (!worth)
            {
                card.Children.Add(Text(respec.Remove.Count == 0
                    ? "Aucune case ouverte n'est à retirer : ton chemin actuel fait partie de la meilleure répartition de tes points."
                    : $"Ton placement est proche de l'optimal : réorganiser ne rapporterait que {Fmt.Pct(Math.Max(0, respec.Gain))}.", 12, color: Muted));
            }
            else
            {
                card.Children.Add(Text($"Avec les mêmes {respec.Budget} points, ce chemin rapporte {Fmt.Pct(respec.Gain)} de plus pour ta classe en {GameDatabase.ModeName(r.Mode)}. " +
                                       "Sur les plateaux ci-dessous : contour rouge = case à retirer, chiffre vert = case à prendre (dans l'ordre).", 12, color: Muted));
                var left = Stack(2, Text($"À retirer ({respec.Remove.Count} cases, {Fmt.Num(respec.Remove.Sum(x => x.Cost))} points)", 13, true, Bad));
                foreach (var x in respec.Remove)
                    left.Children.Add(Text($"✕ {x.Board.Name} l.{x.Node.Row} c.{x.Node.Col} — {x.Effects} · {Reason(x.Node)}", 12));
                var right = Stack(2, Text($"À prendre à la place ({respec.Add.Count} cases, dans l'ordre)", 13, true, Good));
                foreach (var x in respec.Add)
                    right.Children.Add(Text($"{x.Step,2}. {x.Board.Name} l.{x.Node.Row} c.{x.Node.Col} — {x.Effects} · {Reason(x.Node)}", 12, color: x.IsPassage ? Muted : null));
                var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
                Grid.SetColumn(right, 2);
                columns.Children.Add(left);
                columns.Children.Add(right);
                card.Children.Add(columns);
            }
            panel.Children.Add(Card(card));
        }

        var boards = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in d.Boards.Where(b => b.Relevant && b.Board.Nodes.Count > 0))
            boards.Children.Add(new Border { Margin = new Thickness(0, 0, 12, 12), Child = Card(BoardGrid(b.Board, b.Pool, d), bottom: 0) });
        panel.Children.Add(boards);

        foreach (var pool in d.ByPool)
        {
            var list = Stack(2, Text($"Prochaines cases — {PoolName(pool.Key)}", 15, true),
                Text("Sans rien retirer : où mettre tes prochains points, dans l'ordre (meilleur gain par point, cases de passage comprises).", 12, color: Muted));
            foreach (var s in pool)
                list.Children.Add(Text($"{s.Step,2}. {s.Board.Name} (ligne {s.Node.Row}, col. {s.Node.Col}) — {s.Effects}{(s.IsPassage ? " · passage" : "")}{(s.Affordable ? " · faisable maintenant" : "")}",
                    12, color: s.IsPassage ? Muted : s.Affordable ? Good : null));
            panel.Children.Add(Card(list));
        }
        return panel;
    }

    /// <summary>Pourquoi une case vaut peu ou beaucoup : importance de sa stat ou de sa compétence pour ta classe dans ce mode.</summary>
    string Reason(DaevanionNode n)
    {
        var parts = new List<string>();
        foreach (var e in n.Effects)
        {
            if (e.IsSkill && r.Skills.Skills.FirstOrDefault(a => a.Skill.Name == e.SkillName) is { } a)
                parts.Add($"{a.Role.ToLowerInvariant()}, importance {a.Priority:0.00}");
            else if (e.IsStat)
                parts.Add($"importance {r.Profile.Weight(e.StatKey!):0.00}" + (r.Stats.Categories.FirstOrDefault(c => c.Category == db.Stat(e.StatKey!)?.Category) is { Gap: > 0.06 } c
                    ? $", {GameDatabase.CategoryName(c.Category).ToLowerInvariant()} déjà au-dessus de sa cible" : ""));
        }
        return parts.Count == 0 ? "passage" : string.Join(" ; ", parts.Distinct());
    }

    static int Value(Dictionary<string, NumericUpDown> boxes, string pool, int current) =>
        boxes.TryGetValue(pool, out var box) ? (int)(box.Value ?? 0) : current;

    /// <summary>
    /// Plateau 15×15. Si une réorganisation est conseillée : contour rouge = à retirer, chiffre vert = à prendre.
    /// Sinon : chiffre blanc = prochaines cases. Cases ouvertes en couleur de rareté.
    /// </summary>
    Control BoardGrid(DaevanionBoard board, string pool, DaevanionPlan plan)
    {
        const int size = 22;
        var respec = plan.Respecs.FirstOrDefault(x => x.Pool == pool && x.Remove.Count > 0 && x.Gain >= db.Settings.Daevanion.RespecMinGain);
        var numbers = respec is not null
            ? respec.Add.Where(s => s.Board.Id == board.Id).ToDictionary(s => s.Node.Id, s => s.Step)
            : plan.Steps.Where(s => s.Board.Id == board.Id).ToDictionary(s => s.Node.Id, s => s.Step);
        var removed = respec?.Remove.Where(x => x.Board.Id == board.Id).Select(x => x.Node.Id).ToHashSet() ?? [];
        var rows = board.Nodes.Max(n => n.Row);
        var cols = board.Nodes.Max(n => n.Col);
        var canvas = new Canvas { Width = cols * size, Height = rows * size };
        foreach (var n in board.Nodes)
        {
            var color = n.IsStart ? Brushes.White : Brush(db.Grade(n.Grade).Color);
            var isRemoved = removed.Contains(n.Id);
            var step = numbers.TryGetValue(n.Id, out var s) ? s : (int?)null;
            var cell = new Border
            {
                Width = size - 3, Height = size - 3,
                CornerRadius = new CornerRadius(n.IsSkill ? 10 : 3),
                Background = n.Open || n.IsStart ? color : CardBackground,
                BorderBrush = isRemoved ? Bad : step is not null ? (respec is not null ? Good : Brushes.White) : color,
                BorderThickness = new Thickness(isRemoved || step is not null ? 2 : 1),
                Opacity = n.Open || n.IsStart || step is not null ? 1 : 0.45,
                Child = isRemoved
                    ? new TextBlock { Text = "✕", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Bad, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    : step is null ? null : new TextBlock
                    {
                        Text = step.ToString(), FontSize = 10, FontWeight = FontWeight.Bold,
                        Foreground = respec is not null ? Good : Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
            };
            var tip = $"{(n.Name.Length > 0 ? n.Name : n.Type)} — {db.Settings.Daevanion.NodeCost.GetValueOrDefault(n.Grade, 0)} point(s)\n{string.Join("\n", n.Effects.Select(e => e.Raw))}" +
                      (isRemoved ? "\nÀ retirer : rapporte peu pour ta classe dans ce mode" : n.Open ? "\n(ouverte)" : step is not null ? $"\nÉtape {step} du plan" : "");
            ToolTip.SetTip(cell, tip);
            Canvas.SetLeft(cell, (n.Col - 1) * size);
            Canvas.SetTop(cell, (n.Row - 1) * size);
            canvas.Children.Add(cell);
        }
        return Stack(6, Text($"{board.Name} — {board.OpenNodes}/{board.TotalNodes}", 13, true), canvas,
            Text(respec is not null
                ? "Rond = compétence · carré = stat · ✕ rouge = à retirer · chiffre vert = à prendre"
                : "Rond = compétence · carré = stat · chiffre = prochaines cases", 11, color: Muted));
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
