using System.Text.RegularExpressions;
using Aion2Builder.Analysis;
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

enum Screen { Import, Home, Stats, Gear, Skills, Build, Daeva, Pets, Details }

/// <summary>État de l'interface : écran, sélections et blocs repliés (hors rapport).</summary>
sealed class UiState
{
    public Screen Screen { get; set; } = Screen.Home;
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
    public HashSet<Area> Collapsed { get; } = [];
}

/// <summary>
/// Écrans de la refonte « IDE » : pour chacun, le contenu central et l'inspecteur de droite.
/// Le rapport (<see cref="AdviceReport"/>) ne change pas ; seuls la présentation et la navigation changent.
/// </summary>
sealed class ScreenViews(GameDatabase db, AdviceReport r, PlayerInputs inputs, UiState s,
    Action<Action<UiState>> update, Action<PlayerInputs> onInputs, string? petKeyHint, Action<string?> onPets)
{
    public static readonly (Screen Screen, string Title, string Short)[] Nav =
    [
        (Screen.Home, "Accueil", "Accueil"), (Screen.Stats, "Stats", "Stats"), (Screen.Gear, "Équipement", "Équip."),
        (Screen.Skills, "Compétences", "Compét."), (Screen.Build, "Build et rotation", "Build"), (Screen.Daeva, "Daevanion", "Daeva."), (Screen.Pets, "Familiers", "Famil."),
        (Screen.Details, "Détails", "Détails"),
    ];

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
        Screen.Home => (Doll(), HomeInspector()),
        Screen.Gear => (GearScreen(), GearInspector()),
        Screen.Stats => (Stats(), StatInspector()),
        Screen.Skills => (Skills(), SkillInspector()),
        Screen.Build => (BuildScreen(), BuildInspector()),
        Screen.Daeva => (Daeva(), DaevaInspector()),
        Screen.Pets => (Pets(), PetInspector()),
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

    void Go(Finding f) => update(st =>
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
                st.Slot = r.Gear.Slots.FirstOrDefault(x => f.Title.StartsWith(x.SlotName, StringComparison.Ordinal))?.Slot
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
            ColumnDefinitions = new ColumnDefinitions("200,*,*,200"), ColumnSpacing = 12, RowSpacing = 5,
            Margin = new Thickness(20, 18), VerticalAlignment = VerticalAlignment.Top,
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
        return grid;
    }

    (string Label, int Value, Screen Screen)[] AreaScores =>
    [
        ("Stats", r.Scores.Stats, Screen.Stats), ("Équipement", r.Scores.Gear, Screen.Gear),
        ("Compétences", r.Scores.Skills, Screen.Skills), ("Daevanion", r.Scores.Daevanion, Screen.Daeva),
    ];

    /// <summary>Accueil : les quatre notes (clic = écran), puis les actions les plus rentables (clic = l'endroit où agir).</summary>
    Control HomeCenter()
    {
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
        var i = 0;
        foreach (var (label, v, screen) in AreaScores)
        {
            var tile = Clickable(new StackPanel { Spacing = 2, Children = { T(v.ToString(), 22, Tok.Score(v), FontWeight.SemiBold, mono: true), T(label, 11, Tok.Muted) } },
                () => GoTo(screen), Tok.Panel, Tok.TileHover, new Thickness(12, 8));
            tile.CornerRadius = new CornerRadius(4);
            tile.BorderBrush = Tok.Border;
            tile.BorderThickness = new Thickness(1);
            Grid.SetColumn(tile, i++);
            tiles.Children.Add(tile);
        }
        var actions = new StackPanel();
        foreach (var f in r.ActionPlan.Take(5)) actions.Children.Add(CompactAction(f));
        if (actions.Children.Count == 0) actions.Children.Add(T("✓ Rien d'important à faire : tout est en ordre pour cette vue.", 13, Tok.Good).WithMargin(4, 8));
        var header = new DockPanel { Margin = new Thickness(2, 6, 2, 0) };
        var all = Clickable(T($"tout voir ({r.Findings.Count})", 11, Accent.Main), () => GoTo(Screen.Details));
        DockPanel.SetDock(all, Dock.Right);
        header.Children.Add(all);
        header.Children.Add(Label($"À faire maintenant · {Mode}"));
        return new StackPanel { Spacing = 8, Children = { tiles, header, actions } };
    }

    /// <summary>Une action sur une ligne : gravité, titre, zone ; le détail passe en infobulle.</summary>
    Border CompactAction(Finding f)
    {
        var (icon, color) = Sev(f.Severity);
        var row = Clickable(ColumnsSpaced("16,*,Auto", 8, T(icon, 12, color, FontWeight.SemiBold, mono: true), T(f.Title, 13, weight: FontWeight.Medium),
                T(Fmt.Area(f.Area), 11, Tok.Muted, mono: true)),
            () => Go(f), hover: Tok.TileHover, padding: new Thickness(8, 7));
        row.BorderBrush = Tok.RowLine2;
        row.BorderThickness = new Thickness(0, 0, 0, 1);
        return Tip(row, $"{f.Detail}\n→ {f.Action}");
    }

    /// <summary>Équipement : palier, pièces à traiter par statut et gravures à revoir.</summary>
    Control GearCenter()
    {
        var g = r.Gear;
        var lines = new StackPanel { Spacing = 6 };
        lines.Children.Add(T(g.NextStage is null ? $"Palier « {g.Stage.Name} »" : $"Palier « {g.Stage.Name} » → « {g.NextStage.Name} » à {g.NextStage.MinItemLevel}", 15, weight: FontWeight.SemiBold));
        foreach (var (status, label) in new[] { (SlotStatus.Empty, "vide(s)"), (SlotStatus.Replace, "à remplacer"), (SlotStatus.Weak, "en retard"), (SlotStatus.Enchant, "à enchanter") })
        {
            var slots = g.Slots.Where(x => x.Status == status).ToList();
            if (slots.Count == 0) continue;
            var (code, color) = StatusCode(status);
            lines.Children.Add(ColumnsSpaced("18,*", 8, T(code, 12, color, FontWeight.SemiBold, mono: true),
                T($"{slots.Count} {label} : {string.Join(", ", slots.OrderByDescending(x => x.Priority).Select(x => x.SlotName))}", 13, Tok.Text2, wrap: true)));
        }
        var engr = r.Engravings.Items.Count(e => e.NeedsWork);
        if (engr > 0)
            lines.Children.Add(ColumnsSpaced("18,*", 8, T("G", 12, Tok.Warn, FontWeight.SemiBold, mono: true), T($"{engr} pièce(s) aux gravures à revoir (tableau sous la silhouette)", 13, Tok.Text2, wrap: true)));
        if (g.Slots.All(x => x.Status == SlotStatus.Ok) && engr == 0)
            lines.Children.Add(T("✓ Toutes les pièces sont au niveau de ton palier.", 13, Tok.Good));
        lines.Children.Add(T("Clique une pièce pour son détail dans l'inspecteur.", 12, Tok.Muted).WithMargin(0, 6, 0, 0));
        return new Border { Padding = new Thickness(16, 12), Background = Tok.Frame, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = lines };
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

        var tile = Clickable(dock, () => update(st => { st.Screen = Screen.Gear; st.Slot = p.Slot; }),
            selected ? Accent.Dim : Tok.Panel, selected ? null : Tok.TileHover, new Thickness(9, 6));
        tile.CornerRadius = new CornerRadius(3);
        tile.BorderBrush = selected ? Accent.Main : Tok.Border;
        tile.BorderThickness = new Thickness(1);
        var tip = p.Item is null
            ? $"{p.SlotName} : emplacement vide"
            : $"{p.Item.Name} — {db.Grade(p.Item.Grade).Name}, niveau d'objet {p.Power}, +{p.Item.EnchantLevel}\n" +
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
            : empty > 0 ? $"graver {empty} pierre(s)"
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

    /// <summary>Écran Équipement : la silhouette, puis le tableau des gravures (pièces à revoir en tête).</summary>
    Control GearScreen()
    {
        var panel = new StackPanel { Children = { Doll() } };
        var items = r.Engravings.Items;
        if (items.Count == 0) return panel;
        var todo = items.Count(e => e.NeedsWork);
        panel.Children.Add(SectionHeader($"Gravures · {todo} pièce(s) à revoir", T("✓ utile   ~ moyen   ✕ à remplacer", 11, Tok.Muted, mono: true), 20, topBorder: true));
        panel.Children.Add(T("Ordre économe : corrige d'abord les ✕ des pièces que tu gardes (arme en tête), monte ensuite le lien d'âme des pièces " +
            "aux bonnes options, puis les pierres. Rien sur les pièces à remplacer.", 12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(20, 10, 20, 8));
        const string cols = "120,90,110,60,*";
        panel.Children.Add(TableHeader(cols, "Pièce", "Lien d'âme", "Options", "Pierres", "À faire"));
        var selected = SelectedSlot?.Slot;
        foreach (var e in items.OrderByDescending(e => e.NeedsWork).ThenByDescending(e => e.Priority))
        {
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (var l in e.SoulLines) options.Children.Add(T(VerdictIcon(l.Verdict), 12, VerdictBrush(l.Verdict), FontWeight.SemiBold, mono: true));
            var rate = e.HasSoulBinding ? $"{Fmt.Num(e.SoulBindRate ?? 0)} %" : e.SoulLines.Count > 0 ? "fixes" : "—";
            var stones = e.StoneSlots > 0 ? $"{e.Stones.Count}/{e.StoneSlots}" : "—";
            var stoneColor = e.Stones.Count < e.StoneSlots ? Tok.Bad : e.Stones.Any(x => x.Verdict == LineVerdict.Poor) ? Tok.Warn : Tok.Text2;
            panel.Children.Add(Tip(TableRow(Columns(cols,
                    T(e.SlotName, 13), T(rate, 13, !e.HasSoulBinding ? Tok.Muted : (e.SoulBindRate ?? 0) >= 100 ? Tok.Good : Tok.Text, mono: true), options,
                    T(stones, 13, stoneColor, mono: true), T(ActionShort(e), 12, TodoBrush(e))),
                e.Slot == selected, () => update(st => st.Slot = e.Slot)), $"{e.Item.Name}{EngravingTip(e)}"));
        }
        return panel;
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
                    ("serveur", $"{c.ServerName} ({c.Region})", null), ("niveau_objet", c.ItemLevel.ToString(), null), ("puissance", c.CombatPower.ToString(), null),
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
            ("niveau_objet", item is null ? "—" : p.Power.ToString(), null),
            ("enchantement", item is null ? "—" : $"+{item.EnchantLevel} / {item.MaxEnchantLevel}", null),
            ("statut", Fmt.Status(p.Status), StatusBrush(p.Status)),
        ]));
        body.Children.Add(Field("Conseil", p.Advice));
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

    IReadOnlyList<StatTotal> VisibleStats => r.Stats.Stats.Where(x => x.Budget > 0 || x.ExpectedShare >= 0.03).ToList();

    static int Pts(double ratio) => (int)Math.Round(ratio * 100);

    /// <summary>Total court pour le tableau (le détail plat + % est dans l'infobulle et l'inspecteur).</summary>
    static string CompactTotal(StatTotal x) => x.Flat > 0 ? Fmt.Num(x.Flat) : $"{Fmt.Num(x.Percent)} %";

    /// <summary>Couleur de la part : rouge s'il en manque, orange si une stat secondaire prend trop de place.</summary>
    static IBrush ShareColor(StatTotal x) =>
        x.ExpectedShare - x.Share > 0.02 ? Tok.Bad : x.Share - x.ExpectedShare > 0.08 && x.Weight < 0.6 ? Tok.Warn : Tok.Text;

    static (string Text, IBrush Color) Verdict(StatTotal x)
    {
        if (x.ExpectedShare - x.Share > 0.02)
            return ($"Il te manque environ {Pts(x.ExpectedShare - x.Share)} points de part : à augmenter.", Tok.Bad);
        if (x.Share - x.ExpectedShare > 0.08)
            return x.Weight < 0.6
                ? ($"Tu y mets {Pts(x.Share - x.ExpectedShare)} points de part de trop : à réduire au profit des stats manquantes.", Tok.Warn)
                : ("Au-dessus du repère, mais c'est une stat prioritaire pour ta classe : rien à réduire.", Tok.Good);
        return ("Proche du repère de ta classe.", Tok.Good);
    }

    Control Stats()
    {
        var sp = r.Stats;
        var tolerance = db.Settings.Stats.CategoryTolerance;
        var panel = new StackPanel();
        var efficiency = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { T("efficacité", 11, Tok.Muted, mono: true), T(Fmt.Pct(sp.Efficiency), 11, Accent.Main, FontWeight.Bold, mono: true) } };
        panel.Children.Add(SectionHeader($"Répartition des stats ajustables · {Mode}", efficiency, 20));

        var families = new StackPanel { Spacing = 12 };
        foreach (var c in sp.Categories)
        {
            var color = Math.Abs(c.Gap) <= tolerance ? Tok.Good : c.Gap > 0 ? Tok.Warn : Tok.Bad;
            var gap = Math.Abs(c.Gap) <= tolerance ? "équilibré" : c.Gap > 0 ? $"+{Pts(c.Gap)} pts de trop" : $"−{Pts(-c.Gap)} pts manquants";
            var numbers = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 0,
                Children =
                {
                    T(Fmt.Pct(c.Share), 12, Tok.Text, FontWeight.Bold, mono: true), T($" / {Fmt.Pct(c.Target)}  ", 12, Tok.Muted, mono: true),
                    T(gap, 12, color, mono: true),
                },
            };
            families.Children.Add(Tip(ColumnsSpaced("80,*,220", 16, T(GameDatabase.CategoryName(c.Category), 13),
                Bar(c.Share * 1.6, color, 8, c.Target * 1.6), numbers), GameDatabase.CategoryDescription(c.Category)));
        }
        families.Children.Add(Collapsible("Qu'est-ce qu'une stat ajustable ? (trait blanc = repère)",
            "Ce que tu peux changer : stats secondaires des pièces, cases Daevanion, titres, stats divines. Les stats fixes d'une pièce " +
            "(la DÉF d'une armure, l'ATQ d'une arme) viennent avec elle et restent hors répartition.",
            s.StatsHow, () => update(st => st.StatsHow = !st.StatsHow), default));
        panel.Children.Add(new Border { Padding = new Thickness(20, 16), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = families });

        const string cols = "150,100,90,80,80,*";
        panel.Children.Add(TableHeader(cols, "Stat", "Famille", "Total", "Part", "Repère", "Poids"));
        var selected = SelectedStat?.Def.Key;
        foreach (var x in VisibleStats)
        {
            var weight = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { new Border { Width = 140, Child = Bar(x.Weight, Accent.Main, 3) }, T(x.Weight.ToString("0.00"), 12, Tok.Muted, mono: true) },
            };
            panel.Children.Add(TableRow(Columns(cols,
                    T(x.Def.Name, 13), T(GameDatabase.CategoryName(x.Def.Category), 13, Tok.Muted), Tip(T(CompactTotal(x), 13, mono: true), x.Display),
                    T(Fmt.Pct(x.Share), 13, ShareColor(x), mono: true), T(Fmt.Pct(x.ExpectedShare), 13, Tok.Muted, mono: true), weight),
                x.Def.Key == selected, () => update(st => st.Stat = x.Def.Key)));
        }
        return panel;
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
            ("repère", Fmt.Pct(x.ExpectedShare), null),
            ("poids", x.Weight.ToString("0.00"), null),
        ]));
        body.Children.Add(new Border
        {
            Padding = new Thickness(12, 10), Background = Tok.Frame, BorderBrush = color, BorderThickness = new Thickness(2, 0, 0, 0),
            Child = T(verdict, 13, wrap: true).WithLineHeight(1.5),
        });
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

    string? SelectedSkillName => s.Skill is { } n && (r.Skills.Skills.Any(a => a.Skill.Name == n) || r.Skills.Stigmas.Any(a => a.Skill.Name == n))
        ? n
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

    /// <summary>Pourquoi une compétence prend des points : son rôle et son importance contre ceux des donneurs.</summary>
    static string MoveReason(SkillAdvice to, List<SkillMove> moves)
    {
        var donors = moves.Where(m => m.From is not null).Select(m => m.From!).Distinct().ToList();
        var head = $"{to.Skill.Name} — {to.Role.ToLowerInvariant()}, importance {to.Priority:0.00}";
        return donors.Count == 0
            ? $"{head} : tes points non dépensés y servent le plus."
            : $"{head} — passe avant {string.Join(", ", donors.Select(d => $"{d.Skill.Name} ({d.Role.ToLowerInvariant()}, {d.Priority:0.00})"))}.";
    }

    /// <summary>Déplacements groupés par compétence qui reçoit : « +9 Coup de choc ← Défense d'égide −8, Sceau de protection −1 ».</summary>
    Control SkillMoves(SkillPlan sk)
    {
        var list = new StackPanel { Spacing = 2 };
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
            var to = group.Key.Skill.Name;
            // La raison passe en infobulle (et dans l'inspecteur au clic) : une ligne par compétence.
            list.Children.Add(Tip(Clickable(line, () => update(st => st.Skill = to), hover: Tok.Hover, padding: new Thickness(6, 4)), MoveReason(group.Key, moves)));
        }
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

    Control Skills()
    {
        var sk = r.Skills;
        var set = db.Settings.Skills;
        var panel = new StackPanel();

        var budget = new DockPanel();
        var right = T($"réinit. gratuite · max base {set.BaseMax}", 12, Tok.Muted, mono: true);
        DockPanel.SetDock(right, Dock.Right);
        budget.Children.Add(right);
        budget.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                T("budget =", 12, Tok.Muted, mono: true), T(sk.SpentPoints.ToString(), 12, Tok.Text, FontWeight.Bold, mono: true),
                T("investis +", 12, Tok.Muted, mono: true),
                Stepper(sk.UnspentPoints, v => onInputs(inputs with { UnspentSkillPoints = v })),
                T("non dépensés =", 12, Tok.Muted, mono: true), T(sk.Budget.ToString(), 12, Accent.Main, FontWeight.Bold, mono: true),
            },
        });
        panel.Children.Add(new Border { Padding = new Thickness(20, 12), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = budget });

        // Ce qu'il faut faire tout de suite (réinitialisation gratuite), puis où vont les points suivants.
        if (sk.Moves.Count > 0)
        {
            panel.Children.Add(SectionHeader($"Maintenant · {Pts(sk.Moves.Sum(m => m.Points))} à déplacer (réinitialisation gratuite) · survole une ligne pour la raison", padX: 20));
            panel.Children.Add(new Border { Padding = new Thickness(14, 6), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = SkillMoves(sk) });
        }
        else
            panel.Children.Add(new Border
            {
                Padding = new Thickness(20, 10), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
                Child = T(sk.UnspentPoints > 0 ? "✓ Rien à déplacer : place tes points non dépensés dans l'ordre ci-dessous."
                    : "✓ Tes points sont bien placés : rien à changer. La liste ci-dessous sert quand tu gagnes de nouveaux points.", 13, Tok.Good, wrap: true),
            });
        panel.Children.Add(SectionHeader("Prochains points · le site ne montre pas tes points libres : suis la liste jusqu'à épuisement", padX: 20));
        panel.Children.Add(new Border { Padding = new Thickness(14, 6), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = NextPoints(sk) });

        const string cols = "*,190,90,64,110";
        panel.Children.Add(TableHeader(cols, "Compétence", "Rôle", "Niveau", "Visé", "Maintenant"));
        var selected = SelectedSkillName;
        foreach (var a in sk.Skills)
        {
            var kind = a.Kind == SkillKind.Passive ? "passif" : a.InBar ? null : "hors barre";
            Control delta = a.Delta == 0 ? T("=", 12, Tok.Muted, mono: true) : Pill(DeltaText(a), DeltaColor(a));
            var (aimText, aimColor, aimTip) = Aim(a);
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T(a.Skill.Name, 13, a.Skill.Acquired ? null : Tok.Muted) } };
            if (kind is not null) name.Children.Add(T(kind, 11, Tok.Faint, mono: true));
            panel.Children.Add(TableRow(Columns(cols,
                    name, Tip(T(a.Role, 13, Tok.Text2), a.Role),
                    Tip(T(a.Skill.Acquired ? $"{a.Base} +{a.Bonus} = {a.Total}" : "—", 13, mono: true), "Niveau acheté + niveaux gratuits (Daevanion, équipement) = niveau total."),
                    Tip(T(aimText, 13, aimColor, FontWeight.SemiBold, mono: true), aimTip), delta),
                a.Skill.Name == selected, () => update(st => st.Skill = a.Skill.Name)));
        }

        var stigmaHeader = SectionHeader($"Stigmas · {db.Settings.Stigmas.Slots} emplacements · échange gratuit", padX: 20, topBorder: true);
        stigmaHeader.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(stigmaHeader);
        const string stigmaCols = "80,*,140,90,110";
        foreach (var g in sk.Stigmas.Where(x => x.Recommended || x.Skill.Equipped))
        {
            var (label, color) = g.Recommended ? (g.Skill.Equipped ? ("Garder", Tok.Good) : ("Mettre", Tok.Info)) : ("Retirer", Tok.Bad);
            panel.Children.Add(TableRow(Columns(stigmaCols,
                    T(label, 12, color, FontWeight.SemiBold, mono: true), T(g.Skill.Name, 13, g.Skill.Acquired ? null : Tok.Muted), T(g.Role, 13, Tok.Text2),
                    T(g.Recommended ? $"{g.Skill.Level} → {g.Target}" : $"{g.Skill.Level}", 13, mono: true),
                    T(g.ShardsToTarget > 0 ? $"{g.ShardsToTarget} fragments" : "", 13, Tok.Muted, mono: true)),
                g.Skill.Name == selected, () => update(st => st.Skill = g.Skill.Name)));
        }

        // Fragments : par paliers de 5 niveaux, là où les effets des stigmas se débloquent.
        if (sk.NextShards.Count > 0)
        {
            panel.Children.Add(SectionHeader("Prochains fragments · par paliers (effets aux niveaux 5, 10, 15, 20)", padX: 20, topBorder: true));
            var list = new StackPanel { Spacing = 2 };
            var i = 0;
            foreach (var x in sk.NextShards)
            {
                var name = x.Stigma.Skill.Name;
                list.Children.Add(Clickable(ColumnsSpaced("24,*,Auto", 8,
                        T($"{++i}.", 12, Tok.Muted, mono: true),
                        T($"{name}   {x.From} → {x.To}", 13),
                        T($"{x.Shards} fragments · palier {x.To}", 12, Tok.Muted, mono: true)),
                    () => update(st => st.Skill = name), hover: Tok.Hover, padding: new Thickness(6, 4)));
            }
            panel.Children.Add(new Border { Padding = new Thickness(14, 8), Child = list });
        }
        return panel;
    }

    Control SkillInspector()
    {
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · compétence") } };
        var name = SelectedSkillName;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        if (r.Skills.Skills.FirstOrDefault(a => a.Skill.Name == name) is { } a)
        {
            body.Children.Add(T(a.Skill.Name, 17, weight: FontWeight.SemiBold, wrap: true));
            body.Children.Add(KeyValue(
            [
                ("type", a.Kind == SkillKind.Passive ? "Passive" : a.InBar ? "Active (dans la barre)" : "Active", null),
                ("rôle", a.Role, null),
                ("niveau", $"{a.Base} +{a.Bonus} = {a.Total}", null),
                ("visé", Aim(a).Text, Aim(a).Color),
                ("maintenant", a.Delta == 0 ? "rien à changer" : DeltaText(a), DeltaColor(a)),
                ("priorité", a.Priority.ToString("0.00"), null),
            ]));
            body.Children.Add(Field("Pourquoi", a.Why, a.Unverified ? Tok.Warn : null));
            // Échanges : d'où viennent ses points, ou à qui elle les cède, et pourquoi.
            var exchanges = r.Skills.Moves.Where(m => m.To == a).Select(m => m.From is null
                    ? $"Reçoit {Pts(m.Points)} non dépensé(s)."
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
                ("conseil", g.Recommended ? (g.Skill.Equipped ? "garder" : "mettre") : "retirer", g.Recommended ? (g.Skill.Equipped ? Tok.Good : Tok.Info) : Tok.Bad),
                ("fragments", g.ShardsToTarget.ToString(), null),
                ("priorité", g.Priority.ToString("0.00"), null),
            ]));
            body.Children.Add(Field("Pourquoi", g.Why, g.Unverified ? Tok.Warn : null));
        }
        panel.Children.Add(body);
        return panel;
    }

    // ----- Build : version des joueurs et version conçue, côte à côte -----

    Control BuildScreen()
    {
        var cls = r.Profile.Class;
        var panel = new StackPanel();
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
        panel.Children.Add(SectionHeader($"Build · {cls.Name} · {Mode}", choice, 20));

        var players = r.Profile with { UsesDesigned = false };
        var mine = r.Profile with { UsesDesigned = true };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var left = BuildColumn("Build des joueurs", cls.SkillsSource ?? "Analyse générique (pas de fiche pour cette classe).", players.Skills, null,
            cls.Play.GetValueOrDefault(r.Mode), !designed);
        var right = cls.Designed is { } d
            ? BuildColumn("Build conçu", d.Summary, mine.Skills, players.Skills, d.Play.GetValueOrDefault(r.Mode), designed)
            : new StackPanel { Margin = new Thickness(20, 16), Children = { Label("Build conçu"), T("Pas encore rédigé pour cette classe.", 13, Tok.Muted, wrap: true).WithMargin(0, 8, 0, 0) } };
        Grid.SetColumn(right, 1);
        grid.Children.Add(new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = left });
        grid.Children.Add(right);
        panel.Children.Add(grid);

        if (cls.Designed is { Differences.Count: > 0 } diff)
        {
            panel.Children.Add(SectionHeader("Différences entre les deux builds, et pourquoi", padX: 20, topBorder: true));
            var list = new StackPanel { Spacing = 6, Margin = new Thickness(20, 10, 20, 16) };
            foreach (var x in diff.Differences) list.Children.Add(ColumnsSpaced("14,*", 6, T("•", 13, Accent.Main), T(x, 13, Tok.Text2, wrap: true).WithLineHeight(1.45)));
            panel.Children.Add(list);
        }
        return panel;
    }

    /// <summary>Une colonne de build : priorités du mode (niveaux visés), rotation en phases, façon de jouer.</summary>
    Control BuildColumn(string title, string summary, IReadOnlyList<SkillKnowledge> skills, IReadOnlyList<SkillKnowledge>? reference, PlayGuide? play, bool active)
    {
        var col = new StackPanel { Spacing = 12, Margin = new Thickness(20, 14) };
        var head = new DockPanel();
        if (active)
        {
            var tag = Pill("suivi par les conseils", Accent.Main);
            DockPanel.SetDock(tag, Dock.Right);
            head.Children.Add(tag);
        }
        head.Children.Add(T(title, 15, weight: FontWeight.SemiBold));
        col.Children.Add(head);
        col.Children.Add(T(summary, 12, Tok.Muted, wrap: true).WithLineHeight(1.45));

        // Ce qui diffère du build des joueurs est marqué « ≠ ».
        bool Differs(SkillKnowledge k) => reference?.FirstOrDefault(x => x.Name == k.Name && x.Kind == k.Kind) is { } o &&
            (Math.Abs(o.Priority.GetValueOrDefault(r.Mode) - k.Priority.GetValueOrDefault(r.Mode)) >= 0.1 || o.Target.GetValueOrDefault(r.Mode) != k.Target.GetValueOrDefault(r.Mode));
        void Group(string label, string kind, int take)
        {
            var list = skills.Where(k => k.Kind == kind).OrderByDescending(k => k.Priority.GetValueOrDefault(r.Mode)).Take(take).ToList();
            if (list.Count == 0) return;
            var box = new StackPanel { Spacing = 2, Children = { Label(label) } };
            foreach (var k in list)
            {
                var p = k.Priority.GetValueOrDefault(r.Mode);
                var differs = Differs(k);
                box.Children.Add(Tip(ColumnsSpaced("36,*,Auto,14", 6,
                    T(p.ToString("0.00"), 11, p >= 0.85 ? Accent.Main : p >= 0.5 ? Tok.Text2 : Tok.Faint, mono: true),
                    T(k.Name, 12, p >= 0.5 ? Tok.Text : Tok.Muted),
                    T($"→ {k.Target.GetValueOrDefault(r.Mode)}", 11, Tok.Muted, mono: true),
                    T(differs ? "≠" : "", 12, Accent.Main, FontWeight.Bold)), $"{k.Role} — {k.Why}"));
            }
            col.Children.Add(box);
        }
        Group("Actives (importance · niveau visé)", "Active", 12);
        Group("Passifs", "Passive", 10);
        Group("Stigmas (les 4 premiers sont équipés)", "Stigma", 6);

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
        {
            var tips = new StackPanel { Spacing = 4, Children = { Label("Façon de jouer") } };
            foreach (var x in play.Tips) tips.Children.Add(ColumnsSpaced("14,*", 6, T("•", 12, Accent.Main), T(x, 12, Tok.Text2, wrap: true).WithLineHeight(1.4)));
            col.Children.Add(tips);
        }
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
        if (cls.Designed is { } d) body.Children.Add(Field("Méthode", d.Method, Tok.Text2));
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
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in RelevantBoards)
        {
            var on = b.Board.Id == selected?.Board.Id;
            // Onglet d'éditeur : bordure haute de 2 px en couleur secondaire si actif, séparateur gris à droite.
            var inner = Clickable(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { T(b.Board.Name, 13, on ? Tok.Text : Tok.Muted), T($"{b.Board.OpenNodes}/{b.Board.TotalNodes}", 11, Tok.Muted, mono: true) },
                },
                () => update(st => st.Board = b.Board.Id), on ? Tok.Window : null, on ? null : Tok.Hover, new Thickness(16, 0));
            inner.BorderBrush = on ? Accent.Main : Brushes.Transparent;
            inner.BorderThickness = new Thickness(0, 2, 0, 0);
            tabs.Children.Add(new Border { Height = 34, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = inner });
        }
        Border Segment(string text, bool on, bool route)
        {
            var seg = Clickable(T(text, 12, on ? Tok.OnAccent : Tok.Muted, FontWeight.SemiBold, mono: true), () => update(st => { st.DaevaRoute = route; st.AllNext = false; }),
                on ? Accent.Main : null, padding: new Thickness(10, 2));
            seg.CornerRadius = new CornerRadius(3);
            return seg;
        }
        var switcher = new Border
        {
            Background = Tok.Window, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Segment("Ton plateau", !s.DaevaRoute, false), Segment("Route conçue", s.DaevaRoute, true) } },
        };
        ToolTip.SetTip(switcher, "Ton plateau : la suite à partir de tes cases ouvertes (et la réorganisation si elle vaut le coup).\n" +
                                 "Route conçue : le meilleur chemin depuis le centre pour tous tes points, sans tenir compte de ton placement actuel.");
        DockPanel.SetDock(switcher, Dock.Right);
        var tabBar = new Border { Height = 34, Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = new DockPanel { Children = { switcher, tabs } } };
        DockPanel.SetDock(tabBar, Dock.Top);
        panel.Children.Add(tabBar);
        if (selected is null)
        {
            panel.Children.Add(T("Aucun plateau Daevanion détaillé pour ce personnage.", 13, Tok.Muted).WithMargin(20));
            return panel;
        }

        var info = new StackPanel { Spacing = 10 };
        var budget = r.Daevanion.Budgets.FirstOrDefault(x => x.Pool == selected.Pool);
        info.Children.Add(T(selected.Board.Name, 15, Tok.Text, FontWeight.SemiBold));
        info.Children.Add(T($"{selected.Board.OpenNodes}/{selected.Board.TotalNodes} cases · {selected.Spent}/{selected.Total} pts", 12, Tok.Muted, mono: true));
        info.Children.Add(T($"réserve : {PoolShort(selected.Pool)}", 12, Tok.Muted, mono: true));
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
        info.Children.Add(new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 4, 0, 0), Child = legend });

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28, Margin = new Thickness(20), Children = { BoardGrid(selected), info } };
        panel.Children.Add(new StackPanel { Children = { body, DaevaNext(selected) } });
        return panel;
    }

    /// <summary>
    /// Sous le plateau : les cases à prendre de la réserve, dans l'ordre et avec les mêmes numéros que la grille
    /// (après réinitialisation si elle est conseillée, sinon avec tes prochains points).
    /// </summary>
    Control DaevaNext(BoardSummary selected)
    {
        if (s.DaevaRoute && RouteFor(selected.Pool) is { } route) return RouteList(route, selected);
        var panel = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        var respec = WorthRespec(selected.Pool);
        var steps = respec?.Add ?? r.Daevanion.Steps.Where(x => x.Pool == selected.Pool).ToList();
        var unspent = r.Daevanion.Budgets.FirstOrDefault(x => x.Pool == selected.Pool)?.Unspent ?? 0;
        panel.Children.Add(SectionHeader(respec is not null
            ? $"Après réinitialisation ({respec.Remove.Count} case(s) à retirer, +{Fmt.Pct(respec.Gain)} de valeur) · dans cet ordre"
            : unspent > 0
                ? $"Prochaines cases · réserve {PoolShort(selected.Pool)} · tes {unspent} points libres couvrent les cases « faisable »"
                : $"Prochaines cases · réserve {PoolShort(selected.Pool)} · suis la liste jusqu'à épuisement de tes points", padX: 0));
        var list = new StackPanel { Spacing = 0, Margin = new Thickness(0, 4, 0, 0) };
        const int shown = 15;
        foreach (var x in s.AllNext ? steps : steps.Take(shown))
        {
            var tag = x.Affordable ? ("faisable", Tok.Good) : x.IsPassage ? ("passage", Tok.Faint) : ("", Tok.Muted);
            list.Children.Add(NodeRow(x.Step.ToString(), Accent.Main, x, selected, tag));
        }
        if (steps.Count > shown) list.Children.Add(MoreToggle(steps.Count - shown, "cases"));
        if (steps.Count == 0)
            list.Children.Add(T("Rien à prendre : la réserve est pleine, ou ce qui reste vaut peu pour ta classe.", 12, Tok.Muted, wrap: true));
        panel.Children.Add(list);
        return panel;
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
        var panel = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        var inBudget = route.InBudget.ToList();
        var toOpen = inBudget.Where(x => !x.Node.Open).ToList();
        var inRoute = inBudget.Select(x => x.Node.Id).ToHashSet();
        var outside = r.Daevanion.Boards.Where(b => b.Pool == route.Pool)
            .SelectMany(b => b.Board.Nodes.Where(n => n.Open && !n.IsStart && !inRoute.Contains(n.Id)).Select(n => (b.Board, Node: n))).ToList();
        var later = route.Nodes.Where(x => !x.Affordable && !x.Node.Open).ToList();

        if (s.AllNext)
        {
            panel.Children.Add(SectionHeader($"Ordre complet depuis le centre · {inBudget.Count} cases pour tes {route.Budget} points, puis la suite", padX: 0));
            var all = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var x in route.Nodes)
                all.Children.Add(NodeRow(x.Step.ToString(), x.Node.Open ? Tok.Muted : Accent.Main, x, selected,
                    x.Node.Open ? ("ouverte", Tok.Faint) : x.Affordable ? ("à ouvrir", Tok.Good) : ("suite", Tok.Muted)));
            all.Children.Add(MoreToggle(0, ""));
            panel.Children.Add(all);
            return panel;
        }

        panel.Children.Add(SectionHeader($"À ouvrir pour suivre la route · {toOpen.Count} case(s), {Fmt.Num(toOpen.Sum(x => x.Cost))} points", padX: 0));
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var x in toOpen) list.Children.Add(NodeRow(x.Step.ToString(), Accent.Main, x, selected, ("à ouvrir", Tok.Good)));
        if (toOpen.Count == 0) list.Children.Add(T("Tes cases ouvertes suivent déjà la route conçue.", 12, Tok.Good).WithMargin(6, 4));
        panel.Children.Add(list);

        if (outside.Count > 0)
        {
            panel.Children.Add(SectionHeader($"Ouvertes chez toi, hors de la route · {outside.Count} case(s)", padX: 0));
            var off = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            foreach (var (board, node) in outside)
            {
                var boardId = board.Id;
                off.Children.Add(Tip(Clickable(ColumnsSpaced("34,70,*", 8, T("✕", 12, Tok.Bad, FontWeight.Bold, mono: true), T(board.Name, 12, Tok.Muted),
                        T(string.Join(", ", node.Effects.Select(e => e.Raw)), 13, Tok.Text2)),
                    () => update(st => st.Board = boardId), hover: Tok.Hover, padding: new Thickness(6, 3)), Reason(node)));
            }
            panel.Children.Add(off);
        }

        panel.Children.Add(SectionHeader($"La suite, avec tes prochains points · {later.Count} case(s)", padX: 0));
        var next = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var x in later.Take(12)) next.Children.Add(NodeRow(x.Step.ToString(), Tok.Text2, x, selected, ("suite", Tok.Muted)));
        next.Children.Add(Clickable(T("▾ ordre complet depuis le centre (pour suivre la route après une réinitialisation)", 12, Accent.Main),
            () => update(st => st.AllNext = true), hover: Tok.Hover, padding: new Thickness(6, 5)));
        panel.Children.Add(next);
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
            Tip(cell, $"{(n.Name.Length > 0 ? n.Name : n.Type)} — {db.Settings.Daevanion.NodeCost.GetValueOrDefault(n.Grade, 0)} point(s)\n" +
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
        var panel = new StackPanel();
        if (r.Pets.Collection is not { } c)
        {
            panel.Children.Add(SectionHeader("Familiers · non reliés", padX: 20));
            panel.Children.Add(PetLink());
            return panel;
        }

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
        panel.Children.Add(SectionHeader($"Familiers · {genera.Sum(g => g.Owned)}/{genera.Sum(g => g.Total)} obtenus · lus le {c.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}", actions, 20));
        panel.Children.Add(T("Chaque familier obtenu donne des stats selon son espèce et son niveau (effets de possession), cumulées sur toute la collection. " +
            "Les stats secondaires comptent dans ta répartition ; les stats principales sont déjà dans les totaux du site officiel.",
            12, Tok.Text2, wrap: true).WithLineHeight(1.45).WithMargin(20, 10, 20, 8));

        const string cols = "100,80,110,*,150";
        panel.Children.Add(TableHeader(cols, "Espèce", "Obtenus", "Niv. 1 · 2 · 3", "Stats données", "Intérêt"));
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
            panel.Children.Add(TableRow(Columns(cols, T(g.Name, 13), T($"{g.Owned}/{g.Total}", 13, mono: true),
                    T($"{g.Counts[1]} · {g.Counts[2]} · {g.Counts[3]}", 13, Tok.Text2, mono: true), T(now.Length > 0 ? now : "—", 12, Tok.Text2), interest),
                g.Key == selected, () => update(st => st.Genus = g.Key)));
        }

        if (best is not null)
        {
            panel.Children.Add(SectionHeader($"À farmer en premier · {best.Name} · les plus proches du niveau suivant", padX: 20, topBorder: true));
            const string pcols = "240,100,*";
            panel.Children.Add(TableHeader(pcols, "Familier", "Niveau", "Âmes manquantes"));
            foreach (var x in best.Next.Take(12))
                panel.Children.Add(TableRow(Columns(pcols, T(x.Pet.Name, 13),
                        T(x.Level == 0 ? "à obtenir" : $"{x.Level} → {x.Level + 1}", 12, x.Level == 0 ? Tok.Warn : Tok.Text2, mono: true),
                        T($"{x.Missing}  ({x.Souls}/{c.Tiers[x.Level]})", 12, Tok.Text2, mono: true)),
                    false, () => update(st => st.Genus = best.Key)));
        }
        panel.Children.Add(T("Non compté : les stats tirées par l'analyse de perception d'espèce (ni le site officiel ni le Pet Meter ne les lisent).",
            11, Tok.Faint, wrap: true).WithMargin(20, 12, 20, 16));
        return panel;
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
            Spacing = 12, Margin = new Thickness(20, 16),
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

    Control Details()
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionHeader($"Problèmes · {Mode}", T(r.Findings.Count.ToString(), 11, Tok.Muted, mono: true), 20));
        foreach (var group in r.Findings.GroupBy(f => f.Area).OrderBy(g => g.Key))
        {
            var collapsed = s.Collapsed.Contains(group.Key);
            var header = Clickable(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { T($"{(collapsed ? "▸" : "▾")} {Fmt.Area(group.Key)}", 12, weight: FontWeight.SemiBold), T(group.Count().ToString(), 12, Tok.Muted, mono: true) },
                },
                () => update(st => { if (!st.Collapsed.Remove(group.Key)) st.Collapsed.Add(group.Key); }), Tok.Frame, padding: new Thickness(20, 8));
            header.BorderBrush = Tok.Border;
            header.BorderThickness = new Thickness(0, 0, 0, 1);
            panel.Children.Add(header);
            if (collapsed) continue;
            foreach (var f in group)
            {
                var (icon, color) = Sev(f.Severity);
                var iconText = T(icon, 13, color, FontWeight.SemiBold, mono: true);
                iconText.VerticalAlignment = VerticalAlignment.Top;
                var text = new StackPanel { Spacing = 3, Children = { T(f.Title, 13, weight: FontWeight.Medium, wrap: true) } };
                foreach (var line in f.Detail.Split('\n')) text.Children.Add(T(line, 12, Tok.Muted, wrap: true).WithLineHeight(1.45));
                var action = T(f.Action is null ? "" : $"→ {f.Action}", 12, Tok.Text2, wrap: true).WithLineHeight(1.45);
                action.VerticalAlignment = VerticalAlignment.Top;
                var row = Clickable(Columns("22,*,*", iconText, text, action), () => Go(f), hover: Tok.Hover, padding: new Thickness(36, 9, 20, 9));
                row.BorderBrush = Tok.RowLine;
                row.BorderThickness = new Thickness(0, 0, 0, 1);
                panel.Children.Add(row);
            }
        }
        return panel;
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
