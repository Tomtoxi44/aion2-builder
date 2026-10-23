using System.Diagnostics;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;
using Aion2Builder.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Dernières saisies, pour rouvrir l'application sur le même personnage, le même écran et le même thème.</summary>
sealed record UiPrefs(string Region = "eu", string Server = "", string Name = "", GameMode Mode = GameMode.PvE, string? LastFile = null,
    int Tab = 0, string Screen = "Home", string Accent = "Orange")
{
    static string PathOf => System.IO.Path.Combine(AppPaths.Root, "ui.json");

    public static UiPrefs Load()
    {
        try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(PathOf), GameDataStore.Json) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, GameDataStore.Json));
        }
        catch (IOException) { /* préférence non vitale */ }
    }
}

/// <summary>
/// Fenêtre « IDE » : barre de titre (menus, thème, JcE | JcJ), rail d'activité avec les scores, contenu + inspecteur,
/// barre d'état. L'écran d'import remplace l'ancienne barre d'outils.
/// </summary>
public sealed class MainWindow : Window
{
    readonly CharacterStore store = new();
    readonly ServerCache serverCache = new();
    readonly UiState state = new();
    GameDatabase db;
    UiPrefs prefs = UiPrefs.Load();
    CharacterSnapshot? current;
    PlayerInputs inputs = new();
    AdviceReport? report;
    string status = "";
    bool statusError;
    string dataSource = "données par défaut";
    TextBlock? statusText;

    // Écran d'import (gardé d'un rendu à l'autre pour ne pas perdre la saisie).
    readonly TextBox search = new()
    {
        FontFamily = Fonts.Mono, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        PlaceholderText = "Nom du personnage", Padding = new Thickness(0), MinHeight = 0, Classes = { "bare" },
    };
    readonly ComboBox region = new() { ItemsSource = ArmoryClient.Regions, MinWidth = 180 };
    readonly AutoCompleteBox server = new()
    {
        PlaceholderText = "tous serveurs", Width = 180, MinimumPrefixLength = 0, MaxDropDownHeight = 300,
        FilterMode = AutoCompleteFilterMode.Contains, ItemTemplate = new FuncDataTemplate<ServerInfo>((s, _) => ServerItem(s)),
    };
    readonly StackPanel hitsPanel = new();
    readonly StackPanel recentPanel = new();
    readonly TextBlock scopeTag = T("", 11, Tok.Muted, mono: true);
    Control? importView;
    List<SearchHit> hits = [];
    bool searching;
    CancellationTokenSource? searchCts;

    public MainWindow()
    {
        Title = "Aion 2 Builder";
        Width = 1280;
        Height = 860;
        MinWidth = 1180;
        MinHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Tok.Window;
        Foreground = Tok.Text;
        FontFamily = Fonts.Sans;
        AddBareTextBoxStyles();

        db = LoadData();
        region.SelectedItem = ArmoryClient.Regions.Contains(prefs.Region) ? prefs.Region : "eu";
        server.Text = prefs.Server;
        search.Text = prefs.Name;
        server.ItemsSource = serverCache.Load(RegionCode);
        UpdateScopeTag();

        search.TextChanged += (_, _) => ScheduleSearch();
        search.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await ImportBestAsync(); }
            else if (e.Key == Key.Escape && report is not null) { e.Handled = true; Navigate(Screen.Home); }
        };
        region.SelectionChanged += async (_, _) => { server.Text = ""; UpdateScopeTag(); SavePrefs(); await LoadServersAsync(force: false); ScheduleSearch(); };
        server.TextChanged += (_, _) => { UpdateScopeTag(); ScheduleSearch(); };
        server.GotFocus += (_, _) => server.IsDropDownOpen = true;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; ShowImport(); }
        };
        Opened += async (_, _) => await LoadServersAsync(force: false);

        var last = prefs.LastFile is not null ? store.List().FirstOrDefault(s => s.Path == prefs.LastFile) : null;
        if (last is not null && TryLoad(last.Path) is { } snapshot)
        {
            state.Screen = Enum.TryParse<Screen>(prefs.Screen, out var screen) ? screen : Screen.Home;
            Open(snapshot, keepScreen: true);
        }
        else
        {
            state.Screen = Screen.Import;
            Render();
        }
    }

    string RegionCode => (string?)region.SelectedItem ?? "eu";
    string? ServerFilter => string.IsNullOrWhiteSpace(server.Text) ? null : server.Text.Trim();

    /// <summary>Champ de recherche sans cadre ni fond, même au survol et au focus (palette d'import).</summary>
    void AddBareTextBoxStyles()
    {
        foreach (var pseudo in new[] { "", ":pointerover", ":focus" })
            Styles.Add(new Style(x => (pseudo.Length == 0 ? x.OfType<TextBox>().Class("bare") : x.OfType<TextBox>().Class("bare").Class(pseudo))
                .Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, Brushes.Transparent),
                    new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                },
            });
    }

    // ----- Données, personnage, rapport -----

    GameDatabase LoadData()
    {
        try
        {
            var data = GameDataStore.Load();
            var custom = GameDataStore.Sources().Where(s => s.Overridden).Select(s => s.File).ToList();
            dataSource = custom.Count == 0 ? "données par défaut" : $"données personnalisées ({string.Join(", ", custom)})";
            return data;
        }
        catch (InvalidDataException ex)
        {
            SetStatus(ex.Message + " — données par défaut utilisées.", error: true);
            dataSource = "données par défaut";
            return GameDataStore.Load(overrideDir: System.IO.Path.Combine(AppPaths.Root, "__defaults_only__"));
        }
    }

    CharacterSnapshot? TryLoad(string path)
    {
        try { return store.Load(path); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            SetStatus($"Impossible de lire {path} : {ex.Message}", error: true);
            return null;
        }
    }

    void Open(CharacterSnapshot snapshot, bool keepScreen = false)
    {
        current = snapshot;
        inputs = store.LoadInputs(snapshot);
        state.Slot = state.Stat = state.Skill = null;
        state.Board = null;
        if (!keepScreen || state.Screen == Screen.Import) state.Screen = Screen.Home;
        Recompute();
    }

    void Recompute()
    {
        if (current is null) { Render(); return; }
        try { report = new Advisor(db).Analyze(current, prefs.Mode, inputs); }
        catch (InvalidOperationException ex) { report = null; SetStatus(ex.Message, error: true); state.Screen = Screen.Import; }
        Render();
    }

    void Update(Action<UiState> change)
    {
        change(state);
        prefs = prefs with { Screen = state.Screen.ToString() };
        prefs.Save();
        Render();
    }

    void Navigate(Screen screen) => Update(st => st.Screen = screen);

    void ShowImport()
    {
        Navigate(Screen.Import);
        search.Focus();
        search.SelectAll();
    }

    void OnInputs(PlayerInputs updated)
    {
        if (current is null) return;
        inputs = updated;
        store.SaveInputs(current, updated);
        Recompute();
    }

    void SetMode(GameMode mode)
    {
        if (prefs.Mode == mode) return;
        prefs = prefs with { Mode = mode };
        prefs.Save();
        Recompute();
    }

    void SetAccent(string name)
    {
        prefs = prefs with { Accent = name };
        prefs.Save();
        importView = null; // ses couleurs dépendent du thème
        Render();
    }

    void SavePrefs()
    {
        prefs = prefs with { Region = RegionCode, Server = server.Text ?? "", Name = search.Text ?? "" };
        prefs.Save();
    }

    void SetStatus(string text, bool error = false)
    {
        status = text;
        statusError = error;
        if (statusText is not null) statusText.Text = StatusMessage;
    }

    string StatusMessage => status.Length == 0 ? "" : statusError ? $"⚠ {status}" : status;

    // ----- Rendu -----

    void Render()
    {
        Accent = AccentTheme.Find(prefs.Accent);
        var root = new Grid { RowDefinitions = new RowDefinitions("36,*,24") };
        var title = TitleBar();
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*") };
        var rail = Rail();
        var main = MainArea();
        Grid.SetColumn(main, 1);
        middle.Children.Add(rail);
        middle.Children.Add(main);
        Grid.SetRow(middle, 1);
        var bar = StatusBar();
        Grid.SetRow(bar, 2);
        root.Children.Add(title);
        root.Children.Add(middle);
        root.Children.Add(bar);
        Content = root;
    }

    Control TitleBar()
    {
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                T("Aion 2 Builder", 13, Tok.Text, FontWeight.SemiBold),
                Menu("Fichier", ("Importer un personnage…  Ctrl+K", ShowImport), ("Quitter", Close)),
                Menu("Personnage", ("Actualiser depuis le site officiel", () => _ = RefreshAsync()), ("Changer de personnage", ShowImport)),
                Menu("Données", ("Recharger les données", ReloadData), ("Ouvrir le dossier des données", OpenDataFolder)),
                Menu("Aide", ("Comment c'est calculé ?", () => Update(st => { st.Screen = report is null ? Screen.Import : Screen.Home; st.HomeHow = true; }))),
            },
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { ThemeButton(), T("mode", 12, Tok.Muted, mono: true), ModeSwitch() },
        };
        DockPanel.SetDock(right, Dock.Right);
        return new Border
        {
            Background = Tok.Chrome, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 0),
            Child = new DockPanel { Children = { right, left } },
        };
    }

    static TextBlock Menu(string label, params (string Header, Action Do)[] items)
    {
        var text = T(label, 13, Tok.Muted);
        text.Cursor = new Cursor(StandardCursorType.Hand);
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ItemsSource = items.Select(i =>
            {
                var item = new MenuItem { Header = i.Header };
                item.Click += (_, _) => i.Do();
                return item;
            }).ToList(),
        };
        FlyoutBase.SetAttachedFlyout(text, flyout);
        text.PointerPressed += (_, _) => FlyoutBase.ShowAttachedFlyout(text);
        text.PointerEntered += (_, _) => text.Foreground = Tok.Text;
        text.PointerExited += (_, _) => text.Foreground = Tok.Muted;
        return text;
    }

    Control ThemeButton()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        for (var i = 0; i < AccentTheme.All.Length; i++)
        {
            var theme = AccentTheme.All[i];
            var dot = new Panel { Width = 32, Height = 32 };
            if (theme.Name == Accent.Name) dot.Children.Add(new Ellipse { Width = 32, Height = 32, Stroke = Tok.Text, StrokeThickness = 2 });
            dot.Children.Add(new Ellipse { Width = 28, Height = 28, Fill = theme.Main });
            var swatch = Clickable(new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { dot, T(theme.Name, 10, Tok.Text2).Centered() } },
                () => { flyout.Hide(); SetAccent(theme.Name); });
            Grid.SetColumn(swatch, i % 4);
            Grid.SetRow(swatch, i / 4);
            grid.Children.Add(swatch);
        }
        flyout.Content = new StackPanel { Width = 190, Spacing = 8, Children = { Label("Couleur secondaire"), grid } };

        var button = Clickable(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { new Ellipse { Width = 10, Height = 10, Fill = Accent.Main }, T("thème", 12, Tok.Muted, mono: true) },
            },
            () => { }, hover: Tok.HoverStrong, padding: new Thickness(9, 3));
        button.BorderBrush = Tok.Border;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(4);
        FlyoutBase.SetAttachedFlyout(button, flyout);
        button.PointerPressed += (_, _) => FlyoutBase.ShowAttachedFlyout(button);
        return button;
    }

    Control ModeSwitch()
    {
        Border Segment(string text, GameMode mode)
        {
            var on = prefs.Mode == mode;
            var seg = Clickable(T(text, 12, on ? Tok.OnAccent : Tok.Muted, FontWeight.SemiBold, mono: true), () => SetMode(mode),
                on ? Accent.Main : null, padding: new Thickness(12, 3));
            seg.CornerRadius = new CornerRadius(3);
            return seg;
        }
        return new Border
        {
            Background = Tok.Window, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Segment("JcE", GameMode.PvE), Segment("JcJ", GameMode.PvP) } },
        };
    }

    Control Rail()
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        var initial = current is null ? "+" : current.Name[..1].ToUpperInvariant();
        var avatar = Clickable(T(initial, 14, Accent.Main, FontWeight.SemiBold, mono: true).Centered(), ShowImport, Tok.Border);
        avatar.Width = avatar.Height = 36;
        avatar.CornerRadius = new CornerRadius(6);
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        avatar.Margin = new Thickness(0, 0, 0, 10);
        ToolTip.SetTip(avatar, "Importer ou changer de personnage (Ctrl+K)");
        panel.Children.Add(avatar);

        foreach (var (screen, title, shortName) in ScreenViews.Nav)
        {
            var on = state.Screen == screen;
            var score = report is null ? null : ScreenViews.ScoreOf(report, screen);
            var entry = Clickable(new StackPanel
                {
                    Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        T(score?.ToString() ?? "···", 13, score is { } v ? Tok.Score(v) : Tok.Muted, FontWeight.SemiBold, mono: true).Centered(),
                        T(shortName, 10, on ? Tok.Text : Tok.Muted).Centered(),
                    },
                },
                () => { if (report is not null) Navigate(screen); }, padding: new Thickness(0, 8));
            entry.Width = 52;
            entry.BorderBrush = on ? Accent.Main : Brushes.Transparent;
            entry.BorderThickness = new Thickness(2, 0, 0, 0);
            ToolTip.SetTip(entry, title);
            panel.Children.Add(entry);
        }
        return new Border { Background = Tok.Chrome, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    Control MainArea()
    {
        if (state.Screen == Screen.Import || report is null) return ImportHost();

        var grid = new Grid { RowDefinitions = new RowDefinitions("36,*") };
        grid.Children.Add(Breadcrumb(report));
        var (content, inspector) = new ScreenViews(db, report, inputs, state, Update, OnInputs).Build();
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions(inspector is null ? "*" : "*,340") };
        body.Children.Add(Scroll(content));
        if (inspector is not null)
        {
            var side = new Border { Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1, 0, 0, 0), Child = Scroll(inspector) };
            Grid.SetColumn(side, 1);
            body.Children.Add(side);
        }
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    Control Breadcrumb(AdviceReport r)
    {
        var c = r.Character;
        TextBlock Value(string label, string value) => new()
        {
            FontFamily = Fonts.Mono, FontSize = 12, Foreground = Tok.Muted, VerticalAlignment = VerticalAlignment.Center,
            Inlines = new Avalonia.Controls.Documents.InlineCollection
            {
                new Avalonia.Controls.Documents.Run(label + " "),
                new Avalonia.Controls.Documents.Run(value) { Foreground = Tok.Text, FontWeight = FontWeight.Bold },
            },
        };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                T($"{r.Profile.Class.Name} · {r.Profile.Role.Name} · {c.Level}", 12, Tok.Muted, mono: true),
                T($"{c.ServerName} ({c.Region})", 12, Tok.Muted, mono: true),
                Value("obj", c.ItemLevel.ToString()), Value("pc", c.CombatPower.ToString()),
            },
        };
        DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { T(c.Name.ToLowerInvariant(), 12, Tok.Muted, mono: true), T("›", 12, Tok.Muted, mono: true), T(ScreenViews.Title(state.Screen), 12, Tok.Text, mono: true) },
        };
        return new Border
        {
            Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 0),
            Child = new DockPanel { Children = { right, left } },
        };
    }

    Control StatusBar()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        if (current is null) left.Children.Add(T("● aucun personnage", 11, Tok.StatusText, mono: true));
        else
        {
            left.Children.Add(T($"● {current.Name} · {current.ServerName}", 11, Tok.StatusText, mono: true));
            left.Children.Add(T(ScreenViews.ModeLabel(prefs.Mode), 11, Tok.StatusText, mono: true));
            if (report is not null) left.Children.Add(T($"{ScreenViews.Todo(report)} pièces à traiter", 11, Tok.StatusText, mono: true));
        }
        statusText = T(StatusMessage, 11, Tok.StatusText, mono: true);
        left.Children.Add(statusText);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        if (current is not null) right.Children.Add(T($"importé {current.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}", 11, Tok.StatusText, mono: true));
        right.Children.Add(T(dataSource, 11, Tok.StatusText, mono: true));
        DockPanel.SetDock(right, Dock.Right);
        return new Border { Background = Accent.Bar, Padding = new Thickness(12, 0), Child = new DockPanel { Children = { right, left } } };
    }

    // ----- Import -----

    Control ImportHost()
    {
        importView ??= BuildImport();
        if (importView.Parent is Border previous) previous.Child = null;
        RenderHits();
        RenderRecents();
        return new Border { Child = importView };
    }

    Control BuildImport()
    {
        var prompt = T(">", 13, Accent.Main, mono: true);
        prompt.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(prompt, Dock.Left);

        var tag = Clickable(scopeTag, () => { }, padding: new Thickness(6, 2));
        tag.BorderBrush = Tok.Border;
        tag.BorderThickness = new Thickness(1);
        tag.CornerRadius = new CornerRadius(3);
        tag.Margin = new Thickness(8, 0, 0, 0);
        var scopeFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel { Spacing = 8, Width = 200, Children = { Label("Région"), region, Label("Serveur (vide = tous)"), server } },
        };
        FlyoutBase.SetAttachedFlyout(tag, scopeFlyout);
        tag.PointerPressed += (_, _) => FlyoutBase.ShowAttachedFlyout(tag);
        DockPanel.SetDock(tag, Dock.Right);

        if (search.Parent is DockPanel oldRow) oldRow.Children.Remove(search);
        var input = new Border
        {
            Padding = new Thickness(12, 10), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { prompt, tag, search } },
        };

        var palette = new Border
        {
            Width = 620, Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            BoxShadow = BoxShadows.Parse("0 12 40 0 #80000000"), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    input,
                    Label("Site officiel").WithMargin(12, 8, 12, 4), hitsPanel,
                    Label("Récents").WithMargin(12, 8, 12, 4), recentPanel,
                    new Border { Height = 6 },
                },
            },
        };
        var hint = T("Entrée pour importer · Ctrl+K pour rouvrir", 12, Tok.Muted, mono: true).Centered();
        hint.Margin = new Thickness(0, 18, 0, 0);

        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(new GridLength(14, GridUnitType.Star)), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(new GridLength(86, GridUnitType.Star)),
            },
        };
        Grid.SetRow(palette, 1);
        Grid.SetRow(hint, 2);
        grid.Children.Add(palette);
        grid.Children.Add(hint);
        return grid;
    }

    void UpdateScopeTag() => scopeTag.Text = $"{RegionCode} · {ServerFilter ?? "tous serveurs"}";

    static Control ServerItem(ServerInfo? s) => new DockPanel
    {
        Children =
        {
            new TextBlock { Text = s?.Race ?? "", FontSize = 11, Foreground = Tok.Muted, Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
            new TextBlock { Text = s?.Name ?? "", VerticalAlignment = VerticalAlignment.Center },
        },
    };

    void RenderHits()
    {
        hitsPanel.Children.Clear();
        var text = search.Text?.Trim() ?? "";
        if (searching) { hitsPanel.Children.Add(T("recherche…", 13, Tok.Muted).WithMargin(12, 7)); return; }
        if (text.Length < 2) { hitsPanel.Children.Add(T("tape au moins 2 lettres du nom", 13, Tok.Faint).WithMargin(12, 7)); return; }
        if (hits.Count == 0) { hitsPanel.Children.Add(T("aucun résultat", 13, Tok.Faint).WithMargin(12, 7)); return; }
        foreach (var h in hits.Take(8))
        {
            var level = T(h.Level.ToString(), 13, Tok.Muted, mono: true);
            level.HorizontalAlignment = HorizontalAlignment.Right;
            hitsPanel.Children.Add(Clickable(Columns("*,120,100,60",
                    T(h.Name, 13, weight: FontWeight.SemiBold), T(h.ClassCode is null ? "?" : db.FindClass(h.ClassCode)?.Name ?? h.ClassCode, 13, Tok.Muted),
                    T(h.ServerName, 13, Tok.Muted), level),
                () => _ = ImportAsync(h.Name, (client, progress) => client.Fetch(db, RegionCode, h.ServerId, h.CharacterId, progress)),
                hover: Tok.HoverStrong, padding: new Thickness(12, 7)));
        }
    }

    void RenderRecents()
    {
        recentPanel.Children.Clear();
        var list = store.List();
        if (list.Count == 0) { recentPanel.Children.Add(T("aucun personnage enregistré", 13, Tok.Faint).WithMargin(12, 7)); return; }
        foreach (var s in list.Take(6))
        {
            var date = T(s.FetchedAtUtc.ToLocalTime().ToString("dd/MM"), 13, Tok.Muted, mono: true);
            DockPanel.SetDock(date, Dock.Right);
            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { T(s.Name, 13), T($"— {db.FindClass(s.ClassCode)?.Name ?? s.ClassCode} · {s.ServerName} ({s.Region})", 13, Tok.Muted) },
            };
            var saved = s;
            recentPanel.Children.Add(Clickable(new DockPanel { Children = { date, name } }, () => OpenSaved(saved), hover: Tok.HoverStrong, padding: new Thickness(12, 7)));
        }
    }

    async void ScheduleSearch()
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        var text = search.Text?.Trim() ?? "";
        if (text.Length < 2)
        {
            hits = [];
            searching = false;
            RenderHits();
            return;
        }
        try
        {
            await Task.Delay(400, cts.Token);
            searching = true;
            RenderHits();
            using var client = ArmoryClient.Create();
            var found = await client.Search(RegionCode, text, ServerFilter, cts.Token);
            if (!cts.IsCancellationRequested) hits = found;
        }
        catch (OperationCanceledException) { return; }
        catch (ArmoryException ex)
        {
            hits = [];
            SetStatus(ex.Message, error: true);
        }
        if (cts != searchCts) return;
        searching = false;
        RenderHits();
    }

    async Task ImportBestAsync()
    {
        var name = search.Text?.Trim() ?? "";
        if (name.Length == 0) { SetStatus("Indique le nom du personnage.", error: true); return; }
        var best = hits.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? hits.FirstOrDefault();
        if (best is not null)
            await ImportAsync(best.Name, (client, progress) => client.Fetch(db, RegionCode, best.ServerId, best.CharacterId, progress));
        else
            await ImportAsync(name, async (client, progress) => (await client.FetchByName(db, RegionCode, name, ServerFilter, progress)).Snapshot);
    }

    async Task RefreshAsync()
    {
        if (current is null) { ShowImport(); return; }
        var c = current;
        await ImportAsync(c.Name, (client, progress) => client.Fetch(db, c.Region, c.ServerId, c.CharacterId, progress));
    }

    async Task ImportAsync(string name, Func<ArmoryClient, IProgress<string>, Task<CharacterSnapshot>> fetch)
    {
        SetStatus($"Import de {name}…");
        try
        {
            using var client = ArmoryClient.Create();
            var snapshot = await fetch(client, new Progress<string>(m => SetStatus(m)));
            var path = store.Save(snapshot);
            prefs = prefs with { LastFile = path, Name = search.Text ?? snapshot.Name, Region = snapshot.Region };
            prefs.Save();
            SetStatus($"importé depuis le site officiel");
            Open(snapshot);
        }
        catch (ArmoryException ex) { SetStatus(ex.Message, error: true); }
    }

    void OpenSaved(SavedCharacter s)
    {
        if (TryLoad(s.Path) is not { } snapshot) return;
        prefs = prefs with { LastFile = s.Path };
        prefs.Save();
        SetStatus("");
        Open(snapshot);
    }

    async Task LoadServersAsync(bool force)
    {
        var code = RegionCode;
        server.ItemsSource = serverCache.Load(code);
        if (!force && !serverCache.IsStale(code)) return;
        try
        {
            using var client = ArmoryClient.Create();
            var list = await serverCache.Get(client, code, force);
            if (RegionCode == code) server.ItemsSource = list;
        }
        catch (ArmoryException ex) { SetStatus($"Liste des serveurs indisponible : {ex.Message}", error: true); }
    }

    void ReloadData()
    {
        db = LoadData();
        SetStatus("données rechargées");
        Recompute();
        _ = LoadServersAsync(force: true);
    }

    void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        if (GameDataStore.Sources().All(s => !s.Overridden)) GameDataStore.ExportDefaults();
        try { Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDir, UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus($"Dossier des données : {AppPaths.DataDir}");
        }
    }
}
