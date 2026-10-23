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

enum Screen { Import, Home, Stats, Gear, Skills, Daeva, Pets, Details }

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
        (Screen.Skills, "Compétences", "Compét."), (Screen.Daeva, "Daevanion", "Daeva."), (Screen.Pets, "Familiers", "Famil."),
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

        var placeholder = Placeholder();
        Grid.SetRow(placeholder, 0);
        Grid.SetRowSpan(placeholder, 6);
        Grid.SetColumn(placeholder, 1);
        Grid.SetColumnSpan(placeholder, 2);
        grid.Children.Add(placeholder);

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

    static Control Placeholder()
    {
        var hatch = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(14.14, 14.14, RelativeUnit.Absolute),
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops =
            {
                new GradientStop(Color.Parse("#18191C"), 0), new GradientStop(Color.Parse("#18191C"), 0.5),
                new GradientStop(Color.Parse("#151619"), 0.5), new GradientStop(Color.Parse("#151619"), 1),
            },
        };
        return new Panel
        {
            Children =
            {
                new Border { Background = hatch, CornerRadius = new CornerRadius(4) },
                new Rectangle { Stroke = Tok.Dashed, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 4, 3 }, RadiusX = 4, RadiusY = 4 },
                T("rendu du personnage", 12, Tok.Faint, mono: true).Centered(),
            },
        };
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
        var panel = new StackPanel();
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

        var all = r.Findings.Count;
        panel.Children.Add(SectionHeader($"Top 3 · {Mode}", Clickable(T($"tout voir ({all})", 11, Accent.Main), () => GoTo(Screen.Details))));
        foreach (var f in r.ActionPlan.Take(3))
            panel.Children.Add(ActionRow(f));

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

        const string cols = "80,*,140,60,60,60,110";
        panel.Children.Add(TableHeader(cols, "Type", "Compétence", "Rôle", "Acheté", "Bonus", "Total", "Conseil"));
        var selected = SelectedSkillName;
        foreach (var a in sk.Skills)
        {
            var kind = a.Kind == SkillKind.Passive ? "Passive" : a.InBar ? "Active ✓" : "Active";
            Control delta = a.Delta == 0 ? T("=", 12, Tok.Muted, mono: true) : Pill(DeltaText(a), DeltaColor(a));
            panel.Children.Add(TableRow(Columns(cols,
                    T(kind, 12, Tok.Muted), T(a.Skill.Name, 13, a.Skill.Acquired ? null : Tok.Muted), T(a.Role, 13, Tok.Text2),
                    T(a.Base.ToString(), 13, mono: true), T($"+{a.Bonus}", 13, Tok.Muted, mono: true), T(a.Total.ToString(), 13, mono: true), delta),
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
                ("conseil", a.Delta == 0 ? "rien à changer" : DeltaText(a), DeltaColor(a)),
                ("priorité", a.Priority.ToString("0.00"), null),
            ]));
            body.Children.Add(Field("Pourquoi", a.Why, a.Unverified ? Tok.Warn : null));
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
        var tabBar = new Border { Height = 34, Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs };
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
                new TextBlock { FontSize = 12, Foreground = Tok.Muted, Inlines = new InlineCollection { new Run("✕") { Foreground = Tok.Bad, FontWeight = FontWeight.Bold }, new Run(" à retirer") } },
                T("coût : gris 1 · vert 2 · bleu 3 · orange 4", 12, Tok.Muted),
            },
        };
        info.Children.Add(new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 4, 0, 0), Child = legend });

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28, Margin = new Thickness(20), Children = { BoardGrid(selected), info } };
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>Grille 15 × 15 : case ouverte pleine, fermée en contour, à prendre numérotée, à retirer barrée.</summary>
    Control BoardGrid(BoardSummary summary)
    {
        const int size = 26, gap = 2;
        var board = summary.Board;
        var respec = WorthRespec(summary.Pool);
        var numbers = respec is not null
            ? respec.Add.Where(x => x.Board.Id == board.Id).ToDictionary(x => x.Node.Id, x => x.Step)
            : r.Daevanion.Steps.Where(x => x.Board.Id == board.Id).ToDictionary(x => x.Node.Id, x => x.Step);
        var removed = respec?.Remove.Where(x => x.Board.Id == board.Id).Select(x => x.Node.Id).ToHashSet() ?? [];
        var canvas = new Canvas { Width = 15 * (size + gap) - gap, Height = 15 * (size + gap) - gap };
        foreach (var n in board.Nodes)
        {
            var open = n.Open || n.IsStart;
            var grade = n.IsStart ? Brushes.White : Tok.B(db.Grade(n.Grade).Color);
            var isRemoved = removed.Contains(n.Id);
            int? step = numbers.TryGetValue(n.Id, out var st) ? st : null;
            var cell = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(n.IsSkill ? size / 2.0 : 3),
                Background = isRemoved ? Tok.CellOff : open ? grade : Tok.CellOff,
                BorderBrush = isRemoved ? Tok.Bad : step is not null ? Accent.Main : open ? grade : Tok.Alpha(grade, 0.33),
                BorderThickness = new Thickness(isRemoved || step is not null ? 2 : 1),
                Opacity = open || step is not null || isRemoved ? 1 : 0.55,
                Child = isRemoved ? T("✕", 11, Tok.Bad, FontWeight.SemiBold, mono: true).Centered()
                    : step is null ? null : T(step.ToString()!, 11, Accent.Main, FontWeight.SemiBold, mono: true).Centered(),
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

        var steps = r.Daevanion.Steps.Where(x => x.Pool == board.Pool).ToList();
        if (steps.Count > 0)
        {
            panel.Children.Add(SectionHeader("Prochaines cases", topBorder: true));
            var lines = new StackPanel { Spacing = 6, Margin = new Thickness(14, 10) };
            foreach (var x in steps)
                lines.Children.Add(Line(x.Step.ToString(), Accent.Main, $"{x.Board.Name} l.{x.Node.Row} c.{x.Node.Col} — {x.Effects}",
                    x.IsPassage ? Tok.Muted : x.Affordable ? Tok.Good : null, x.Affordable ? "faisable maintenant" : x.IsPassage ? "passage" : null));
            panel.Children.Add(lines);
        }
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
