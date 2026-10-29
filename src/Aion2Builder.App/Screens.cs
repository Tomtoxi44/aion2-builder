using System.Text.RegularExpressions;
using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Aion2Builder.GameData;
using Aion2Builder.Model;
using Aion2Builder.Storage;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

enum Screen { Import, Home, Stats, Gear, Skills, Build, Combat, Daeva, Pets, Simulate, Progress, Details }

/// <summary>Sous-pages de l'écran Équipement (sous-menu du rail).</summary>
enum GearPage { Overview, Roadmap, Pieces, Enchant, Stones, Arcana }

/// <summary>Sous-pages de l'écran Compétences.</summary>
enum SkillPage { Overview, Specs, Moves, Stigmas }

/// <summary>Sous-pages de l'écran Build.</summary>
enum BuildPage { Compare, Rotations, Differences }

/// <summary>Barre secondaire d'un écran (à côté du rail) : titre, boutons de l'en-tête, puis ses lignes.</summary>
sealed record SidePanel(string Title, Control? Actions, Control Body);

/// <summary>État de l'interface : écran, sélections et blocs repliés (hors rapport).</summary>
sealed class UiState
{
    public Screen Screen { get; set; } = Screen.Home;
    public GearPage GearPage { get; set; } = GearPage.Overview;
    public SkillPage SkillPage { get; set; } = SkillPage.Overview;
    public BuildPage BuildPage { get; set; } = BuildPage.Compare;
    /// <summary>Combat : le tableau de tous les combats au lieu du détail d'un combat.</summary>
    public bool CombatList { get; set; }
    public string? Slot { get; set; }
    public string? Stat { get; set; }
    public string? Skill { get; set; }
    public int? Board { get; set; }
    public string? Genus { get; set; }
    public bool HomeHow { get; set; }
    public bool StatsHow { get; set; }
    /// <summary>Listes longues (prochains points, prochaines cases) affichées en entier.</summary>
    public bool AllNext { get; set; }
    /// <summary>Daevanion : route conçue depuis le centre au lieu du plan à partir du plateau actuel.</summary>
    public bool DaevaRoute { get; set; }
    /// <summary>Détails : afficher aussi les simples informations.</summary>
    public bool ShowInfo { get; set; }
    /// <summary>Stats : afficher aussi les stats qui ne comptent pas pour la classe.</summary>
    public bool AllStats { get; set; }
    public bool SkillsMore { get; set; }
    public bool SkillBonus { get; set; }
    public bool SkillNext { get; set; }
    /// <summary>Simulation « et si » : changements appliqués à une copie du personnage.</summary>
    public SimulationOptions Sim { get; set; } = new();
    /// <summary>Rotations simulées de l'écran Build : 1 cible ou 4.</summary>
    public int RotationTargets { get; set; } = 1;
    /// <summary>Combat : combat ouvert (null = historique), séance dont le global est ouvert, sort sélectionné.</summary>
    public int? CombatIndex { get; set; }
    public int? CombatGroup { get; set; }
    public string? CombatSkill { get; set; }
    public HashSet<Area> Collapsed { get; } = [];
    /// <summary>Sections repliables ouvertes (refonte visuelle), par identifiant.</summary>
    public HashSet<string> Open { get; } = [];
}

/// <summary>
/// Écrans de la refonte « IDE » : pour chacun, le contenu central et l'inspecteur de droite.
/// Le rapport (<see cref="AdviceReport"/>) ne change pas ; seuls la présentation et la navigation changent.
/// </summary>
sealed class ScreenViews(GameDatabase db, AdviceReport r, PlayerInputs inputs, UiState s,
    Action<Action<UiState>> update, Action<PlayerInputs> onInputs, string? petKeyHint, Action<string?> onPets,
    IReadOnlyList<ProgressPoint> progress, Func<SimulationOptions, AdviceReport> simulate,
    IReadOnlyList<(string Path, CombatLog Log)> combats, Action importCombat, Action<string> deleteCombat, Action reloadCombats)
{
    public static readonly (Screen Screen, string Title, string Short)[] Nav =
    [
        (Screen.Home, "Accueil", "Accueil"), (Screen.Stats, "Stats", "Stats"), (Screen.Gear, "Équipement", "Équip."),
        (Screen.Skills, "Compétences", "Compét."), (Screen.Build, "Build et rotation", "Build"), (Screen.Combat, "Combat (Pet Meter, NotMeter)", "Combat"), (Screen.Daeva, "Daevanion", "Daeva."), (Screen.Pets, "Familiers", "Famil."),
        (Screen.Simulate, "Simulation « et si »", "Et si"), (Screen.Progress, "Progression", "Prog."),
        (Screen.Details, "Détails", "Détails"),
    ];

    public static readonly (GearPage Page, string Title, string Short)[] GearPages =
    [
        (GearPage.Overview, "Vue d'ensemble", "Vue"), (GearPage.Roadmap, "Feuille de route", "Route"), (GearPage.Pieces, "Pièces et sources", "Pièces"),
        (GearPage.Enchant, "Enchantement", "Enchant."), (GearPage.Stones, "Pierres et gravures", "Pierres"), (GearPage.Arcana, "Arcanes", "Arcanes"),
    ];

    public static readonly (SkillPage Page, string Title)[] SkillPages =
    [
        (SkillPage.Overview, "Vue d'ensemble"), (SkillPage.Specs, "Spécialisations"), (SkillPage.Moves, "Déplacements"),
        (SkillPage.Stigmas, "Stigmas"),
    ];

    public static readonly (BuildPage Page, string Title)[] BuildPages =
    [
        (BuildPage.Compare, "Les deux builds"), (BuildPage.Rotations, "Rotations simulées"), (BuildPage.Differences, "Différences"),
    ];

    /// <summary>Icônes des lignes de la barre secondaire (tracés 14 × 14, trait seul).</summary>
    static class Icons
    {
        public const string Grid = "M2 2h4v4H2zM8 2h4v4H8zM2 8h4v4H2zM8 8h4v4H8z";
        public const string Route = "M2 12c3 0 3-4 5-4s2-4 5-4M2 12h.01M12 4h.01";
        public const string Box = "M2 5l5-3 5 3v5l-5 3-5-3zM2 5l5 3 5-3M7 8v5";
        public const string Up = "M7 12V3M3.5 6.5L7 3l3.5 3.5";
        public const string Gem = "M4 2h6l2 3-5 7-5-7zM2 5h10";
        public const string List = "M2 3h10M2 7h10M2 11h10";
        public const string Swap = "M2 4h9M8 1.5L11 4 8 6.5M12 10H3M6 7.5L3 10l3 2.5";
        public const string Shield = "M7 1.5l5 2v3.5c0 3-2.2 4.8-5 5.5-2.8-.7-5-2.5-5-5.5V3.5z";
        public const string Board = "M2 2h10v10H2zM2 7h10M7 2v10";
        public const string Play = "M3 2l9 5-9 5z";
        public const string Diff = "M4 2v10M10 2v10M1.5 5h5M7.5 9h5";
        public const string Card = "M3 1.5h8v11H3zM5 5h4M5 8h4M5 10.5h2";
        public const string Star = "M7 1.5l1.7 3.5 3.8.5-2.8 2.6.7 3.8L7 10.1l-3.4 1.8.7-3.8L1.5 5.5l3.8-.5z";
    }

    /// <summary>Barre secondaire de l'écran ouvert (sous-pages, combats, plateaux…), null pour les écrans d'une seule page.</summary>
    public SidePanel? Side() => s.Screen switch
    {
        Screen.Gear => GearSide(),
        Screen.Combat => CombatSide(),
        Screen.Daeva => DaevaSide(),
        Screen.Skills => SkillsSide(),
        Screen.Build => BuildSide(),
        _ => null,
    };

    /// <summary>Ce qui suit l'écran dans le fil d'Ariane : la sous-page, le combat ou le plateau ouvert.</summary>
    public string? Crumb() => s.Screen switch
    {
        Screen.Gear => GearPages.First(p => p.Page == s.GearPage).Title,
        Screen.Skills => SkillPages.First(p => p.Page == s.SkillPage).Title,
        Screen.Build => BuildPages.First(p => p.Page == s.BuildPage).Title,
        Screen.Daeva => SelectedBoard?.Board.Name,
        Screen.Combat => s.CombatList ? "Tous les combats"
            : CombatLogShown is { } log ? s.CombatGroup is not null ? $"Séance · {log.Target}" : $"{log.Target} · {log.ImportedAtUtc.ToLocalTime():HH:mm}" : null,
        _ => null,
    };

    /// <summary>
    /// Ligne de la barre secondaire : icône, titre, badge à droite, aperçu dessous (et un contrôle en plus) ; la ligne ouverte
    /// a le trait de couleur secondaire à gauche et le fond « dim », comme un nœud ouvert dans l'explorateur d'un éditeur.
    /// </summary>
    Control SideItem(string? icon, string title, string? badge, IBrush? badgeColor, string? preview, bool selected, Action open, Control? extra = null, string? tip = null)
    {
        var text = new StackPanel { Spacing = 2, Children = { T(title, 13, selected ? Tok.Text : Tok.Text2, selected ? FontWeight.SemiBold : FontWeight.Normal) } };
        // Aperçu sur deux lignes au plus (coupé avec « … » au-delà) plutôt que tronqué à la largeur de la barre.
        if (preview is not null) text.Children.Add(new TextBlock { Text = preview, FontSize = 11, Foreground = Tok.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (extra is not null) text.Children.Add(extra);
        var dock = new DockPanel();
        if (badge is not null)
        {
            var b = new Border
            {
                Background = Tok.HoverStrong, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top, Child = T(badge, 11, badgeColor ?? Tok.Text2, FontWeight.SemiBold, mono: true),
            };
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        if (icon is not null)
        {
            var glyph = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(icon), Stroke = selected ? Accent.Main : Tok.Muted, StrokeThickness = 1.4, StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0),
            };
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
        }
        dock.Children.Add(text);
        var row = Clickable(dock, open, selected ? Accent.Dim : null, selected ? null : Tok.Hover, new Thickness(12, 7, 10, 7));
        row.BorderBrush = selected ? Accent.Main : Brushes.Transparent;
        row.BorderThickness = new Thickness(2, 0, 0, 0);
        return tip is null ? row : Tip(row, tip);
    }

    static TextBlock SideGroup(string title) => Label(title).WithMargin(14, 12, 14, 4);

    public const string HowText = "Score global = moyenne pondérée des quatre notes. Stats : part de tes stats ajustables placée dans des stats " +
        "utiles à ta classe. Équipement : pièces au niveau de ton palier. Compétences : niveaux visés atteints, pondérés par leur importance. " +
        "Daevanion : part des plateaux utiles déjà ouverte. Les actions sont classées par gain attendu / ressources dépensées.";

    public static string Title(Screen screen) => Nav.FirstOrDefault(n => n.Screen == screen).Title ?? "Import";

    public static int? ScoreOf(AdviceReport r, Screen screen) => screen switch
    {
        Screen.Home => r.Scores.Overall,
        Screen.Stats => r.Scores.Stats,
        Screen.Gear => r.Scores.Gear,
        Screen.Skills => r.Scores.Skills,
        Screen.Daeva => r.Scores.Daevanion,
        Screen.Pets => r.Pets.Progress,
        _ => null,
    };

    public static string ModeLabel(GameMode mode) => mode == GameMode.PvE ? "vue JcE" : "vue JcJ";

    /// <summary>Pièces qui demandent une action (barre d'état).</summary>
    public static int Todo(AdviceReport r) => r.Gear.Slots.Count(x => x.Status != SlotStatus.Ok && !(x.Status == SlotStatus.Empty && x.Priority <= 1));

    public (Control Content, Control? Inspector) Build() => s.Screen switch
    {
        Screen.Home => (HomeScreen(), HomeInspector()),
        Screen.Gear => (GearScreen(), GearInspector()),
        Screen.Stats => (Stats(), StatInspector()),
        Screen.Skills => (Skills(), SkillInspector()),
        Screen.Build => (BuildScreen(), BuildInspector()),
        Screen.Combat => (CombatScreen(), CombatInspector()),
        Screen.Daeva => (Daeva(), DaevaInspector()),
        Screen.Pets => (Pets(), PetInspector()),
        Screen.Simulate => (Simulate(), SimulateInspector()),
        Screen.Progress => (ProgressScreen(), null),
        _ => (Details(), null),
    };

    string Mode => ModeLabel(r.Mode);

    // ----- Navigation depuis un conseil -----

    static (string Icon, IBrush Color) Sev(Severity severity) => severity switch
    {
        Severity.Critical => ("✕", Tok.Bad),
        Severity.Important => ("!", Tok.Warn),
        Severity.Advice => ("i", Tok.Info),
        _ => ("·", Tok.Muted),
    };

    /// <summary>
    /// Nom cité dans un texte : d'abord la dernière citation « … » (« remplace « DÉF » par « PV » » → PV),
    /// sinon la première mention (la plus longue à égalité : « Boost de DGT CRIT » avant « CRIT »).
    /// </summary>
    static string? Named(string text, IEnumerable<(string Key, string Name)> candidates)
    {
        var list = candidates.Where(c => c.Name.Length > 0).ToList();
        var quoted = list.Select(c => (c.Key, Index: text.LastIndexOf($"« {c.Name} »", StringComparison.Ordinal)))
            .Where(x => x.Index >= 0).OrderByDescending(x => x.Index).FirstOrDefault();
        if (quoted.Key is not null) return quoted.Key;
        return list.Select(c => (c.Key, c.Name, Match: Regex.Match(text, $@"(?<!\w){Regex.Escape(c.Name)}(?!\w)")))
            .Where(x => x.Match.Success).OrderBy(x => x.Match.Index).ThenByDescending(x => x.Name.Length)
            .Select(x => x.Key).FirstOrDefault();
    }

    public void Go(Finding f) => update(st =>
    {
        st.Screen = f.Area switch
        {
            Area.Gear or Area.Enchant => Screen.Gear,
            Area.Stats => Screen.Stats,
            Area.Skills => Screen.Skills,
            Area.Daevanion => Screen.Daeva,
            Area.Pets => Screen.Pets,
            _ => Screen.Details,
        };
        var text = $"{f.Title} {f.Action} {f.Detail}";
        switch (st.Screen)
        {
            case Screen.Gear:
                st.GearPage = f.Area == Area.Enchant ? GearPage.Enchant : GearPage.Overview;
                st.Slot =r.Gear.Slots.FirstOrDefault(x => f.Title.StartsWith(x.SlotName, StringComparison.Ordinal))?.Slot
                          ?? (f.Area == Area.Enchant ? r.Gear.Slots.Where(x => x.Status == SlotStatus.Enchant).OrderByDescending(x => x.Priority).FirstOrDefault()?.Slot : null)
                          ?? st.Slot;
                break;
            case Screen.Stats:
                st.Stat = Named(text, r.Stats.Stats.Select(x => (x.Def.Key, x.Def.Name))) ?? st.Stat;
                break;
            case Screen.Skills:
                st.Skill = Named(text, r.Skills.Skills.Select(x => (x.Skill.Name, x.Skill.Name))
                    .Concat(r.Skills.Stigmas.Select(x => (x.Skill.Name, x.Skill.Name)))) ?? st.Skill;
                break;
            case Screen.Daeva:
                st.Board = Named(text, r.Daevanion.Boards.Select(x => (x.Board.Id.ToString(), x.Board.Name))) is { } id ? int.Parse(id) : st.Board;
                break;
            case Screen.Pets:
                st.Genus = Named(text, r.Pets.Genera.Select(x => (x.Key, x.Name))) ?? st.Genus;
                break;
        }
    });

    void GoTo(Screen screen) => update(st => st.Screen = screen);

    // ----- Mise en page commune (refonte) : barre de section, puis des panneaux -----

    /// <summary>Page : barre de section (titre ; badges ou boutons à droite), puis les blocs espacés (les null sont sautés).</summary>
    static StackPanel Page(string title, Control? right, params Control?[] blocks)
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(20, 14, 20, 24) };
        foreach (var b in blocks)
            if (b is not null) body.Children.Add(b);
        return new StackPanel { Children = { SectionHeader(title, right, 20), body } };
    }

    /// <summary>Badges côte à côte (les null sont sautés).</summary>
    static StackPanel Badges(params Control?[] badges)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in badges)
            if (b is not null) row.Children.Add(b);
        return row;
    }

    /// <summary>Tableau dans un panneau : en-tête de colonnes puis lignes, sans marge intérieure.</summary>
    static Border TableCard(string title, Control? right, Control header, IEnumerable<Control> rows)
    {
        var table = new StackPanel { Children = { header } };
        foreach (var row in rows) table.Children.Add(row);
        return Card(table, title, right, new Thickness(0));
    }

    /// <summary>
    /// « À faire en priorité » d'un écran : les trois actions les plus rentables de ses domaines, en cartes (liseré de
    /// gravité, titre, action) ; clic = l'endroit où agir ; le reste est dans Détails. Null s'il n'y a rien à faire.
    /// </summary>
    Control? TodoCards(params Area[] areas)
    {
        var todo = r.ActionPlan.Where(f => areas.Contains(f.Area) && !IsDone(f)).ToList();
        if (todo.Count == 0) return null;
        var head = new DockPanel();
        if (todo.Count > 3)
        {
            var more = Clickable(T($"{todo.Count - 3} autre{(todo.Count > 4 ? "s" : "")} dans Détails ›", 12, Accent.Main), () => GoTo(Screen.Details), hover: Tok.Hover, padding: new Thickness(4, 0));
            DockPanel.SetDock(more, Dock.Right);
            head.Children.Add(more);
        }
        head.Children.Add(Label("À faire en priorité"));
        var cards = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var f in todo.Take(3)) cards.Children.Add(ActionCard(f).WithMargin(0, 0, 10, 0));
        return new StackPanel { Spacing = 8, Children = { head, cards } };
    }

    /// <summary>Une action en carte : case « fait », domaine, titre, ce qu'il faut faire ; le détail chiffré en infobulle.</summary>
    Control ActionCard(Finding f)
    {
        var head = new DockPanel();
        var area = T(Fmt.Area(f.Area), 11, Tok.Muted, mono: true);
        DockPanel.SetDock(area, Dock.Right);
        head.Children.Add(area);
        head.Children.Add(Check(f));
        var body = new StackPanel
        {
            Spacing = 6,
            Children = { head, T(f.Title, 13, Tok.Text, FontWeight.SemiBold, wrap: true).WithLineHeight(1.3) },
        };
        if (f.Action is not null) body.Children.Add(T(f.Action, 12, Tok.Text2, wrap: true).WithLineHeight(1.4));
        var stripe = new Border { Width = 3, Background = LevelColor(f.Severity) };
        DockPanel.SetDock(stripe, Dock.Left);
        var card = Clickable(new DockPanel { Children = { stripe, new Border { Padding = new Thickness(12, 10), Child = body } } }, () => Go(f), Tok.Panel, Tok.TileHover, new Thickness(0));
        card.CornerRadius = new CornerRadius(4);
        card.BorderBrush = Tok.Border;
        card.BorderThickness = new Thickness(1);
        card.ClipToBounds = true;
        return Tip(card, f.Detail);
    }

    // ----- Accueil et Équipement : la fiche en silhouette -----

    static readonly (string Slot, int Row, int Col)[] DollLayout =
    [
        ("Helmet", 0, 0), ("Shoulder", 1, 0), ("Torso", 2, 0), ("Pants", 3, 0), ("Gloves", 4, 0), ("Boots", 5, 0), ("Cape", 6, 0),
        ("Necklace", 0, 3), ("Earring1", 1, 3), ("Earring2", 2, 3), ("Ring1", 3, 3), ("Ring2", 4, 3), ("Bracelet1", 5, 3), ("Bracelet2", 6, 3), ("Belt", 7, 3),
        ("MainHand", 6, 1), ("SubHand", 6, 2), ("Rune1", 7, 1), ("Amulet", 7, 2), ("Rune2", 7, 0),
    ];

    SlotPlan? SelectedSlot => r.Gear.Slots.FirstOrDefault(x => x.Slot == s.Slot)
                              ?? r.Gear.Slots.Where(x => x.Status != SlotStatus.Ok).OrderByDescending(x => x.Priority).FirstOrDefault()
                              ?? r.Gear.Slots.FirstOrDefault();

    Control Doll()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("200,*,*,200"), ColumnSpacing = 12, RowSpacing = 5, VerticalAlignment = VerticalAlignment.Top,
        };
        for (var i = 0; i < 8; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Au centre de la silhouette : le tableau de bord sur l'accueil, l'état de l'équipement sur l'écran Équipement.
        var center = s.Screen == Screen.Home ? HomeCenter() : GearCenter();
        Grid.SetRow(center, 0);
        Grid.SetRowSpan(center, 6);
        Grid.SetColumn(center, 1);
        Grid.SetColumnSpan(center, 2);
        grid.Children.Add(center);

        var selected = s.Screen == Screen.Gear ? SelectedSlot?.Slot : null;
        foreach (var (slot, row, col) in DollLayout)
        {
            var plan = r.Gear.Slots.FirstOrDefault(x => x.Slot == slot);
            if (plan is null || slot == "Rune2" && plan.Item is null) continue;
            var tile = SlotTile(plan, plan.Slot == selected);
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, col);
            grid.Children.Add(tile);
        }
        if (s.Screen != Screen.Home) return grid;
        // Sur l'accueil, la signification des lettres des pièces (l'écran Équipement la donne dans son cadre central).
        return new StackPanel { Spacing = 10, Children = { grid, LetterLegend() } };
    }

    /// <summary>Une ligne : chaque lettre de pièce dans sa couleur, suivie de ce qu'elle veut dire.</summary>
    static Control LetterLegend()
    {
        var line = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (code, color, text) in new[]
        {
            ("E", Tok.Bad, "vide"), ("W", Tok.Warn, "à remplacer ou en retard"), ("I", Tok.Info, "à enchanter"),
            ("G", Tok.Warn, "gravures ou pierres à revoir"), ("✓", Tok.Good, "rien à faire"),
        })
            line.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(8, 0),
                Children = { T(code, 11, color, FontWeight.SemiBold, mono: true), T(text, 11, Tok.Muted) },
            });
        return line;
    }

    (string Label, int Value, Screen Screen)[] AreaScores =>
    [
        ("Stats", r.Scores.Stats, Screen.Stats), ("Équipement", r.Scores.Gear, Screen.Gear),
        ("Compétences", r.Scores.Skills, Screen.Skills), ("Daevanion", r.Scores.Daevanion, Screen.Daeva),
    ];

    /// <summary>Accueil : la silhouette, la note globale et le nombre d'actions en badges.</summary>
    Control HomeScreen()
    {
        var (advice, important, _) = Fmt.Counts(r.Findings);
        return Page($"Accueil · {r.Character.Name} · {Mode}", Badges(
            Tip(Badge($"score global {r.Scores.Overall}", Tok.Score(r.Scores.Overall)), HowText),
            Badge(advice == 0 ? "✓ aucun conseil" : Fmt.CountsText(r.Findings), important > 0 ? Tok.Warn : advice > 0 ? Tok.Text2 : Tok.Good)), Doll());
    }

    /// <summary>Ce que mesure chaque note (survol des tuiles de l'Accueil).</summary>
    static string ScoreTip(Screen screen) => screen switch
    {
        Screen.Stats => "Note Stats : part de tes stats ajustables placée dans des stats utiles à ta classe. Clic : l'écran Stats.",
        Screen.Gear => "Note Équipement : tes pièces face au niveau de ton palier. Clic : l'écran Équipement.",
        Screen.Skills => "Note Compétences : niveaux visés atteints, pondérés par l'importance de chaque compétence. Clic : l'écran Compétences.",
        _ => "Note Daevanion : part des plateaux utiles déjà ouverte. Clic : l'écran Daevanion.",
    };

    /// <summary>Accueil : les quatre notes (clic = écran), puis les actions les plus rentables (clic = l'endroit où agir).</summary>
    Control HomeCenter()
    {
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
        var i = 0;
        foreach (var (label, v, screen) in AreaScores)
        {
            var tile = Clickable(new StackPanel
                {
                    Spacing = 4,
                    Children = { Label(Nav.First(n => n.Screen == screen).Short), T(v.ToString(), 22, Tok.Score(v), FontWeight.SemiBold, mono: true), RoundBar(v / 100.0, Tok.Score(v), 4) },
                },
                () => GoTo(screen), Tok.Panel, Tok.TileHover, new Thickness(12, 8, 12, 10));
            tile.CornerRadius = new CornerRadius(4);
            tile.BorderBrush = Tok.Border;
            tile.BorderThickness = new Thickness(1);
            Tip(tile, ScoreTip(screen));
            Grid.SetColumn(tile, i++);
            tiles.Children.Add(tile);
        }
        var actions = new StackPanel();
        var done = r.ActionPlan.Count(IsDone);
        foreach (var f in r.ActionPlan.Where(f => !IsDone(f)).Take(5)) actions.Children.Add(CompactAction(f));
        if (actions.Children.Count == 0) actions.Children.Add(T("✓ Rien d'important à faire : tout est en ordre pour cette vue.", 13, Tok.Good).WithMargin(4, 8));
        if (done > 0) actions.Children.Add(T($"{Fmt.Plural(done, "conseil coché", "conseils cochés")} « fait » depuis l'import : dans Détails.", 11, Tok.Muted, mono: true).WithMargin(8, 6, 0, 0));
        var all = Tip(Clickable(T("tout voir ›", 12, Accent.Main), () => GoTo(Screen.Details), hover: Tok.HoverStrong, padding: new Thickness(4, 0)),
            $"Tous les conseils, par écran : {Fmt.CountsText(r.Findings)}, plus {Fmt.Plural(Fmt.Counts(r.Findings).Info, "info")}.");
        return new StackPanel { Spacing = 12, Children = { tiles, Card(actions, "À faire maintenant", all, new Thickness(6, 2, 6, 6)) } };
    }

    bool IsDone(Finding f) => inputs.IsDone(f.Title, r.Character.FetchedAtUtc);

    /// <summary>Coche / décoche « fait » (gardé jusqu'au prochain import du personnage).</summary>
    void ToggleDone(Finding f)
    {
        var done = inputs.DoneFor == r.Character.FetchedAtUtc ? inputs.Done.ToList() : [];
        if (!done.Remove(f.Title)) done.Add(f.Title);
        onInputs(inputs with { Done = done, DoneFor = r.Character.FetchedAtUtc });
    }

    Border Check(Finding f)
    {
        var done = IsDone(f);
        var box = Clickable(T(done ? "☑" : "☐", 14, done ? Tok.Good : Tok.Muted), () => ToggleDone(f), hover: Tok.HoverStrong, padding: new Thickness(3, 0));
        box.CornerRadius = new CornerRadius(3);
        box.VerticalAlignment = VerticalAlignment.Top;
        return Tip(box, done ? "Fait : décoche si ce n'est pas le cas" : "Coche quand c'est fait (jusqu'au prochain import, qui le vérifiera)");
    }

    /// <summary>Une action sur une ligne : case « fait », gravité, titre, zone ; le détail passe en infobulle.</summary>
    Border CompactAction(Finding f)
    {
        var (icon, color) = Sev(f.Severity);
        var row = Clickable(ColumnsSpaced("22,16,*,Auto", 6, Check(f), T(icon, 12, color, FontWeight.SemiBold, mono: true), T(f.Title, 13, weight: FontWeight.Medium),
                T(Fmt.Area(f.Area), 11, Tok.Muted, mono: true)),
            () => Go(f), hover: Tok.TileHover, padding: new Thickness(6, 6));
        row.BorderBrush = Tok.RowLine2;
        row.BorderThickness = new Thickness(0, 0, 0, 1);
        return Tip(row, $"{f.Detail}\n→ {f.Action}");
    }

    /// <summary>Équipement : palier, pièces à traiter par statut et gravures à revoir.</summary>
    Control GearCenter()
    {
        var g = r.Gear;
        var lines = new StackPanel { Spacing = 6 };
        if (g.NextStage is { } next)
            lines.Children.Add(new StackPanel
            {
                Spacing = 4, Margin = new Thickness(0, 0, 0, 6),
                Children =
                {
                    ColumnsSpaced("*,Auto", 8, T($"vers « {next.Name} »", 12, Tok.Text2), T($"niv. d'objet {r.Character.ItemLevel} / {next.MinItemLevel}", 12, Tok.Muted, mono: true)),
                    RoundBar(g.ProgressToNext(r.Character.ItemLevel), Accent.Main, 6),
                },
            });
        foreach (var (status, label) in new[] { (SlotStatus.Empty, "vide"), (SlotStatus.Replace, "à remplacer"), (SlotStatus.Weak, "en retard"), (SlotStatus.Enchant, "à enchanter") })
        {
            var slots = g.Slots.Where(x => x.Status == status).ToList();
            if (slots.Count == 0) continue;
            var (code, color) = StatusCode(status);
            lines.Children.Add(ColumnsSpaced("18,*", 8, T(code, 12, color, FontWeight.SemiBold, mono: true),
                T($"{(status == SlotStatus.Empty ? Fmt.Plural(slots.Count, "vide") : $"{slots.Count} {label}")} : {string.Join(", ", slots.OrderByDescending(x => x.Priority).Select(x => x.SlotName))}", 13, Tok.Text2, wrap: true)));
        }
        var engr = r.Engravings.Items.Count(e => e.NeedsWork);
        if (engr > 0)
            lines.Children.Add(ColumnsSpaced("18,*", 8, T("G", 12, Tok.Warn, FontWeight.SemiBold, mono: true), T($"{Fmt.Plural(engr, "pièce")} aux gravures à revoir (sous-page Pierres)", 13, Tok.Text2, wrap: true)));
        if (g.Slots.All(x => x.Status == SlotStatus.Ok) && engr == 0)
            lines.Children.Add(T("✓ Toutes les pièces sont au niveau de ton palier.", 13, Tok.Good));
        lines.Children.Add(T("Clique une pièce pour son détail dans l'inspecteur.", 12, Tok.Muted).WithMargin(0, 6, 0, 0));
        return Card(lines, $"Palier « {g.Stage.Name} »");
    }

    static (string Code, IBrush Color) StatusCode(SlotStatus status) => status switch
    {
        SlotStatus.Empty => ("E", Tok.Bad),
        SlotStatus.Replace or SlotStatus.Weak => ("W", Tok.Warn),
        SlotStatus.Enchant => ("I", Tok.Info),
        _ => ("✓", Tok.Good),
    };

    static IBrush StatusBrush(SlotStatus status) => StatusCode(status).Color;

    IBrush GradeBrush(EquippedItem? item) => item is null ? Tok.B("#6A6A72") : Tok.B(db.Grade(item.Grade).Color);

    Border SlotTile(SlotPlan p, bool selected)
    {
        var (code, color) = StatusCode(p.Status);
        var engraving = r.Engravings.For(p.Slot);
        var bar = new Border { Width = 3, Height = 30, Background = GradeBrush(p.Item), Margin = new Thickness(0, 0, 9, 0) };
        // « G » : gravures d'âme ou pierres à revoir, en plus du statut de la pièce.
        var codeText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (engraving is { NeedsWork: true }) codeText.Children.Add(T("G", 11, Tok.Warn, FontWeight.SemiBold, mono: true));
        if (engraving is not { NeedsWork: true } || p.Status != SlotStatus.Ok) codeText.Children.Add(T(code, 11, color, FontWeight.SemiBold, mono: true));
        DockPanel.SetDock(bar, Dock.Left);
        DockPanel.SetDock(codeText, Dock.Right);
        var power = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        power.Children.Add(T(p.Item is null ? "—" : p.Power.ToString(), 13, mono: true));
        if (p.Item is not null) power.Children.Add(T($"+{p.Item.EnchantLevel}", 13, Tok.Muted, mono: true));
        var dock = new DockPanel { Children = { bar, codeText, new StackPanel { Children = { T(p.SlotName, 11, Tok.Muted), power } } } };

        var tile = Clickable(dock, () => update(st => { if (st.Screen != Screen.Gear) st.GearPage = GearPage.Overview; st.Screen = Screen.Gear; st.Slot = p.Slot; }),
            selected ? Accent.Dim : Tok.Panel, selected ? null : Tok.TileHover, new Thickness(9, 6));
        tile.CornerRadius = new CornerRadius(3);
        tile.BorderBrush = selected ? Accent.Main : Tok.Border;
        tile.BorderThickness = new Thickness(1);
        var tip = p.Item is null
            ? $"{p.SlotName} : emplacement vide (E)"
            : $"{p.Item.Name} — {db.Grade(p.Item.Grade).Name}, niveau d'objet {p.Power}, +{p.Item.EnchantLevel}\n" +
              "État : " + string.Join(" · ", new[]
              {
                  p.Status != SlotStatus.Ok ? $"{code} = {Fmt.Status(p.Status).ToLowerInvariant()}" : null,
                  engraving is { NeedsWork: true } ? "G = gravures ou pierres à revoir" : null,
              }.OfType<string>().DefaultIfEmpty("✓ = rien à faire")) + "\n" +
              string.Join("\n", p.Item.MainStats.Select(e => e.Raw)) +
              (engraving is null ? "" : EngravingTip(engraving));
        return Tip(tile, tip);
    }

    static string EngravingTip(ItemEngraving e)
    {
        var text = "";
        if (e.HasSoulBinding)
            text += $"\nGravures d'âme · lien d'âme {Fmt.Num(e.SoulBindRate ?? 0)} %\n" + string.Join("\n", e.SoulLines.Select(EngravingAdvisor.Describe));
        else if (e.SoulLines.Count > 0)
            text += "\nOptions fixes\n" + string.Join("\n", e.SoulLines.Select(EngravingAdvisor.Describe));
        if (e.Stones.Count > 0 || e.StoneSlots > 0)
            text += $"\nPierres {e.Stones.Count}/{e.StoneSlots}\n" + string.Join("\n", e.Stones.Select(EngravingAdvisor.Describe));
        return e.NeedsWork ? text + "\nG = gravures à revoir (détail dans l'inspecteur)" : text;
    }

    static IBrush VerdictBrush(LineVerdict v) => v switch { LineVerdict.Good => Tok.Good, LineVerdict.Poor => Tok.Bad, _ => Tok.Warn };

    static string VerdictIcon(LineVerdict v) => v switch { LineVerdict.Good => "✓", LineVerdict.Poor => "✕", _ => "~" };

    static IBrush ActionBrush(ItemEngraving e) => e.Action switch
    {
        SoulAction.Reset => Tok.Bad,
        SoulAction.Sync => Tok.Warn,
        SoulAction.RaiseRate => Tok.Info,
        SoulAction.Skip => Tok.Muted,
        SoulAction.Locked when e.PoorLines.Any() => Tok.Warn,
        _ => Tok.Good,
    };

    static IBrush TodoBrush(ItemEngraving e) => e.Action switch
    {
        SoulAction.Reset => Tok.Bad,
        SoulAction.Sync => Tok.Warn,
        SoulAction.Skip => Tok.Muted,
        _ => e.StoneAdvice is not null ? Tok.Warn : e.Action == SoulAction.RaiseRate ? Tok.Info : Tok.Muted,
    };

    /// <summary>Action courte pour le tableau des gravures.</summary>
    static string ActionShort(ItemEngraving e)
    {
        var soul = e.Action switch
        {
            SoulAction.Reset => "Réinitialiser",
            SoulAction.Sync => $"Synchroniser « {e.PoorLines.OrderBy(l => l.Weight).First().StatName} »",
            SoulAction.RaiseRate => "Monter le lien d'âme",
            SoulAction.Locked => "Verrouillée",
            SoulAction.Skip => "Rien : pièce à remplacer",
            _ => null,
        };
        var empty = e.StoneSlots - e.Stones.Count;
        var stones = e.StoneAdvice is null ? null
            : empty > 0 ? $"graver {Fmt.Plural(empty, "pierre")}"
            : e.Stones.Any(x => x.Verdict == LineVerdict.Poor) ? "changer des pierres" : "monter le grade des pierres";
        return string.Join(" · ", new[] { soul, stones }.Where(x => x is not null)) is { Length: > 0 } t ? t : "—";
    }

    /// <summary>Encadré de conseil : fond « frame » et bordure gauche de la couleur du verdict.</summary>
    static Border Callout(string text, IBrush color) => new()
    {
        Padding = new Thickness(12, 10), Background = Tok.Frame, BorderBrush = color, BorderThickness = new Thickness(2, 0, 0, 0),
        Child = T(text, 13, wrap: true).WithLineHeight(1.5),
    };

    Control EngravingRow(EngravingLine l)
    {
        var tip = l.Effect.StatKey is null ? $"{l.Effect.Raw} : effet non reconnu, non jugé."
            : l.Verdict switch
            {
                LineVerdict.Good => $"{l.StatName} : utile à ta classe (importance {l.Weight:0.00}).",
                LineVerdict.Poor => $"{l.StatName} : sert peu à ta classe (importance {l.Weight:0.00}) : à remplacer.",
                _ => $"{l.StatName} : utilité moyenne pour ta classe (importance {l.Weight:0.00}).",
            };
        var grade = l.Grade is null ? new TextBlock() : T(db.Grade(l.Grade).Name, 11, Tok.B(db.Grade(l.Grade).Color), mono: true);
        return Tip(ColumnsSpaced("14,*,Auto", 6, T(VerdictIcon(l.Verdict), 12, VerdictBrush(l.Verdict), FontWeight.SemiBold, mono: true),
            T(l.Effect.Raw, 12, Tok.Text2, mono: true, wrap: true), grade), tip);
    }

    /// <summary>Gravures d'âme, pierres et panoplie de la pièce sélectionnée.</summary>
    Control EngravingSection(ItemEngraving e)
    {
        var panel = new StackPanel { Spacing = 14 };
        if (e.HasSoulBinding)
        {
            var rate = e.SoulBindRate ?? 0;
            var header = new DockPanel();
            var rateText = T($"lien d'âme {Fmt.Num(rate)} %", 11, rate >= 100 ? Tok.Good : Tok.Text2, mono: true);
            DockPanel.SetDock(rateText, Dock.Right);
            header.Children.Add(rateText);
            header.Children.Add(Label("Gravures d'âme"));
            var soul = new StackPanel { Spacing = 4 };
            soul.Children.Add(Tip(header, "Lien d'âme : progression des gravures d'âme vers leur valeur maximale. À 100 %, toutes les lignes sont " +
                "au maximum et ne se changent plus. Synchroniser remplace une ligne ; réinitialiser relance tout et remet le lien à 0 %."));
            soul.Children.Add(Bar(rate / 100, rate >= 100 ? Tok.Good : Accent.Main, 3, track: Tok.Border).WithMargin(0, 2, 0, 4));
            foreach (var l in e.SoulLines) soul.Children.Add(EngravingRow(l));
            panel.Children.Add(soul);
        }
        else if (e.SoulLines.Count > 0)
        {
            var fixedLines = new StackPanel { Spacing = 4, Children = { Tip(Label("Options fixes"), "Options sans lien d'âme : déjà à leur valeur, elles ne se gravent pas. Pour en changer, il faut changer de pièce.") } };
            foreach (var l in e.SoulLines) fixedLines.Children.Add(EngravingRow(l));
            panel.Children.Add(fixedLines);
        }
        if (e.HasSoulBinding || e.Action == SoulAction.Skip) panel.Children.Add(Callout(e.SoulAdvice, ActionBrush(e)));
        if (e.StoneSlots > 0 || e.Stones.Count > 0)
        {
            var stones = new StackPanel { Spacing = 4, Children = { Label($"{e.StoneKind} · {e.Stones.Count}/{e.StoneSlots}") } };
            foreach (var l in e.Stones) stones.Children.Add(EngravingRow(l));
            for (var i = e.Stones.Count; i < e.StoneSlots; i++)
                stones.Children.Add(ColumnsSpaced("14,*", 6, T("○", 12, Tok.Bad, mono: true), T("emplacement libre", 12, Tok.Faint, mono: true)));
            panel.Children.Add(stones);
            if (e.StoneAdvice is not null) panel.Children.Add(Callout(e.StoneAdvice, e.Stones.Count < e.StoneSlots ? Tok.Bad : Tok.Warn));
            if (e.StoneUpgrade is not null) panel.Children.Add(T(e.StoneUpgrade, 12, Tok.Muted, wrap: true).WithLineHeight(1.45));
        }
        if (e.Item.SetName is { } set)
        {
            var bonuses = new StackPanel { Spacing = 3, Children = { Label($"Panoplie · {set}") } };
            foreach (var b in e.Item.ActiveSetBonuses) bonuses.Children.Add(T(b, 12, Tok.Text2, wrap: true).WithLineHeight(1.4));
            if (e.Item.ActiveSetBonuses.Count == 0) bonuses.Children.Add(T("Aucun bonus actif.", 12, Tok.Muted));
            bonuses.Children.Add(T("Bonus souvent conditionnels : non comptés dans les stats.", 11, Tok.Faint, wrap: true));
            panel.Children.Add(bonuses);
        }
        return panel;
    }

    // ----- Équipement : vue d'ensemble et sous-pages (sous-menu du rail) -----

    /// <summary>Feuille de route d'équipement (paliers de score, pièces, enchantement), calculée une fois par rendu.</summary>
    GearRoadmapPlan Route => route ??= GearRoadmap.Build(db, r);
    GearRoadmapPlan? route;

    Control GearScreen() => s.GearPage switch
    {
        GearPage.Roadmap => GearRoadmapPage(),
        GearPage.Pieces => GearPiecesPage(),
        GearPage.Enchant => GearEnchantPage(),
        GearPage.Stones => GearStonesPage(),
        GearPage.Arcana => GearArcanaPage(),
        _ => GearOverview(),
    };

    // ----- Barres secondaires : une ligne par sous-page (ou par combat, par plateau), avec un chiffre et un aperçu -----

    SidePanel GearSide()
    {
        var plan = Route;
        var body = new StackPanel();
        void Page(string icon, GearPage page, string? badge, IBrush color, string preview) =>
            body.Children.Add(SideItem(icon, GearPages.First(p => p.Page == page).Title, badge, color, preview, s.GearPage == page, () => update(st => st.GearPage = page)));
        var todo = Todo(r);
        Page(Icons.Grid, GearPage.Overview, todo > 0 ? $"{todo}" : "✓", todo > 0 ? Tok.Warn : Tok.Good, "silhouette · actions les plus rentables");
        Page(Icons.Route, GearPage.Roadmap, plan.Next is { } next ? Fmt.Num(next.Score) : "✓", plan.Next is null ? Tok.Good : Tok.Warn,
            plan.Next is { } n ? $"prochain palier · +{n.Score - plan.Score}" : "tous les paliers atteints");
        var replace = plan.Slots.Where(x => x.Gain > 0).ToList();
        Page(Icons.Box, GearPage.Pieces, replace.Count > 0 ? $"+{replace.Sum(x => x.Gain)}" : "✓", replace.Count > 0 ? Tok.Warn : Tok.Good,
            replace.Count > 0 ? $"{replace.Count} à passer en {plan.Available?.ItemLevel}" : "au palier accessible");
        var raise = plan.Enchants.Where(e => e.Verdict == EnchantVerdict.Raise).ToList();
        Page(Icons.Up, GearPage.Enchant, raise.Count > 0 ? $"{raise.Count}" : "✓", raise.Count > 0 ? Tok.Warn : Tok.Good,
            raise.Count > 0 ? $"{raise[0].SlotName.ToLowerInvariant()} +{raise[0].Item.EnchantLevel} → +{raise[0].Target}" : "rien à monter sans risque");
        var engr = r.Engravings.Items.Count(e => e.NeedsWork);
        Page(Icons.Gem, GearPage.Stones, engr > 0 ? $"{engr}" : "✓", engr > 0 ? Tok.Warn : Tok.Good,
            plan.SacredSlots.Count > 0 ? $"pierre sacrée : {Fmt.Plural(plan.SacredSlots.Sum(x => x.Count), "emplacement")}" : "gravures et pierres");
        var cards = Arcana;
        var lost = r.Skills.GearBonuses.Count(b => b.Arcana && !b.Useful);
        Page(Icons.Card, GearPage.Arcana, lost > 0 ? $"{lost}" : $"{cards.Count(a => a.Item is not null)}/{cards.Count}", lost > 0 ? Tok.Warn : Tok.Text2,
            WeakestArcana is { } low ? $"d'abord : {low.Kind.ToLowerInvariant()}{(low.Item is { } i ? $" ({db.Grade(i.Grade).Name.Split(' ')[0].ToLowerInvariant()})" : " (vide)")}" : "cartes de Transcendance");
        return new SidePanel("Équipement", null, body);
    }

    SidePanel CombatSide()
    {
        var body = new StackPanel();
        body.Children.Add(SideItem(Icons.List, "Tous les combats", $"{combats.Count}", Tok.Text2, "le tableau de l'historique", s.CombatList,
            () => update(st => { st.CombatList = true; st.CombatIndex = null; st.CombatGroup = null; st.CombatSkill = null; })));
        var profile = db.Resolve(r.Character.ClassCode, GameMode.PvE, inputs.UsesDesigned);
        var analyzer = new CombatAnalyzer(db);
        var best = combats.Count > 0 ? combats.Max(x => x.Log.Dps) : 0;
        var shown = ShownCombat;
        for (var g = 0; g < CombatSessions.Count; g++)
        {
            var session = CombatSessions[g];
            var logs = session.Select(i => combats[i].Log).ToList();
            var first = logs.Min(l => l.ImportedAtUtc).ToLocalTime();
            var last = logs.Max(l => l.ImportedAtUtc.AddSeconds(l.Duration)).ToLocalTime();
            body.Children.Add(SideGroup($"{first:dd/MM} · {first:HH:mm}–{last:HH:mm}"));
            var group = g;
            if (session.Count > 1)
            {
                var total = logs.Sum(l => l.TotalDamage) / Math.Max(1, logs.Sum(l => l.Duration));
                body.Children.Add(SideItem(null, "Global de la séance", Fmt.Big(total), Accent.Main, $"{session.Count} combats · {Duration(logs.Sum(l => l.Duration))}",
                    s.CombatGroup == g, () => update(st => { st.CombatGroup = group; st.CombatIndex = null; st.CombatSkill = null; st.CombatList = false; })));
            }
            foreach (var i in session)
            {
                var log = combats[i].Log;
                var index = i;
                var todo = analyzer.Analyze(log, profile).Findings.Count(f => f.Severity <= Severity.Advice);
                var dps = ColumnsSpaced("56,*", 8, T(Fmt.Big(log.Dps), 12, Accent.Main, FontWeight.SemiBold, mono: true), RoundBar(log.Dps / Math.Max(1, best), Accent.Main, 4));
                body.Children.Add(SideItem(null, log.Target, todo == 0 ? "✓" : $"{todo}", todo == 0 ? Tok.Good : Tok.Warn,
                    $"{log.ImportedAtUtc.ToLocalTime():HH:mm:ss} · {Duration(log.Duration)} · {PetMeterFights.KindName(log) ?? (log.Source == "Pet Meter" ? "Pet Meter" : "captures")}",
                    shown == i, () => update(st => { st.CombatIndex = index; st.CombatGroup = null; st.CombatSkill = null; st.CombatList = false; }), dps,
                    todo == 0 ? "Rien à corriger" : $"{Fmt.Plural(todo, "point")} à corriger"));
            }
        }
        if (combats.Count == 0)
            body.Children.Add(T("Aucun combat : lance le Pet Meter pendant tes combats, ou importe des captures NotMeter avec « + ».", 12, Tok.Muted, wrap: true)
                .WithLineHeight(1.4).WithMargin(14, 10));
        else
            body.Children.Add(T($"Seuls les boss, le mannequin et le JcJ sont gardés (les {CombatStore.Keep} derniers), groupés par séance (même carte, moins de 10 min d'écart).", 11, Tok.Muted, wrap: true)
                .WithLineHeight(1.4).WithMargin(14, 10));
        var import = Clickable(T("+", 13, Accent.Main, FontWeight.SemiBold, mono: true).Centered(), importCombat, hover: Tok.HoverStrong, padding: new Thickness(8, 1));
        import.BorderBrush = Tok.Border;
        import.BorderThickness = new Thickness(1);
        import.CornerRadius = new CornerRadius(3);
        var reload = Clickable(T("↻", 13, Accent.Main, FontWeight.SemiBold, mono: true).Centered(), reloadCombats, hover: Tok.HoverStrong, padding: new Thickness(7, 1));
        reload.BorderBrush = Tok.Border;
        reload.BorderThickness = new Thickness(1);
        reload.CornerRadius = new CornerRadius(3);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Tip(reload, "Reprendre les derniers combats du Pet Meter"), Tip(import, "Importer des captures NotMeter « Détails du combat »") },
        };
        return new SidePanel($"Combats · {combats.Count}", actions, body);
    }

    SidePanel DaevaSide()
    {
        var body = new StackPanel();
        var selected = SelectedBoard;
        body.Children.Add(SideGroup("Plateaux"));
        foreach (var b in RelevantBoards)
        {
            var steps = r.Daevanion.Steps.Count(x => x.Board.Id == b.Board.Id);
            var id = b.Board.Id;
            body.Children.Add(SideItem(Icons.Board, b.Board.Name, $"{b.Board.OpenNodes}/{b.Board.TotalNodes}", Tok.Text2,
                $"réserve {PoolShort(b.Pool)}" + (steps > 0 ? $" · {Fmt.Plural(steps, "case")} à prendre" : ""), b.Board.Id == selected?.Board.Id, () => update(st => st.Board = id)));
        }
        body.Children.Add(SideGroup("Plan"));
        var respec = r.Daevanion.Respecs.Count(x => x.Remove.Count > 0 && x.Gain >= db.Settings.Daevanion.RespecMinGain);
        body.Children.Add(SideItem(Icons.Route, "Ton plateau", respec > 0 ? "réorg." : null, Tok.Warn, "la suite depuis tes cases ouvertes", !s.DaevaRoute,
            () => update(st => { st.DaevaRoute = false; st.AllNext = false; })));
        body.Children.Add(SideItem(Icons.Swap, "Route conçue", null, null, "le meilleur chemin depuis le centre", s.DaevaRoute,
            () => update(st => { st.DaevaRoute = true; st.AllNext = false; })));
        return new SidePanel("Daevanion", null, body);
    }

    SidePanel SkillsSide()
    {
        var sk = r.Skills;
        var body = new StackPanel();
        void Page(string icon, SkillPage page, string? badge, IBrush color, string preview) =>
            body.Children.Add(SideItem(icon, SkillPages.First(p => p.Page == page).Title, badge, color, preview, s.SkillPage == page, () => update(st => st.SkillPage = page)));
        var moved = sk.Moves.Sum(m => m.Points);
        Page(Icons.Grid, SkillPage.Overview, moved > 0 ? $"{moved}" : "✓", moved > 0 ? Tok.Warn : Tok.Good, moved > 0 ? "toutes tes compétences · points à déplacer" : "toutes tes compétences");
        var specs = Specs;
        Page(Icons.Star, SkillPage.Specs, specs.Count > 0 ? $"{specs.Sum(p => p.Slots)}" : "—", Accent.Main,
            specs.Count > 0 ? "lesquelles équiper, sort par sort" : "pas de données pour cette classe");
        Page(Icons.Swap, SkillPage.Moves, sk.Moves.Count > 0 ? $"{sk.Moves.Count}" : "—", sk.Moves.Count > 0 ? Tok.Warn : Tok.Muted, "la raison de chaque point déplacé");
        var equipped = sk.Stigmas.Count(x => x.Slotted);
        Page(Icons.Shield, SkillPage.Stigmas, $"{equipped}/{db.Settings.Stigmas.Slots}", Tok.Text2,
            sk.NextShards.Count > 0 ? $"{Fmt.Plural(sk.NextShards.Count, "palier")} de fragments" : "stigmas au niveau visé");
        return new SidePanel("Compétences", null, body);
    }

    SidePanel BuildSide()
    {
        var cls = r.Profile.Class;
        var body = new StackPanel();
        void Page(string icon, BuildPage page, string? badge, IBrush color, string preview) =>
            body.Children.Add(SideItem(icon, BuildPages.First(p => p.Page == page).Title, badge, color, preview, s.BuildPage == page, () => update(st => st.BuildPage = page)));
        Page(Icons.Grid, BuildPage.Compare, r.Profile.UsesDesigned ? "conçu" : "joueurs", Accent.Main, "priorités, rotation, façon de jouer");
        Page(Icons.Play, BuildPage.Rotations, s.RotationTargets == 1 ? "1 cible" : $"{s.RotationTargets} cibles", Tok.Text2, "joueurs contre conçue, 2 minutes");
        if (cls.Designed is { Differences.Count: > 0 } d)
            Page(Icons.Diff, BuildPage.Differences, $"{d.Differences.Count}", Tok.Text2, "pourquoi le build conçu diffère");
        return new SidePanel($"Build · {cls.Name}", null, body);
    }

    /// <summary>Badges d'état des pièces : vides, à remplacer, en retard, à enchanter.</summary>
    StackPanel GearBadges()
    {
        var g = r.Gear;
        var badges = Badges(new[] { (SlotStatus.Empty, "vide"), (SlotStatus.Replace, "à remplacer"), (SlotStatus.Weak, "en retard"), (SlotStatus.Enchant, "à enchanter") }
            .Select(x => (x.Item2, Count: g.Slots.Count(p => p.Status == x.Item1), Color: StatusBrush(x.Item1)))
            .Where(x => x.Count > 0)
            .Select(x => (Control)Badge($"{x.Count} {x.Item1}{(x.Item1 == "vide" && x.Count > 1 ? "s" : "")}", x.Color)).ToArray());
        if (badges.Children.Count == 0) badges.Children.Add(Badge("✓ tout au niveau du palier", Tok.Good));
        return badges;
    }

    /// <summary>Vue d'ensemble : la silhouette, puis les trois actions les plus rentables (les sous-pages sont dans la barre secondaire).</summary>
    Control GearOverview() => Page($"Équipement · {Mode}", GearBadges(), Doll(), TodoCards(Area.Gear, Area.Enchant));

    /// <summary>
    /// Feuille de route : le score d'équipement sur la piste des paliers, puis les actions chiffrées (enchantement garanti,
    /// pièces du palier accessible) jusqu'aux paliers suivants, et ce qui compte aussi hors des pièces.
    /// </summary>
    Control GearRoadmapPage()
    {
        var plan = Route;
        var rules = db.Progression.Roadmap;
        var badges = Badges(Badge($"score {Fmt.Num(plan.Score)}", Accent.Main),
            plan.Next is { } n ? Badge($"prochain palier {Fmt.Num(n.Score)} · +{n.Score - plan.Score}", Tok.Warn) : Badge("✓ tous les paliers", Tok.Good));

        // 1. Où tu en es : la piste des paliers, ce qui est atteint et le suivant.
        var track = new MilestoneTrack(plan.Score, 500, rules.Gates.Count > 0 ? rules.Gates.Max(g => g.Score) + 100 : 3000, Accent.Main);
        foreach (var g in rules.Gates)
            track.Mark(g.Score, Fmt.Num(g.Score), g.Score <= plan.Score ? Tok.Good : g == plan.Next ? Tok.Warn : Tok.Muted);
        var where = new StackPanel { Spacing = 10, Children = { track } };
        if (plan.Reached is { } reached)
            where.Children.Add(ColumnsSpaced("90,*", 8, T("✓ atteint", 12, Tok.Good, mono: true), T($"{Fmt.Num(reached.Score)} · {reached.Unlocks}", 13, Tok.Text2, wrap: true)));
        if (plan.Next is { } next)
            where.Children.Add(ColumnsSpaced("90,*", 8, T("▶ prochain", 12, Tok.Warn, mono: true), T($"{Fmt.Num(next.Score)} · {next.Unlocks}", 13, Tok.Text, FontWeight.SemiBold, wrap: true)));
        where.Children.Add(Expander($"Les {rules.Gates.Count} paliers", IsOpen("route:gates"), () => Toggle("route:gates"), () =>
        {
            var all = new StackPanel { Spacing = 4, Margin = new Thickness(22, 0, 0, 0) };
            foreach (var g in rules.Gates)
            {
                var (mark, color) = g.Score <= plan.Score ? ("✓", Tok.Good) : g == plan.Next ? ("▶", Tok.Warn) : ("○", Tok.Muted);
                all.Children.Add(ColumnsSpaced("16,56,*", 8, T(mark, 12, color, mono: true), T(Fmt.Num(g.Score), 12, color, mono: true),
                    T(g.Unlocks, 12, Tok.Text2, wrap: true)));
            }
            return all;
        }));

        Control? levelUp = plan.Level < plan.MaxLevel
            ? Card(T(rules.LevelUp, 13, Tok.Text, wrap: true), $"D'abord : niveau {plan.MaxLevel} (tu es niveau {plan.Level})")
            : null;

        // 2. Les actions chiffrées, par tranche : ce qui mène au prochain palier est affiché, chaque tranche suivante
        //    (« puis pour 2 500 ») est repliée ; un palier franchi ferme sa tranche.
        var segments = new List<(GearScoreGate? Gate, StackPanel Rows, int Count, int Gain)>();
        var rows = new StackPanel();
        int count = 0, gain = 0, total = plan.Score;
        var ahead = rules.Gates.Where(g => g.Score > plan.Score).ToList();
        foreach (var step in plan.Steps)
        {
            total += step.Gain;
            rows.Children.Add(RouteRow(step, total));
            count++;
            gain += step.Gain;
            while (ahead.Count > 0 && ahead[0].Score <= total)
            {
                if (count == 0 && segments.Count > 0) segments[^1].Rows.Children.Add(GateRow(ahead[0]));
                else
                {
                    rows.Children.Add(GateRow(ahead[0]));
                    segments.Add((ahead[0], rows, count, gain));
                    (rows, count, gain) = (new StackPanel(), 0, 0);
                }
                ahead.RemoveAt(0);
            }
        }
        if (count > 0) segments.Add((null, rows, count, gain));

        var shown = new StackPanel();
        for (var i = 0; i < segments.Count; i++)
        {
            var (gate, list, actions, points) = segments[i];
            if (i == 0) { shown.Children.Add(list); continue; }
            var id = $"route:seg:{i}";
            shown.Children.Add(Expander(gate is null ? $"Ensuite : {Fmt.Plural(actions, "action")}, +{points}" : $"Puis pour {Fmt.Num(gate.Score)} : {Fmt.Plural(actions, "action")}, +{points}",
                IsOpen(id), () => Toggle(id), () => list, new Thickness(10, 6)));
        }
        if (plan.Steps.Count == 0)
            shown.Children.Add(T("Tes pièces sont au palier accessible et à +10 : le score monte maintenant par les cartes d'arcanes, le Daevanion et les pierres.",
                13, Tok.Good, wrap: true).WithMargin(14, 10));
        else if ((ahead.FirstOrDefault(g => !g.Optional) ?? ahead.FirstOrDefault()) is { } further)
            shown.Children.Add(T($"Avec tout ça : {Fmt.Num(total)}. Pour {Fmt.Num(further.Score)} (+{further.Score - total}) : cartes d'arcanes, Daevanion et pierres" +
                                 (plan.NextTier is { } nt ? $", puis les pièces {nt.ItemLevel} (score {Fmt.Num(nt.Gate ?? 0)})." : "."),
                12, Tok.Muted, wrap: true).WithMargin(14, 10));
        var title = plan.Next is { } goal ? $"Pour passer {Fmt.Num(goal.Score)} : +{goal.Score - plan.Score}" : "Pour progresser encore";

        // 3. Ce qui compte aussi pour le palier suivant, hors des pièces.
        Control? how = null;
        if (plan.Next is { How.Count: > 0 } nextGate)
        {
            var list = new StackPanel { Spacing = 5 };
            foreach (var x in nextGate.How) list.Children.Add(ColumnsSpaced("14,*", 6, T("•", 13, Accent.Main), T(x, 13, Tok.Text2, wrap: true).WithLineHeight(1.4)));
            how = Card(list, $"Ce que conseillent les guides pour {Fmt.Num(nextGate.Score)}");
        }

        return Page($"Équipement · feuille de route · {Mode}", badges,
            Card(where, "Paliers de score d'équipement", T("score = niveau d'objet du personnage", 11, Tok.Muted, mono: true)),
            levelUp,
            Card(shown, title, T("1 niveau de pièce ou d'enchantement = 1 point", 11, Tok.Muted, mono: true), new Thickness(0)),
            how,
            Expander("D'où viennent ces seuils et ces sources", IsOpen("route:source"), () => Toggle("route:source"), () =>
                T("Seuils et sources des serveurs Global relevés dans les guides (raidrelay.com, odealo.com, metabot.gg, expcarry.com, ateliergaming.fr, octobre 2026) ; " +
                  "noms de lieux traduits de l'anglais : à valider en jeu. Le score d'équipement compte aussi le Daevanion, les arcanes, les quêtes et les pierres : " +
                  "les gains ci-dessus ne comptent que le niveau des pièces et leur enchantement. " + rules.CraftNote, 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0)));
    }

    /// <summary>Une action de la feuille de route : gain, titre et détail, score cumulé ; clic = la pièce dans l'inspecteur.</summary>
    Border RouteRow(RouteStep step, int total)
    {
        var text = new StackPanel { Spacing = 2, Children = { T(step.Title, 13, Tok.Text, FontWeight.Medium, wrap: true), T(step.Detail, 12, Tok.Muted, wrap: true).WithLineHeight(1.35) } };
        var row = Clickable(ColumnsSpaced("52,*,70", 10,
                T(step.Gain > 0 ? $"+{step.Gain}" : "—", 13, step.Gain > 0 ? Tok.Good : Tok.Muted, FontWeight.SemiBold, mono: true).AlignRight(),
                text, T($"= {Fmt.Num(total)}", 12, Tok.Muted, mono: true).AlignRight()),
            () => { if (step.Slot is { } slot) update(st => st.Slot = slot); }, hover: Tok.Hover, padding: new Thickness(10, 8, 14, 8));
        row.BorderBrush = Tok.RowLine;
        row.BorderThickness = new Thickness(0, 0, 0, 1);
        return row;
    }

    /// <summary>Palier franchi entre deux actions : liseré vert et ce qu'il ouvre.</summary>
    static Border GateRow(GearScoreGate gate) => new()
    {
        Background = Tok.Alpha(Tok.Good, 0.08), BorderBrush = Tok.Good, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 6),
        Child = T($"▶ {Fmt.Num(gate.Score)} atteint · {gate.Unlocks}", 12, Tok.Good, FontWeight.SemiBold, wrap: true),
    };

    /// <summary>Pièces à remplacer : où trouver mieux palier par palier (donjons puis artisanat), puis chaque pièce et son gain.</summary>
    Control GearPiecesPage()
    {
        var plan = Route;
        var rules = db.Progression.Roadmap;
        var replace = plan.Slots.Where(x => x.Gain > 0).ToList();
        var badges = Badges(
            replace.Count > 0 ? Badge($"{replace.Count} à passer en {plan.Available?.ItemLevel} · +{replace.Sum(x => x.Gain)}", Tok.Warn) : Badge("✓ au palier accessible", Tok.Good),
            plan.Available is { } av ? Badge($"pièces {av.ItemLevel} accessibles", Tok.Info) : null);

        // Où trouver mieux : à partir du palier accessible, les donjons puis l'artisanat jusqu'au dernier palier.
        const string tcols = "50,96,*,*,*,*";
        var tierRows = new List<Control>();
        foreach (var t in rules.Tiers.Where(t => t.ItemLevel >= (plan.Available?.ItemLevel ?? 0)))
        {
            var open = t.Gate is { } gate && gate <= plan.Score;
            var access = t.Gate is { } g2 ? (open ? "accessible" : $"score {Fmt.Num(g2)}") : "artisanat";
            tierRows.Add(new Border
            {
                Padding = new Thickness(20, 7), BorderBrush = Tok.RowLine, BorderThickness = new Thickness(0, 0, 0, 1),
                Background = t == plan.Available ? Tok.Alpha(Tok.Good, 0.06) : null,
                Child = Columns(tcols, T($"{t.ItemLevel}", 13, open ? Tok.Good : Tok.Text, FontWeight.SemiBold, mono: true),
                    T(access, 12, open ? Tok.Good : t.Gate is null ? Tok.Info : Tok.Muted, mono: true),
                    T(t.WhereFor("Weapon"), 12, Tok.Text2, wrap: true), T(t.WhereFor("Guard"), 12, Tok.Text2, wrap: true),
                    T(t.WhereFor("Armor"), 12, Tok.Text2, wrap: true), T(t.WhereFor("Accessory"), 12, Tok.Text2, wrap: true)),
            });
        }

        // Tes pièces, du plus gros gain au plus petit.
        const string cols = "120,*,70,46,*,46";
        var selected = SelectedSlot?.Slot;
        var rows = plan.Slots.Where(x => !x.Special).OrderByDescending(x => x.Gain).ThenBy(x => x.Power).Select(x => (Control)Tip(TableRow(Columns(cols,
                T(x.SlotName, 13), T(x.Item?.Name ?? "vide", 13, x.Item is null ? Tok.Bad : GradeBrush(x.Item)),
                T(x.Item is null ? "—" : $"{x.Item.ItemLevel} +{x.Item.EnchantLevel}", 12, Tok.Text2, mono: true),
                T(x.Target is { } t ? $"{t.ItemLevel}" : "—", 13, x.Gain > 0 ? Tok.Warn : Tok.Muted, mono: true),
                T(x.Where, 12, x.Gain > 0 ? Tok.Text : Tok.Muted, wrap: true),
                T(x.Gain > 0 ? $"+{x.Gain}" : "—", 13, x.Gain > 0 ? Tok.Good : Tok.Faint, FontWeight.SemiBold, mono: true)),
            x.Slot == selected, () => update(st => st.Slot = x.Slot)),
            x.Item is null ? "Emplacement vide."
                : x.Gain > 0 ? $"{x.Item.Name} : {x.Power} → {x.NewPower}" + (x.Item.Grade == "Unique" && x.Item.EnchantLevel > 0 ? $" (transfert du +{x.Item.EnchantLevel})" : " (pièce non unique : l'enchantement ne se transfère pas)")
                : x.Target is null ? "Rien de mieux connu pour cet emplacement." : $"Au palier accessible ; ensuite : pièce {x.Target.ItemLevel}.")).ToList();

        var special = new StackPanel { Spacing = 6 };
        foreach (var x in plan.Slots.Where(x => x.Special))
            special.Children.Add(ColumnsSpaced("120,90,*", 10, T(x.SlotName, 13),
                T(x.Item is null ? "vide" : $"{x.Item.ItemLevel} +{x.Item.EnchantLevel}", 12, x.Item is null ? Tok.Bad : Tok.Text2, mono: true),
                T(x.Where, 12, Tok.Text2, wrap: true)));

        return Page($"Équipement · pièces et sources · {Mode}", badges,
            TableCard("Où trouver mieux : donjons, puis artisanat", T("noms traduits des guides : à valider", 11, Tok.Muted, mono: true),
                TableHeader(tcols, "Niveau", "Accès", "Arme", "Garde", "Armures", "Accessoires"), tierRows),
            TableCard("Tes pièces, du plus gros gain au plus petit", T("gain = points de score", 11, Tok.Muted, mono: true),
                TableHeader(cols, "Emplacement", "Pièce", "Actuelle", "Visée", "Où", "Gain"), rows),
            Card(special, "Ceinture, amulette et runes"),
            Expander("Remplacer sans perdre l'enchantement", IsOpen("pieces:transfer"), () => Toggle("pieces:transfer"), () =>
                T($"{rules.Enchant.Transfer} {rules.CraftNote}", 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0)));
    }

    static IBrush EnchantBrush(EnchantVerdict v) => v switch
    {
        EnchantVerdict.Raise => Tok.Warn,
        EnchantVerdict.Transmute => Tok.Info,
        EnchantVerdict.Replace => Tok.Bad,
        EnchantVerdict.Done => Tok.Good,
        EnchantVerdict.Optional => Tok.Muted,
        _ => Tok.Text2,
    };

    /// <summary>Enchantement : les trois règles en chiffres, puis chaque pièce (cases faites / à faire) et quoi en faire.</summary>
    Control GearEnchantPage()
    {
        var plan = Route;
        var rules = db.Progression.Roadmap.Enchant;
        var raise = plan.Enchants.Where(e => e.Verdict == EnchantVerdict.Raise).ToList();
        var stop = plan.Enchants.Count(e => e.Verdict == EnchantVerdict.Stop);
        var badges = Badges(
            raise.Count > 0 ? Badge($"{raise.Count} à monter · +{raise.Sum(e => e.Gain)}", Tok.Warn) : Badge("✓ rien à monter sans risque", Tok.Good),
            stop > 0 ? Badge($"{stop} à laisser à +10", Tok.Info) : null);

        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        kpis.Children.Add(Kpi("+1 à +10", "garanti", Tok.Good, 18).WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("+11 à +15", rules.Rates.Count > 0 ? $"{rules.Rates[0]} → {rules.Rates[^1]} %" : "chance", Tok.Warn, 18).WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Transfert", "garde le +", Tok.Info, 18));

        var order = new[] { EnchantVerdict.Raise, EnchantVerdict.Transmute, EnchantVerdict.Replace, EnchantVerdict.Stop, EnchantVerdict.Push, EnchantVerdict.Optional, EnchantVerdict.Done };
        const string cols = "120,190,64,*";
        var selected = SelectedSlot?.Slot;
        // Les pièces au même niveau et au même conseil (souvent tout un set) tiennent sur une ligne.
        var rows = plan.Enchants.GroupBy(e => (e.Verdict, e.Text, e.Item.EnchantLevel, e.Max, e.Target))
            .OrderBy(g => Array.IndexOf(order, g.Key.Verdict)).Select(g =>
        {
            var e = g.First();
            var level = e.Item.EnchantLevel;
            // Cases : vert = fait, orange = à monter (jusqu'au niveau visé), vert pâle = au-delà de +10.
            var pips = Pips(Math.Min(level, rules.Guaranteed), e.Verdict == EnchantVerdict.Raise ? e.Target : Math.Min(level, rules.Guaranteed), 10, 8);
            var exceed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { pips } };
            for (var i = rules.Guaranteed; i < Math.Min(level, e.Max); i++)
                exceed.Children.Add(new Border { Width = 10, Height = 8, CornerRadius = new CornerRadius(2), Background = Tok.Alpha(Tok.Good, 0.45), Margin = new Thickness(0, 2, 3, 2) });
            Control name = g.Count() == 1 ? T(e.SlotName, 13)
                : new StackPanel { Spacing = 2, Children = { T($"{g.Count()} pièces", 13), T(string.Join(", ", g.Select(x => x.SlotName)), 11, Tok.Muted, wrap: true) } };
            return (Control)TableRow(Columns(cols, name, exceed, T($"+{level} / {e.Max}", 12, Tok.Text2, mono: true),
                T(e.Text, 12, EnchantBrush(e.Verdict), wrap: true)), g.Any(x => x.Slot == selected), () => update(st => st.Slot = e.Slot));
        }).ToList();

        return Page($"Équipement · enchantement · {Mode}", badges, kpis,
            TableCard("Pièce par pièce", T("vert = fait · orange = à monter", 11, Tok.Muted, mono: true), TableHeader(cols, "Emplacement", "Niveau", "", "Conseil"), rows),
            Expander("Les règles, en détail", IsOpen("enchant:rules"), () => Toggle("enchant:rules"), () => new StackPanel
            {
                Spacing = 6, Margin = new Thickness(22, 0, 0, 0),
                Children =
                {
                    T(rules.KeepTen, 12, Tok.Text2, wrap: true).WithLineHeight(1.45),
                    T(rules.Transfer, 12, Tok.Text2, wrap: true).WithLineHeight(1.45),
                    T(rules.Transmute, 12, Tok.Text2, wrap: true).WithLineHeight(1.45),
                },
            }));
    }

    /// <summary>Les 5 emplacements d'arcanes, dans l'ordre du jeu.</summary>
    static readonly (string Slot, string Kind)[] ArcanaSlots =
        [("Arcana1", "Calice"), ("Arcana2", "Manuscrit"), ("Arcana3", "Boussole"), ("Arcana4", "Cloche"), ("Arcana5", "Miroir")];

    List<(string Slot, string Kind, EquippedItem? Item)> Arcana =>
        ArcanaSlots.Select(a => (a.Slot, a.Kind, r.Character.Equipment.FirstOrDefault(i => i.Slot == a.Slot))).ToList();

    /// <summary>La carte à améliorer d'abord : un emplacement vide, sinon la plus basse (grade, puis niveau).</summary>
    (string Slot, string Kind, EquippedItem? Item)? WeakestArcana => Arcana.Count == 0 ? null
        : Arcana.OrderBy(a => a.Item is null ? -1 : db.Grade(a.Item.Grade).Rank).ThenBy(a => a.Item?.ItemLevel ?? 0).First();

    /// <summary>
    /// Ce qu'une stat divine augmente, d'après stats.json (« Stat divine Temps [Siel] » dans « où la trouver ») : la valeur d'un
    /// point n'est pas connue, elle n'est donc pas comptée dans l'analyse des stats.
    /// </summary>
    string DivineTip(string raw)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(raw, @"\s*\+?\d+([.,]\d+)?\s*%?$", "");
        var stats = db.Stats.Where(d => d.Where.Any(w => w.Equals($"Stat divine {name}", StringComparison.OrdinalIgnoreCase))).Select(d => d.Name).ToList();
        return $"{raw} : points de stat divine{(stats.Count > 0 ? $" ({string.Join(", ", stats)}, d'après les guides)" : "")}. " +
               "La valeur d'un point n'est pas connue : pas encore comptée dans l'analyse des stats.";
    }

    /// <summary>
    /// Arcanes (cartes de Transcendance) : chaque carte, son grade et son niveau, sa stat divine, ses compétences bonus jugées
    /// comme dans l'onglet Compétences ; la carte à améliorer d'abord et les cartes que le score d'équipement ouvre.
    /// </summary>
    Control GearArcanaPage()
    {
        var cards = Arcana;
        var bonuses = r.Skills.GearBonuses.Where(b => b.Arcana).ToList();
        var lost = bonuses.Count(b => !b.Useful);
        var filled = cards.Count(a => a.Item is not null);
        var badges = Badges(
            Badge($"{filled}/{cards.Count} cartes", filled < cards.Count ? Tok.Warn : Tok.Text2),
            lost > 0 ? Badge(Fmt.Plural(lost, "bonus perdu", "bonus perdus"), Tok.Warn) : bonuses.Count > 0 ? Badge("✓ bonus utiles", Tok.Good) : null);

        const string cols = "110,120,70,160,*";
        var rows = new List<Control>();
        foreach (var (slot, kind, item) in cards)
        {
            if (item is null)
            {
                rows.Add(TableRow(Columns(cols, T(kind, 13), T("vide", 12, Tok.Bad), T("—", 12, Tok.Muted, mono: true), T("—", 12, Tok.Muted),
                    T("emplacement à remplir", 12, Tok.Bad)), false, () => { }));
                continue;
            }
            var grade = db.Grade(item.Grade);
            var divine = string.Join(", ", item.MainStats.Concat(item.SubStats).Select(e => e.Raw));
            var skills = new WrapPanel();
            foreach (var b in bonuses.Where(b => b.Slot == slot))
                skills.Children.Add(Tip(T($"{b.SkillName} +{b.Levels}", 12, !b.Useful ? Tok.Bad : b.Strong ? Tok.Good : Tok.Text2), b.Verdict).WithMargin(0, 0, 12, 2));
            if (skills.Children.Count == 0) skills.Children.Add(T("aucune compétence bonus", 12, Tok.Muted));
            rows.Add(TableRow(Columns(cols,
                    new StackPanel { Spacing = 1, Children = { T(kind, 13), T(item.Name, 11, Tok.Muted) } },
                    T(grade.Name, 12, Tok.B(grade.Color)),
                    T($"{item.ItemLevel} +{item.EnchantLevel}", 12, Tok.Text2, mono: true),
                    divine.Length > 0 ? Tip(T(divine, 12, Tok.Text2), DivineTip(divine)) : T("—", 12, Tok.Muted),
                    skills), false, () => { }));
        }

        // Quoi faire : la carte la plus basse, les bonus perdus, ce qu'il faut chercher, et les cartes que ton score ouvre.
        var todo = new StackPanel { Spacing = 8 };
        if (WeakestArcana is { } low)
            todo.Children.Add(T(low.Item is { } w
                ? $"Améliore d'abord le {low.Kind.ToLowerInvariant()} (« {w.Name} », {db.Grade(w.Grade).Name}, niveau {w.ItemLevel}) : c'est ta carte la plus basse."
                : $"Remplis d'abord l'emplacement du {low.Kind.ToLowerInvariant()} : une carte vide ne donne rien.", 13, wrap: true).WithLineHeight(1.45));
        if (lost > 0)
            todo.Children.Add(T($"Bonus perdus : {string.Join(", ", bonuses.Where(b => !b.Useful).Select(b => $"{b.SkillName} ({cards.First(a => a.Slot == b.Slot).Kind.ToLowerInvariant()})"))}. " +
                                "À remplacer en priorité quand tu changes ou relances la carte (survole un bonus pour sa raison).", 12, Tok.Warn, wrap: true).WithLineHeight(1.45));
        if (r.Skills.BonusWanted.Count > 0)
            todo.Children.Add(T($"À chercher sur tes prochaines cartes : {string.Join(", ", r.Skills.BonusWanted.Select(a => a.DamageShare > 0 ? $"{a.Skill.Name} ({Fmt.Pct(a.DamageShare)})" : a.Skill.Name))}. " +
                                $"Un niveau bonus s'ajoute au-dessus des {db.Settings.Skills.BaseMax} achetés : il rapporte le plus sur les sorts qui font le plus de dégâts.",
                12, Tok.Text2, wrap: true).WithLineHeight(1.45));
        var gates = db.Progression.Roadmap.Gates.Where(g => g.Unlocks.Contains("cartes", StringComparison.OrdinalIgnoreCase)).ToList();
        var score = r.Character.ItemLevel;
        if (gates.LastOrDefault(g => g.Score <= score) is { } reached)
            todo.Children.Add(T($"Ton score d'équipement ({Fmt.Num(score)}) ouvre la {reached.Unlocks}." +
                                (gates.FirstOrDefault(g => g.Score > score) is { } next ? $" Ensuite, la {next.Unlocks}, à {Fmt.Num(next.Score)} (+{next.Score - score})." : ""),
                12, Tok.Text2, wrap: true).WithLineHeight(1.45));
        else if (gates.FirstOrDefault() is { } first)
            todo.Children.Add(T($"Les arcanes s'ouvrent à {Fmt.Num(first.Score)} de score d'équipement : {first.Unlocks}.", 12, Tok.Text2, wrap: true));

        return Page($"Équipement · arcanes · {Mode}", badges,
            T("Cinq cartes de Transcendance : chacune donne des points de stat divine et +1 à des compétences de ta classe, tirées au hasard. " +
              "Leur niveau compte dans ton score d'équipement.", 12, Tok.Muted, wrap: true).WithLineHeight(1.4),
            TableCard("Tes cartes", T("vert = bon bonus · rouge = perdu", 11, Tok.Muted, mono: true), TableHeader(cols, "Carte", "Grade", "Niveau", "Stat divine", "Compétences bonus"), rows),
            Card(todo, "Quoi faire"),
            Expander("Stats divines : pas encore comptées", IsOpen("arcana:divine"), () => Toggle("arcana:divine"), () =>
                T("Les points de stat divine de tes cartes (Temps [Siel], Vie [Yustiel]…) ne sont pas dans l'analyse des stats : on sait quelles stats ils augmentent " +
                  "(survole la colonne), pas de combien par point. Une capture de leur infobulle en jeu suffirait à les compter.", 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0)));
    }

    /// <summary>Pierres : pierre sacrée des armes (conseil selon la classe et le mode), puis gravures et pierres de mana ou d'âme.</summary>
    Control GearStonesPage()
    {
        var plan = Route;
        var stones = db.Progression.Roadmap.Stones;
        var todo = r.Engravings.Items.Count(e => e.NeedsWork);
        var sacredCount = plan.SacredSlots.Sum(x => x.Count);
        var badges = Badges(
            todo > 0 ? Badge($"{Fmt.Plural(todo, "pièce")} à revoir", Tok.Warn) : Badge("✓ gravures en ordre", Tok.Good),
            sacredCount > 0 ? Badge($"{Fmt.Plural(sacredCount, "emplacement")} de pierre sacrée", Tok.Info) : null);

        var sacred = new StackPanel { Spacing = 8 };
        foreach (var (slotName, count) in plan.SacredSlots)
            sacred.Children.Add(ColumnsSpaced("120,*", 10, T(slotName, 13),
                T($"{count} emplacement · le site officiel ne dit pas quelle pierre y est posée", 12, Tok.Muted, wrap: true)));
        if (plan.SacredSlots.Count == 0) sacred.Children.Add(T("Aucune de tes armes n'a d'emplacement de pierre sacrée.", 13, Tok.Muted));
        else sacred.Children.Add(Callout(stones.SacredFor(r.Profile.Class.Code, r.Mode), Accent.Main));
        sacred.Children.Add(Expander("Comment marchent les pierres sacrées", IsOpen("stones:sacred"), () => Toggle("stones:sacred"), () =>
            T(stones.Sacred + " " + (r.Mode == GameMode.PvP ? "" : stones.SacredPvP), 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0)));

        return Page($"Équipement · pierres et gravures · {Mode}", badges,
            Card(sacred, "Pierre sacrée (armes)"),
            Card(T(stones.Mana, 12, Tok.Text2, wrap: true).WithLineHeight(1.45), "Pierres de mana et d'âme"),
            EngravingsBlock());
    }

    /// <summary>Tableau des gravures (pièces à revoir en tête) et l'ordre conseillé, replié ; null sans gravures lues.</summary>
    Control? EngravingsBlock()
    {
        var items = r.Engravings.Items;
        if (items.Count == 0) return null;
        var todo = items.Count(e => e.NeedsWork);
        const string cols = "120,90,110,60,*";
        var rows = new List<Control>();
        var selected = SelectedSlot?.Slot;
        foreach (var e in items.OrderByDescending(e => e.NeedsWork).ThenByDescending(e => e.Priority))
        {
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (var l in e.SoulLines) options.Children.Add(T(VerdictIcon(l.Verdict), 12, VerdictBrush(l.Verdict), FontWeight.SemiBold, mono: true));
            var rate = e.HasSoulBinding ? $"{Fmt.Num(e.SoulBindRate ?? 0)} %" : e.SoulLines.Count > 0 ? "fixes" : "—";
            var stoneCount = e.StoneSlots > 0 ? $"{e.Stones.Count}/{e.StoneSlots}" : "—";
            var stoneColor = e.Stones.Count < e.StoneSlots ? Tok.Bad : e.Stones.Any(x => x.Verdict == LineVerdict.Poor) ? Tok.Warn : Tok.Text2;
            rows.Add(Tip(TableRow(Columns(cols,
                    T(e.SlotName, 13), T(rate, 13, !e.HasSoulBinding ? Tok.Muted : (e.SoulBindRate ?? 0) >= 100 ? Tok.Good : Tok.Text, mono: true), options,
                    T(stoneCount, 13, stoneColor, mono: true), T(ActionShort(e), 12, TodoBrush(e))),
                e.Slot == selected, () => update(st => st.Slot = e.Slot)), $"{e.Item.Name}{EngravingTip(e)}"));
        }
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                TableCard($"Gravures · {Fmt.Plural(todo, "pièce")} à revoir", T("✓ utile   ~ moyen   ✕ à remplacer", 11, Tok.Muted, mono: true),
                    TableHeader(cols, "Pièce", "Lien d'âme", "Options", "Pierres", "À faire"), rows),
                Expander("Dans quel ordre corriger", IsOpen("gear:order"), () => Toggle("gear:order"), () =>
                    T("Ordre économe : corrige d'abord les ✕ des pièces que tu gardes (arme en tête), monte ensuite le lien d'âme des pièces " +
                      "aux bonnes options, puis les pierres. Rien sur les pièces à remplacer.", 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0)),
            },
        };
    }

    Control HomeInspector()
    {
        var c = r.Character;
        var panel = new StackPanel();
        panel.Children.Add(SectionHeader("Personnage"));
        panel.Children.Add(new StackPanel
        {
            Spacing = 8, Margin = new Thickness(14, 12),
            Children =
            {
                T(c.Name, 17, weight: FontWeight.SemiBold),
                T($"{r.Profile.Class.Name} · {r.Profile.Role.Name} · niveau {c.Level}", 13, Tok.Text2),
                KeyValue([
                    ("serveur", $"{c.ServerName} ({c.Region})", null), ("niv. d'objet", Fmt.Big(c.ItemLevel), null), ("puissance", Fmt.Big(c.CombatPower), null),
                    ("vue", Mode, Accent.Main), ("build", inputs.UsesDesigned ? "conçu" : "joueurs", null),
                    ("importé", c.FetchedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), null),
                ]),
            },
        });
        panel.Children.Add(SectionHeader("Score"));

        var global = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var value = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        value.Children.Add(T(r.Scores.Overall.ToString(), 26, Tok.Score(r.Scores.Overall), FontWeight.SemiBold, mono: true));
        var outOf = T("/100", 13, Tok.Muted, mono: true);
        outOf.VerticalAlignment = VerticalAlignment.Bottom;
        outOf.Margin = new Thickness(0, 0, 0, 5);
        value.Children.Add(outOf);
        DockPanel.SetDock(value, Dock.Right);
        global.Children.Add(value);
        global.Children.Add(T("Global", 13));
        var scores = new StackPanel { Spacing = 8, Children = { global } };
        foreach (var (label, v, screen) in new[]
                 {
                     ("Stats", r.Scores.Stats, Screen.Stats), ("Équipement", r.Scores.Gear, Screen.Gear),
                     ("Compétences", r.Scores.Skills, Screen.Skills), ("Daevanion", r.Scores.Daevanion, Screen.Daeva),
                 })
        {
            var number = T(v.ToString(), 12, Tok.Score(v), mono: true);
            number.HorizontalAlignment = HorizontalAlignment.Right;
            scores.Children.Add(Clickable(ColumnsSpaced("96,*,28", 10, T(label, 12, Tok.Muted), Bar(v / 100.0, Tok.Score(v), 4, track: Tok.Border), number),
                () => GoTo(screen)));
        }
        panel.Children.Add(new Border { Padding = new Thickness(14), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = scores });

        // « Et si… » : le score global si l'on suivait chaque conseil (même calcul que les notes).
        var gains = WhatIf.Gains(r).Where(g => g.Delta > 0).ToList();
        if (gains.Count > 0)
        {
            panel.Children.Add(SectionHeader("Et si…", Tip(T("score global", 11, Tok.Muted, mono: true), "Même calcul que les notes ; les stats d'une pièce remplacée ne sont pas simulées.")));
            var rows = new StackPanel { Spacing = 6, Margin = new Thickness(14, 10) };
            foreach (var g in gains)
            {
                // Un clic ouvre le simulateur avec ce changement déjà choisi (stats recalculées en entier).
                var preset = g.Area switch
                {
                    Area.Gear => new SimulationOptions(GearAtStage: true),
                    Area.Skills => new SimulationOptions(Skills: true),
                    _ => new SimulationOptions(DaevanionChoice.Next),
                };
                rows.Children.Add(Tip(Clickable(ColumnsSpaced("40,*,Auto", 8, T($"+{g.Delta}", 13, Tok.Good, FontWeight.SemiBold, mono: true),
                        T(g.Label, 12, Tok.Text2, wrap: true), T($"→ {g.Overall}", 12, Tok.Score(g.Overall), mono: true)),
                    () => update(st => { st.Screen = Screen.Simulate; st.Sim = preset; }), hover: Tok.Hover, padding: new Thickness(4, 3)), $"Si tu suivais ce conseil ({g.Detail}), ton score global passerait de {g.Overall - g.Delta} à {g.Overall}.\nClic : ouvrir la simulation."));
            }
            panel.Children.Add(rows);
        }

        // Comparaison aux joueurs de la classe (niveau 45) : position dans l'échantillon, de 0 à 100 %.
        if (r.Profile.Class.Benchmark is { ItemLevel.Count: > 0 } bench && c.Level >= 45)
        {
            var peers = StatAnalyzer.Peers(r.Profile, c)?.Count;
            panel.Children.Add(SectionHeader("Comparé aux joueurs", Tip(T($"{bench.ItemLevel.Count} {r.Profile.Class.Name}s", 11, Tok.Muted, mono: true),
                $"{bench.Source}\nIci, tous les {r.Profile.Class.Name}s relevés ({bench.ItemLevel.Count}) : ta place dans la classe." +
                (peers is { } n ? $"\nL'écran Stats n'en garde que {n}, ceux au niveau d'objet le plus proche du tien, pour comparer des stats à équipement égal." : ""))));
            var rows = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12) };
            var nodes = r.Daevanion.Boards.Where(b => b.Pool == DaevanionPlanner.NormalPool).Sum(b => b.Board.OpenNodes);
            foreach (var (label, mine, sample) in new[] { ("Niveau d'objet", c.ItemLevel, bench.ItemLevel), ("Puissance", c.CombatPower, bench.CombatPower), ("Cases Daevanion", nodes, bench.DaevanionNodes) })
            {
                if (sample.Count == 0) continue;
                var pct = ClassBenchmark.Percentile(sample, mine);
                var color = pct >= 0.6 ? Tok.Good : pct >= 0.3 ? Tok.Warn : Tok.Bad;
                rows.Children.Add(Tip(new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        ColumnsSpaced("*,Auto", 8, T(label, 12, Tok.Muted), T($"{mine} · médiane {ClassBenchmark.Median(sample)}", 12, mono: true)),
                        ColumnsSpaced("*,Auto", 8, Bar(pct, color, 4, track: Tok.Border), T($"devant {Fmt.Pct(pct)}", 11, color, mono: true)),
                    },
                }, $"{label} : {mine}, devant {Fmt.Pct(pct)} des {r.Profile.Class.Name}s relevés (médiane {ClassBenchmark.Median(sample)}).\nÉchantillon : {string.Join(", ", sample)}."));
            }
            panel.Children.Add(rows);
        }

        // Progression : un point par import qui a changé quelque chose (niveau, niveau d'objet, puissance de combat).
        if (progress.Count >= 2)
        {
            static string Delta(int d) => d == 0 ? "" : d > 0 ? $" (+{d})" : $" ({d})";
            var first = progress[0];
            var last = progress[^1];
            panel.Children.Add(SectionHeader("Progression", Clickable(T($"tout voir ({progress.Count} imports)", 11, Accent.Main), () => GoTo(Screen.Progress))));
            var rows = new StackPanel { Spacing = 5, Margin = new Thickness(14, 10) };
            rows.Children.Add(T($"depuis le {first.FetchedAtUtc.ToLocalTime():dd/MM} : objet {Delta(last.ItemLevel - first.ItemLevel).Trim()} · puissance {Delta(last.CombatPower - first.CombatPower).Trim()}",
                12, last.ItemLevel > first.ItemLevel || last.CombatPower > first.CombatPower ? Tok.Good : Tok.Text2, mono: true, wrap: true));
            for (var i = progress.Count - 1; i >= Math.Max(0, progress.Count - 6); i--)
            {
                var p = progress[i];
                var prev = i > 0 ? progress[i - 1] : null;
                rows.Children.Add(ColumnsSpaced("86,*", 8, T(p.FetchedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"), 11, Tok.Muted, mono: true),
                    T((prev is not null && prev.Level != p.Level ? $"niv. {p.Level} · " : "") +
                      $"objet {p.ItemLevel}{Delta(p.ItemLevel - (prev?.ItemLevel ?? p.ItemLevel))} · puissance {p.CombatPower}{Delta(p.CombatPower - (prev?.CombatPower ?? p.CombatPower))}",
                        11, Tok.Text2, mono: true, wrap: true)));
            }
            panel.Children.Add(rows);
        }

        panel.Children.Add(Collapsible("Comment c'est calculé ?", HowText, s.HomeHow, () => update(st => st.HomeHow = !st.HomeHow), new Thickness(14, 12, 14, 14)));
        return panel;
    }

    Border ActionRow(Finding f)
    {
        var (icon, color) = Sev(f.Severity);
        var text = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                T(f.Title, 13, weight: FontWeight.Medium, wrap: true).WithLineHeight(1.35),
                T($"{Fmt.Area(f.Area)} — {f.Action ?? f.Detail}", 12, Tok.Muted, wrap: true).WithLineHeight(1.4),
            },
        };
        var iconText = T(icon, 12, color, FontWeight.SemiBold, mono: true);
        iconText.VerticalAlignment = VerticalAlignment.Top;
        var row = Clickable(ColumnsSpaced("18,*", 8, iconText, text), () => Go(f), hover: Tok.TileHover, padding: new Thickness(14, 10));
        row.BorderBrush = Tok.RowLine2;
        row.BorderThickness = new Thickness(0, 0, 0, 1);
        return row;
    }

    Control GearInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · pièce") } };
        if (SelectedSlot is not { } p) return panel;
        var item = p.Item;
        var grade = item is null ? null : db.Grade(item.Grade);
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        body.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { T(p.SlotName, 12, Tok.Muted), T(item?.Name ?? "Emplacement vide", 17, item is null ? Tok.Muted : GradeBrush(item), FontWeight.SemiBold, wrap: true) },
        });
        body.Children.Add(KeyValue(
        [
            ("rareté", grade?.Name ?? "—", null),
            ("niv. d'objet", item is null ? "—" : p.Power.ToString(), null),
            ("enchantement", item is null ? "—" : $"+{item.EnchantLevel} / {item.MaxEnchantLevel}", null),
            ("statut", Fmt.Status(p.Status), StatusBrush(p.Status)),
        ]));
        body.Children.Add(Field("Conseil", p.Advice));
        if (p.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak)
        {
            var after = WhatIf.SlotFixed(r, p.Slot);
            body.Children.Add(Field("Et si", $"Cette pièce au niveau du palier : score global {r.Scores.Overall} → {after} ({(after > r.Scores.Overall ? "+" : "")}{after - r.Scores.Overall}).", Tok.Good));
        }
        body.Children.Add(Field("Où chercher", p.NextSource ?? r.Gear.Stage.Gear.FirstOrDefault(g => g.Group == p.Group)?.For(r.Mode) ?? "—", Tok.Text2));
        if (item is { HasDetail: true })
        {
            var stats = new StackPanel { Spacing = 3, Children = { Label("Stats de base") } };
            foreach (var e in item.MainStats)
                stats.Children.Add(T(e.Raw, 12, Tok.Text2, mono: true, wrap: true));
            body.Children.Add(stats);
            if (r.Engravings.For(p.Slot) is { } engraving) body.Children.Add(EngravingSection(engraving));
            else if (item.SubStats.Count > 0)
            {
                var subs = new StackPanel { Spacing = 3, Children = { Label("Gravures d'âme") } };
                foreach (var e in item.SubStats) subs.Children.Add(T(e.Raw, 12, Tok.Text2, mono: true, wrap: true));
                body.Children.Add(subs);
            }
        }
        panel.Children.Add(body);
        return panel;
    }

    // ----- Stats -----

    StatTotal? SelectedStat => VisibleStats.FirstOrDefault(x => x.Def.Key == s.Stat) ?? VisibleStats.FirstOrDefault();

    IReadOnlyList<StatTotal> AllVisibleStats => r.Stats.Stats.Where(x => x.Budget > 0 || x.ExpectedShare >= 0.03).ToList();

    /// <summary>Par défaut, les stats qui comptent : utiles à la classe, ou qui prennent de la place, ou visées ; les autres se déplient.</summary>
    IReadOnlyList<StatTotal> VisibleStats => s.AllStats ? AllVisibleStats
        : AllVisibleStats.Where(x => x.Weight >= 0.5 || x.Share >= 0.03 || x.ExpectedShare >= 0.03 || x.Def.Key == s.Stat).ToList();

    static int Pts(double ratio) => (int)Math.Round(ratio * 100);

    /// <summary>Total court pour le tableau (le détail plat + % est dans l'infobulle et l'inspecteur).</summary>
    static string CompactTotal(StatTotal x) => x.Flat > 0 ? Fmt.Num(x.Flat) : $"{Fmt.Num(x.Percent)} %";

    /// <summary>Couleur de la part : rouge s'il en manque, orange si une stat secondaire prend trop de place.</summary>
    static IBrush ShareColor(StatTotal x) =>
        x.ExpectedShare - x.Share > 0.02 ? Tok.Bad : x.Share - x.ExpectedShare > 0.08 && x.Weight < 0.6 ? Tok.Warn : Tok.Text;

    /// <summary>
    /// Verdict d'une stat, en une phrase. Avec des joueurs comparés, eux seuls décident (comme les cartes des familles et les
    /// conseils) ; sans joueurs (JcJ, pas d'échantillon), la part face à la part visée.
    /// </summary>
    (string Text, IBrush Color) Verdict(StatTotal x)
    {
        var importance = StatImportance(x).Text;
        switch (r.Stats.Comparison.PlayerVerdict(x.Def.Key))
        {
            case < 0:
                return x.Weight >= 0.6
                    ? ("En dessous de la moyenne des joueurs de ton niveau d'objet : à monter.", Tok.Bad)
                    : ($"En dessous de la moyenne des joueurs, mais {importance} pour ta classe : pas prioritaire.", Tok.Text2);
            case 0:
                return ("Dans la moyenne des joueurs de ton niveau d'objet : rien à faire.", Tok.Good);
            case > 0:
                return x.Weight < 0.5
                    ? ($"Au-dessus de la moyenne des joueurs et {importance} pour ta classe : de la place à reprendre.", Tok.Warn)
                    : ("Au-dessus de la moyenne des joueurs de ton niveau d'objet : rien à faire.", Tok.Good);
        }
        if (x.ExpectedShare - x.Share > 0.02)
            return ($"Il te manque environ {Pts(x.ExpectedShare - x.Share)} points de part : à augmenter.", Tok.Bad);
        if (x.Share - x.ExpectedShare > 0.08)
            return x.Weight < 0.6
                ? ($"Tu y mets {Pts(x.Share - x.ExpectedShare)} points de part de trop : à réduire au profit des stats manquantes.", Tok.Warn)
                : ($"Au-dessus de la part visée, mais c'est une stat {importance} pour ta classe : rien à réduire.", Tok.Good);
        if (x.Share - x.ExpectedShare > 0.02)
            return ($"Un peu au-dessus de la part visée (+{Pts(x.Share - x.ExpectedShare)} points) : rien d'urgent.", Tok.Good);
        return ("Proche de la part visée pour ta classe.", Tok.Good);
    }

    /// <summary>Ce que vaut la stat pour la classe, en mots (le poids chiffré reste dans l'inspecteur).</summary>
    static (string Text, IBrush Color) StatImportance(StatTotal x) =>
        x.Weight >= 0.8 ? ("prioritaire", Tok.Text) : x.Weight >= 0.5 ? ("utile", Tok.Text2) : ("peu utile", Tok.Muted);

    /// <summary>Ta valeur face à la moyenne des joueurs comparés, en un mot (au-dessus, dans la moyenne, en dessous) ; rouge seulement pour une stat qui compte.</summary>
    static (string Text, IBrush Color) VsWords(StatVsPlayers v) => v.Verdict switch
    {
        > 0 => ("au-dessus", Tok.Good),
        < 0 => ("en dessous", v.Stat.Weight >= 0.6 ? Tok.Bad : Tok.Warn),
        _ => ("dans la moyenne", Tok.Text2),
    };

    /// <summary>
    /// Valeur conseillée d'une stat, en mots : la prochaine à atteindre (« ↑ 900 · Conquête »), ✓ si toutes le sont, un
    /// plafond dépassé, ou, sans chiffre dans les guides, « le plus possible » pour une stat prioritaire (pas de plafond).
    /// </summary>
    (string Text, IBrush Color, string Tip) Advised(StatTotal x, StatComparison cmp)
    {
        var targets = cmp.TargetsFor(x);
        if (targets.Count == 0)
            return x.Weight >= 0.8
                ? ("le plus possible", Tok.Text2, $"Stat prioritaire pour un {r.Profile.Class.Name} en {Mode}, sans plafond connu : chaque point compte.")
                : ("—", Tok.Faint, "Pas de valeur conseillée par les guides pour cette stat.");
        var tip = "Conseillé par les guides (pas d'après les joueurs) :\n" + string.Join("\n", targets.Select(t =>
            $"{StatValue(x, t.Value)} · {t.Label}{(t.Cap ? " (au-delà, ne sert plus)" : "")} — {t.Source}"));
        if (cmp.CapExceeded(x) is { } cap) return ($"plafond {StatValue(x, cap.Value)} dépassé", Tok.Warn, tip);
        if (cmp.NextTarget(x) is { } next)
            return ($"↑ {StatValue(x, next.Value)} · {next.Label}", x.Weight >= 0.6 ? Tok.Bad : Tok.Warn,
                $"Il te manque {StatValue(x, next.Value - x.BenchValue)}.\n{tip}");
        return ($"✓ {StatValue(x, targets[^1].Value)} atteint", Tok.Good, tip);
    }

    /// <summary>
    /// Une stat sur une ligne, à toute la largeur disponible : un trait gris par joueur comparé, un trait clair à leur
    /// moyenne, un triangle vert par valeur conseillée, un losange pour toi.
    /// </summary>
    sealed class StatStrip(double mine, IReadOnlyList<double> players, double? average, IReadOnlyList<double> targets) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(0, 22);

        static Geometry Shape(params Point[] points)
        {
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(points[0], true);
                foreach (var p in points.Skip(1)) c.LineTo(p);
                c.EndFigure(true);
            }
            return geometry;
        }

        public override void Render(DrawingContext ctx)
        {
            var width = Bounds.Width;
            var all = players.Concat(targets).Append(mine).ToList();
            var lo = all.Min();
            var hi = all.Max();
            double X(double v) => hi - lo < 1e-9 ? width / 2 : 7 + (v - lo) / (hi - lo) * (width - 14);
            ctx.FillRectangle(Tok.Border, new Rect(0, 12, width, 2));
            foreach (var v in players) ctx.FillRectangle(Tok.Faint, new Rect(X(v) - 1, 7, 2, 12));
            if (average is { } avg) ctx.FillRectangle(Tok.Text, new Rect(X(avg) - 1, 4, 2, 18));
            foreach (var t in targets)
            {
                ctx.FillRectangle(Tok.Good, new Rect(X(t) - 0.5, 4, 1, 18));
                ctx.DrawGeometry(Tok.Good, null, Shape(new Point(X(t) - 5, 0), new Point(X(t) + 5, 0), new Point(X(t), 7)));
            }
            ctx.DrawGeometry(Accent.Main, null, Shape(new Point(X(mine), 7), new Point(X(mine) + 6, 13), new Point(X(mine), 19), new Point(X(mine) - 6, 13)));
        }
    }

    /// <summary>
    /// Verdict d'une famille, en une phrase. Avec des joueurs comparés, ses stats utiles face à eux décident : la part ne
    /// compte que les stats ajustables, alors que des PV, par exemple, viennent surtout des stats fixes des pièces ; une part
    /// sous la part visée n'est donc pas un manque si les stats sont au niveau des joueurs. Sans joueurs (JcJ), la part face
    /// à la part visée. Off : famille à revoir (compte dans le badge de la page).
    /// </summary>
    (string Text, IBrush Color, bool Off) FamilyVerdict(FamilyReview f, IReadOnlyList<FamilyReview> all)
    {
        var c = f.Share;
        var tolerance = db.Settings.Stats.CategoryTolerance;
        var name = GameDatabase.CategoryName(c.Category).ToLowerInvariant();
        var part = $"{Fmt.Pct(c.Share)} de tes stats ajustables (part visée {Fmt.Pct(c.Target)})";
        var lacking = all.Where(o => o != f && o.Share.Gap < -tolerance && o.AtPlayers != true)
            .Select(o => GameDatabase.CategoryName(o.Share.Category).ToLowerInvariant()).ToList();
        var elsewhere = lacking.Count > 0 ? $"pour {string.Join(" et ", lacking)}" : "ailleurs";
        static string Names(IEnumerable<StatVsPlayers> v) => string.Join(", ", v.Select(x => x.Stat.Def.Name));

        if (f.AtPlayers is not { } atPlayers)
            return Math.Abs(c.Gap) <= tolerance ? ($"Tu as mis en {name} {part} : c'est équilibré.", Tok.Good, false)
                : c.Gap < 0 ? ($"Tu as mis en {name} {part} : {Pts(-c.Gap)} points de moins que visé, à monter.", Tok.Bad, true)
                : ($"Tu as mis en {name} {part} : {Pts(c.Gap)} points de plus que visé, de la place à reprendre {elsewhere}.", Tok.Warn, true);

        if (atPlayers)
        {
            var above = f.Useful.Where(v => v.Verdict > 0).ToList();
            var level = above.Count > 0 ? $"{Names(above)} au-dessus de la moyenne des joueurs comparés" : "Au niveau de la moyenne des joueurs comparés";
            return c.Gap < -tolerance
                ? ($"{level} : rien d'urgent. Tu n'y as mis que {part} : ce niveau vient surtout des stats fixes de tes pièces et de ta base, que la part ne compte pas.", Tok.Good, false)
                : c.Gap > tolerance
                    ? ($"{level}, avec {part} : tu pourrais reprendre de la place {elsewhere}.", Tok.Warn, lacking.Count > 0)
                    : ($"{level}, avec {part} : c'est équilibré.", Tok.Good, false);
        }
        var low = $"{Names(f.Useful.Where(v => v.Verdict < 0))} en dessous de la moyenne des joueurs comparés";
        return c.Gap < -tolerance
            ? ($"{low}, et tu n'y as mis que {part} : à monter (ci-dessous).", Tok.Bad, true)
            : ($"{low}, alors que tu y as mis {part} : vois ci-dessous quelle stat monter.", Tok.Warn, true);
    }

    /// <summary>
    /// Une famille : son verdict en une phrase, ses stats face à la moyenne des joueurs (ou sans joueurs en JcJ), puis quoi
    /// monter et où reprendre de la place.
    /// </summary>
    Control FamilyCard(FamilyReview f, StatComparison cmp, IReadOnlyList<FamilyReview> all)
    {
        var c = f.Share;
        var name = GameDatabase.CategoryName(c.Category);
        var (sentence, color, off) = FamilyVerdict(f, all);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(T(sentence, 13, color, wrap: true));
        body.Children.Add(Tip(Bar(c.Share, color, 6, c.Target), "Barre = ta part des stats ajustables · trait clair = part visée"));

        // Colonnes proportionnelles : la carte occupe toute la largeur, la ligne des joueurs s'étire avec elle.
        var players = cmp.Peers is not null;
        var cols = players ? "1.4*,0.8*,0.7*,1.3*,0.7*,3*,1*" : "1.4*,0.8*,0.7*,1.3*,3*";
        var rows = new StackPanel();
        rows.Children.Add(new Border
        {
            Padding = new Thickness(20, 6), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Columns(cols, (players
                ? new[] { "Stat", "Importance", "Toi", "Conseillé ▾", "Moyenne", "Joueurs ▏ · conseillé ▾ · toi ◆", "Face aux joueurs" }
                : new[] { "Stat", "Importance", "Toi", "Conseillé ▾", "Conseillé ▾ · toi ◆" }).Select(t => (Control)Label(t)).ToArray()),
        });
        var selected = SelectedStat?.Def.Key;
        foreach (var x in f.Stats)
        {
            var (importance, importanceColor) = StatImportance(x);
            var (advised, advisedColor, advisedTip) = Advised(x, cmp);
            var targets = cmp.TargetsFor(x).Select(t => t.Value).ToList();
            var cells = new List<Control>
            {
                T(x.Def.Name, 13), T(importance, 12, importanceColor), Tip(T(CompactTotal(x), 13, mono: true), x.Display),
                Tip(T(advised, 12, advisedColor), advisedTip),
            };
            if (players)
            {
                if (f.Vs(x) is { } v)
                {
                    var (words, wordsColor) = VsWords(v);
                    var detail = $"Toi {StatValue(x, v.Mine)}, moyenne des {v.Values.Count} joueurs {StatValue(x, v.Average)} (de {StatValue(x, v.Values.Min())} à {StatValue(x, v.Values.Max())}). " +
                                 $"Tu en as plus que {v.Below} d'entre eux sur {v.Values.Count}." + (targets.Count > 0 ? $"\n{advisedTip}" : "");
                    cells.Add(T(StatValue(x, v.Average), 13, Tok.Text2, mono: true));
                    cells.Add(Tip(new StatStrip(v.Mine, v.Values, v.Average, targets), detail));
                    cells.Add(Tip(T(words, 12, wordsColor), detail));
                }
                else cells.AddRange([T("—", 13, Tok.Faint), new Border(), new Border()]);
            }
            else cells.Add(targets.Count > 0 ? Tip(new StatStrip(x.BenchValue, [], null, targets), advisedTip) : new Border());
            rows.Children.Add(TableRow(Columns(cols, cells.ToArray()), x.Def.Key == selected, () => update(st => st.Stat = x.Def.Key)));
        }
        body.Children.Add(new Border { Margin = new Thickness(-14, 0), Child = rows });

        // Quoi faire : monter les stats sous une valeur conseillée ou sous la moyenne (où les trouver), puis où reprendre de la place.
        if (f.Raise.Count > 0)
            body.Children.Add(T("↑ À monter : " + string.Join(" · ", f.Raise.Select(x => $"{x.Stat.Def.Name} ({x.Why}) : {x.Where}")) + ".",
                12, Tok.Text, wrap: true).WithLineHeight(1.5));
        // Les stats utiles en dessous qu'on ne peut plus aller chercher : le dire, plutôt que de les taire.
        var stuck = f.Players.Where(v => v.Verdict < 0 && v.Stat.Weight >= 0.6 && f.Raise.All(x => x.Stat != v.Stat)).Select(v => v.Stat.Def.Name).ToList();
        if (stuck.Count > 0)
            body.Children.Add(T($"{string.Join(", ", stuck)} : en dessous aussi, mais plus rien à prendre sur tes pièces actuelles ni tes cases Daevanion ; " +
                "elles monteront avec tes prochaines pièces (écran Équipement).", 12, Tok.Muted, wrap: true).WithLineHeight(1.5));
        if (f.Free.Count > 0)
            body.Children.Add(T("↓ Où reprendre de la place, aux relances de stats secondaires et aux choix Daevanion : " +
                string.Join(", ", f.Free.Select(x => $"{x.Stat.Def.Name} ({x.Why})")) + ".", 12, Tok.Text2, wrap: true).WithLineHeight(1.5));
        if (f.Raise.Count == 0 && f.Free.Count == 0 && !off)
            body.Children.Add(T("Rien à faire dans cette famille.", 12, Tok.Muted));

        return Card(body, name, T($"part {Fmt.Pct(c.Share)} · visée {Fmt.Pct(c.Target)}", 12, color, mono: true));
    }

    Control Stats()
    {
        var sp = r.Stats;
        var cmp = sp.Comparison;
        var tolerance = db.Settings.Stats.CategoryTolerance;
        var off = cmp.Families.Count(f => FamilyVerdict(f, cmp.Families).Off);
        var below = cmp.Families.SelectMany(f => f.Players).Count(v => v.Verdict < 0 && v.Stat.Weight >= 0.6);
        var badges = Badges(
            Badge(off == 0 ? "✓ familles équilibrées" : $"{Fmt.Plural(off, "famille")} à revoir", off == 0 ? Tok.Good : Tok.Warn),
            cmp.Peers is null ? null : Badge(below == 0 ? "✓ rien sous la moyenne" : Fmt.Plural(below, "stat utile", "stats utiles") + " sous la moyenne", below == 0 ? Tok.Good : Tok.Bad));

        // Comment lire : chaque mot de la page défini une fois.
        var cls = r.Profile.Class.Name;
        var how = new StackPanel { Spacing = 6 };
        void Define(string term, string text) => how.Children.Add(ColumnsSpaced("150,*", 12, T(term, 12, Tok.Text, FontWeight.SemiBold), T(text, 12, Tok.Text2, wrap: true)));
        Define("Part", "ce que tu as mis dans une famille, sur tes stats ajustables : stats secondaires des pièces, cases Daevanion, titres, stats divines. " +
                       "Les stats fixes des pièces (la DÉF d'une armure, l'ATQ d'une arme) n'y sont pas.");
        Define("Part visée", $"la part visée par famille pour un {cls} ({r.Profile.Role.Name.ToLowerInvariant()}) en {Mode}, tirée des poids de stats de la classe : une base de réglage, pas une moyenne de joueurs. " +
                             "Quand des joueurs sont comparés, ce sont eux qui décident : une famille sous sa part visée mais au niveau des joueurs ne manque de rien (ses stats viennent des stats fixes des pièces).");
        Define("Conseillé ▾", $"la valeur que visent les guides du {cls} pour chaque stat (Précision, CRIT…), triangle vert sur la ligne ; sans chiffre dans " +
                              "les guides, « le plus possible » pour une stat prioritaire, qui n'a pas de plafond. Ce que lisent les guides, pas ce que font les joueurs.");
        Define("Moyenne joueurs", cmp.PeersText is { } who
            ? $"{who[0].ToString().ToUpperInvariant()}{who[1..]}, stats totales calculées comme les tiennes. « Dans la moyenne » : à 3 % près."
            : r.Mode == GameMode.PvE ? "pas d'échantillon de joueurs pour ta classe à ton niveau." : "relevée en JcE seulement : passe en vue JcE pour la voir.");

        var blocks = new List<Control?> { Expander("Comment lire cette page", IsOpen("stats:how"), () => Toggle("stats:how"), () => Card(how, null)) };
        blocks.AddRange(cmp.Families.Where(f => f.Stats.Count > 0 || Math.Abs(f.Share.Gap) > tolerance).Select(f => FamilyCard(f, cmp, cmp.Families)));
        blocks.Add(Clickable(T(s.AllStats ? "▴ masquer le tableau de toutes les stats" : "▾ toutes les stats en tableau (part, part visée, poids)", 12, Accent.Main),
            () => update(st => st.AllStats = !st.AllStats), hover: Tok.Hover, padding: new Thickness(4, 6)));
        if (s.AllStats) blocks.Add(AllStatsTable());
        return Page($"Stats · {Mode}", badges, blocks.ToArray());
    }

    /// <summary>Toutes les stats en tableau (part, part visée, poids), pour qui veut le détail.</summary>
    Control AllStatsTable()
    {
        const string cols = "150,100,90,70,70,150,*";
        var rows = new List<Control>();
        var selected = SelectedStat?.Def.Key;
        foreach (var x in VisibleStats)
        {
            var weight = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { new Border { Width = 140, Child = Bar(x.Weight, Accent.Main, 3) }, T(x.Weight.ToString("0.00"), 12, Tok.Muted, mono: true) },
            };
            var (rank, rankColor, rankTip) = PlayersRank(x);
            rows.Add(TableRow(Columns(cols,
                    T(x.Def.Name, 13), T(GameDatabase.CategoryName(x.Def.Category), 13, Tok.Muted), Tip(T(CompactTotal(x), 13, mono: true), x.Display),
                    T(Fmt.Pct(x.Share), 13, ShareColor(x), mono: true), T(Fmt.Pct(x.ExpectedShare), 13, Tok.Muted, mono: true),
                    Tip(T(rank, 13, rankColor, mono: true), rankTip), weight),
                x.Def.Key == selected, () => update(st => st.Stat = x.Def.Key)));
        }
        return TableCard("Toutes les stats", T("part et part visée : sur tes stats ajustables · clic = détail dans l'inspecteur", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Stat", "Famille", "Total", "Part", "Part visée", "Face aux joueurs", "Poids"), rows);
    }

    /// <summary>
    /// Joueurs de comparaison : ceux de ta classe au niveau d'objet le plus proche du tien (les stats montent avec lui) ;
    /// échantillon relevé en JcE, rien en JcJ.
    /// </summary>
    List<BenchPlayer>? Peers => r.Mode == GameMode.PvE ? StatAnalyzer.Peers(r.Profile, r.Character) : null;

    /// <summary>Valeurs de la stat chez ces joueurs.</summary>
    List<double>? PlayersSample(StatTotal x) => Peers is { } peers ? ClassBenchmark.Sample(peers, x.Def.Key) : null;

    string StatValue(StatTotal x, double v) => x.Def.Kind == StatKind.Percent ? $"{Fmt.Num(v)} %" : Fmt.Num(Math.Round(v));

    /// <summary>Ta valeur face à la moyenne des joueurs comparés, en mots ; en couleur seulement pour les stats qui comptent.</summary>
    (string Text, IBrush Color, string Tip) PlayersRank(StatTotal x)
    {
        if (Peers is not { } peers || PlayersSample(x) is not { } sample)
            return ("—", Tok.Faint, r.Mode == GameMode.PvE ? "Pas d'échantillon de joueurs pour ta classe à ton niveau." : "Joueurs relevés en JcE : passe en vue JcE.");
        var v = new StatVsPlayers(x, sample);
        var (words, color) = VsWords(v);
        return (words, x.Weight < 0.5 ? Tok.Muted : color,
            $"Toi {StatValue(x, v.Mine)}, moyenne {StatValue(x, v.Average)} : {StatAnalyzer.PeersText(r.Profile, r.Character, peers)}. " +
            $"Tu en as plus que {v.Below} d'entre eux sur {sample.Count} (stats totales calculées comme les tiennes, en JcE).");
    }

    static Border TableHeader(string cols, params string[] titles) => new()
    {
        Padding = new Thickness(20, 8), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
        Child = Columns(cols, titles.Select(t => (Control)Label(t)).ToArray()),
    };

    /// <summary>Ligne de tableau cliquable : survol, et sélection = fond « dim » + bordure gauche de 2 px.</summary>
    static Border TableRow(Control content, bool selected, Action onClick)
    {
        var row = Clickable(content, onClick, selected ? Accent.Dim : null, selected ? null : Tok.Hover, new Thickness(18, 7, 20, 7));
        row.BorderBrush = selected ? Accent.Main : Brushes.Transparent;
        row.BorderThickness = new Thickness(2, 0, 0, 0);
        return row;
    }

    Control StatInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · stat") } };
        if (SelectedStat is not { } x) return panel;
        var (verdict, color) = Verdict(x);
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        body.Children.Add(new StackPanel { Spacing = 4, Children = { T(x.Def.Name, 17, weight: FontWeight.SemiBold), T(x.Def.Desc, 13, Tok.Text2, wrap: true).WithLineHeight(1.5) } });
        body.Children.Add(KeyValue(
        [
            ("famille", GameDatabase.CategoryName(x.Def.Category), null),
            ("total", x.Display, null),
            ("part", Fmt.Pct(x.Share), ShareColor(x)),
            ("part visée", Fmt.Pct(x.ExpectedShare), null),
            ("poids", x.Weight.ToString("0.00"), null),
        ]));
        body.Children.Add(new Border
        {
            Padding = new Thickness(12, 10), Background = Tok.Frame, BorderBrush = color, BorderThickness = new Thickness(2, 0, 0, 0),
            Child = T(verdict, 13, wrap: true).WithLineHeight(1.5),
        });
        if (PlayersSample(x) is { } sample)
            body.Children.Add(Field("Face aux joueurs de ton niveau d'objet",
                $"Toi : {StatValue(x, x.BenchValue)} · moyenne : {StatValue(x, sample.Average())} · de {StatValue(x, sample.Min())} à {StatValue(x, sample.Max())}. " +
                $"{new StatVsPlayers(x, sample).Verdict switch { > 0 => "Au-dessus de la moyenne", < 0 => "En dessous de la moyenne", _ => "Dans la moyenne" }} ; " +
                $"tu en as plus que {StatAnalyzer.Ahead(Peers!, x)} sur {sample.Count}. " +
                $"Joueurs : {StatAnalyzer.PeersText(r.Profile, r.Character, Peers!)} (même calcul, JcE).",
                PlayersRank(x).Color));
        body.Children.Add(Field("Où la trouver", x.Def.Where.Count > 0 ? string.Join(", ", x.Def.Where) : "—", Tok.Text2));
        var sources = x.Sources.GroupBy(z => z.Source)
            .Select(g => (Source: g.Key, Flat: g.Where(z => !z.Effect.IsPercent).Sum(z => z.Effect.Value), Pct: g.Where(z => z.Effect.IsPercent).Sum(z => z.Effect.Value)))
            .OrderByDescending(g => g.Flat + g.Pct * 10)
            .Select(g => $"{g.Source} (+{(g.Flat > 0 ? Fmt.Num(g.Flat) : "")}{(g.Flat > 0 && g.Pct > 0 ? " / +" : "")}{(g.Pct > 0 ? Fmt.Num(g.Pct) + " %" : "")})")
            .Take(4).ToList();
        body.Children.Add(Field("Principales sources", sources.Count > 0 ? string.Join(" · ", sources) : "aucune", Tok.Text2));
        panel.Children.Add(body);
        return panel;
    }

    // ----- Compétences -----

    /// <summary>Compétence choisie (null : aucune, l'inspecteur montre la vue d'ensemble) ; une compétence absente de ce personnage passe à la première à changer.</summary>
    string? SelectedSkillName => s.Skill is not { } n ? null
        : r.Skills.Skills.Any(a => a.Skill.Name == n) || r.Skills.Stigmas.Any(a => a.Skill.Name == n) ? n
        : r.Skills.Skills.Where(a => a.Delta != 0).OrderByDescending(a => a.Priority).Select(a => a.Skill.Name).FirstOrDefault()
          ?? r.Skills.Skills.FirstOrDefault()?.Skill.Name;

    static string DeltaText(SkillAdvice a) => a.Delta == 0 ? "=" : a.Delta > 0 ? $"+{a.Delta} → {a.RecommendedBase}" : $"−{-a.Delta} → {a.RecommendedBase}";

    static IBrush DeltaColor(SkillAdvice a) => a.Delta == 0 ? Tok.Muted : a.Delta > 0 ? Tok.Good : Tok.Bad;

    /// <summary>Niveau visé, indépendamment des points disponibles : ↑ à monter, ↓ au-dessus du visé, = atteint.</summary>
    static (string Text, IBrush Color, string Tip) Aim(SkillAdvice a) =>
        !a.Skill.Acquired ? ("—", Tok.Muted, $"Pas encore apprise (niveau {a.Skill.NeedLevel}).")
        : a.Base < a.Target ? ($"↑ {a.Target}", Tok.Good, $"À monter jusqu'au niveau acheté {a.Target}, dès que tu as des points : {a.Role.ToLowerInvariant()}.")
        : a.Base > a.Target ? ($"↓ {a.Target}", Tok.Warn, $"Au-dessus du niveau visé ({a.Target}) : ces points serviront ailleurs dès qu'une compétence plus importante en manque.")
        : ($"= {a.Target}", Tok.Muted, "Au niveau visé.");

    static string Pts(int n) => $"{n} point{(n > 1 ? "s" : "")}";

    static IBrush ImportanceColor(double priority) =>
        priority >= 0.85 ? Tok.Good : priority >= 0.7 ? Tok.Text : priority >= 0.5 ? Tok.Text2 : priority >= 0.3 ? Tok.Muted : Tok.Faint;

    /// <summary>D'où vient le niveau total : points achetés, cases Daevanion, arcanes, autres pièces.</summary>
    static string LevelSources(SkillAdvice a) =>
        $"acheté {(a.Delta == 0 ? $"{a.Base}" : $"{a.Base} → {a.RecommendedBase}")}" + (a.BonusDaevanion > 0 ? $" · Daevanion +{a.BonusDaevanion}" : "") +
        (a.BonusArcana > 0 ? $" · arcanes +{a.BonusArcana}" : "") + (a.BonusGear > 0 ? $" · équipement +{a.BonusGear}" : "");

    /// <summary>Le prochain moyen de monter une compétence : les points jusqu'au niveau 10, puis les cases Daevanion, puis arcanes et équipement.</summary>
    string NextLever(SkillAdvice a)
    {
        var max = db.Settings.Skills.BaseMax;
        if (a.RecommendedBase < max) return $"points : {a.RecommendedBase}/{max}";
        if (a.OpenableNodes > 0) return $"Daevanion : {a.OpenableNodes} case{(a.OpenableNodes > 1 ? "s" : "")} fermée{(a.OpenableNodes > 1 ? "s" : "")}";
        return "arcanes, équipement";
    }

    string NextSpecialty(SkillAdvice a) =>
        db.Settings.Skills.SpecialtyLevels.FirstOrDefault(l => l > a.TotalAfter) is var l and > 0 ? $"→ {l} (+{l - a.TotalAfter} niv.)" : "max";

    /// <summary>
    /// Déplacements groupés par compétence qui reçoit (« +9 Coup de choc ← Défense d'égide −8 »), chacun avec la raison
    /// de l'algorithme ; puis pourquoi les donneuses baissent.
    /// </summary>
    Control SkillMoves(SkillPlan sk)
    {
        var list = new StackPanel { Spacing = 2 };
        Control Entry(Control head, string reason, string skill) => Clickable(new StackPanel
        {
            Spacing = 2, Children = { head, T(reason, 12, Tok.Muted, wrap: true).WithLineHeight(1.4) },
        }, () => update(st => st.Skill = skill), hover: Tok.Hover, padding: new Thickness(6, 4));
        foreach (var group in sk.Moves.GroupBy(m => m.To))
        {
            var moves = group.ToList();
            var sources = string.Join(", ", moves.Select(m => m.From is null ? $"non dépensés −{m.Points}" : $"{m.From.Skill.Name} −{m.Points}"));
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    T($"+{moves.Sum(m => m.Points)}  {group.Key.Skill.Name}", 13, Tok.Good, FontWeight.SemiBold),
                    T("←", 13, Tok.Muted),
                    T(sources, 13, moves.All(m => m.From is null) ? Tok.Text2 : Tok.Bad),
                },
            };
            list.Children.Add(Entry(line, group.Key.Reason, group.Key.Skill.Name));
        }
        var donors = sk.Moves.Where(m => m.From is not null).Select(m => m.From!).Distinct().OrderBy(d => d.Priority).ToList();
        if (donors.Count > 0)
        {
            list.Children.Add(Label("Pourquoi elles cèdent des points").WithMargin(6, 10, 0, 2));
            foreach (var d in donors)
                list.Children.Add(Entry(T($"−{-d.Delta}  {d.Skill.Name}", 13, Tok.Bad, FontWeight.SemiBold), d.Decision, d.Skill.Name));
        }
        return list;
    }

    /// <summary>Compétences bonus portées par l'équipement et les arcanes : utiles ou perdues, et quoi chercher ensuite.</summary>
    Control GearBonuses(SkillPlan sk)
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var b in sk.GearBonuses.OrderBy(b => b.Useful).ThenByDescending(b => b.Skill?.DamageShare ?? 0))
        {
            var color = !b.Useful ? Tok.Bad : b.Strong ? Tok.Good : Tok.Text2;
            list.Children.Add(Clickable(ColumnsSpaced("190,190,*", 8,
                    T(b.Arcana ? $"Arcane · {b.Item}" : db.SlotName(b.Slot), 12, Tok.Muted),
                    T($"{b.SkillName} +{b.Levels}", 13, b.Useful ? null : Tok.Bad),
                    T(b.Verdict, 12, color, wrap: true)),
                () => { if (b.Skill is { } a) update(st => st.Skill = a.Skill.Name); }, hover: Tok.Hover, padding: new Thickness(6, 3)));
        }
        if (sk.GearBonuses.Count == 0)
            list.Children.Add(T("Aucune compétence bonus sur ton équipement ni sur tes arcanes.", 12, Tok.Muted).WithMargin(6, 2, 0, 0));
        if (sk.BonusWanted.Count > 0)
            list.Children.Add(T($"À chercher sur tes prochaines arcanes et pièces : {string.Join(", ", sk.BonusWanted.Select(a => a.DamageShare > 0 ? $"{a.Skill.Name} ({Fmt.Pct(a.DamageShare)})" : a.Skill.Name))}. " +
                $"Un niveau bonus s'ajoute au-dessus des {db.Settings.Skills.BaseMax} achetés : il rapporte le plus sur les sorts qui font le plus de dégâts.",
                12, Tok.Text2, wrap: true).WithLineHeight(1.4).WithMargin(6, 8, 6, 2));
        return list;
    }

    Control NextPoints(SkillPlan sk)
    {
        var list = new StackPanel { Spacing = 2 };
        if (sk.NextPoints.Count == 0)
            list.Children.Add(T($"Tes compétences utiles sont au niveau acheté maximum ({db.Settings.Skills.BaseMax}) : la suite passe par les cases Daevanion et l'équipement.",
                12, Tok.Muted, wrap: true).WithLineHeight(1.4));
        // Points numérotés de bout en bout (« 3–4 ») : le joueur suit la liste jusqu'à épuisement de ses points.
        const int shown = 8;
        var total = 0;
        foreach (var (x, index) in sk.NextPoints.Select((x, i) => (x, i)))
        {
            var first = total + 1;
            total += x.Points;
            if (index >= shown && !s.AllNext) continue;
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (x.Specialty is { } spec) tags.Children.Add(T($"✦ spé. {spec}", 11, Accent.Main, FontWeight.SemiBold, mono: true));
            if (x.To == x.Skill.Target) tags.Children.Add(T("visé atteint", 11, Tok.Good, mono: true));
            else if (x.BeyondTarget) tags.Children.Add(T("au-delà du visé", 11, Tok.Faint, mono: true));
            tags.Children.Add(T(x.Skill.DamageShare > 0 ? $"{Fmt.Pct(x.Skill.DamageShare)} des dégâts" : SkillAdvisor.Importance(x.Skill.Priority), 11, Tok.Muted, mono: true));
            var name = x.Skill.Skill.Name;
            list.Children.Add(Clickable(ColumnsSpaced("54,*,Auto", 8,
                    T(x.Points == 1 ? $"{first}" : $"{first}–{total}", 12, Tok.Muted, mono: true),
                    T($"{name}   {x.From} → {x.To}", 13, x.BeyondTarget ? Tok.Text2 : null),
                    tags),
                () => update(st => st.Skill = name), hover: Tok.Hover, padding: new Thickness(6, 3)));
        }
        if (sk.NextPoints.Count > shown)
        {
            var rest = sk.NextPoints.Skip(shown).Sum(x => x.Points);
            list.Children.Add(Clickable(T(s.AllNext ? "▴ réduire" : $"▾ voir la suite ({rest} points de plus)", 12, Accent.Main),
                () => update(st => st.AllNext = !st.AllNext), hover: Tok.Hover, padding: new Thickness(6, 4)));
        }
        return list;
    }

    /// <summary>
    /// Compétences, du plus important au plus fin : ce qu'il faut déplacer (schéma retirer → ajouter), ce qui fait les
    /// dégâts, les bonus d'équipement et d'arcanes, les prochains points ; le tableau complet, les raisons et les stigmas sont des sous-pages.
    /// Chaque compétence cliquée montre sa raison dans l'inspecteur.
    /// </summary>
    Control SkillsOverview()
    {
        var sk = r.Skills;
        var set = db.Settings.Skills;
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(20, 14, 20, 24) };

        // 1. En-tête : barre de section (deux badges à droite), puis le budget (points non dépensés à saisir).
        var moved = sk.Moves.Sum(m => m.Points);
        var measured = sk.DamageSource?.StartsWith("mesuré", StringComparison.Ordinal) == true;
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        badges.Children.Add(Badge(moved > 0 ? $"{Pts(moved)} à déplacer" : "✓ points bien placés", moved > 0 ? Tok.Warn : Tok.Good));
        badges.Children.Add(Tip(Badge(measured ? "dégâts mesurés" : "dégâts simulés", measured ? Tok.Good : Tok.Text2), sk.DamageSource ?? "pas de rotation à simuler"));
        page.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                T($"{sk.SpentPoints} points investis +", 12, Tok.Muted, mono: true),
                Stepper(sk.UnspentPoints, v => onInputs(inputs with { UnspentSkillPoints = v })),
                T($"non dépensés · réinitialisation gratuite · niveau acheté max {set.BaseMax}", 12, Tok.Muted, mono: true),
            },
        });

        // 2. D'où viennent les % : écrit en clair, avec ce que deviennent les suites d'enchaînement.
        var source = measured
            ? $"D'où viennent les % : {sk.DamageSource}. Les suites d'enchaînement, que le meter affiche à part, comptent avec la compétence " +
              "qui les lance : c'est elle qu'on monte. Même calcul pour toutes les classes."
            : $"D'où viennent les % : {sk.DamageSource ?? "rien à simuler pour cette classe"}, faute de combat de boss ou de mannequin enregistré pour ce perso. " +
              "Lance le Pet Meter pendant tes combats puis ↻ sur l'écran Combat : tes vrais chiffres remplaceront la simulation.";
        page.Children.Add(new Border
        {
            Background = Tok.Panel, BorderBrush = measured ? Tok.Good : Tok.Warn, BorderThickness = new Thickness(3, 1, 1, 1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 10), Child = T(source, 13, Tok.Text2, wrap: true).WithLineHeight(1.5),
        });

        // 3. Toutes les compétences en une liste, des dégâts aux passives.
        page.Children.Add(Card(SkillList(sk), "Tes compétences, des dégâts aux passives",
            T("clic = la raison", 12, Tok.Muted), new Thickness(0, 6, 0, 8)));

        // 4. Le reste se déplie : bonus d'équipement et d'arcanes, prochains points.
        page.Children.Add(Expander($"Niveaux bonus : équipement et arcanes ({sk.GearBonuses.Count})", s.SkillBonus, () => update(st => st.SkillBonus = !st.SkillBonus),
            () => Card(BonusSummary(sk))));
        var total = sk.NextPoints.Sum(x => x.Points);
        if (total > 0)
            page.Children.Add(Expander($"Quand tu gagnes des points ({total} à placer, dans l'ordre)", s.SkillNext, () => update(st => st.SkillNext = !st.SkillNext),
                () => Card(NextPoints(sk))));

        return new StackPanel { Children = { SectionHeader($"Compétences · {Mode}", badges, 20), page } };
    }

    /// <summary>
    /// Niveau en cases : acheté et gardé (couleur d'accent), ajouté par le conseil (vert), bonus gratuits (bleu), puis les points
    /// qui partent (rouge pâle, en bout de ligne). Les cases pleines font le niveau après conseil.
    /// </summary>
    static Control LevelPips(SkillAdvice a)
    {
        var removed = Math.Max(0, a.Base - a.RecommendedBase);
        var count = Math.Max(16, a.TotalAfter + removed);
        var keep = Math.Min(a.Base, a.RecommendedBase);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < count; i++)
            row.Children.Add(new Border
            {
                Width = 5, Height = 12, CornerRadius = new CornerRadius(1),
                Background = i < keep ? Accent.Main : i < a.RecommendedBase ? Tok.Good : i < a.TotalAfter ? Tok.Info
                    : i < a.TotalAfter + removed ? Tok.Alpha(Tok.Bad, 0.4) : Tok.HoverStrong,
            });
        row.Children.Add(new Border { Margin = new Thickness(6, 0, 0, 0), Child = T($"{a.TotalAfter}", 12, mono: true) });
        return row;
    }

    /// <summary>
    /// Toutes les compétences en une liste : celles qui font les dégâts (de la plus grosse part à la plus petite, leurs suites
    /// d'enchaînement en dessous), les actives sans dégâts directs, les passives à revoir ; les passives bien placées et les
    /// compétences pas encore apprises se déplient.
    /// </summary>
    Control SkillList(SkillPlan sk)
    {
        const string cols = "1.7*,130,124,*";
        var set = db.Settings.Skills;
        var list = new StackPanel();
        var selected = SelectedSkillName;
        var max = Math.Max(0.0001, sk.Skills.Select(a => a.DamageShare).DefaultIfEmpty(0).Max());
        static string SharePct(double v) => v > 0 && v < 0.005 ? "< 1 %" : Fmt.Pct(v);

        // Légende des cases, puis en-tête de colonnes.
        static Control Swatch(IBrush color, string text) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            Children = { new Border { Width = 5, Height = 12, CornerRadius = new CornerRadius(1), Background = color }, T(text, 11, Tok.Muted) },
        };
        list.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(20, 4, 20, 6),
            Children = { T("Niveau :", 11, Tok.Muted), Swatch(Accent.Main, "acheté"), Swatch(Tok.Good, "à ajouter"), Swatch(Tok.Info, "bonus gratuit"), Swatch(Tok.Alpha(Tok.Bad, 0.4), "à retirer") },
        });
        list.Children.Add(TableHeader(cols, "Compétence", "Part des dégâts", "Niveau", "À faire"));

        // Court dans la colonne ; la raison entière (qui donne les points, pourquoi) est dans l'infobulle et l'inspecteur.
        string Todo(SkillAdvice a) =>
            !a.Skill.Acquired ? $"au niveau {a.Skill.NeedLevel}"
            : a.Delta > 0 ? $"+{a.Delta} → {a.RecommendedBase}"
            : a.Delta < 0 ? $"−{-a.Delta} → {a.RecommendedBase}"
            : a.RecommendedBase >= set.BaseMax ? "✓ max" : "✓";
        IBrush TodoColor(SkillAdvice a) => !a.Skill.Acquired ? Tok.Faint : a.Delta > 0 ? Tok.Good : a.Delta < 0 ? Tok.Bad : Tok.Muted;
        void Group(string title) => list.Children.Add(new Border { Padding = new Thickness(20, 12, 20, 4), Child = Label(title) });
        void Row(SkillAdvice a)
        {
            var name = a.Skill.Name;
            var share = a.DamageShare > 0
                ? ColumnsSpaced("*,38", 6, Bar(a.DamageShare / max, Accent.Main, 6), T(SharePct(a.DamageShare), 12, Tok.Text2, mono: true).AlignRight())
                : (Control)T("—", 12, Tok.Faint, mono: true);
            list.Children.Add(Tip(TableRow(Columns(cols,
                    T(name, 13, a.Skill.Acquired ? null : Tok.Muted), share, LevelPips(a), T(Todo(a), 12, TodoColor(a))),
                name == selected, () => update(st => st.Skill = name)), a.Decision));
            // Suites d'enchaînement de ce sort : déjà comptées dans sa part, montrées en dessous.
            foreach (var c in sk.Chains.Where(c => GameDatabase.NormalizeLabel(c.Parent) == GameDatabase.NormalizeLabel(name)))
                list.Children.Add(Tip(new Border
                {
                    Padding = new Thickness(20, 3, 20, 3),
                    Child = Columns(cols, T($"  ↳ {c.Skill}", 12, Tok.Muted),
                        ColumnsSpaced("*,38", 6, Bar(c.Share / max, Accent.Dim, 6), T(SharePct(c.Share), 12, Tok.Muted, mono: true).AlignRight()),
                        new Border(), T("sa suite", 12, Tok.Faint)),
                }, $"Le meter l'affiche à part. Elle suit {Fmt.Pct(c.Regular)} des lancers de {name}{(c.Via is null ? "" : $" (après {c.Via})")}, " +
                   $"{Fmt.Num(Math.Round(c.Lag, 1))} s après : c'est la même compétence, on monte {name}."));
        }

        var learned = sk.Skills.Where(a => a.Skill.Acquired).ToList();
        var dealing = learned.Where(a => a.DamageShare > 0).OrderByDescending(a => a.DamageShare).ToList();
        var actives = learned.Where(a => a.Kind != SkillKind.Passive && a.DamageShare <= 0).OrderBy(a => a.Delta == 0).ThenByDescending(a => a.Priority).ToList();
        var review = learned.Where(a => a.Kind == SkillKind.Passive && a.DamageShare <= 0 && a.Delta != 0).OrderByDescending(a => a.Delta).ToList();
        var settled = learned.Where(a => a.Kind == SkillKind.Passive && a.DamageShare <= 0 && a.Delta == 0).OrderByDescending(a => a.Priority).ToList();
        var later = sk.Skills.Where(a => !a.Skill.Acquired).ToList();

        if (dealing.Count > 0) { Group("Elles font tes dégâts"); dealing.ForEach(Row); }
        if (actives.Count > 0) { Group("Sans dégâts directs · utiles à ton rôle"); actives.ForEach(Row); }
        if (review.Count > 0) { Group("Passives à revoir"); review.ForEach(Row); }
        var folded = settled.Count + later.Count;
        if (folded > 0)
        {
            list.Children.Add(Clickable(T(s.SkillsMore ? "▴ réduire"
                    : $"▸ {string.Join(" · ", new[] { settled.Count > 0 ? $"{settled.Count} passive{(settled.Count > 1 ? "s" : "")} bien placée{(settled.Count > 1 ? "s" : "")}" : null, later.Count > 0 ? $"{later.Count} pas encore apprise{(later.Count > 1 ? "s" : "")}" : null }.OfType<string>())}",
                    12, Accent.Main),
                () => update(st => st.SkillsMore = !st.SkillsMore), hover: Tok.Hover, padding: new Thickness(20, 8)));
            if (s.SkillsMore)
            {
                if (settled.Count > 0) { Group("Passives bien placées"); settled.ForEach(Row); }
                if (later.Count > 0) { Group("Pas encore apprises"); later.ForEach(Row); }
            }
        }
        return list;
    }

    /// <summary>Écran Compétences : la vue d'ensemble, ou une sous-page de la barre secondaire.</summary>
    Control Skills()
    {
        var sk = r.Skills;
        var moved = sk.Moves.Sum(m => m.Points);
        var badge = Badge(moved > 0 ? $"{Pts(moved)} à déplacer" : "✓ points bien placés", moved > 0 ? Tok.Warn : Tok.Good);
        return s.SkillPage switch
        {
            SkillPage.Moves => Page($"Compétences · déplacements · {Mode}", badge, sk.Moves.Count > 0
                ? Card(SkillMoves(sk), "Pourquoi ces déplacements, compétence par compétence")
                : Card(T("Rien à déplacer : tes points sont bien placés.", 13, Tok.Good), "Déplacements")),
            SkillPage.Stigmas => Page($"Compétences · stigmas ({db.Settings.Stigmas.Slots} emplacements) · {Mode}", null, StigmasBlock(sk)),
            SkillPage.Specs => SpecsPage(),
            _ => SkillsOverview(),
        };
    }

    /// <summary>Bonus d'équipement et d'arcanes : une barre bons / corrects / perdus, puis les sorts à chercher ; la liste dépliable.</summary>
    Control BonusSummary(SkillPlan sk)
    {
        var panel = new StackPanel { Spacing = 12 };
        var good = sk.GearBonuses.Count(b => b.Strong);
        var lost = sk.GearBonuses.Count(b => !b.Useful);
        var fair = sk.GearBonuses.Count - good - lost;
        if (sk.GearBonuses.Count > 0)
        {
            var bar = new Grid { Height = 12 };
            foreach (var (n, color) in new[] { (good, Tok.Good), (fair, Brush("#5C6068")), (lost, Tok.Bad) }.Where(x => x.Item1 > 0))
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(n, GridUnitType.Star)));
                var part = new Border { Background = color };
                Grid.SetColumn(part, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(part);
            }
            panel.Children.Add(new Border { CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = bar });
            panel.Children.Add(new WrapPanel
            {
                Children =
                {
                    T($"{good} bon{(good > 1 ? "s" : "")}", 13, Tok.Good, FontWeight.SemiBold).WithMargin(0, 0, 16, 0),
                    T($"{fair} correct{(fair > 1 ? "s" : "")}", 13, Tok.Text2, FontWeight.SemiBold).WithMargin(0, 0, 16, 0),
                    T($"{lost} perdu{(lost > 1 ? "s" : "")}", 13, lost > 0 ? Tok.Bad : Tok.Text2, FontWeight.SemiBold),
                },
            });
        }
        else panel.Children.Add(T("Aucune compétence bonus sur ton équipement ni sur tes arcanes.", 13, Tok.Muted));
        if (sk.BonusWanted.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var a in sk.BonusWanted)
                chips.Children.Add(new Border
                {
                    Background = Tok.HoverStrong, CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 6),
                    Child = T(a.DamageShare > 0 ? $"{a.Skill.Name} · {Fmt.Pct(a.DamageShare)}" : a.Skill.Name, 12, Tok.Text, mono: true),
                });
            panel.Children.Add(new StackPanel { Spacing = 6, Children = { T("À chercher sur tes prochaines arcanes et pièces", 12, Tok.Muted), chips } });
        }
        if (sk.GearBonuses.Count > 0)
            panel.Children.Add(Expander($"voir les {sk.GearBonuses.Count} bonus", IsOpen("skills:bonus"), () => Toggle("skills:bonus"), () => GearBonuses(sk)));
        return panel;
    }

    /// <summary>Stigmas conseillés (garder, mettre, retirer) et prochains fragments par paliers (replié par défaut).</summary>
    Control StigmasBlock(SkillPlan sk)
    {
        var panel = new StackPanel();
        var selected = SelectedSkillName;
        const string stigmaCols = "80,*,140,90,110";
        foreach (var g in sk.Stigmas.Where(x => x.Recommended || x.Slotted))
        {
            var (label, color) = g.Recommended ? (g.Slotted ? ("Garder", Tok.Good) : ("Mettre", Tok.Info)) : ("Retirer", Tok.Bad);
            panel.Children.Add(TableRow(Columns(stigmaCols,
                    T(label, 12, color, FontWeight.SemiBold, mono: true), T(g.Skill.Name, 13, g.Skill.Acquired ? null : Tok.Muted), T(g.Role, 13, Tok.Text2),
                    T(g.Recommended ? $"{g.Skill.Level} → {g.Target}" : $"{g.Skill.Level}", 13, mono: true),
                    T(g.ShardsToTarget > 0 ? $"{g.ShardsToTarget} fragments" : "", 13, Tok.Muted, mono: true)),
                g.Skill.Name == selected, () => update(st => st.Skill = g.Skill.Name)));
        }
        // Fragments : par paliers de 5 niveaux, là où les effets des stigmas se débloquent.
        if (sk.NextShards.Count > 0)
        {
            panel.Children.Add(new Border { Padding = new Thickness(20, 12, 20, 4), Child = Label("Prochains fragments · par paliers (effets aux niveaux 5, 10, 15, 20)") });
            var i = 0;
            foreach (var x in sk.NextShards)
            {
                var name = x.Stigma.Skill.Name;
                panel.Children.Add(Clickable(ColumnsSpaced("24,*,Auto", 8,
                        T($"{++i}.", 12, Tok.Muted, mono: true),
                        db.Spell(x.Stigma.Skill.Id)?.Specs.FirstOrDefault(sp => sp.Level == x.To) is { } effect
                            ? new StackPanel { Spacing = 2, Children = { T($"{name}   {x.From} → {x.To}", 13), T($"débloque : {effect.Text}", 11, Tok.Text2, wrap: true) } }
                            : T($"{name}   {x.From} → {x.To}", 13),
                        T($"{x.Shards} fragments · palier {x.To}", 12, Tok.Muted, mono: true)),
                    () => update(st => st.Skill = name), hover: Tok.Hover, padding: new Thickness(20, 4)));
            }
        }
        return new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = panel };
    }

    // ----- Spécialisations : lesquelles équiper, sort par sort -----

    /// <summary>Conseil de spécialisations des compétences actives (calculé une fois par rendu).</summary>
    List<SkillSpecPlan> Specs => specs ??= SpecAdvisor.Plan(db, r);
    List<SkillSpecPlan>? specs;

    /// <summary>
    /// Une carte par compétence active (ce qui fait les dégâts d'abord) : ses 5 spécialisations dans l'ordre du jeu, celles à
    /// équiper numérotées, celle du prochain emplacement marquée « ensuite », les verrouillées avec leur niveau.
    /// </summary>
    Control SpecsPage()
    {
        var plans = Specs;
        var slots = db.Settings.Skills.SpecialtySlots;
        var open = plans.Sum(p => p.Slots);
        var badges = Badges(
            Badge(Fmt.Plural(open, "emplacement ouvert", "emplacements ouverts"), Accent.Main),
            Tip(Badge("à comparer en jeu", Tok.Info), "Le site officiel ne dit pas quelles spécialisations sont équipées : compare avec ta fenêtre de compétence."));
        if (plans.Count == 0)
            return Page($"Compétences · spécialisations · {Mode}", null,
                T("Pas de spécialisations connues pour les compétences de cette classe.", 13, Tok.Muted));
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var p in plans) grid.Children.Add(SpecCard(p).WithMargin(0, 0, 10, 10));
        return Page($"Compétences · spécialisations · {Mode}", badges,
            T($"Emplacements : 1 au niveau {slots.ElementAtOrDefault(0)}, 2 au {slots.ElementAtOrDefault(1)}, 3 au {slots.ElementAtOrDefault(2)} (niveau total, bonus compris). " +
              "Rien ne s'équipe tout seul : clique l'option dans la fenêtre de la compétence.", 12, Tok.Muted, wrap: true).WithLineHeight(1.4),
            grid,
            Expander("Comment c'est choisi", IsOpen("specs:how"), () => Toggle("specs:how"), () =>
                T($"Chaque spécialisation est classée d'après son texte (dégâts, enchaînement en plus, recharge, critique, vitesse, contrôle, soin, PM, défense), " +
                  $"puis notée pour ton rôle ({r.Profile.Role.Name}) en {GameDatabase.ModeName(r.Mode)} : en JcE, ce qui fait des dégâts sur un boss d'abord, " +
                  "le contrôle y sert peu ; en JcJ, le contrôle et les dégâts. Un effet conditionnel (« si », « lors d'un coup CRIT », « chances ») compte un peu moins. " +
                  "Spécialisations relevées sur metabot.gg (octobre 2026).", 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0)));
    }

    /// <summary>Carte d'une compétence : niveau et emplacements, puis ses spécialisations ; clic sur le titre = l'inspecteur.</summary>
    Control SpecCard(SkillSpecPlan p)
    {
        var total = db.Settings.Skills.SpecialtySlots.Count;
        var name = p.Skill.Skill.Name;
        var list = new StackPanel { Spacing = 6 };
        foreach (var o in p.Options)
            list.Children.Add(Tip(ColumnsSpaced("56,*", 8, SpecMarker(o),
                T(o.Spec.Text, 12, o.Rank is not null ? Tok.Text : o.Unlocked ? Tok.Text2 : Tok.Muted, o.Rank is not null ? FontWeight.SemiBold : FontWeight.Normal, wrap: true)),
                $"{o.Why} · {(o.Unlocked ? "débloquée" : $"débloquée au niveau {o.Spec.Level}")}"));
        if (p.NextSlot is { } next)
            list.Children.Add(T(p.LevelAfter >= next && p.Level < next
                    ? $"Emplacement {p.Slots + 1} au niveau {next} : atteint avec les déplacements conseillés ({p.Level} → {p.LevelAfter})."
                    : $"Emplacement {p.Slots + 1} au niveau {next} ({next - p.Level} niveau{(next - p.Level > 1 ? "x" : "")} de plus).",
                11, Tok.Muted, wrap: true).WithMargin(0, 2, 0, 0));
        var head = Clickable(new DockPanel
            {
                Children =
                {
                    Tip(new Border { [DockPanel.DockProperty] = Dock.Right, Background = Brushes.Transparent, Child = Pips(p.Slots, total, 10, 8) },
                        $"Emplacements de spécialisation : {p.Slots} ouvert{(p.Slots > 1 ? "s" : "")} sur {total} (vert = ouvert, orange = encore fermé). " +
                        $"Ils s'ouvrent aux niveaux {string.Join(", ", db.Settings.Skills.SpecialtySlots)} de la compétence."),
                    T($"{name} · niv. {p.Level}", 13, name == SelectedSkillName ? Accent.Main : Tok.Text, FontWeight.SemiBold),
                },
            },
            () => update(st => st.Skill = name), hover: Tok.Hover, padding: new Thickness(0, 2));
        return Card(new StackPanel { Spacing = 10, Children = { head, list } }, null);
    }

    /// <summary>Repère d'une option : rang à équiper (1, 2, 3), « ensuite » pour le prochain emplacement, niveau si verrouillée.</summary>
    static Control SpecMarker(SpecChoice o)
    {
        if (o.Rank is { } rank)
            return new Border
            {
                Background = Tok.Alpha(Accent.Main, 0.15), BorderBrush = Tok.Alpha(Accent.Main, 0.5), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(6, 0),
                Child = T($"✓ {rank}", 11, Accent.Main, FontWeight.SemiBold, mono: true),
            };
        if (o.Later) return T("ensuite", 11, Tok.Info, mono: true).WithMargin(0, 1, 0, 0);
        return T(o.Unlocked ? "·" : $"niv {o.Spec.Level}", 11, Tok.Muted, mono: true).WithMargin(0, 1, 0, 0);
    }

    Control SkillInspector()
    {
        // Sans compétence choisie : la vue d'ensemble (ce qui bouge, ce qui fait les dégâts) plutôt qu'une compétence au hasard.
        if (s.Skill is null) return new StackPanel { Children = { SectionHeader("Inspecteur · compétences"), SkillSummary() } };
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · compétence") } };
        var name = SelectedSkillName;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        if (r.Skills.Skills.FirstOrDefault(a => a.Skill.Name == name) is { } a)
        {
            body.Children.Add(T(a.Skill.Name, 17, weight: FontWeight.SemiBold, wrap: true));
            body.Children.Add(Field("Décision", a.Decision, a.Delta > 0 ? Tok.Good : a.Delta < 0 ? Tok.Bad : Tok.Text2));
            // Ce que contient sa part quand le meter montre ses suites d'enchaînement à part.
            var chains = r.Skills.Chains.Where(c => GameDatabase.NormalizeLabel(c.Parent) == GameDatabase.NormalizeLabel(a.Skill.Name)).ToList();
            if (chains.Count > 0)
                body.Children.Add(Field($"Ce que comptent ses {Fmt.Pct(a.DamageShare)}",
                    string.Join("\n", chains.Select(c => $"{c.Skill} (sa suite) : {Fmt.Pct(c.Share)} · suit {Fmt.Pct(c.Regular)} de ses lancers, {Fmt.Num(Math.Round(c.Lag, 1))} s après")
                        .Prepend($"la compétence elle-même : {Fmt.Pct(Math.Max(0, a.DamageShare - chains.Sum(c => c.Share)))}")), Tok.Text2));
            body.Children.Add(KeyValue(
            [
                ("type", a.Kind == SkillKind.Passive ? "Passive" : a.InBar ? "Active (dans la barre)" : "Active", null),
                ("rôle", a.Role, null),
                ("importance", $"{SkillAdvisor.Importance(a.Priority)} ({a.Priority:0.00})", ImportanceColor(a.Priority)),
                ("dégâts", a.DamageShare > 0 ? $"{Fmt.Pct(a.DamageShare)} des dégâts {(r.Skills.MeasuredFights > 0 ? "mesurés" : "simulés")} (n° {a.DamageRank})" : "aucun direct", a.DamageShare > 0 ? Tok.Text : Tok.Muted),
                ("niveau", $"{a.Base} +{a.Bonus} = {a.Total}", null),
                ("provenance", LevelSources(a), Tok.Text2),
                ("visé", Aim(a).Text, Aim(a).Color),
                ("maintenant", a.Delta == 0 ? "rien à changer" : DeltaText(a), DeltaColor(a)),
                ("pour monter", a.Skill.Acquired ? $"{NextLever(a)} · prochaine spé. {NextSpecialty(a)}" : "à apprendre", null),
            ]));
            // Données du sort (niveau 1, metabot.gg) : recharge, % d'ATQ, cibles, et ce que ça représente par seconde.
            if (db.Spell(a.Skill.Id) is { Coef: > 0 } spell)
            {
                var perSecond = spell.Cooldown > 0 ? spell.Coef / spell.Cooldown : 0;
                body.Children.Add(KeyValue(
                [
                    ("recharge", spell.Cooldown > 0 ? $"{Fmt.Num(spell.Cooldown)} s" : "aucune", null),
                    ("dégâts", $"{Fmt.Num(spell.Coef)} % d'ATQ{(spell.Targets > 1 ? $" · jusqu'à {spell.Targets} cibles" : "")}", null),
                    ("par seconde", perSecond > 0 ? $"{Fmt.Num(perSecond)} % d'ATQ sur une cible" : "sans recharge : limitée par le temps de lancer et les PM", Tok.Text2),
                ]));
            }
            body.Children.Add(Field("Rôle dans la classe", a.Why, a.Unverified ? Tok.Warn : null));
            // Échanges : d'où viennent ses points, ou à qui elle les cède, et pourquoi.
            var exchanges = r.Skills.Moves.Where(m => m.To == a).Select(m => m.From is null
                    ? $"Reçoit {Pts(m.Points)} non {(m.Points > 1 ? "dépensés" : "dépensé")}."
                    : $"Reçoit {Pts(m.Points)} de {m.From.Skill.Name} ({m.From.Role.ToLowerInvariant()}, importance {m.From.Priority:0.00}).")
                .Concat(r.Skills.Moves.Where(m => m.From == a).Select(m =>
                    $"Cède {Pts(m.Points)} à {m.To.Skill.Name} ({m.To.Role.ToLowerInvariant()}, importance {m.To.Priority:0.00} contre {a.Priority:0.00})."))
                .ToList();
            if (exchanges.Count > 0) body.Children.Add(Field("Échanges conseillés", string.Join("\n", exchanges), Tok.Text2));
            var rank = r.Skills.NextPoints.FindIndex(x => x.Skill == a);
            if (rank >= 0)
            {
                var step = r.Skills.NextPoints[rank];
                body.Children.Add(Field("Prochains points", $"N° {rank + 1} dans l'ordre : {step.From} → {step.To}" +
                    (step.Specialty is { } spec ? $", ce qui débloque la spécialisation du niveau {spec}." : "."), Tok.Text2));
            }
            if (Specs.FirstOrDefault(p => p.Skill.Skill.Name == a.Skill.Name) is { } plan)
            {
                // Spécialisations : les conseillées d'abord, puis celle du prochain emplacement.
                var lines = plan.Recommended.Select(o => $"✓ {o.Rank} · {o.Spec.Text}")
                    .Concat(plan.Options.Where(o => o.Later).Select(o => $"ensuite · {o.Spec.Text}")).ToList();
                body.Children.Add(Field($"Spécialisations · {plan.Slots}/{db.Settings.Skills.SpecialtySlots.Count} emplacements",
                    lines.Count > 0 ? string.Join("\n", lines) : "Aucune avant le niveau 8.", Accent.Main));
            }
            body.Children.Add(T("Bonus : niveaux gratuits venant des cases Daevanion et de l'équipement.", 12, Tok.Muted, wrap: true).WithLineHeight(1.5));
        }
        else if (r.Skills.Stigmas.FirstOrDefault(x => x.Skill.Name == name) is { } g)
        {
            body.Children.Add(T(g.Skill.Name, 17, weight: FontWeight.SemiBold, wrap: true));
            body.Children.Add(KeyValue(
            [
                ("type", "Stigma", null),
                ("rôle", g.Role, null),
                ("niveau", g.Recommended ? $"{g.Skill.Level} → {g.Target}" : g.Skill.Level.ToString(), null),
                ("conseil", g.Recommended ? (g.Slotted ? "garder" : "mettre") : g.Slotted ? "retirer" : "pas utile", g.Recommended ? (g.Slotted ? Tok.Good : Tok.Info) : g.Slotted ? Tok.Bad : Tok.Muted),
                ("fragments", g.ShardsToTarget.ToString(), null),
                ("priorité", g.Priority.ToString("0.00"), null),
            ]));
            body.Children.Add(Field("Pourquoi", g.Why, g.Unverified ? Tok.Warn : null));
        }
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>L'inspecteur des compétences sans sélection : points à déplacer, stigmas, et ce qui fait les dégâts (clic = la compétence).</summary>
    Control SkillSummary()
    {
        var sk = r.Skills;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        body.Children.Add(T("Vue d'ensemble", 17, weight: FontWeight.SemiBold));
        var moved = sk.Moves.Sum(m => m.Points);
        body.Children.Add(KeyValue(
        [
            ("à déplacer", moved > 0 ? $"{Pts(moved)} ({Fmt.Plural(sk.Moves.Count, "échange")})" : "rien", moved > 0 ? Tok.Warn : Tok.Good),
            ("stigmas", $"{sk.Stigmas.Count(x => x.Slotted)}/{db.Settings.Stigmas.Slots} équipés", null),
            ("dégâts", sk.MeasuredFights > 0 ? $"mesurés sur {Fmt.Plural(sk.MeasuredFights, "combat")}" : "simulés (aucun combat importé)", Tok.Text2),
        ]));
        var focus = sk.Focus.Where(a => a.DamageShare >= 0.005).Take(8).ToList();
        if (focus.Count > 0)
        {
            var rows = new StackPanel { Spacing = 2 };
            foreach (var a in focus)
            {
                var name = a.Skill.Name;
                var share = T(Fmt.Pct(a.DamageShare), 11, Tok.Text2, mono: true);
                DockPanel.SetDock(share, Dock.Right);
                rows.Children.Add(Clickable(new DockPanel { Children = { share, T(name, 12, Tok.Text2) } },
                    () => update(st => st.Skill = name), hover: Tok.Hover, padding: new Thickness(4, 3)));
            }
            body.Children.Add(new StackPanel { Spacing = 6, Children = { Label("Ce qui fait tes dégâts"), rows } });
        }
        body.Children.Add(T("Clique une compétence, ici ou dans la page, pour son détail.", 12, Tok.Muted, wrap: true));
        return body;
    }

    // ----- Combat : combat NotMeter lu sur des captures -----

    CombatReport? combatReport;

    /// <summary>Séances (donjons) : index des combats, du plus récent au plus ancien.</summary>
    List<List<int>> CombatSessions => combatSessions ??= CombatLog.Sessions(combats.Select(x => x.Log).ToList());
    List<List<int>>? combatSessions;

    /// <summary>
    /// Combat affiché (null : l'historique) : un combat, ou le global d'une séance. Jugé avec le profil JcE (les combats
    /// mesurés sont des combats JcE : mannequin, boss, donjon).
    /// </summary>
    CombatReport? Combat => combatReport ??= CombatLogShown is { } log
        ? new CombatAnalyzer(db).Analyze(log, db.Resolve(r.Character.ClassCode, GameMode.PvE, inputs.UsesDesigned)) : null;

    CombatLog? CombatLogShown =>
        s.CombatList ? null
        : s.CombatGroup is int g && g < CombatSessions.Count ? CombatLog.Merge(CombatSessions[g].Select(i => combats[i].Log).ToList())
        : ShownCombat is int i ? combats[i].Log : null;

    /// <summary>Combat ouvert : celui choisi, sinon le plus récent (la barre secondaire liste les autres).</summary>
    int? ShownCombat => s.CombatList || s.CombatGroup is not null ? null
        : s.CombatIndex is int i && i < combats.Count ? i
        : combats.Count > 0 ? combats.Select((c, index) => (c.Log.ImportedAtUtc, index)).MaxBy(x => x.ImportedAtUtc).index : null;

    static string Duration(double seconds) => seconds >= 60 ? $"{(int)seconds / 60} min {(int)seconds % 60:00}" : $"{seconds:0} s";

    /// <summary>
    /// Historique : les combats gardés (les 20 derniers), regroupés par séance (même carte, moins de 10 min d'écart) ;
    /// un clic ouvre le détail, « global » additionne une séance (un donjon).
    /// </summary>
    Control CombatHistory()
    {
        var list = new StackPanel { Spacing = 14, Margin = new Thickness(20, 14, 20, 24) };
        const string cols = "90,*,80,90,90,70";
        var profile = db.Resolve(r.Character.ClassCode, GameMode.PvE, inputs.UsesDesigned);
        var analyzer = new CombatAnalyzer(db);
        var best = combats.Count > 0 ? combats.Max(x => x.Log.Dps) : 0;
        for (var g = 0; g < CombatSessions.Count; g++)
        {
            var session = CombatSessions[g];
            var logs = session.Select(i => combats[i].Log).ToList();
            var first = logs.Min(l => l.ImportedAtUtc).ToLocalTime();
            var last = logs.Max(l => l.ImportedAtUtc.AddSeconds(l.Duration)).ToLocalTime();
            var group = g;
            Control? global = null;
            if (session.Count > 1)
            {
                var total = logs.Sum(l => l.TotalDamage) / Math.Max(1, logs.Sum(l => l.Duration));
                global = Clickable(T($"global · {Fmt.Big(total)} DPS ›", 11, Accent.Main, FontWeight.SemiBold, mono: true),
                    () => update(st => { st.CombatGroup = group; st.CombatIndex = null; st.CombatSkill = null; st.CombatList = false; }), hover: Tok.HoverStrong, padding: new Thickness(6, 0));
            }
            var map = logs.Select(l => l.Map).FirstOrDefault(m => m is not null);
            var rows = new List<Control>();
            foreach (var i in session)
            {
                var (_, log) = combats[i];
                var index = i;
                var todo = analyzer.Analyze(log, profile).Findings.Count(f => f.Severity <= Severity.Advice);
                // DPS en barre (le meilleur combat gardé = pleine barre) : la séance se lit d'un coup d'œil.
                var dps = ColumnsSpaced("54,*", 6, T(Fmt.Big(log.Dps), 13, Accent.Main, FontWeight.SemiBold, mono: true), RoundBar(log.Dps / Math.Max(1, best), Accent.Main, 4));
                rows.Add(TableRow(Columns(cols,
                        T(log.ImportedAtUtc.ToLocalTime().ToString("HH:mm:ss"), 12, Tok.Muted, mono: true), T(log.Target, 13),
                        T(Duration(log.Duration), 12, mono: true), dps,
                        T(PetMeterFights.KindName(log) ?? (log.Source == "Pet Meter" ? "Pet Meter" : "captures"), 12, Tok.Muted),
                        T(todo == 0 ? "✓" : $"{todo}", 12, todo == 0 ? Tok.Good : Tok.Warn, mono: true)),
                    false, () => update(st => { st.CombatIndex = index; st.CombatGroup = null; st.CombatSkill = null; st.CombatList = false; })));
            }
            list.Children.Add(TableCard($"{first:dd/MM} · {first:HH:mm}–{last:HH:mm} · {session.Count} combat{(session.Count > 1 ? "s" : "")}" +
                $" · {Duration(logs.Sum(l => l.Duration))} de combat" + (map is { } m ? $" · carte {m}" : ""), global,
                TableHeader(cols, "Heure", "Cible", "Durée", "DPS", "Type", "À revoir"), rows));
        }
        list.Children.Add(T($"Seuls les boss, le mannequin et le JcJ sont gardés (les {CombatStore.Keep} derniers) ; une séance = même carte et moins de 10 min entre deux combats (un donjon). Clic = le détail.",
            11, Tok.Muted, wrap: true));
        return list;
    }

    static IBrush LevelColor(Severity level) => level switch { Severity.Critical => Tok.Bad, Severity.Important => Tok.Warn, Severity.Advice => Tok.Info, _ => Tok.Text2 };

    Control CombatScreen()
    {
        var panel = new StackPanel();
        var import = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children =
            {
                Tip(Clickable(T("↻ derniers combats", 12, Accent.Main, FontWeight.SemiBold, mono: true), reloadCombats, hover: Tok.HoverStrong, padding: new Thickness(8, 2)),
                    "Reprendre les derniers combats du Pet Meter"),
                Clickable(T("+ captures…", 12, Accent.Main, FontWeight.SemiBold, mono: true), importCombat, hover: Tok.HoverStrong, padding: new Thickness(8, 2)),
            },
        };
        panel.Children.Add(SectionHeader("Combat · Pet Meter ou captures NotMeter", import, 20));
        if (Combat is null && combats.Count > 0)
        {
            panel.Children.Add(CombatHistory());
            return panel;
        }
        if (Combat is not { } cr)
        {
            var steps = new StackPanel { Spacing = 10, Margin = new Thickness(20, 16) };
            steps.Children.Add(T("Analyse un combat (mannequin ou boss) relevé par NotMeter : sorts lancés moins souvent que leur recharge le permet, " +
                "sorts de ta rotation jamais lancés, buffs mal entretenus, placement, et les vraies parts de dégâts de tes sorts " +
                "(elles remplacent alors la simulation dans le focus dégâts des Compétences).", 13, Tok.Text2, wrap: true).WithLineHeight(1.5));
            foreach (var (n, text) in new[]
                     {
                         ("1", "Dans NotMeter, ouvre « Détails du combat » sur ton combat."),
                         ("2", "Capture la fenêtre (Win + Maj + S, ou l'appareil photo de NotMeter). La liste des compétences défile : une capture par écran, jusqu'en bas."),
                         ("3", "« + captures… » et sélectionne-les toutes ensemble."),
                     })
                steps.Children.Add(ColumnsSpaced("24,*", 8, T(n, 13, Accent.Main, FontWeight.Bold, mono: true), T(text, 13, wrap: true)));
            steps.Children.Add(T("Le texte est lu sur ton PC par la reconnaissance de texte de Windows : rien n'est envoyé. NotMeter doit être en français. " +
                "Plus simple : laisse tourner le Pet Meter (v0.7.0 ou plus) pendant tes combats, ils arrivent ici tout seuls, avec tes lancers chronométrés, " +
                "tes combos ratés, tes temps morts et les dégâts subis ; « ↻ derniers combats » reprend ceux écrits depuis.",
                12, Tok.Muted, wrap: true).WithLineHeight(1.4));
            panel.Children.Add(steps);
            return panel;
        }
        panel.Children.Add(CombatDetail(cr));
        return panel;
    }

    bool IsOpen(string id) => s.Open.Contains(id);

    void Toggle(string id) => update(st => { if (!st.Open.Remove(id)) st.Open.Add(id); });

    /// <summary>
    /// Détail d'un combat, du plus important au plus fin : le DPS et ce qu'il faut retenir, au plus 3 choses à corriger,
    /// les dégâts et les recharges en graphiques, la frise du combat ; les tableaux complets et la méthode sont repliés.
    /// </summary>
    Control CombatDetail(CombatReport cr)
    {
        var log = cr.Log;
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(20, 14, 20, 24) };
        void Select(string name) => update(st => st.CombatSkill = name);
        page.Children.Add(Clickable(T(s.CombatGroup is not null ? $"‹ tous les combats · global de la séance ({log.Target})" : "‹ tous les combats", 13, Accent.Main),
            () => update(st => { st.CombatIndex = null; st.CombatGroup = null; st.CombatSkill = null; st.CombatList = true; }), hover: Tok.Hover, padding: new Thickness(0, 2)));

        // 1. Résumé : le DPS, deux badges, quatre chiffres.
        var todo = cr.Findings.Where(f => f.Severity <= Severity.Advice).ToList();
        var idle = todo.FirstOrDefault(f => f.Title.StartsWith("Temps morts", StringComparison.Ordinal));
        var badges = new WrapPanel();
        if (log.Casts is { Count: > 0 })
            badges.Children.Add(Badge(idle is null ? "Bon rythme : aucune pause" : idle.Title, idle is null ? Tok.Good : Tok.Warn).WithMargin(0, 0, 8, 6));
        badges.Children.Add(Badge(todo.Count == 0 ? "✓ rien à corriger" : $"{todo.Count} point{(todo.Count > 1 ? "s" : "")} à corriger", todo.Count == 0 ? Tok.Good : Tok.Warn)
            .WithMargin(0, 0, 8, 6));
        var subtitle = s.CombatGroup is not null
            ? $"{log.Target} du {log.ImportedAtUtc.ToLocalTime():dd/MM HH:mm}, mis bout à bout · {log.Duration:0} s" + (log.Map is { } mapId ? $" · carte {mapId}" : "")
            : $"{log.Target} · {log.ImportedAtUtc.ToLocalTime():dd/MM HH:mm} · {log.Duration:0} s · " + (log.Source == "Pet Meter" ? "Pet Meter" : $"{log.Pages} capture{(log.Pages > 1 ? "s" : "")} NotMeter");
        var hero = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                T(subtitle, 13, Tok.Muted, wrap: true),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { T(Fmt.Big(log.Dps), 44, Accent.Main, FontWeight.SemiBold, mono: true), T("DPS", 15, Tok.Text2) } },
                badges,
            },
        }, padding: new Thickness(22, 18));
        var kpis = new List<Control> { Kpi(log.Timeline is null ? "Dégâts" : $"Dégâts sur {Who(log)}", Fmt.Big(log.TotalDamage)), Kpi("Critique", cr.Crit is { } c ? Fmt.Pct(c) : "—") };
        if (log.Heals is not null || log.DamageTaken is not null)
        {
            kpis.Add(Kpi("Soins sur toi", Fmt.Big(log.Heals ?? 0), Tok.Good));
            kpis.Add(Kpi("Dégâts subis", Fmt.Big(log.DamageTaken ?? 0), log.DamageTaken is > 0 ? Tok.Warn : null));
        }
        else
        {
            kpis.Add(Kpi("Coups", log.Hits.ToString()));
            kpis.Add(cr.Back is { } b ? Kpi("De dos", Fmt.Pct(b)) : Kpi("Sorts lus", log.SkillCount > 0 ? $"{log.Skills.Count}/{log.SkillCount}" : $"{log.Skills.Count}"));
        }
        var kpiGrid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2, Rows = 2 };
        foreach (var k in kpis) kpiGrid.Children.Add(k.WithMargin(0, 0, 10, 10));
        page.Children.Add(Columns("*,*", hero, kpiGrid));
        if (log.Timeline is { Count: > 1 }) page.Children.Add(TargetTimeline(log));

        // 2. À corriger en priorité : au plus 3 cartes ; le reste replié.
        if (todo.Count > 0)
        {
            page.Children.Add(Label("À corriger en priorité"));
            var cards = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
            for (var i = 0; i < Math.Min(3, todo.Count); i++) cards.Children.Add(FixCard(cr, todo[i], i).WithMargin(0, 0, 12, 0));
            page.Children.Add(cards);
            if (todo.Count > 3)
                page.Children.Add(Expander(Fmt.Plural(todo.Count - 3, "autre conseil", "autres conseils"), IsOpen("combat:more"), () => Toggle("combat:more"), () =>
                {
                    var more = new StackPanel { Spacing = 10 };
                    foreach (var f in todo.Skip(3))
                        more.Children.Add(new StackPanel { Spacing = 3, Children = { T(f.Title, 13, Tok.Text, FontWeight.SemiBold, wrap: true), T(f.Detail, 12, Tok.Text2, wrap: true).WithLineHeight(1.4) } });
                    return more;
                }));
        }

        // 3. Où vont les dégâts, et les sorts à recharge en cases.
        var max = cr.Skills.Select(x => x.Skill.Share).DefaultIfEmpty(1).Max();
        var damage = new StackPanel { Spacing = 8 };
        foreach (var x in cr.Skills.OrderByDescending(x => x.Skill.Share).Take(8))
            damage.Children.Add(BarRow(x.Skill.Name, x.Skill.Share / Math.Max(0.0001, max), Fmt.Pct(x.Skill.Share), Accent.Main, onClick: () => Select(x.Skill.Name),
                tip: x.Verdict, selected: x.Skill.Name == s.CombatSkill));
        var rest = cr.Skills.OrderByDescending(x => x.Skill.Share).Skip(8).ToList();
        if (rest.Count > 0) damage.Children.Add(T($"{rest.Count} autre{(rest.Count > 1 ? "s" : "")} sort{(rest.Count > 1 ? "s" : "")} : {Fmt.Pct(rest.Sum(x => x.Skill.Share))} · clic sur un sort = son détail à droite", 12, Tok.Muted));
        var timed = cr.Skills.Where(x => x.Possible is not null).OrderByDescending(x => x.Spell?.Cooldown ?? 0).ToList();
        var recharge = new StackPanel { Spacing = 8 };
        foreach (var x in timed)
        {
            var done = x.Skill.Uses;
            var possible = x.Possible!.Value;
            var color = done >= possible ? Tok.Good : done >= 0.8 * possible ? Tok.Text2 : Tok.Warn;
            var row = ColumnsSpaced("190,*,58", 10, T(x.Skill.Name, 13, x.Skill.Name == s.CombatSkill ? Accent.Main : null), Pips(done, possible),
                T($"{done} / {possible}", 12, color, mono: true).AlignRight());
            recharge.Children.Add(Tip(Clickable(row, () => Select(x.Skill.Name), hover: Tok.Hover, padding: new Thickness(0, 2)), x.Verdict));
        }
        if (timed.Count == 0) recharge.Children.Add(T("Aucun sort à recharge reconnu dans ce combat.", 13, Tok.Muted));
        recharge.Children.Add(T("vert = lancé · orange = occasion perdue · vert pâle = au-delà de la recharge (réinitialisations)", 12, Tok.Muted, wrap: true));
        page.Children.Add(Columns("*,*",
            Card(damage, "Où vont tes dégâts", T(log.Source == "Pet Meter" || log.Pages > 0 ? "mesuré sur ce combat" : "", 12, Tok.Muted)),
            Card(recharge, "Tes sorts à recharge", T("lancés / possibles", 12, Tok.Muted))));

        // 4. Frise du combat : chaque lancer des sorts à recharge, et les pauses.
        if (log.Casts is { Count: > 2 } casts && log.Duration > 0)
        {
            var frise = new StackPanel { Spacing = 6 };
            var colors = new[] { Accent.Main, Tok.Info, Brush("#A57CF0"), Tok.Good, Brush("#E6C04A"), Brush("#F06BA8") };
            foreach (var (x, i) in timed.OrderByDescending(x => x.Skill.Uses).Take(6).Select((x, i) => (x, i)))
            {
                var track = new TimelineTrack(log.Duration);
                foreach (var t in casts.Where(cast => NotMeterReader.Key(cast.Name) == NotMeterReader.Key(x.Skill.Name))) track.Dot(t.T, colors[i % colors.Length]);
                frise.Children.Add(ColumnsSpaced("190,*", 10, T(x.Skill.Name, 12, Tok.Text2), new Border { Background = Tok.Frame, CornerRadius = new CornerRadius(3), Child = track }));
            }
            var pauses = new TimelineTrack(log.Duration);
            var gaps = 0;
            foreach (var (a, b) in casts.Zip(casts.Skip(1)))
                if (b.T - a.T > 2.5) { pauses.Span(a.T, b.T, Tok.Alpha(Tok.Warn, 0.6)); gaps++; }
            frise.Children.Add(ColumnsSpaced("190,*", 10, T("Pauses de plus de 2,5 s", 12, gaps > 0 ? Tok.Warn : Tok.Good),
                new Border { Background = Tok.Frame, CornerRadius = new CornerRadius(3), Child = pauses }));
            var axis = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            axis.Children.Add(T("0 s", 11, Tok.Muted, mono: true));
            var mid = T($"{log.Duration / 2:0} s", 11, Tok.Muted, mono: true).Centered();
            Grid.SetColumn(mid, 1);
            axis.Children.Add(mid);
            var end = T($"{log.Duration:0} s", 11, Tok.Muted, mono: true).AlignRight();
            Grid.SetColumn(end, 2);
            axis.Children.Add(end);
            frise.Children.Add(ColumnsSpaced("190,*", 10, new Control(), axis));
            page.Children.Add(Card(frise, "Déroulé du combat", T(gaps == 0 ? "aucune pause de plus de 2,5 s" : $"{gaps} pause{(gaps > 1 ? "s" : "")}", 12, gaps == 0 ? Tok.Good : Tok.Warn)));
        }

        // 5. Replié : tous les chiffres, la méthode, la suppression.
        var folded = new StackPanel { Spacing = 4 };
        folded.Children.Add(Expander($"Tous les chiffres : {cr.Skills.Count} sorts" + (cr.Buffs.Count > 0 ? $" et {cr.Buffs.Count} buffs" : ""), IsOpen("combat:all"), () => Toggle("combat:all"), () => CombatTables(cr)));
        folded.Children.Add(Expander("Comment c'est calculé", IsOpen("combat:how"), () => Toggle("combat:how"), () =>
        {
            var how = new StackPanel { Spacing = 8 };
            foreach (var f in cr.Findings.Where(f => f.Severity == Severity.Info))
                how.Children.Add(T($"{f.Title} — {f.Detail}", 12, Tok.Text2, wrap: true).WithLineHeight(1.4));
            how.Children.Add(T("Lancers possibles = durée du combat ÷ recharge du sort (+1 au départ), sans réduction de recharge ; un sort qui ne part qu'après un autre " +
                "(Jugement après un coup de bouclier) est compté sur ses vraies occasions. Chaque utilisation envoie deux messages (le lancer, puis sa suite) : " +
                "comptés une fois. « Simulé » = part de dégâts prévue par la rotation simulée.", 12, Tok.Muted, wrap: true).WithLineHeight(1.4));
            return how;
        }));
        if (s.CombatGroup is null && s.CombatIndex is int shown && shown < combats.Count)
        {
            var path = combats[shown].Path;
            folded.Children.Add(Clickable(T("supprimer ce combat", 12, Tok.Muted), () => deleteCombat(path), hover: Tok.Hover, padding: new Thickness(0, 6)));
        }
        page.Children.Add(new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Child = folded });
        return page;
    }

    /// <summary>La cible principale d'un combat du Pet Meter, pour les phrases : « le boss », « le mannequin »…</summary>
    static string Who(CombatLog log) => log.Kind switch { "boss" => "le boss", "dummy" => "le mannequin", "pvp" => "le joueur", _ => "la cible" };

    /// <summary>
    /// Quand tu frappais la cible principale (le boss), et combien : tes dégâts sur elle seconde par seconde (un trou =
    /// une seconde sans coup), puis où ce moment se place dans le combat entier (adds et monstres d'avant compris).
    /// </summary>
    Control TargetTimeline(CombatLog log)
    {
        var values = log.Timeline!;
        var who = Who(log);
        var idle = values.Count(v => v <= 0);
        var chart = new Histogram(values, Accent.Main) { Height = 90 };
        var mid = values.Count / 2;
        var axis = ColumnsSpaced("*,*,*", 0, T("0 s", 11, Tok.Muted, mono: true), T(Duration(mid), 11, Tok.Muted, mono: true).Centered(),
            T(Duration(values.Count), 11, Tok.Muted, mono: true).AlignRight());
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(T($"De ton premier à ton dernier coup sur {who} : {Duration(log.Duration)}, {Fmt.Big(log.TotalDamage)} dégâts, " +
                            $"{Fmt.Big(log.Dps)} par seconde.", 13, Tok.Text, wrap: true));
        if (log.WholeDuration is { } whole && log.FocusOffset is { } offset && whole - log.Duration >= 1)
            text.Children.Add(T($"Le combat entier a duré {Duration(whole)} ({Fmt.Big(log.WholeDamage ?? 0)} dégâts, adds et monstres compris) ; " +
                                $"tu as commencé à frapper {who} à {Duration(offset)}. Le reste de l'écran ne compte que {who}.", 12, Tok.Text2, wrap: true).WithLineHeight(1.4));
        if (idle > 0)
            text.Children.Add(T($"{idle} s sans coup sur {who} : mécanique, déplacement, adds ou temps mort.", 12, idle * 10 > values.Count ? Tok.Warn : Tok.Muted, wrap: true));
        return Card(new StackPanel { Spacing = 8, Children = { chart, axis, text } }, $"Tes dégâts sur {who}, seconde par seconde",
            T($"pic {Fmt.Big(values.Max())}/s", 11, Tok.Muted, mono: true));
    }

    /// <summary>Carte « à corriger » : pastille, gain estimé, titre, cases lancés / possibles, ce qu'il faut faire ; « Pourquoi » déplie la raison.</summary>
    Control FixCard(CombatReport cr, Finding f, int index)
    {
        var review = cr.Skills.FirstOrDefault(r => f.Title.StartsWith($"« {r.Skill.Name} »", StringComparison.Ordinal));
        var gain = review is { Possible: { } p } && review.Skill.Uses > 0 && cr.Log.TotalDamage > 0 && p > review.Skill.Uses
            ? (p - review.Skill.Uses) * review.Skill.Damage / review.Skill.Uses / cr.Log.TotalDamage : 0;
        var tag = gain >= 0.005 ? $"+{Fmt.Pct(gain)} DPS"
            : f.Title.StartsWith("Jamais", StringComparison.Ordinal) ? "rotation" : f.Title.StartsWith("Combo", StringComparison.Ordinal) ? "combo"
            : f.Title.StartsWith("Temps morts", StringComparison.Ordinal) ? "rythme" : f.Title.StartsWith("Placement", StringComparison.Ordinal) ? "placement"
            : f.Title.StartsWith("Buff", StringComparison.Ordinal) ? "buff" : "conseil";
        var head = new DockPanel();
        var badge = Badge(tag, Accent.Main);
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(T($"#{index + 1}", 12, Tok.Muted, mono: true));
        var body = new StackPanel { Spacing = 10, Children = { head, T(f.Title, 14, Tok.Text, FontWeight.SemiBold, wrap: true).WithLineHeight(1.3) } };
        if (review is { Possible: { } possible }) body.Children.Add(Pips(review.Skill.Uses, possible, 18));
        body.Children.Add(T(f.Action ?? f.Detail, 13, Tok.Text2, wrap: true).WithLineHeight(1.4));
        var id = $"combat:why:{index}";
        body.Children.Add(Clickable(T(IsOpen(id) ? "Pourquoi ▾" : "Pourquoi ›", 13, Accent.Main), () =>
        {
            if (review is not null) update(st => { st.CombatSkill = review.Skill.Name; if (!st.Open.Remove(id)) st.Open.Add(id); });
            else Toggle(id);
        }, hover: Tok.Hover));
        if (IsOpen(id)) body.Children.Add(T(f.Detail, 12, Tok.Muted, wrap: true).WithLineHeight(1.4));
        // Liseré à gauche à la couleur de la gravité, comme un marqueur de problème dans un éditeur.
        var stripe = new Border { Width = 3, Background = LevelColor(f.Severity) };
        DockPanel.SetDock(stripe, Dock.Left);
        return new Border
        {
            Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Child = new DockPanel { Children = { stripe, new Border { Padding = new Thickness(14, 12), Child = body } } },
        };
    }

    /// <summary>Tableaux complets d'un combat (repliés par défaut) : tous les sorts, puis les buffs.</summary>
    Control CombatTables(CombatReport cr)
    {
        var panel = new StackPanel();
        const string cols = "*,40,80,56,64,90,64,56";
        panel.Children.Add(TableHeader(cols, "Sort", "Niv.", "Dégâts", "Part", "Simulé", "Lancers", "Critique", "Dos"));
        foreach (var x in cr.Skills)
        {
            var name = x.Skill.Name;
            var uses = x.Possible is { } max ? $"{x.Skill.Uses} / {max}" : x.Skill.Uses > 0 ? $"{x.Skill.Uses}" : "—";
            panel.Children.Add(TableRow(Columns(cols,
                    Tip(T(name, 13, x.Spell is null ? Tok.Text2 : null), x.Verdict), T(x.Skill.Level?.ToString() ?? "—", 12, Tok.Muted, mono: true),
                    T(Fmt.Big(x.Skill.Damage), 12, mono: true), T(Fmt.Pct(x.Skill.Share), 12, Tok.Text, FontWeight.SemiBold, mono: true),
                    T(x.Simulated is { } sim ? Fmt.Pct(sim) : "—", 12, Tok.Muted, mono: true),
                    Tip(T(uses, 12, x.Level <= Severity.Advice ? LevelColor(x.Level) : Tok.Text2, mono: true), x.Verdict),
                    T(x.Skill.Crit is { } c ? Fmt.Pct(c) : "—", 12, Tok.Muted, mono: true), T(x.Skill.Back is { } b ? Fmt.Pct(b) : "—", 12, Tok.Muted, mono: true)),
                name == s.CombatSkill, () => update(st => st.CombatSkill = name)));
        }
        if (cr.Buffs.Count > 0)
        {
            const string buffCols = "220,64,56,*";
            panel.Children.Add(TableHeader(buffCols, "Buff", "Actif", "Fois", "Verdict").WithMargin(0, 12, 0, 0));
            foreach (var b in cr.Buffs)
                panel.Children.Add(new Border
                {
                    Padding = new Thickness(20, 6),
                    Child = Columns(buffCols, T(b.Buff.Name, 13), T(b.Buff.Uptime is { } u ? Fmt.Pct(u) : "?", 12, Tok.Text, mono: true),
                        T(b.Possible is { } m ? $"{b.Buff.Count} / {m}" : $"{b.Buff.Count}", 12, Tok.Muted, mono: true),
                        T(b.Verdict, 12, b.Level <= Severity.Advice ? LevelColor(b.Level) : Tok.Muted, wrap: true)),
                });
        }
        return new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = panel };
    }

    Control CombatInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · combat") } };
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        var cr = Combat;
        var name = s.CombatSkill ?? cr?.Skills.Where(x => x.Level <= Severity.Advice).Select(x => x.Skill.Name).FirstOrDefault() ?? cr?.Skills.FirstOrDefault()?.Skill.Name;
        if (cr?.Skills.FirstOrDefault(x => x.Skill.Name == name) is { } x)
        {
            body.Children.Add(T(x.Skill.Name, 17, weight: FontWeight.SemiBold, wrap: true));
            body.Children.Add(Field("Verdict", x.Verdict, x.Level <= Severity.Advice ? LevelColor(x.Level) : Tok.Text2));
            body.Children.Add(KeyValue(
            [
                ("dégâts", $"{Fmt.Big(x.Skill.Damage)} · {Fmt.Pct(x.Skill.Share)}", null),
                ("simulé", x.Simulated is { } sim ? Fmt.Pct(sim) : "—", Tok.Text2),
                ("lancers", x.Possible is { } max ? $"{x.Skill.Uses} sur {max} possibles" : $"{x.Skill.Uses}", null),
                ("recharge", x.Spell is { Cooldown: > 0 } sp ? $"{Fmt.Num(sp.Cooldown)} s" : "aucune", null),
                ("coups", x.Skill.Hits > 0 ? x.Skill.Hits.ToString() : "—", null),
                ("critique", x.Skill.Crit is { } c ? Fmt.Pct(c) : "—", null),
                ("de dos", x.Skill.Back is { } b ? Fmt.Pct(b) : "—", null),
                ("intervalle", x.Skill.IntervalMs is { } ms ? $"{Fmt.Num(ms / 1000)} s entre deux coups" : "—", Tok.Text2),
            ]));
        }
        body.Children.Add(Expander("Lancers possibles, « simulé »", IsOpen("combat:fields"), () => Toggle("combat:fields"), () =>
            T("Lancers possibles = durée du combat ÷ recharge du sort (+1 au départ), sans réduction de recharge : un sort réinitialisé " +
            "(Clone illusoire pour Coup au cœur) peut dépasser. « Simulé » = part de dégâts prévue par la rotation simulée ; les enchaînements " +
            "(sorts absents des données) n'y figurent pas. Combats du Pet Meter : lancers chronométrés (combos, temps morts), dégâts subis et soins ; les captures NotMeter n'ont ni l'un ni l'autre.",
            12, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        panel.Children.Add(body);
        return panel;
    }

    // ----- Build : version des joueurs et version conçue, côte à côte -----

    Control BuildScreen()
    {
        var cls = r.Profile.Class;
        var designed = r.Profile.UsesDesigned;

        Border Segment(string text, bool on, bool enabled, string build)
        {
            var seg = Clickable(T(text, 12, on ? Tok.OnAccent : enabled ? Tok.Muted : Tok.Faint, FontWeight.SemiBold, mono: true),
                () => { if (enabled && !on) onInputs(inputs with { Build = build }); }, on ? Accent.Main : null, padding: new Thickness(12, 3));
            seg.CornerRadius = new CornerRadius(3);
            return seg;
        }
        var choice = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                T("conseils selon", 11, Tok.Muted, mono: true),
                new Border
                {
                    Background = Tok.Window, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children = { Segment("Joueurs", !designed, true, "players"), Segment("Conçue", designed, cls.Designed is not null, "designed") },
                    },
                },
            },
        };

        // Les deux builds côte à côte : celui que suivent les conseils est marqué.
        var players = r.Profile with { UsesDesigned = false };
        var mine = r.Profile with { UsesDesigned = true };
        Control Followed(bool active) => active ? Badge("suivi par les conseils", Accent.Main) : new Control();
        var left = Card(BuildColumn("players", cls.SkillsSource ?? "Analyse générique (pas de fiche pour cette classe).", players.Skills, null, cls.Play.GetValueOrDefault(r.Mode)),
            "Build des joueurs", Followed(!designed));
        var right = cls.Designed is { } d
            ? Card(BuildColumn("designed", d.Summary, mine.Skills, players.Skills, d.Play.GetValueOrDefault(r.Mode)), "Build conçu", Followed(designed))
            : Card(T("Pas encore rédigé pour cette classe.", 13, Tok.Muted, wrap: true), "Build conçu");
        var builds = Columns("*,*", left, right);

        // Rotations simulées : même moteur pour les deux, sur une cible ou sur un pack de 4, pendant 2 minutes.
        var targets = s.RotationTargets;
        var sims = new[]
        {
            ("Rotation des joueurs", RotationSimulator.Run(db, cls.Code, RotationSimulator.Priority(cls.Play.GetValueOrDefault(r.Mode)), 120, targets)),
            ("Rotation conçue", RotationSimulator.Run(db, cls.Code, RotationSimulator.Priority(cls.Designed?.Play.GetValueOrDefault(r.Mode)), 120, targets)),
        };
        Control? rotations = null;
        if (sims.Any(x => x.Item2.Damage > 0))
        {
            var pick = Choice([("1 cible (boss)", targets == 1, () => update(st => st.RotationTargets = 1)), ("4 cibles (pack)", targets == 4, () => update(st => st.RotationTargets = 4))]);
            var best = sims.Max(x => x.Item2.PerSecond);
            var cols = new List<Control>();
            foreach (var (title, sim) in sims)
            {
                var col = new StackPanel { Spacing = 6 };
                col.Children.Add(Label(title));
                if (sim.Damage <= 0) { col.Children.Add(T("Pas de rotation à simuler.", 12, Tok.Muted)); }
                else
                {
                    var gap = best > 0 ? sim.PerSecond / best - 1 : 0;
                    col.Children.Add(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4),
                        Children =
                        {
                            T(Fmt.Num(Math.Round(sim.PerSecond)), 22, gap > -0.005 ? Tok.Good : Tok.Text, FontWeight.SemiBold, mono: true),
                            T("% d'ATQ par seconde", 12, Tok.Muted).WithMargin(0, 6, 0, 0),
                            gap < -0.005 ? T($"({Fmt.Pct(gap)})", 12, Tok.Warn, mono: true).WithMargin(0, 6, 0, 0) : new Control(),
                        },
                    });
                    // Part de chaque sort en barres : on voit d'un coup d'œil ce qui porte la rotation.
                    var top = sim.Casts.Take(7).ToList();
                    var max = Math.Max(1e-9, top.Max(c => c.Damage));
                    foreach (var c in top)
                        col.Children.Add(ColumnsSpaced("150,*,40,44", 8, T(c.Triggered ? $"{c.Name} (déclenché)" : c.Name, 12, Tok.Text2), RoundBar(c.Damage / max, Accent.Main, 6),
                            T($"×{c.Casts}", 11, Tok.Muted, mono: true).AlignRight(), T(Fmt.Pct(c.Damage / sim.Damage), 11, Tok.Muted, mono: true).AlignRight()));
                    foreach (var x in sim.Skipped) col.Children.Add(T($"écarté : {x}", 11, Tok.Faint, wrap: true));
                }
                cols.Add(col);
            }
            rotations = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Card(ColumnsSpaced("*,*", 28, cols[0], cols[1]), "Rotations simulées · 2 minutes", pick),
                    Expander("Comment c'est simulé", IsOpen("build:sim"), () => Toggle("build:sim"), () =>
                        T("Les sorts à recharge partent dans l'ordre de la rotation dès qu'ils sont prêts, le meilleur sort sans recharge entre deux ; 1 s par lancer, 2 s pour un sort chargé. " +
                          "Données des sorts au niveau 1, sans buffs, PM ni critiques : sert à comparer les ordres de priorité, pas à prédire tes dégâts.", 12, Tok.Text2, wrap: true)
                            .WithLineHeight(1.45).WithMargin(22, 0, 0, 0)),
                },
            };
        }

        Control? differences = null;
        if (cls.Designed is { Differences.Count: > 0 } diff)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var x in diff.Differences) list.Children.Add(ColumnsSpaced("14,*", 6, T("•", 13, Accent.Main), T(x, 13, Tok.Text2, wrap: true).WithLineHeight(1.45)));
            differences = Card(list, $"Différences entre les deux builds, et pourquoi · {diff.Differences.Count}");
        }
        return s.BuildPage switch
        {
            BuildPage.Rotations => Page($"Build · rotations simulées · {Mode}", choice,
                rotations ?? T("Pas de rotation à simuler pour cette classe.", 13, Tok.Muted)),
            BuildPage.Differences when differences is not null => Page($"Build · différences · {Mode}", choice, differences),
            _ => Page($"Build · {cls.Name} · {Mode}", choice, builds),
        };
    }

    /// <summary>Une colonne de build : priorités du mode (niveaux visés), rotation en phases, façon de jouer ; passifs et stigmas repliés.</summary>
    Control BuildColumn(string id, string summary, IReadOnlyList<SkillKnowledge> skills, IReadOnlyList<SkillKnowledge>? reference, PlayGuide? play)
    {
        var col = new StackPanel { Spacing = 12 };
        col.Children.Add(T(summary, 12, Tok.Muted, wrap: true).WithLineHeight(1.45));

        // Ce qui diffère du build des joueurs est marqué « ≠ ».
        bool Differs(SkillKnowledge k) => reference?.FirstOrDefault(x => x.Name == k.Name && x.Kind == k.Kind) is { } o &&
            (Math.Abs(o.Priority.GetValueOrDefault(r.Mode) - k.Priority.GetValueOrDefault(r.Mode)) >= 0.1 || o.Target.GetValueOrDefault(r.Mode) != k.Target.GetValueOrDefault(r.Mode));
        Control? Group(string label, string kind, int take)
        {
            var list = skills.Where(k => k.Kind == kind).OrderByDescending(k => k.Priority.GetValueOrDefault(r.Mode)).Take(take).ToList();
            if (list.Count == 0) return null;
            var box = new StackPanel { Spacing = 3 };
            if (label.Length > 0) box.Children.Add(Label(label));
            foreach (var k in list)
            {
                var p = k.Priority.GetValueOrDefault(r.Mode);
                var differs = Differs(k);
                box.Children.Add(Tip(ColumnsSpaced("*,60,32,14", 6,
                    T(k.Name, 12, p >= 0.5 ? Tok.Text : Tok.Muted),
                    RoundBar(p, p >= 0.85 ? Accent.Main : p >= 0.5 ? Tok.Text2 : Tok.Faint, 4),
                    T($"→ {k.Target.GetValueOrDefault(r.Mode)}", 11, Tok.Muted, mono: true),
                    T(differs ? "≠" : "", 12, Accent.Main, FontWeight.Bold)), $"importance {p:0.00} · {k.Role} — {k.Why}"));
            }
            return box;
        }
        if (Group("Actives · importance · niveau visé", "Active", 12) is { } actives) col.Children.Add(actives);
        var passives = Group("Passifs", "Passive", 10);
        var stigmas = Group("Stigmas (les 4 premiers sont équipés)", "Stigma", 6);
        if (passives is not null || stigmas is not null)
            col.Children.Add(Expander("Passifs et stigmas", IsOpen($"build:{id}:more"), () => Toggle($"build:{id}:more"), () =>
            {
                var more = new StackPanel { Spacing = 12, Margin = new Thickness(22, 0, 0, 0) };
                if (passives is not null) more.Children.Add(passives);
                if (stigmas is not null) more.Children.Add(stigmas);
                return more;
            }));

        if (play is null)
        {
            col.Children.Add(T("Rotation pas encore rédigée pour ce mode.", 12, Tok.Muted, wrap: true));
            return col;
        }
        var rotation = new StackPanel { Spacing = 10, Children = { Label("Rotation") } };
        foreach (var phase in play.Rotation)
        {
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < phase.Steps.Count; i++)
            {
                if (i > 0) chips.Children.Add(T("→", 12, Tok.Muted).WithMargin(4, 2));
                var chip = new Border
                {
                    Background = Tok.Frame, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(7, 2), Margin = new Thickness(0, 2), Child = T(phase.Steps[i], 12),
                };
                chips.Children.Add(chip);
            }
            var block = new StackPanel { Spacing = 4, Children = { T(phase.Name, 12, Accent.Main, FontWeight.SemiBold), chips } };
            if (phase.Note.Length > 0) block.Children.Add(T(phase.Note, 12, Tok.Muted, wrap: true).WithLineHeight(1.4));
            rotation.Children.Add(block);
        }
        col.Children.Add(rotation);
        if (play.Tips.Count > 0)
            col.Children.Add(Expander($"Façon de jouer · {Fmt.Plural(play.Tips.Count, "conseil")}", IsOpen($"build:{id}:tips"), () => Toggle($"build:{id}:tips"), () =>
            {
                var tips = new StackPanel { Spacing = 4, Margin = new Thickness(22, 0, 0, 0) };
                foreach (var x in play.Tips) tips.Children.Add(ColumnsSpaced("14,*", 6, T("•", 12, Accent.Main), T(x, 12, Tok.Text2, wrap: true).WithLineHeight(1.4)));
                return tips;
            }));
        return col;
    }

    Control BuildInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · build") } };
        var cls = r.Profile.Class;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        body.Children.Add(T("Deux versions du build", 15, weight: FontWeight.SemiBold));
        body.Children.Add(T("Joueurs : guides communautaires croisés avec les compétences réellement montées par des joueurs niveau 45 des serveurs EU.",
            12, Tok.Text2, wrap: true).WithLineHeight(1.45));
        body.Children.Add(T("Conçue : construite à partir des seules données des sorts (dégâts en % d'ATQ, temps de recharge, conditions, spécialisations), " +
            "sans guide ni profil de joueur. Ses rotations et priorités viennent du calcul, pas de l'expérience en jeu : à tester.",
            12, Tok.Text2, wrap: true).WithLineHeight(1.45));
        if (cls.Designed is { } d)
            body.Children.Add(Expander("Méthode du build conçu", IsOpen("build:method"), () => Toggle("build:method"), () => T(d.Method, 12, Tok.Text2, wrap: true).WithLineHeight(1.45)));
        body.Children.Add(Callout(r.Profile.UsesDesigned
            ? "Les conseils de compétences de ce personnage suivent le build conçu. « Joueurs » pour revenir au build des joueurs."
            : "Les conseils de compétences de ce personnage suivent le build des joueurs." + (cls.Designed is null ? "" : " « Conçue » pour suivre le build conçu."),
            Accent.Main));
        body.Children.Add(T("≠ : priorité ou niveau visé différent du build des joueurs. Survole une compétence pour sa raison.", 12, Tok.Muted, wrap: true));
        panel.Children.Add(body);
        return panel;
    }

    // ----- Daevanion -----

    string PoolShort(string pool) => pool switch { "PvE" => "Ariel", "PvP" => "Azphel", _ => "Commun" };

    List<BoardSummary> RelevantBoards => r.Daevanion.Boards.Where(b => b.Relevant && b.Board.Nodes.Count > 0).ToList();

    RespecPlan? WorthRespec(string pool) => r.Daevanion.Respecs.FirstOrDefault(x => x.Pool == pool && x.Remove.Count > 0 && x.Gain >= db.Settings.Daevanion.RespecMinGain);

    BoardSummary? SelectedBoard
    {
        get
        {
            var boards = RelevantBoards;
            return boards.FirstOrDefault(b => b.Board.Id == s.Board)
                   ?? boards.FirstOrDefault(b => WorthRespec(b.Pool) is { } x && (x.Remove.Any(n => n.Board.Id == b.Board.Id) || x.Add.Any(n => n.Board.Id == b.Board.Id)))
                   ?? boards.FirstOrDefault(b => r.Daevanion.Steps.Any(n => n.Board.Id == b.Board.Id))
                   ?? boards.FirstOrDefault();
        }
    }

    /// <summary>Pourquoi une case vaut peu ou beaucoup : importance de sa stat ou de sa compétence pour la classe dans ce mode.</summary>
    string Reason(DaevanionNode n)
    {
        var parts = new List<string>();
        foreach (var e in n.Effects)
        {
            if (e.IsSkill && r.Skills.Skills.FirstOrDefault(a => a.Skill.Name == e.SkillName) is { } a)
                parts.Add($"{a.Role.ToLowerInvariant()}, importance {a.Priority:0.00}");
            else if (e.IsStat)
                parts.Add($"importance {r.Profile.Weight(e.StatKey!):0.00}" +
                          (r.Stats.Categories.FirstOrDefault(c => c.Category == db.Stat(e.StatKey!)?.Category) is { } c && c.Gap > db.Settings.Stats.CategoryTolerance
                              ? $", {GameDatabase.CategoryName(c.Category).ToLowerInvariant()} déjà au-dessus de sa cible" : ""));
        }
        return parts.Count == 0 ? "passage" : string.Join(" ; ", parts.Distinct());
    }

    Control Daeva()
    {
        var panel = new DockPanel();
        var selected = SelectedBoard;
        // Les plateaux et le choix du plan sont dans la barre secondaire ; ici, le plateau ouvert et son plan.
        var header = SectionHeader(selected is null ? $"Daevanion · {Mode}" : $"{selected.Board.Name} · {(s.DaevaRoute ? "route conçue" : "ton plateau")} · {Mode}",
            selected is null ? null : Badge($"{selected.Board.OpenNodes}/{selected.Board.TotalNodes} cases", Tok.Text2), 20);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (selected is null)
        {
            panel.Children.Add(T("Aucun plateau Daevanion détaillé pour ce personnage.", 13, Tok.Muted).WithMargin(20));
            return panel;
        }

        var info = new StackPanel { Spacing = 10, Width = 250 };
        var opened = selected.Board.OpenNodes / (double)Math.Max(1, selected.Board.TotalNodes);
        info.Children.Add(new Border
        {
            Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Label($"{selected.Board.Name} · réserve {PoolShort(selected.Pool)}"),
                    T($"{selected.Board.OpenNodes}/{selected.Board.TotalNodes} cases", 20, Tok.Text, FontWeight.SemiBold, mono: true),
                    RoundBar(opened, Accent.Main, 6),
                    T($"{selected.Spent}/{selected.Total} points dépensés", 12, Tok.Muted, mono: true),
                },
            },
        });
        if (WorthRespec(selected.Pool) is { } worth && !s.DaevaRoute)
            info.Children.Add(Badge($"réorganisation : +{Fmt.Pct(worth.Gain)} de valeur", Tok.Warn));
        var legend = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                T("● compétence · ■ stat", 12, Tok.Muted),
                new TextBlock { FontSize = 12, Foreground = Tok.Muted, Inlines = new InlineCollection { new Run("1 2 3") { Foreground = Accent.Main, FontWeight = FontWeight.Bold }, new Run(" à prendre, dans l'ordre") } },
                s.DaevaRoute ? T("chiffres sur les cases pleines : ordre depuis le centre", 12, Tok.Muted) : new Control(),
                new TextBlock { FontSize = 12, Foreground = Tok.Muted, Inlines = new InlineCollection { new Run("✕") { Foreground = Tok.Bad, FontWeight = FontWeight.Bold }, new Run(" à retirer") } },
                T("coût : gris 1 · vert 2 · bleu 3 · orange 4", 12, Tok.Muted),
            },
        };
        info.Children.Add(Card(legend, "Légende"));

        // Le plateau, puis ses chiffres à droite ; s'il manque de place (barre secondaire et inspecteur ouverts), dessous.
        var body = new WrapPanel { ItemSpacing = 20, LineSpacing = 14, Children = { BoardGrid(selected), info } };
        panel.Children.Add(new StackPanel { Spacing = 14, Margin = new Thickness(20, 14, 20, 24), Children = { body, TodoCards(Area.Daevanion) ?? new Control(), DaevaNext(selected) } });
        return panel;
    }

    /// <summary>
    /// Sous le plateau : les cases à prendre de la réserve, dans l'ordre et avec les mêmes numéros que la grille
    /// (après réinitialisation si elle est conseillée, sinon avec tes prochains points).
    /// </summary>
    Control DaevaNext(BoardSummary selected)
    {
        if (s.DaevaRoute && RouteFor(selected.Pool) is { } route) return RouteList(route, selected);
        var respec = WorthRespec(selected.Pool);
        var steps = respec?.Add ?? r.Daevanion.Steps.Where(x => x.Pool == selected.Pool).ToList();
        var unspent = r.Daevanion.Budgets.FirstOrDefault(x => x.Pool == selected.Pool)?.Unspent ?? 0;
        var title = respec is not null
            ? $"Après réinitialisation ({Fmt.Plural(respec.Remove.Count, "case")} à retirer, +{Fmt.Pct(respec.Gain)} de valeur) · dans cet ordre"
            : unspent > 0
                ? $"Prochaines cases · réserve {PoolShort(selected.Pool)} · tes {unspent} points libres couvrent les cases « faisable »"
                : $"Prochaines cases · réserve {PoolShort(selected.Pool)} · suis la liste jusqu'à épuisement de tes points";
        var list = new StackPanel { Spacing = 0 };
        const int shown = 10;
        foreach (var x in s.AllNext ? steps : steps.Take(shown))
        {
            var tag = x.Affordable ? ("faisable", Tok.Good) : x.IsPassage ? ("passage", Tok.Faint) : ("", Tok.Muted);
            list.Children.Add(NodeRow(x.Step.ToString(), Accent.Main, x, selected, tag));
        }
        if (steps.Count > shown) list.Children.Add(MoreToggle(steps.Count - shown, "cases"));
        if (steps.Count == 0)
            list.Children.Add(T("Rien à prendre : la réserve est pleine, ou ce qui reste vaut peu pour ta classe.", 12, Tok.Muted, wrap: true));
        return Card(list, title, padding: new Thickness(8, 6));
    }

    DesignedRoute? RouteFor(string pool) => r.Daevanion.Routes.FirstOrDefault(x => x.Pool == pool && x.Nodes.Count > 0);

    /// <summary>Une case sur une ligne : numéro, plateau, effet, coût et cumul ; la raison passe en infobulle.</summary>
    Border NodeRow(string number, IBrush numberColor, PlannedNode x, BoardSummary selected, (string Text, IBrush Color) tag)
    {
        var boardId = x.Board.Id;
        var row = Clickable(ColumnsSpaced("34,70,*,100,70", 8,
                T(number, 12, numberColor, FontWeight.Bold, mono: true),
                T(x.Board.Name, 12, boardId == selected.Board.Id ? Tok.Text : Tok.Muted),
                T(x.Effects, 13, x.IsPassage ? Tok.Muted : null),
                T($"{x.Cost:0} pt · Σ {Fmt.Num(x.Cumulative)}", 11, Tok.Muted, mono: true),
                T(tag.Text, 11, tag.Color, mono: true)),
            () => update(st => st.Board = boardId), hover: Tok.Hover, padding: new Thickness(6, 3));
        return Tip(row, $"{x.Board.Name} ligne {x.Node.Row} col. {x.Node.Col} — {(x.IsPassage ? "case de passage vers les suivantes" : Reason(x.Node))}");
    }

    Border MoreToggle(int hidden, string what) =>
        Clickable(T(s.AllNext ? "▴ réduire" : $"▾ voir la suite ({hidden} {what} de plus)", 12, Accent.Main), () => update(st => st.AllNext = !st.AllNext),
            hover: Tok.Hover, padding: new Thickness(6, 5));

    /// <summary>
    /// Route conçue : cases à ouvrir pour la suivre, cases ouvertes hors route, puis la suite ; l'ordre complet depuis le
    /// centre (utile après une réinitialisation) se déplie.
    /// </summary>
    Control RouteList(DesignedRoute route, BoardSummary selected)
    {
        var panel = new StackPanel { Spacing = 14 };
        var inBudget = route.InBudget.ToList();
        var toOpen = inBudget.Where(x => !x.Node.Open).ToList();
        var inRoute = inBudget.Select(x => x.Node.Id).ToHashSet();
        var outside = r.Daevanion.Boards.Where(b => b.Pool == route.Pool)
            .SelectMany(b => b.Board.Nodes.Where(n => n.Open && !n.IsStart && !inRoute.Contains(n.Id)).Select(n => (b.Board, Node: n))).ToList();
        var later = route.Nodes.Where(x => !x.Affordable && !x.Node.Open).ToList();

        if (s.AllNext)
        {
            var allTitle = $"Ordre complet depuis le centre · {inBudget.Count} cases pour tes {route.Budget} points, puis la suite";
            var all = new StackPanel();
            foreach (var x in route.Nodes)
                all.Children.Add(NodeRow(x.Step.ToString(), x.Node.Open ? Tok.Muted : Accent.Main, x, selected,
                    x.Node.Open ? ("ouverte", Tok.Faint) : x.Affordable ? ("à ouvrir", Tok.Good) : ("suite", Tok.Muted)));
            all.Children.Add(MoreToggle(0, ""));
            panel.Children.Add(Card(all, allTitle, padding: new Thickness(8, 6)));
            return panel;
        }

        var openTitle = $"À ouvrir pour suivre la route · {Fmt.Plural(toOpen.Count, "case")}, {Fmt.Num(toOpen.Sum(x => x.Cost))} points";
        var list = new StackPanel();
        foreach (var x in toOpen) list.Children.Add(NodeRow(x.Step.ToString(), Accent.Main, x, selected, ("à ouvrir", Tok.Good)));
        if (toOpen.Count == 0) list.Children.Add(T("Tes cases ouvertes suivent déjà la route conçue.", 12, Tok.Good).WithMargin(6, 4));
        panel.Children.Add(Card(list, openTitle, padding: new Thickness(8, 6)));

        if (outside.Count > 0)
        {
            var offTitle = $"Ouvertes chez toi, hors de la route · {Fmt.Plural(outside.Count, "case")}";
            var off = new StackPanel();
            foreach (var (board, node) in outside)
            {
                var boardId = board.Id;
                off.Children.Add(Tip(Clickable(ColumnsSpaced("34,70,*", 8, T("✕", 12, Tok.Bad, FontWeight.Bold, mono: true), T(board.Name, 12, Tok.Muted),
                        T(string.Join(", ", node.Effects.Select(e => e.Raw)), 13, Tok.Text2)),
                    () => update(st => st.Board = boardId), hover: Tok.Hover, padding: new Thickness(6, 3)), Reason(node)));
            }
            panel.Children.Add(Card(off, offTitle, padding: new Thickness(8, 6)));
        }

        var nextTitle = $"La suite, avec tes prochains points · {Fmt.Plural(later.Count, "case")}";
        var next = new StackPanel();
        foreach (var x in later.Take(12)) next.Children.Add(NodeRow(x.Step.ToString(), Tok.Text2, x, selected, ("suite", Tok.Muted)));
        next.Children.Add(Clickable(T("▾ ordre complet depuis le centre (pour suivre la route après une réinitialisation)", 12, Accent.Main),
            () => update(st => st.AllNext = true), hover: Tok.Hover, padding: new Thickness(6, 5)));
        panel.Children.Add(Card(next, nextTitle, padding: new Thickness(8, 6)));
        return panel;
    }

    /// <summary>Grille 15 × 15 : case ouverte pleine, fermée en contour, à prendre numérotée, à retirer barrée.</summary>
    Control BoardGrid(BoardSummary summary)
    {
        const int size = 30, gap = 2;
        var board = summary.Board;
        var route = s.DaevaRoute ? RouteFor(summary.Pool) : null;
        var respec = route is null ? WorthRespec(summary.Pool) : null;
        Dictionary<long, int> numbers;
        HashSet<long> removed;
        if (route is not null)
        {
            // Route conçue : toutes les cases de la route numérotées dans l'ordre depuis le centre (ouvertes comprises).
            var inBudget = route.InBudget.Where(x => x.Board.Id == board.Id).ToList();
            numbers = inBudget.ToDictionary(x => x.Node.Id, x => x.Step);
            var kept = inBudget.Select(x => x.Node.Id).ToHashSet();
            removed = board.Nodes.Where(n => n.Open && !n.IsStart && !kept.Contains(n.Id)).Select(n => n.Id).ToHashSet();
        }
        else
        {
            numbers = respec is not null
                ? respec.Add.Where(x => x.Board.Id == board.Id).ToDictionary(x => x.Node.Id, x => x.Step)
                : r.Daevanion.Steps.Where(x => x.Board.Id == board.Id).ToDictionary(x => x.Node.Id, x => x.Step);
            removed = respec?.Remove.Where(x => x.Board.Id == board.Id).Select(x => x.Node.Id).ToHashSet() ?? [];
        }
        var canvas = new Canvas { Width = 15 * (size + gap) - gap, Height = 15 * (size + gap) - gap };
        foreach (var n in board.Nodes)
        {
            var open = n.Open || n.IsStart;
            var grade = n.IsStart ? Brushes.White : Tok.B(db.Grade(n.Grade).Color);
            var isRemoved = removed.Contains(n.Id);
            int? step = numbers.TryGetValue(n.Id, out var st) ? st : null;
            // Une case déjà ouverte qui fait partie de la route garde sa couleur, avec son numéro en sombre.
            var openStep = open && step is not null;
            var cell = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(n.IsSkill ? size / 2.0 : 3),
                Background = isRemoved ? Tok.CellOff : open ? grade : Tok.CellOff,
                BorderBrush = isRemoved ? Tok.Bad : step is not null && !open ? Accent.Main : open ? grade : Tok.Alpha(grade, 0.33),
                BorderThickness = new Thickness(isRemoved || step is not null && !open ? 2 : 1),
                Opacity = open || step is not null || isRemoved ? 1 : 0.55,
                Child = isRemoved ? T("✕", 11, Tok.Bad, FontWeight.SemiBold, mono: true).Centered()
                    : step is null ? null : T(step.ToString()!, step >= 100 ? 9 : 11, openStep ? Tok.OnAccent : Accent.Main, FontWeight.SemiBold, mono: true).Centered(),
            };
            Tip(cell, $"{(n.Name.Length > 0 ? n.Name : n.Type)} — {Fmt.Plural(db.Settings.Daevanion.NodeCost.GetValueOrDefault(n.Grade, 0), "point")}\n" +
                      string.Join("\n", n.Effects.Select(e => e.Raw)) +
                      (isRemoved ? "\nÀ retirer : " + Reason(n) : n.Open ? "\n(ouverte)" : step is not null ? $"\nÉtape {step} du plan" : ""));
            Canvas.SetLeft(cell, (n.Col - 1) * (size + gap));
            Canvas.SetTop(cell, (n.Row - 1) * (size + gap));
            canvas.Children.Add(cell);
        }
        return new Border
        {
            Padding = new Thickness(12), Background = Tok.Frame, BorderBrush = Tok.Border, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top, Child = canvas,
        };
    }

    Control DaevaInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Points") } };
        var points = new StackPanel { Spacing = 8 };
        var budgets = r.Daevanion.Budgets.Where(b => DaevanionPlanner.Relevant(b.Pool, r.Mode)).ToList();
        foreach (var b in budgets)
        {
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { T($"{b.Spent} dépensés · ", 12, mono: true), T(b.Unspent.ToString(), 12, Accent.Main, FontWeight.Bold, mono: true), T(" libres", 12, mono: true) },
            };
            DockPanel.SetDock(right, Dock.Right);
            points.Children.Add(new DockPanel { Children = { right, T(PoolShort(b.Pool), 12, Tok.Muted, mono: true) } });
        }
        foreach (var b in budgets)
        {
            var pool = b.Pool;
            points.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0),
                Children =
                {
                    T($"libres {PoolShort(pool)}", 12, Tok.Muted, mono: true),
                    Stepper(b.Unspent, v => onInputs(pool switch
                    {
                        "PvE" => inputs with { UnspentAriel = v },
                        "PvP" => inputs with { UnspentAzphel = v },
                        _ => inputs with { UnspentDaevanion = v },
                    }), Tok.Window),
                },
            });
        }
        panel.Children.Add(new Border { Padding = new Thickness(14, 12), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = points });

        var board = SelectedBoard;
        if (board is null) return panel;
        if (s.DaevaRoute && RouteFor(board.Pool) is { } route)
        {
            var inBudget = route.InBudget.ToList();
            var open = inBudget.Count(x => x.Node.Open);
            panel.Children.Add(SectionHeader("Route conçue", T($"{(route.Gain >= 0 ? "+" : "")}{Fmt.Pct(route.Gain)}", 12, route.Gain > 0.005 ? Tok.Good : Tok.Muted, mono: true)));
            panel.Children.Add(new StackPanel
            {
                Spacing = 10, Margin = new Thickness(14, 12),
                Children =
                {
                    T($"Construite depuis le centre pour tes {route.Budget} points, sans tenir compte de ton placement : {inBudget.Count} cases, dont {open} déjà ouvertes chez toi.",
                        13, Tok.Text2, wrap: true).WithLineHeight(1.5),
                    T(route.Gain > 0.005 ? $"Elle vaut {Fmt.Pct(route.Gain)} de plus que ton placement actuel : à suivre si tu réinitialises."
                        : "Ton placement actuel vaut autant : inutile de réinitialiser, continue simplement par la suite de la route.", 13, wrap: true).WithLineHeight(1.5),
                    T("Chaque case vaut selon l'importance de sa stat ou de sa compétence pour ta classe (build suivi) et ce mode, rapportée à son coût, chemins d'accès compris.",
                        12, Tok.Muted, wrap: true).WithLineHeight(1.45),
                },
            });
            return panel;
        }
        if (WorthRespec(board.Pool) is { } respec)
        {
            panel.Children.Add(SectionHeader("Réorganisation", T($"+{Fmt.Pct(respec.Gain)}", 12, Tok.Good, mono: true)));
            var lines = new StackPanel { Spacing = 6, Margin = new Thickness(14, 10) };
            foreach (var x in respec.Remove)
                lines.Children.Add(Line("−", Tok.Bad, $"{x.Board.Name} l.{x.Node.Row} c.{x.Node.Col} — {x.Effects} · {Reason(x.Node)}", null, null));
            foreach (var x in respec.Add)
                lines.Children.Add(Line(x.Step.ToString(), Accent.Main, $"{x.Board.Name} l.{x.Node.Row} c.{x.Node.Col} — {x.Effects}", null, null));
            panel.Children.Add(lines);
        }
        else
        {
            panel.Children.Add(SectionHeader("Réorganisation"));
            panel.Children.Add(T($"Aucune case à retirer sur {board.Board.Name} : ton chemin fait déjà partie de la meilleure répartition de tes points.",
                13, Tok.Text2, wrap: true).WithLineHeight(1.5).WithMargin(14, 12));
        }
        // Les prochaines cases, dans l'ordre, sont sous le plateau (contenu central).
        return panel;
    }

    static TextBlock Line(string prefix, IBrush prefixColor, string text, IBrush? color, string? tag)
    {
        var inlines = new InlineCollection { new Run(prefix + " ") { Foreground = prefixColor, FontWeight = FontWeight.Bold }, new Run(text) };
        if (tag is not null) inlines.Add(new Run("  " + tag) { Foreground = Tok.Good });
        var block = new TextBlock { FontFamily = Fonts.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Inlines = inlines };
        block.LineHeight = 12 * 1.45;
        if (color is not null) block.Foreground = color;
        return block;
    }

    // ----- Familiers -----

    GenusPlan? SelectedGenus => r.Pets.For(s.Genus ?? "") ?? r.Pets.Best ?? r.Pets.Genera.FirstOrDefault();

    Control Pets()
    {
        if (r.Pets.Collection is not { } c)
            return Page("Familiers", Badge("non reliés", Tok.Warn), Card(PetLink(), "Relier ta collection"));

        var genera = r.Pets.Genera.Where(g => g.Total > 0).ToList();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Children =
            {
                Clickable(T("actualiser", 11, Accent.Main), () => onPets(null)),
                Tip(Clickable(T("oublier la clé", 11, Tok.Muted), () => onPets("")), $"Retire la clé Aion 2 Todo {petKeyHint} de ce PC."),
            },
        };
        // Chiffres clés : collection, niveau 3, espèce à farmer.
        var owned = genera.Sum(g => g.Owned);
        var total = genera.Sum(g => g.Total);
        var maxed = genera.Sum(g => g.Maxed);
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        kpis.Children.Add(Kpi("Obtenus", $"{owned} / {total}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Au niveau 3", $"{maxed}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("À farmer en premier", r.Pets.Best?.Name ?? "—", Accent.Main));

        const string cols = "100,80,110,*,150";
        var rows = new List<Control>();
        var selected = SelectedGenus?.Key;
        var best = r.Pets.Best;
        var maxValue = Math.Max(0.01, genera.Max(g => g.PetValue));
        foreach (var g in genera)
        {
            var now = string.Join(", ", g.Stats.Where(x => x.Now > 0).Select(x => $"{x.Name} +{Fmt.Num(x.Now)}"));
            var interest = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new Border { Width = 70, Child = Bar(g.PetValue / maxValue, g == best ? Accent.Main : Tok.Text2, 3) },
                    T(g == best ? "à farmer" : g.PetValue.ToString("0.00"), 12, g == best ? Accent.Main : Tok.Muted, mono: true),
                },
            };
            rows.Add(TableRow(Columns(cols, T(g.Name, 13), T($"{g.Owned}/{g.Total}", 13, mono: true),
                    T($"{g.Counts[1]} · {g.Counts[2]} · {g.Counts[3]}", 13, Tok.Text2, mono: true), T(now.Length > 0 ? now : "—", 12, Tok.Text2), interest),
                g.Key == selected, () => update(st => st.Genus = g.Key)));
        }

        Control? farm = null;
        if (best is not null)
        {
            const string pcols = "240,100,*";
            var farmRows = new List<Control>();
            foreach (var x in best.Next.Take(8))
                farmRows.Add(TableRow(Columns(pcols, T(x.Pet.Name, 13),
                        T(x.Level == 0 ? "à obtenir" : $"{x.Level} → {x.Level + 1}", 12, x.Level == 0 ? Tok.Warn : Tok.Text2, mono: true),
                        T($"{x.Missing}  ({x.Souls}/{c.Tiers[x.Level]})", 12, Tok.Text2, mono: true)),
                    false, () => update(st => st.Genus = best.Key)));
            farm = TableCard($"À farmer en premier · {best.Name} · les plus proches du niveau suivant", null, TableHeader(pcols, "Familier", "Niveau", "Âmes manquantes"), farmRows);
        }
        var how = Expander("Ce que donnent les familiers", IsOpen("pets:how"), () => Toggle("pets:how"), () =>
            T("Chaque familier obtenu donne des stats selon son espèce et son niveau (effets de possession), cumulées sur toute la collection. " +
              "Les stats secondaires comptent dans ta répartition ; les stats principales sont déjà dans les totaux du site officiel. " +
              "Non compté : les stats tirées par l'analyse de perception d'espèce (ni le site officiel ni le Pet Meter ne les lisent).",
                12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(22, 0, 0, 0));
        return Page($"Familiers · lus le {c.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}", actions, kpis, farm,
            TableCard("Espèces", T("clic = détail dans l'inspecteur", 11, Tok.Muted, mono: true), TableHeader(cols, "Espèce", "Obtenus", "Niv. 1 · 2 · 3", "Stats données", "Intérêt"), rows),
            how);
    }

    /// <summary>Relier la collection : clé de synchro Aion 2 Todo (celle du Pet Meter).</summary>
    Control PetLink()
    {
        var box = new TextBox { PasswordChar = '•', PlaceholderText = "a2t_…", FontFamily = Fonts.Mono, FontSize = 13, Width = 320 };
        void Submit() { if (!string.IsNullOrWhiteSpace(box.Text)) onPets(box.Text.Trim()); }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Submit(); } };
        var link = Clickable(T("Relier", 12, Tok.OnAccent, FontWeight.SemiBold), Submit, Accent.Main, padding: new Thickness(14, 6));
        link.CornerRadius = new CornerRadius(4);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { box, link } };

        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                T("Les stats de tes familiers ne sont pas encore comptées. Le Pet Meter les lit en jeu et les envoie à Aion 2 Todo : " +
                  "l'application les y relit avec ta clé de synchro.", 13, Tok.Text2, wrap: true).WithLineHeight(1.5),
                T("1. Sur Aion 2 Todo : onglet Familiers → Pet Meter → « Créer ma clé ». Elle n'est affichée qu'une fois et remplace l'ancienne : " +
                  "recolle-la aussi dans le Pet Meter (menu ⋯ → Clé de synchro).\n2. Colle-la ici, puis « Relier ». Elle reste sur ce PC (%APPDATA%\\Aion2Builder\\pets.json).",
                    12, Tok.Muted, wrap: true).WithLineHeight(1.5),
                row,
            },
        };
        if (PetStore.PetMeterKey() is { } meterKey)
            panel.Children.Add(Clickable(T($"Utiliser la clé du Pet Meter de ce PC (…{meterKey[^4..]})", 12, Accent.Main), () => onPets(meterKey)));
        return panel;
    }

    /// <summary>Effets d'une espèce par niveau : « PV +10 / +20 / +30 ».</summary>
    string PerLevel(PetEffect e) => $"{db.StatName(e.Stat)} " + string.Join(" / ", e.Levels.Select(v => v == 0 ? "—" : $"+{Fmt.Num(v)}"));

    Control PetInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · espèce") } };
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        panel.Children.Add(body);
        var rules = db.Progression.Pets.Genera;
        if (!r.Pets.Linked)
        {
            body.Children.Add(T("Ce que donne chaque familier obtenu, au niveau 1 / 2 / 3 :", 12, Tok.Text2, wrap: true));
            foreach (var genus in rules.Values.Where(x => x.Effects.Count > 0))
            {
                var block = new StackPanel { Spacing = 3, Children = { Label(genus.Name) } };
                foreach (var e in genus.Effects) block.Children.Add(T(PerLevel(e), 12, Tok.Text2, mono: true, wrap: true));
                body.Children.Add(block);
            }
            return panel;
        }
        if (SelectedGenus is not { } g) return panel;

        body.Children.Add(new StackPanel { Spacing = 4, Children = { T("espèce de familiers", 12, Tok.Muted), T(g.Name, 17, weight: FontWeight.SemiBold) } });
        body.Children.Add(KeyValue(
        [
            ("obtenus", $"{g.Owned} / {g.Total}", null),
            ("niveau 3", g.Maxed.ToString(), null),
            ("à obtenir", g.Counts[0].ToString(), g.Counts[0] > 0 ? Tok.Warn : null),
            ("intérêt", g.PetValue.ToString("0.00"), g == r.Pets.Best ? Accent.Main : null),
        ]));
        if (rules.GetValueOrDefault(g.Key) is { } rule)
        {
            var effects = new StackPanel { Spacing = 3, Children = { Label("Par familier · niv. 1 / 2 / 3") } };
            foreach (var e in rule.Effects) effects.Children.Add(T(PerLevel(e), 12, Tok.Text2, mono: true, wrap: true));
            if (rule.Other is { } other) effects.Children.Add(T(other, 11, Tok.Faint, wrap: true));
            body.Children.Add(effects);
        }
        var totals = new StackPanel { Spacing = 3, Children = { Label("Ta collection · au maximum") } };
        foreach (var x in g.Stats)
            totals.Children.Add(Tip(T($"{x.Name} +{Fmt.Num(x.Now)}  ·  +{Fmt.Num(x.Max)}{(x.Counted ? "" : "  (déjà dans tes stats principales)")}", 12,
                x.Counted ? Tok.Text2 : Tok.Muted, mono: true, wrap: true), x.Counted ? "Compté dans ta répartition de stats." : "Stat principale : déjà comprise dans les totaux du site officiel."));
        body.Children.Add(totals);
        if (g == r.Pets.Best)
            body.Children.Add(Callout($"Espèce la plus utile pour un {r.Profile.Class.Name} en {GameDatabase.ModeName(r.Mode)} : farme-la en premier, " +
                "en commençant par les familiers les plus proches du niveau suivant.", Accent.Main));
        if (g.Next.Count > 0)
        {
            var next = new StackPanel { Spacing = 3, Children = { Label("Les plus proches") } };
            foreach (var x in g.Next.Take(8)) next.Children.Add(T(PetAdvisor.Missing(x), 12, x.Level == 0 ? Tok.Warn : Tok.Text2, wrap: true));
            body.Children.Add(next);
        }
        return panel;
    }

    // ----- Détails -----

    // ----- Simulation « et si » -----

    /// <summary>Choix exclusifs en boutons (l'actif en couleur secondaire).</summary>
    static Control Choice(IEnumerable<(string Text, bool On, Action Do)> options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (text, on, act) in options)
        {
            var b = Clickable(T(text, 12, on ? Tok.OnAccent : Tok.Text2, FontWeight.SemiBold, mono: true), act, on ? Accent.Main : Tok.Window, on ? null : Tok.HoverStrong, new Thickness(10, 3));
            b.CornerRadius = new CornerRadius(3);
            b.BorderBrush = Tok.Border;
            b.BorderThickness = new Thickness(1);
            row.Children.Add(b);
        }
        return row;
    }

    void Sim(Func<SimulationOptions, SimulationOptions> change) => update(st => st.Sim = change(st.Sim));

    Control Simulate()
    {
        var o = s.Sim;
        var reset = Clickable(T("tout remettre", 11, Accent.Main), () => Sim(_ => new SimulationOptions()), hover: Tok.HoverStrong, padding: new Thickness(4, 0));
        var title = $"Simulation « et si » · {Mode} · sur une copie de ton personnage";

        // Options : ce qu'on change.
        var designedNow = inputs.UsesDesigned;
        var options = new StackPanel { Spacing = 12 };
        var daeva = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        daeva.Children.Add(Choice([
            ("rien", o.Daevanion == DaevanionChoice.None, () => Sim(x => x with { Daevanion = DaevanionChoice.None })),
            ("prochaines cases", o.Daevanion == DaevanionChoice.Next, () => Sim(x => x with { Daevanion = DaevanionChoice.Next })),
            ("réorganisation", o.Daevanion == DaevanionChoice.Respec, () => Sim(x => x with { Daevanion = DaevanionChoice.Respec })),
            ("route conçue", o.Daevanion == DaevanionChoice.Route, () => Sim(x => x with { Daevanion = DaevanionChoice.Route })),
        ]));
        if (o.Daevanion == DaevanionChoice.Next)
            daeva.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children = { Stepper(o.NextNodes, v => Sim(x => x with { NextNodes = Math.Clamp(v, 1, 200) })), T("cases par réserve", 12, Tok.Muted, mono: true) },
            });
        options.Children.Add(ColumnsSpaced("110,*", 12, T("Daevanion", 13, weight: FontWeight.Medium), daeva));
        options.Children.Add(ColumnsSpaced("110,*", 12, T("Compétences", 13, weight: FontWeight.Medium), Choice([
            ("telles quelles", !o.Skills, () => Sim(x => x with { Skills = false })),
            ("répartition conseillée", o.Skills, () => Sim(x => x with { Skills = true })),
        ])));
        options.Children.Add(ColumnsSpaced("110,*", 12, T("Build suivi", 13, weight: FontWeight.Medium), Choice([
            ($"joueurs{(designedNow ? "" : " (actuel)")}", (o.Designed ?? designedNow) == false, () => Sim(x => x with { Designed = designedNow ? false : null })),
            ($"conçu{(designedNow ? " (actuel)" : "")}", (o.Designed ?? designedNow), () => Sim(x => x with { Designed = designedNow ? null : true })),
        ])));
        options.Children.Add(ColumnsSpaced("110,*", 12, T("Équipement", 13, weight: FontWeight.Medium), Choice([
            ("tel quel", !o.GearAtStage, () => Sim(x => x with { GearAtStage = false })),
            ("pièces en retard au niveau du palier", o.GearAtStage, () => Sim(x => x with { GearAtStage = true })),
        ])));
        var choices = Card(options, "Ce que tu changes", o.Any ? reset : null);

        if (!o.Any)
            return Page(title, null, choices,
                T("Choisis un ou plusieurs changements : les notes, la répartition des familles et les stats sont recalculées sur une copie de ton personnage, rien n'est enregistré.",
                    13, Tok.Muted, wrap: true).WithLineHeight(1.5));

        var after = simulate(o);
        // Notes avant → après.
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 8 };
        var scores = new[]
        {
            ("Global", r.Scores.Overall, after.Scores.Overall), ("Stats", r.Scores.Stats, after.Scores.Stats), ("Équip.", r.Scores.Gear, after.Scores.Gear),
            ("Compét.", r.Scores.Skills, after.Scores.Skills), ("Daeva.", r.Scores.Daevanion, after.Scores.Daevanion),
        };
        for (var i = 0; i < scores.Length; i++)
        {
            var (label, before, now) = scores[i];
            var delta = now - before;
            var tile = new Border
            {
                Padding = new Thickness(12, 8), Background = Tok.Panel, BorderBrush = i == 0 ? Accent.Main : Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 6,
                            Children = { T(before.ToString(), 13, Tok.Muted, mono: true), T("→", 13, Tok.Muted), T(now.ToString(), 20, Tok.Score(now), FontWeight.SemiBold, mono: true) },
                        },
                        T($"{label}  {(delta > 0 ? "+" : "")}{(delta == 0 ? "=" : delta.ToString())}", 11, delta > 0 ? Tok.Good : delta < 0 ? Tok.Bad : Tok.Muted, mono: true),
                    },
                },
            };
            Grid.SetColumn(tile, i);
            tiles.Children.Add(tile);
        }

        // Ce que ça demande en jeu.
        var cost = new StackPanel { Spacing = 4 };
        switch (o.Daevanion)
        {
            case DaevanionChoice.Next:
                foreach (var pool in r.Daevanion.ByPool)
                {
                    var taken = pool.Take(o.NextNodes).ToList();
                    cost.Children.Add(T($"Daevanion ({PoolShort(pool.Key)}) : ouvrir {Fmt.Plural(taken.Count, "case")}, {Fmt.Num(taken.Sum(x => x.Cost))} points.", 12, Tok.Text2));
                }
                break;
            case DaevanionChoice.Respec:
                foreach (var x in r.Daevanion.Respecs.Where(x => x.Remove.Count > 0))
                    cost.Children.Add(T($"Daevanion ({PoolShort(x.Pool)}) : retirer {Fmt.Plural(x.Remove.Count, "case")} et en prendre {x.Add.Count} (réinitialisation du plateau).", 12, Tok.Text2));
                break;
            case DaevanionChoice.Route:
                foreach (var x in r.Daevanion.Routes.Where(x => x.Nodes.Count > 0))
                {
                    var inBudget = x.InBudget.ToList();
                    cost.Children.Add(T($"Daevanion ({PoolShort(x.Pool)}) : réinitialiser et suivre la route conçue, {inBudget.Count} cases pour {x.Budget} points ({inBudget.Count(n => !n.Node.Open)} à ouvrir).", 12, Tok.Text2));
                }
                break;
        }
        if (o.Skills) cost.Children.Add(T($"Compétences : réinitialiser (gratuit) et refaire la répartition conseillée ({Fmt.Plural(r.Skills.Skills.Count(a => a.Delta != 0), "compétence change", "compétences changent")}).", 12, Tok.Text2));
        if (o.Designed is { } designed) cost.Children.Add(T($"Build : suivre le build {(designed ? "conçu" : "des joueurs")} (écran Build).", 12, Tok.Text2));
        if (o.GearAtStage)
            cost.Children.Add(T($"Équipement : {Fmt.Plural(r.Gear.Slots.Count(x => x.Status is SlotStatus.Empty or SlotStatus.Replace or SlotStatus.Weak), "pièce")} à remplacer ou monter (note seulement : leurs stats futures sont inconnues).", 12, Tok.Text2));
        // Familles de stats avant → après.
        var families = new StackPanel { Spacing = 8 };
        foreach (var (b, a) in r.Stats.Categories.Zip(after.Stats.Categories))
        {
            var d = (int)Math.Round((a.Share - b.Share) * 100);
            families.Children.Add(ColumnsSpaced("80,*,240", 12, T(GameDatabase.CategoryName(b.Category), 13),
                new StackPanel { Spacing = 3, Children = { Bar(b.Share * 1.6, Tok.Muted, 4, b.Target * 1.6), Bar(a.Share * 1.6, Accent.Main, 4, a.Target * 1.6) } },
                T($"{Fmt.Pct(b.Share)} → {Fmt.Pct(a.Share)} ({(d >= 0 ? "+" : "")}{d}) · visé {Fmt.Pct(a.Target)}", 12, d == 0 ? Tok.Muted : Tok.Text2, mono: true)));
        }
        // Stats qui changent.
        var changes = Simulator.StatChanges(r, after);
        const string cols = "*,130,130,90";
        Control statChanges;
        if (changes.Count == 0)
            statChanges = Card(T("Aucune stat ne change : ce choix agit sur les niveaux de compétences ou sur la note seulement.", 12, Tok.Muted, wrap: true), "Stats qui changent · 0");
        else
            statChanges = TableCard($"Stats qui changent · {changes.Count}", null, TableHeader(cols, "Stat", "Avant", "Après", "Poids"), changes.Select(x =>
            {
                var (b, a) = x;
                var up = a.Budget > b.Budget;
                return (Control)new Border
                {
                    Padding = new Thickness(20, 5),
                    Child = Columns(cols, T(a.Def.Name, 13), T(CompactTotal(b), 13, Tok.Muted, mono: true), T(CompactTotal(a), 13, up ? Tok.Good : Tok.Bad, FontWeight.SemiBold, mono: true),
                        T(a.Weight.ToString("0.00"), 12, Tok.Muted, mono: true)),
                };
            }));
        return Page(title, null, choices, tiles, Card(cost, "Ce que ça demande en jeu"),
            Card(families, "Familles de stats", T("gris = avant · couleur = après", 11, Tok.Muted, mono: true)), statChanges);
    }

    Control SimulateInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Comment ça marche") } };
        panel.Children.Add(new StackPanel
        {
            Spacing = 10, Margin = new Thickness(14, 12),
            Children =
            {
                T("Les changements choisis sont appliqués à une copie de ton personnage, puis toute l'analyse est refaite : notes, familles de stats, stats.", 13, Tok.Text2, wrap: true).WithLineHeight(1.5),
                T("Daevanion : les stats viennent des effets réels des cases ouvertes ou retirées. Compétences : les niveaux de la répartition conseillée. Build : les priorités du build choisi.", 13, Tok.Text2, wrap: true).WithLineHeight(1.5),
                T("Équipement : une pièce « au niveau du palier » ne change que la note d'équipement ; ses stats futures ne sont pas connues, elles ne sont donc pas ajoutées.", 13, Tok.Text2, wrap: true).WithLineHeight(1.5),
                T("Rien n'est enregistré : ton personnage et tes conseils ne changent pas.", 12, Tok.Muted, wrap: true).WithLineHeight(1.45),
            },
        });
        return panel;
    }

    // ----- Progression -----

    /// <summary>Courbe simple sur les imports (un point par import), avec la dernière valeur et l'écart depuis le premier.</summary>
    static Control Curve(string title, IReadOnlyList<(DateTime At, double Value)> points, Func<double, string> format)
    {
        const double width = 260, height = 70;
        var box = new StackPanel { Spacing = 6, Width = width + 24 };
        var last = points[^1].Value;
        var delta = last - points[0].Value;
        box.Children.Add(new DockPanel
        {
            Children =
            {
                new TextBlock { Text = $"{(delta >= 0 ? "+" : "")}{format(delta)}", FontFamily = Fonts.Mono, FontSize = 11, Foreground = delta > 0 ? Tok.Good : delta < 0 ? Tok.Bad : Tok.Muted, [DockPanel.DockProperty] = Dock.Right },
                T($"{title} · {format(last)}", 12, Tok.Text2),
            },
        });
        var canvas = new Canvas { Width = width, Height = height };
        var min = points.Min(p => p.Value);
        var max = points.Max(p => p.Value);
        var span = Math.Max(1e-9, max - min);
        var coords = points.Select((p, i) => new Point(points.Count == 1 ? width / 2 : i * width / (points.Count - 1), height - 6 - (p.Value - min) / span * (height - 12))).ToList();
        canvas.Children.Add(new Polyline { Points = coords, Stroke = Accent.Main, StrokeThickness = 2 });
        foreach (var c in coords)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Accent.Main };
            Canvas.SetLeft(dot, c.X - 3);
            Canvas.SetTop(dot, c.Y - 3);
            canvas.Children.Add(dot);
        }
        box.Children.Add(new Border { Padding = new Thickness(12, 8), Background = Tok.Frame, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = canvas });
        return box;
    }

    Control ProgressScreen()
    {
        if (progress.Count < 2)
            return Page($"Progression · {progress.Count} import", null,
                T("Un seul import pour l'instant. Actualise ton personnage (Ctrl+R) après tes progrès : chaque import qui change quelque chose " +
                  "(pièces, compétences, cases Daevanion, niveau d'objet) ajoute un point ici, avec ce qui a changé.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5));
        var first = progress[0];
        var key = r.Mode.ToString();
        var curves = new WrapPanel { ItemSpacing = 16, LineSpacing = 12 };
        curves.Children.Add(Curve("Niveau d'objet", progress.Select(p => (p.FetchedAtUtc, (double)p.ItemLevel)).ToList(), v => Fmt.Num(v)));
        curves.Children.Add(Curve("Puissance", progress.Select(p => (p.FetchedAtUtc, (double)p.CombatPower)).ToList(), v => Fmt.Num(v)));
        var scored = progress.Where(p => p.Scores?.ContainsKey(key) == true).Select(p => (p.FetchedAtUtc, (double)p.Scores![key].Overall)).ToList();
        if (scored.Count >= 2) curves.Children.Add(Curve($"Score global ({Mode})", scored, v => Fmt.Num(v)));
        var curvesBlock = new StackPanel { Spacing = 8, Children = { curves } };
        if (scored.Count < 2)
            curvesBlock.Children.Add(T("La courbe du score apparaît dès deux imports notés (le score est gardé à chaque calcul depuis cette version).", 12, Tok.Muted, wrap: true));

        static string Delta(int d) => d == 0 ? "" : d > 0 ? $" (+{d})" : $" ({d})";
        // Les 6 derniers imports visibles, les plus anciens repliés.
        const int shown = 6;
        var recent = new StackPanel();
        var older = new StackPanel();
        for (var i = progress.Count - 1; i >= 0; i--)
        {
            var p = progress[i];
            var prev = i > 0 ? progress[i - 1] : null;
            var score = p.Scores?.GetValueOrDefault(key);
            var prevScore = prev?.Scores?.GetValueOrDefault(key);
            var head = ColumnsSpaced("120,*", 12, T(p.FetchedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), 12, Tok.Muted, mono: true),
                T($"niv. {p.Level} · objet {p.ItemLevel}{Delta(p.ItemLevel - (prev?.ItemLevel ?? p.ItemLevel))} · puissance {p.CombatPower}{Delta(p.CombatPower - (prev?.CombatPower ?? p.CombatPower))}" +
                  (score is null ? "" : $" · score {score.Overall}{(prevScore is null ? "" : Delta(score.Overall - prevScore.Overall))}"), 13, mono: true));
            var block = new StackPanel { Spacing = 3, Children = { head } };
            if (prev is not null)
            {
                var changes = p.ChangesSince(prev, db.SlotName);
                foreach (var change in changes.Take(10)) block.Children.Add(T($"· {change}", 12, Tok.Text2).WithMargin(132, 0, 0, 0));
                if (changes.Count > 10) block.Children.Add(Tip(T($"· et {Fmt.Plural(changes.Count - 10, "autre changement", "autres changements")}", 12, Tok.Muted).WithMargin(132, 0, 0, 0), string.Join("\n", changes.Skip(10))));
                if (changes.Count == 0 && p.Gear is not null && prev.Gear is not null) block.Children.Add(T("· rien d'autre de visible", 12, Tok.Faint).WithMargin(132, 0, 0, 0));
            }
            else block.Children.Add(T("· premier import enregistré", 12, Tok.Faint).WithMargin(132, 0, 0, 0));
            (progress.Count - 1 - i < shown ? recent : older).Children.Add(new Border
                { Padding = new Thickness(14, 8), BorderBrush = Tok.RowLine, BorderThickness = new Thickness(0, 0, 0, 1), Child = block });
        }
        if (older.Children.Count > 0)
            recent.Children.Add(Expander(Fmt.Plural(older.Children.Count, "import plus ancien", "imports plus anciens"),
                IsOpen("progress:older"), () => Toggle("progress:older"), () => older, new Thickness(10, 4)));
        return Page($"Progression · {progress.Count} imports depuis le {first.FetchedAtUtc.ToLocalTime():dd/MM/yyyy}", null,
            Card(curvesBlock, "Courbes"), Card(recent, "Imports, du plus récent au plus ancien", padding: new Thickness(0)));
    }

    Control Details()
    {
        // Les simples informations (bilans chiffrés) sont repliées par défaut : la liste montre d'abord ce qu'il y a à faire.
        var infos = r.Findings.Count(f => f.Severity == Severity.Info);
        var toggle = Clickable(T(s.ShowInfo ? $"masquer les {Fmt.Plural(infos, "info")}" : $"afficher aussi les {Fmt.Plural(infos, "info")}", 11, Accent.Main), () => update(st => st.ShowInfo = !st.ShowInfo),
            hover: Tok.HoverStrong, padding: new Thickness(4, 0));
        var bySeverity = Badges(
            new[] { Severity.Critical, Severity.Important, Severity.Advice }
                .Select(level => (level, n: r.Findings.Count(f => f.Severity == level && !IsDone(f))))
                .Where(x => x.n > 0)
                .Select(x => (Control)Badge($"{Sev(x.level).Icon} " + (x.level switch
                {
                    Severity.Critical => Fmt.Plural(x.n, "critique"),
                    Severity.Important => Fmt.Plural(x.n, "important"),
                    _ => $"{x.n} à suivre",
                }), LevelColor(x.level))).Append(toggle).ToArray());
        var groups = new List<Control>();
        var shown = r.Findings.Where(f => s.ShowInfo || f.Severity != Severity.Info);
        foreach (var group in shown.GroupBy(f => f.Area).OrderBy(g => g.Key))
        {
            var collapsed = s.Collapsed.Contains(group.Key);
            var key = group.Key;
            var fold = Clickable(T(collapsed ? "ouvrir ▸" : "replier ▾", 11, Tok.Muted, mono: true),
                () => update(st => { if (!st.Collapsed.Remove(key)) st.Collapsed.Add(key); }), hover: Tok.HoverStrong, padding: new Thickness(4, 0));
            var list = new StackPanel();
            groups.Add(Card(list, $"{Fmt.Area(group.Key)} · {group.Count()}", fold, new Thickness(0)));
            if (collapsed) continue;
            foreach (var f in group.OrderBy(IsDone).ThenBy(f => f.Severity))
            {
                // Titre et action ; le détail chiffré tient sur deux lignes et s'affiche en entier au survol. Fait = grisé, en bas.
                var (icon, color) = Sev(f.Severity);
                var done = IsDone(f);
                var iconText = T(icon, 13, color, FontWeight.SemiBold, mono: true);
                iconText.VerticalAlignment = VerticalAlignment.Top;
                var title = T(f.Title, 13, done ? Tok.Muted : null, FontWeight.Medium, wrap: true);
                if (done) title.TextDecorations = TextDecorations.Strikethrough;
                var text = new StackPanel { Spacing = 3, Children = { title } };
                if (f.Action is not null && !done) text.Children.Add(T($"→ {f.Action}", 12, Tok.Text2, wrap: true).WithLineHeight(1.45));
                if (!done) text.Children.Add(new TextBlock { Text = f.Detail.Replace('\n', ' '), FontSize = 12, Foreground = Tok.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                var lead = f.Severity == Severity.Info ? (Control)new Border() : Check(f);
                var row = Clickable(ColumnsSpaced("22,22,*", 6, lead, iconText, text), () => Go(f), hover: Tok.Hover, padding: new Thickness(14, 8));
                row.BorderBrush = Tok.RowLine;
                row.BorderThickness = new Thickness(0, 0, 0, 1);
                list.Children.Add(Tip(row, f.Detail));
            }
        }
        return Page($"À faire · {Mode} · {Fmt.CountsText(r.Findings)}", bySeverity, groups.ToArray());
    }
}

static class LayoutExtensions
{
    public static T WithMargin<T>(this T control, double uniform) where T : Control
    {
        control.Margin = new Thickness(uniform);
        return control;
    }

    public static T WithMargin<T>(this T control, double horizontal, double vertical) where T : Control
    {
        control.Margin = new Thickness(horizontal, vertical);
        return control;
    }

    public static T WithMargin<T>(this T control, double left, double top, double right, double bottom) where T : Control
    {
        control.Margin = new Thickness(left, top, right, bottom);
        return control;
    }
}
