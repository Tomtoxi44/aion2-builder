using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Présentation « Confort », réglage « l'essentiel d'abord » (choisi sur la page de maquettes du 14/11/2026) : la première page
/// d'un écran s'ouvre sur une phrase qui résume et, au plus, trois choses à faire dans l'ordre ; le détail (chiffres clés,
/// panneaux titrés) est replié dessous et s'ouvre d'un clic. Les sous-pages, qui sont déjà du détail, restent entières.
/// Rien n'est changé en présentation IDE.
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>Une chose à faire : son titre, une ligne d'explication (Good : un gain, écrit en vert), le mot du bouton, où il mène.</summary>
    sealed record LeadAction(string Title, string? Text, string Button, Action Go, string? Tip = null, bool Good = false);

    /// <summary>L'en-tête d'une page : la phrase (avec son chiffre en couleur), une ligne dessous, les actions, et combien d'autres attendent.</summary>
    sealed record PageLead(string Before, string Figure, IBrush FigureColor, string After, string Sub, List<LeadAction> Actions, int More = 0);

    /// <summary>Marge des blocs d'une page (<see cref="Page"/>) : c'est à elle qu'on reconnaît le corps d'une page.</summary>
    static readonly Thickness PageMargin = new(20, 14, 20, 24);

    /// <summary>Les domaines de conseils de la page ouverte, si elle s'ouvre sur l'essentiel ; null pour une sous-page ou un écran sans note.</summary>
    Area[]? LeadAreas() => s.Screen switch
    {
        Screen.Home => [Area.Stats, Area.Gear, Area.Enchant, Area.Skills, Area.Daevanion, Area.Pets, Area.General],
        Screen.Stats => [Area.Stats],
        Screen.Gear when s.GearPage == GearPage.Overview => [Area.Gear, Area.Enchant],
        Screen.Skills when s.SkillPage == SkillPage.Overview => [Area.Skills],
        Screen.Daeva => [Area.Daevanion],
        Screen.Pets => [Area.Pets],
        _ => null,
    };

    /// <summary>La page ouverte s'ouvre sur l'essentiel : ses cartes « à faire en priorité » d'origine feraient doublon.</summary>
    bool EssentialHere => Skin.Comfort && (LeadAreas() is not null || s.Screen == Screen.Craft && s.CraftPage == CraftPage.Overview);

    /// <summary>L'essentiel d'un écran noté : sa note en une phrase, puis les trois conseils du plan d'action qui rapportent le plus.</summary>
    PageLead? ReportLead()
    {
        if (LeadAreas() is not { } areas || ScoreOf(r, s.Screen) is not { } score) return null;
        var todo = r.ActionPlan.Where(f => areas.Contains(f.Area) && !IsDone(f)).ToList();
        var subject = s.Screen switch
        {
            Screen.Home => $"{r.Character.Name} vaut",
            Screen.Stats => "Tes stats valent",
            Screen.Gear => "Ton équipement vaut",
            Screen.Skills => "Tes compétences valent",
            Screen.Daeva => "Ton Daevanion vaut",
            _ => "Ta collection de familiers vaut",
        };
        var sub = todo.Count switch
        {
            0 => "Rien à faire ici pour l'instant : tout est au niveau de ton étape.",
            1 => "Une chose le ferait monter.",
            <= 3 => $"{todo.Count} choses le feraient monter. La première rapporte le plus.",
            _ => "Voici les trois choses qui rapportent le plus, dans l'ordre.",
        };
        return new PageLead(subject + " ", score.ToString(), Tok.Score(score), $" sur 100 en {GameDatabase.ModeName(r.Mode)}.", sub,
            todo.Take(3).Select(f => new LeadAction(f.Title, f.Action, "y aller", () => Go(f), f.Detail)).ToList(), Math.Max(0, todo.Count - 3));
    }

    /// <summary>L'essentiel du Craft : ce que rapporte la proposition avec ton stock et ses trois premières étapes ; sans stock, les trois recettes les plus rentables.</summary>
    PageLead CraftLead(CraftModel m)
    {
        if (m.Proposal.Count > 0)
        {
            var sum = m.Proposal.Sum(x => x.Total);
            return new PageLead("Avec ton stock, fabriquer te rapporte ", $"+{Fmt.Big(sum)}", Tok.Good, " Kinah.",
                "C'est ce que tu gagnes de plus qu'en vendant tes ingrédients tels quels. Dans l'ordre :",
                m.Proposal.Take(3).Select(x =>
                {
                    var id = x.Plan.Recipe.Id;
                    return new LeadAction($"Fabrique {Fmt.Big(x.N)} × {x.Plan.Recipe.Output.Name}", $"+{Fmt.Big(x.Total)} Kinah", "voir la recette", () => OpenRecipe(id),
                        "Ouvrir la recette : ses ingrédients et le calcul.", true);
                }).ToList(), Math.Max(0, m.Proposal.Count - 3));
        }
        var winners = m.Plans.Where(p => p.Locked is null && p.Gain > 0).OrderByDescending(p => p.Gain).ToList();
        if (winners.Count == 0)
            return new PageLead("Rien de rentable à fabriquer aux prix connus", "", Tok.Text, ".",
                "Note d'autres prix dans l'écran Hôtel des ventes, ou d'autres recettes : le calcul se refait tout seul.", []);
        return new PageLead("", winners.Count.ToString(), Tok.Good, winners.Count > 1 ? " recettes rapportent en ce moment." : " recette rapporte en ce moment.",
            m.HasStock ? "Ton stock ne suffit pour aucune : voici les plus rentables, par fabrication." : "Les plus rentables, par fabrication :",
            winners.Take(3).Select(p =>
            {
                var id = p.Recipe.Id;
                return new LeadAction(p.Recipe.Output.Name, $"+{Fmt.Big(p.Gain!.Value)} Kinah par fabrication", "voir la recette", () => OpenRecipe(id),
                    "Ouvrir la recette : ses ingrédients et le calcul.", true);
            }).ToList(), Math.Max(0, winners.Count - 3));
    }

    /// <summary>La phrase en grand, sa ligne d'explication, puis une carte numérotée par chose à faire (clic : y aller).</summary>
    Control LeadBlock(PageLead lead)
    {
        var sentence = new TextBlock { FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Tok.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 30 };
        sentence.Inlines = [new Run(lead.Before), new Run(lead.Figure) { Foreground = lead.FigureColor }, new Run(lead.After)];
        var head = new StackPanel { Spacing = 4, Children = { sentence, T(lead.Sub, 14, Tok.Text2, wrap: true).WithLineHeight(1.45) } };
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(0, 6, 0, 6), Children = { head } };
        if (lead.Actions.Count == 0) return panel;
        var cards = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        var n = 0;
        foreach (var a in lead.Actions)
        {
            n++;
            var go = new Border
            {
                Background = Accent.Main, CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                Child = T(a.Button + " ›", 12, Tok.OnAccent, FontWeight.SemiBold),
            };
            var body = new StackPanel
            {
                Spacing = 6, Margin = new Thickness(14, 12, 14, 14),
                Children =
                {
                    T(n == 1 ? "1 · à faire d'abord" : n.ToString(), 11, Accent.Main, FontWeight.SemiBold, mono: true),
                    T(a.Title, 14, Tok.Text, FontWeight.SemiBold, wrap: true).WithLineHeight(1.3),
                },
            };
            if (!string.IsNullOrWhiteSpace(a.Text)) body.Children.Add(T(a.Text, 13, a.Good ? Tok.Good : Tok.Text2, a.Good ? FontWeight.SemiBold : FontWeight.Normal, wrap: true).WithLineHeight(1.4));
            body.Children.Add(go);
            var stripe = new Border { Height = 3, Background = n == 1 ? Accent.Main : Tok.Border };
            DockPanel.SetDock(stripe, Dock.Top);
            var card = Clickable(new DockPanel { Children = { stripe, body } }, a.Go, Tok.Panel, Tok.TileHover, new Thickness(0));
            card.CornerRadius = new CornerRadius(Skin.Radius);
            card.BorderBrush = Tok.Border;
            card.BorderThickness = new Thickness(1);
            card.ClipToBounds = true;
            card.Margin = new Thickness(0, 0, 12, 0);
            cards.Children.Add(a.Tip is { Length: > 0 } tip ? Tip(card, tip) : card);
        }
        panel.Children.Add(cards);
        if (lead.More > 0 && s.Screen != Screen.Craft)
        {
            var more = Clickable(T($"{Fmt.Plural(lead.More, "autre conseil", "autres conseils")} dans « Tous les conseils » ›", 12, Accent.Main), () => GoTo(Screen.Details), null, Tok.Hover, new Thickness(4, 2));
            more.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(more);
        }
        panel.Children.Add(T("Le détail", 12, Tok.Muted, FontWeight.SemiBold).WithMargin(0, 6, 0, -4));
        return panel;
    }

    /// <summary>Le titre d'un panneau sans son compteur (« Tes séries · 2 » → « Tes séries ») : la clé de son état replié.</summary>
    string FoldKey(string title) => $"fold:{s.Screen}:{title.Split(" · ")[0]}";

    /// <summary>Un panneau ouvert d'office (les crafts épinglés : tu les as mis là pour les voir) ; un clic inverse, pour la session.</summary>
    static bool OpenByDefault(string title) => title.StartsWith("Épinglés", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replie un panneau titré : sa barre de titre reçoit une flèche et devient cliquable, son contenu est caché tant qu'il est
    /// fermé. L'état est gardé le temps de la session, par écran et par titre.
    /// </summary>
    void Fold(Border card, string title)
    {
        if (card.Child is not DockPanel { Children.Count: >= 2 } dock || dock.Children[0] is not Border head) return;
        var key = FoldKey(title);
        var open = OpenByDefault(title) ^ s.Open.Contains(key);
        if (head.Child is DockPanel line && line.Children.Count > 0 && line.Children[^1] is TextBlock label)
        {
            label.Text = (open ? "▾  " : "▸  ") + label.Text;
            if (!open) label.Foreground = Tok.Text2;
        }
        head.Cursor = new Cursor(StandardCursorType.Hand);
        head.PointerPressed += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            update(st => { if (!st.Open.Remove(key)) st.Open.Add(key); });
        };
        head.PointerEntered += (_, _) => head.Background = Tok.HoverStrong;
        head.PointerExited += (_, _) => head.Background = Tok.Chrome;
        ToolTip.SetTip(head, open ? "Clic : replier" : "Clic : ouvrir");
        if (open) return;
        head.BorderThickness = new Thickness(0);
        for (var i = 1; i < dock.Children.Count; i++) dock.Children[i].IsVisible = false;
    }

    /// <summary>
    /// Présentation Confort : met l'essentiel en tête de la page et replie le détail. Les chiffres clés (une rangée de
    /// <see cref="Ui.Kpi"/>) deviennent un panneau « Chiffres clés » ; un titre de section suivi de son contenu devient un panneau.
    /// </summary>
    Control Compose(Control content)
    {
        if (!Skin.Comfort || !EssentialHere || content is not StackPanel page) return content;
        var body = page.Children.OfType<StackPanel>().FirstOrDefault(p => p.Margin == PageMargin);
        if (body is null) return content;
        var lead = s.Screen == Screen.Craft ? Crafting() is { } m ? CraftLead(m) : null : ReportLead();
        if (lead is null) return content;
        var blocks = body.Children.ToList();
        body.Children.Clear();
        body.Children.Add(LeadBlock(lead));
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            // Une rangée de chiffres clés : repliée sous « Chiffres clés ».
            if (block is Avalonia.Controls.Primitives.UniformGrid { Children.Count: > 0 } grid && grid.Children.All(c => c is Border { Tag: "kpi" }))
            {
                var card = Card(grid, "Chiffres clés", T($"{grid.Children.Count} chiffres", 11, Tok.Muted, mono: true));
                Fold(card, "Chiffres clés");
                body.Children.Add(card);
                continue;
            }
            // Un titre de section (« Ce qui rapporte · 12 ») et le bloc qui le suit : un panneau.
            if (block is TextBlock { Tag: "label" } title && i + 1 < blocks.Count && blocks[i + 1] is not Border { Tag: string })
            {
                var name = title.Text ?? "";
                var card = Card(blocks[++i], name, padding: new Thickness(12, 12, 0, 0));
                Fold(card, name);
                body.Children.Add(card);
                continue;
            }
            if (block is Border { Tag: string cardTitle } titled && cardTitle != "kpi") Fold(titled, cardTitle);
            // Un panneau titré suivi de ses notes (même groupe) : replié, les notes suivent.
            else if (block is StackPanel { Orientation: Orientation.Vertical, Children.Count: > 0 } group && group.Children[0] is Border { Tag: string groupTitle } first && groupTitle != "kpi")
            {
                Fold(first, groupTitle);
                if (!(OpenByDefault(groupTitle) ^ s.Open.Contains(FoldKey(groupTitle))))
                    for (var j = 1; j < group.Children.Count; j++) group.Children[j].IsVisible = false;
            }
            body.Children.Add(block);
        }
        return content;
    }
}
