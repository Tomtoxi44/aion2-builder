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
using Avalonia.Threading;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Dernières saisies, pour rouvrir l'application sur les mêmes personnages (onglets), le même écran, le même thème
/// et la même disposition (largeur de l'inspecteur).
/// </summary>
sealed record UiPrefs(string Region = "eu", string Server = "", string Name = "", GameMode Mode = GameMode.PvE, string? LastFile = null,
    int Tab = 0, string Screen = "Home", string Accent = "Orange", bool AutoUpdate = true,
    List<string>? Tabs = null, double InspectorWidth = 340, bool InspectorHidden = false, int AutoRefreshHours = 0)
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
    readonly PetStore petStore = new();
    readonly UiState state = new();
    GameDatabase db;
    UiPrefs prefs = UiPrefs.Load();
    CharacterSnapshot? current;
    PlayerInputs inputs = new();
    PetStore.Saved pets = new(null, null);
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
    readonly StackPanel historyPanel = new();
    readonly TextBlock historyCount = T("", 11, Tok.Muted, mono: true);
    readonly TextBlock scopeTag = T("", 11, Tok.Muted, mono: true);
    Control? importView;
    List<SearchHit> hits = [];
    bool searching;
    CancellationTokenSource? searchCts;
    // Import en cours : la ligne du personnage cliqué tourne et affiche l'étape (« Détails 5/30… ») ; un seul import à la fois.
    string? loading;
    string loadStep = "";
    TextBlock? loadText;
    string? importError;
    // Mise à jour proposée par Aion 2 Todo (vérifiée au lancement puis toutes les 6 h).
    Updater.Release? update;
    bool installing;
    // Onglets : les personnages ouverts, comme les fichiers d'un éditeur ; leurs fiches restent en mémoire.
    readonly List<string> tabs = [];
    readonly Dictionary<string, CharacterSnapshot> opened = [];
    string? currentPath;
    // Fenêtre par-dessus l'interface : raccourcis clavier (F1) ou paramètres (Ctrl+,).
    enum Overlay { None, Shortcuts, Settings }
    Overlay overlay;
    // Actualisation automatique : une seule tentative par personnage et par session.
    readonly HashSet<string> autoRefreshed = [];
    List<ProgressPoint> progress = [];

    public MainWindow()
    {
        Title = "Aion 2 Builder";
        if (typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico") is { } icon) Icon = new WindowIcon(icon);
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
        pets = petStore.Load();
        region.SelectedItem = ArmoryClient.Regions.Contains(prefs.Region) ? prefs.Region : "eu";
        server.Text = prefs.Server;
        search.Text = prefs.Name;
        server.ItemsSource = serverCache.Load(RegionCode);
        UpdateScopeTag();

        search.TextChanged += (_, _) => { ScheduleSearch(); RenderHistory(); };
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
            if (e.Key == Key.F1) { e.Handled = true; ShowOverlay(Overlay.Shortcuts); return; }
            if (e.Key == Key.Escape && overlay != Overlay.None) { e.Handled = true; ShowOverlay(Overlay.None); return; }
            if (e.Key == Key.F5) { e.Handled = true; _ = RefreshAsync(); return; }
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            // Ctrl+1 à Ctrl+8 : les écrans du rail, dans l'ordre.
            if (e.Key is >= Key.D1 and <= Key.D9 && report is not null && e.Key - Key.D1 < ScreenViews.Nav.Length)
            {
                e.Handled = true;
                Navigate(ScreenViews.Nav[e.Key - Key.D1].Screen);
                return;
            }
            switch (e.Key)
            {
                case Key.K: e.Handled = true; ShowImport(); break;
                case Key.R: e.Handled = true; _ = RefreshAsync(); break;
                case Key.M: e.Handled = true; SetMode(prefs.Mode == GameMode.PvE ? GameMode.PvP : GameMode.PvE); break;
                case Key.OemComma: e.Handled = true; ShowOverlay(Overlay.Settings); break;
                case Key.E: e.Handled = true; _ = ExportAsync(); break;
                case Key.W when currentPath is not null: e.Handled = true; CloseTab(currentPath); break;
                case Key.Tab when tabs.Count > 1 && currentPath is not null:
                    e.Handled = true;
                    var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                    SwitchTab(tabs[(tabs.IndexOf(currentPath) + step + tabs.Count) % tabs.Count]);
                    break;
                case Key.I: e.Handled = true; ToggleInspector(); break;
            }
        };
        Opened += async (_, _) => await LoadServersAsync(force: false);
        Updater.CleanOld();
        Opened += async (_, _) => await CheckUpdateAsync(manual: false);
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => await CheckUpdateAsync(manual: false);
        updateTimer.Start();

        // Onglets de la dernière session (fichiers encore présents), puis le dernier personnage affiché.
        foreach (var path in (prefs.Tabs ?? []).Append(prefs.LastFile).OfType<string>().Distinct())
            if (File.Exists(path) && TryLoad(path) is { } saved)
            {
                tabs.Add(path);
                opened[path] = saved;
            }
        var last = prefs.LastFile is not null && opened.ContainsKey(prefs.LastFile) ? prefs.LastFile : tabs.FirstOrDefault();
        if (last is not null)
        {
            state.Screen = Enum.TryParse<Screen>(prefs.Screen, out var screen) ? screen : Screen.Home;
            Open(opened[last], last, keepScreen: true);
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

    void Open(CharacterSnapshot snapshot, string path, bool keepScreen = false)
    {
        current = snapshot;
        currentPath = path;
        opened[path] = snapshot;
        if (!tabs.Contains(path)) tabs.Add(path);
        prefs = prefs with { LastFile = path, Tabs = [.. tabs] };
        prefs.Save();
        store.MarkViewed(path);
        inputs = store.LoadInputs(snapshot);
        state.Slot = state.Stat = state.Skill = null;
        state.Board = null;
        if (!keepScreen || state.Screen == Screen.Import) state.Screen = Screen.Home;
        Recompute();
        MaybeAutoRefresh(snapshot, path);
    }

    /// <summary>Paramètre « actualiser à l'ouverture » : relit le personnage sur le site officiel s'il date de plus de N heures.</summary>
    void MaybeAutoRefresh(CharacterSnapshot snapshot, string path)
    {
        if (prefs.AutoRefreshHours <= 0 || loading is not null || !autoRefreshed.Add(path)) return;
        if (DateTime.UtcNow - snapshot.FetchedAtUtc < TimeSpan.FromHours(prefs.AutoRefreshHours)) return;
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    void ShowOverlay(Overlay which)
    {
        overlay = which;
        Render();
    }

    void Recompute()
    {
        if (current is null) { Render(); return; }
        try { report = new Advisor(db).Analyze(current, prefs.Mode, inputs, pets.Collection); }
        catch (InvalidOperationException ex) { report = null; SetStatus(ex.Message, error: true); state.Screen = Screen.Import; }
        if (report is not null) store.RecordScores(current, prefs.Mode, report.Scores);
        progress = store.Progress(current);
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

    /// <summary>Passe à un autre personnage ouvert, sur le même écran.</summary>
    void SwitchTab(string path)
    {
        if (loading is not null) return;
        if (path == currentPath && state.Screen != Screen.Import) return;
        var snapshot = opened.TryGetValue(path, out var known) ? known : TryLoad(path);
        if (snapshot is null) return;
        SetStatus("");
        Open(snapshot, path, keepScreen: true);
    }

    /// <summary>Ferme un onglet (le personnage reste dans l'historique) ; sans onglet, retour à l'import.</summary>
    void CloseTab(string path)
    {
        var index = tabs.IndexOf(path);
        if (index < 0 || loading is not null && path == currentPath) return; // l'import en cours ouvrira son personnage
        tabs.RemoveAt(index);
        opened.Remove(path);
        prefs = prefs with { Tabs = [.. tabs] };
        if (path != currentPath) { prefs.Save(); Render(); return; }
        if (tabs.Count > 0) { SwitchTab(tabs[Math.Min(index, tabs.Count - 1)]); return; }
        current = null;
        currentPath = null;
        report = null;
        prefs = prefs with { LastFile = null };
        prefs.Save();
        ShowImport();
    }

    void ToggleInspector()
    {
        prefs = prefs with { InspectorHidden = !prefs.InspectorHidden };
        prefs.Save();
        Render();
    }

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
        if (overlay != Overlay.None)
        {
            var layer = OverlayLayer();
            Grid.SetRowSpan(layer, 3);
            root.Children.Add(layer);
        }
        Content = root;
    }

    /// <summary>Carte centrée sur un voile sombre ; un clic sur le voile ou Échap la ferme.</summary>
    Control OverlayLayer()
    {
        var card = new Border
        {
            Width = 560, Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            BoxShadow = BoxShadows.Parse("0 12 40 0 #80000000"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = overlay == Overlay.Shortcuts ? ShortcutsCard() : SettingsCard(),
        };
        card.PointerPressed += (_, e) => e.Handled = true; // un clic dans la carte ne la ferme pas
        var veil = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Child = card };
        veil.PointerPressed += (_, _) => ShowOverlay(Overlay.None);
        return veil;
    }

    static readonly (string Keys, string What)[] Shortcuts =
    [
        ("Ctrl+K", "importer ou ouvrir un autre personnage"), ("Ctrl+Tab · Ctrl+Maj+Tab", "personnage suivant / précédent"),
        ("Ctrl+W", "fermer l'onglet du personnage"), ("Ctrl+1 … Ctrl+9", "écrans du rail (Accueil, Stats, Équipement…, Et si, Progression)"),
        ("Ctrl+M", "basculer JcE / JcJ"), ("Ctrl+R · F5", "actualiser depuis le site officiel"),
        ("Ctrl+I", "afficher / masquer l'inspecteur (sa largeur se règle en glissant sa bordure)"),
        ("Ctrl+E", "exporter le rapport en Markdown"), ("Ctrl+,", "paramètres"), ("F1", "cette aide"), ("Échap", "fermer"),
    ];

    Control ShortcutsCard()
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(18, 14) };
        foreach (var (keys, what) in Shortcuts)
            list.Children.Add(ColumnsSpaced("190,*", 12, T(keys, 12, Accent.Main, FontWeight.SemiBold, mono: true), T(what, 13, Tok.Text2)));
        return new StackPanel { Children = { SectionHeader("Raccourcis clavier"), list } };
    }

    Control SettingsCard()
    {
        Control Choice(string label, string hint, IEnumerable<(string Text, bool On, Action Do)> options)
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
            return new StackPanel { Spacing = 6, Children = { T(label, 13, weight: FontWeight.Medium), T(hint, 12, Tok.Muted, wrap: true).WithLineHeight(1.4), row } };
        }
        void Set(UiPrefs updated) { prefs = updated; prefs.Save(); Render(); }

        var body = new StackPanel
        {
            Spacing = 18, Margin = new Thickness(18, 14, 18, 18),
            Children =
            {
                Choice("Mises à jour", "Demande à Aion 2 Todo s'il existe une nouvelle version, au lancement puis toutes les 6 h. Le téléchargement n'a lieu qu'au clic.",
                    [("automatique", prefs.AutoUpdate, () => Set(prefs with { AutoUpdate = true })), ("à la demande", !prefs.AutoUpdate, () => Set(prefs with { AutoUpdate = false }))]),
                Choice("Actualiser à l'ouverture", "Relit le personnage sur le site officiel quand tu l'ouvres, si son import date de plus de :",
                    new[] { 0, 2, 6, 24 }.Select(h => (h == 0 ? "jamais" : $"{h} h", prefs.AutoRefreshHours == h, (Action)(() => Set(prefs with { AutoRefreshHours = h }))))),
                Choice("Inspecteur", "Le panneau de droite ; sa largeur se règle en glissant sa bordure gauche.",
                    [("affiché", !prefs.InspectorHidden, () => Set(prefs with { InspectorHidden = false })), ("masqué", prefs.InspectorHidden, () => Set(prefs with { InspectorHidden = true })),
                     ("largeur par défaut", false, () => Set(prefs with { InspectorWidth = 340 }))]),
                Choice("Données", dataSource,
                    [("ouvrir le dossier", false, OpenDataFolder), ("recharger", false, () => { ShowOverlay(Overlay.None); ReloadData(); })]),
            },
        };
        var close = Clickable(T("fermer · Échap", 11, Tok.Muted, mono: true), () => ShowOverlay(Overlay.None));
        return new StackPanel { Children = { SectionHeader("Paramètres", close), body } };
    }

    Control TitleBar()
    {
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                T("Aion 2 Builder", 13, Tok.Text, FontWeight.SemiBold),
                Menu("Fichier", ("Importer un personnage…  Ctrl+K", ShowImport), ("Exporter le rapport (Markdown)…  Ctrl+E", () => _ = ExportAsync()),
                    ("Paramètres…  Ctrl+,", () => ShowOverlay(Overlay.Settings)), ("Quitter", Close)),
                Menu("Personnage", ("Actualiser depuis le site officiel  Ctrl+R", () => _ = RefreshAsync()), ("Changer de personnage", ShowImport),
                    ("Fermer l'onglet  Ctrl+W", () => { if (currentPath is not null) CloseTab(currentPath); })),
                Menu("Données", ("Recharger les données", ReloadData), ("Actualiser les familiers (Aion 2 Todo)", () => _ = PetsAsync(null)), ("Ouvrir le dossier des données", OpenDataFolder)),
                Menu("Aide", ("Raccourcis clavier  F1", () => ShowOverlay(Overlay.Shortcuts)),
                    ("Comment c'est calculé ?", () => Update(st => { st.Screen = report is null ? Screen.Import : Screen.Home; st.HomeHow = true; })),
                    ("Vérifier les mises à jour", () => _ = CheckUpdateAsync(manual: true)),
                    ($"{(prefs.AutoUpdate ? "✓ " : "")}Vérifier automatiquement (au lancement, puis toutes les 6 h)", ToggleAutoUpdate)),
            },
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { ThemeButton(), T("mode", 12, Tok.Muted, mono: true), ModeSwitch() },
        };
        if (update is { } available)
        {
            var label = installing ? "mise à jour…" : $"⬆ {available.Label} disponible";
            var pill = Clickable(T(label, 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), () => _ = InstallUpdateAsync(), Accent.Main, padding: new Thickness(10, 3));
            pill.CornerRadius = new CornerRadius(4);
            ToolTip.SetTip(pill, $"Nouvelle version sur Aion 2 Todo (tu as {Updater.CurrentLabel}) : clic pour la télécharger et relancer l'application.");
            right.Children.Insert(0, pill);
        }
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

        // Le clic ouvre le menu lui-même : Clickable marque le clic comme traité, un second gestionnaire ne serait jamais appelé.
        Border? button = null;
        button = Clickable(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { new Ellipse { Width = 10, Height = 10, Fill = Accent.Main }, T("thème", 12, Tok.Muted, mono: true) },
            },
            () => FlyoutBase.ShowAttachedFlyout(button!), hover: Tok.HoverStrong, padding: new Thickness(9, 3));
        button.BorderBrush = Tok.Border;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(4);
        FlyoutBase.SetAttachedFlyout(button, flyout);
        ToolTip.SetTip(button, "Couleur secondaire de l'interface");
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
        var grid = new Grid { RowDefinitions = new RowDefinitions(tabs.Count > 0 ? "34,*" : "0,*") };
        if (tabs.Count > 0) grid.Children.Add(TabBar());
        var page = state.Screen == Screen.Import || report is null ? ImportHost() : Page(report);
        Grid.SetRow(page, 1);
        grid.Children.Add(page);
        return grid;
    }

    /// <summary>Fil d'Ariane, puis le contenu et l'inspecteur, séparés par une poignée qui règle la largeur de l'inspecteur.</summary>
    Control Page(AdviceReport r)
    {
        var (content, inspector) = new ScreenViews(db, r, inputs, state, Update, OnInputs, pets.KeyHint, key => _ = PetsAsync(key), progress,
            o => Simulator.Run(db, r, inputs, pets.Collection, o)).Build();
        var grid = new Grid { RowDefinitions = new RowDefinitions("36,*") };
        grid.Children.Add(Breadcrumb(r, inspector is not null));
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 420 });
        body.Children.Add(Scroll(content));
        if (inspector is not null && !prefs.InspectorHidden)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            body.ColumnDefinitions.Add(new ColumnDefinition(Math.Clamp(prefs.InspectorWidth, 240, 760), GridUnitType.Pixel) { MinWidth = 240, MaxWidth = 760 });
            var splitter = new GridSplitter { Width = 5, Background = Tok.Border, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            ToolTip.SetTip(splitter, "Glisse pour régler la largeur de l'inspecteur (Ctrl+I pour le masquer)");
            splitter.DragCompleted += (_, _) =>
            {
                prefs = prefs with { InspectorWidth = Math.Round(body.ColumnDefinitions[2].ActualWidth) };
                prefs.Save();
            };
            Grid.SetColumn(splitter, 1);
            body.Children.Add(splitter);
            var side = new Border { Background = Tok.Panel, Child = Scroll(inspector) };
            Grid.SetColumn(side, 2);
            body.Children.Add(side);
        }
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    /// <summary>
    /// Onglets des personnages ouverts, comme les fichiers d'un éditeur : initiale, nom, classe et niveau ; × ferme
    /// (Ctrl+W), Ctrl+Tab passe au suivant, + ouvre l'import (Ctrl+K).
    /// </summary>
    Control TabBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var path in tabs)
        {
            if (!opened.TryGetValue(path, out var c)) continue;
            var on = path == currentPath && state.Screen != Screen.Import;
            var className = db.FindClass(c.ClassCode)?.Name ?? c.ClassCode;
            var chip = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = on ? Accent.Dim : Tok.Border,
                Child = T(c.Name[..1].ToUpperInvariant(), 11, on ? Accent.Main : Tok.Muted, FontWeight.SemiBold, mono: true).Centered(),
            };
            var tabPath = path;
            var close = Clickable(T("×", 14, Tok.Muted).Centered(), () => CloseTab(tabPath), hover: Tok.HoverStrong, padding: new Thickness(5, 0));
            close.CornerRadius = new CornerRadius(3);
            ToolTip.SetTip(close, "Fermer l'onglet (Ctrl+W)");
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children = { chip, T(c.Name, 13, on ? Tok.Text : Tok.Text2, on ? FontWeight.SemiBold : FontWeight.Normal), T($"{className} {c.Level}", 11, Tok.Muted, mono: true), close },
            };
            var tab = Clickable(label, () => SwitchTab(tabPath), on ? Tok.Window : null, on ? null : Tok.Hover, new Thickness(10, 0, 6, 0));
            tab.BorderBrush = on ? Accent.Main : Brushes.Transparent;
            tab.BorderThickness = new Thickness(0, 2, 0, 0);
            ToolTip.SetTip(tab, $"{c.Name} — {className} niv. {c.Level} · {c.ServerName} ({c.Region}) · niveau d'objet {c.ItemLevel} · importé le {c.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}");
            row.Children.Add(new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = tab });
        }
        var plus = Clickable(T("+", 16, state.Screen == Screen.Import ? Accent.Main : Tok.Muted).Centered(), ShowImport, hover: Tok.Hover, padding: new Thickness(14, 0));
        ToolTip.SetTip(plus, "Ouvrir un autre personnage (Ctrl+K)");
        row.Children.Add(plus);
        return new Border
        {
            Background = Tok.Chrome, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
    }

    Control Breadcrumb(AdviceReport r, bool hasInspector)
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
        if (hasInspector)
        {
            var toggle = Clickable(T(prefs.InspectorHidden ? "◧ inspecteur" : "◨ inspecteur", 11, prefs.InspectorHidden ? Tok.Muted : Tok.Text2, mono: true),
                ToggleInspector, hover: Tok.HoverStrong, padding: new Thickness(7, 2));
            toggle.BorderBrush = Tok.Border;
            toggle.BorderThickness = new Thickness(1);
            toggle.CornerRadius = new CornerRadius(3);
            ToolTip.SetTip(toggle, prefs.InspectorHidden ? "Afficher l'inspecteur (Ctrl+I)" : "Masquer l'inspecteur (Ctrl+I) ; sa largeur se règle en glissant sa bordure");
            right.Children.Add(toggle);
        }
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
        right.Children.Add(T(Updater.CurrentLabel, 11, Tok.StatusText, mono: true));
        DockPanel.SetDock(right, Dock.Right);
        return new Border { Background = Accent.Bar, Padding = new Thickness(12, 0), Child = new DockPanel { Children = { right, left } } };
    }

    // ----- Import -----

    Control ImportHost()
    {
        importView ??= BuildImport();
        if (importView.Parent is Border previous) previous.Child = null;
        RenderHits();
        RenderHistory();
        return new Border { Child = importView };
    }

    Control BuildImport()
    {
        // Les contrôles réutilisés (saisie, listes, filtres) quittent l'ancienne palette avant d'entrer dans la nouvelle.
        foreach (var reused in new Control[] { search, scopeTag, hitsPanel, historyPanel, historyCount, region, server }) Detach(reused);

        var prompt = T(">", 13, Accent.Main, mono: true);
        prompt.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(prompt, Dock.Left);

        Border? tag = null;
        tag = Clickable(scopeTag, () => FlyoutBase.ShowAttachedFlyout(tag!), hover: Tok.HoverStrong, padding: new Thickness(6, 2));
        ToolTip.SetTip(tag, "Région et serveur de la recherche");
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
        DockPanel.SetDock(tag, Dock.Right);

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
                    HistoryHeader(), new ScrollViewer { MaxHeight = 280, Content = historyPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
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

    static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(control); break;
            case ContentControl host: host.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }
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
        if (importError is { } error) hitsPanel.Children.Add(T($"✕ {error}", 12, Tok.Bad, wrap: true).WithMargin(12, 7));
        // Import lancé par Entrée sans résultat affiché : ligne à part avec le nom tapé.
        if (loading is { } key && key.StartsWith("name:", StringComparison.Ordinal))
            hitsPanel.Children.Add(LoadingRow(T(key[5..], 13, Accent.Main, FontWeight.SemiBold)));
        var text = search.Text?.Trim() ?? "";
        if (searching) { hitsPanel.Children.Add(T("recherche…", 13, Tok.Muted).WithMargin(12, 7)); return; }
        if (text.Length < 2) { hitsPanel.Children.Add(T("tape au moins 2 lettres du nom", 13, Tok.Faint).WithMargin(12, 7)); return; }
        if (hits.Count == 0) { hitsPanel.Children.Add(T("aucun résultat", 13, Tok.Faint).WithMargin(12, 7)); return; }
        foreach (var h in hits.Take(8))
        {
            var level = T(h.Level.ToString(), 13, Tok.Muted, mono: true);
            level.HorizontalAlignment = HorizontalAlignment.Right;
            var isLoading = h.CharacterId == loading;
            var cells = Columns("*,120,100,60",
                T(h.Name, 13, isLoading ? Accent.Main : null, FontWeight.SemiBold), T(h.ClassCode is null ? "?" : db.FindClass(h.ClassCode)?.Name ?? h.ClassCode, 13, Tok.Muted),
                T(h.ServerName, 13, Tok.Muted), level);
            if (isLoading) { hitsPanel.Children.Add(LoadingRow(cells)); continue; }
            var row = Clickable(cells, () => _ = ImportAsync(h.Name, (client, progress) => client.Fetch(db, RegionCode, h.ServerId, h.CharacterId, progress), h.CharacterId),
                hover: loading is null ? Tok.HoverStrong : null, padding: new Thickness(12, 7));
            if (loading is not null) row.Opacity = 0.4;
            hitsPanel.Children.Add(row);
        }
    }

    /// <summary>Ligne du personnage en cours d'import : fond d'accent, indicateur qui tourne et étape en cours.</summary>
    Border LoadingRow(Control content)
    {
        loadText = T(loadStep, 12, Accent.Main, mono: true);
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 5, 0, 0),
            Children = { Spinner(13, Accent.Main), loadText },
        };
        return new Border
        {
            Background = Accent.Dim, BorderBrush = Accent.Main, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 7, 12, 7),
            Child = new StackPanel { Children = { content, status } },
        };
    }

    Control HistoryHeader()
    {
        DockPanel.SetDock(historyCount, Dock.Right);
        return new DockPanel { Margin = new Thickness(12, 8, 12, 4), Children = { historyCount, Label("Mes personnages · score") } };
    }

    /// <summary>
    /// Tous les personnages déjà consultés, du plus récent au plus ancien, filtrés par le nom tapé
    /// (sans tenir compte des majuscules ni des accents).
    /// </summary>
    void RenderHistory()
    {
        historyPanel.Children.Clear();
        var all = store.History();
        var query = search.Text?.Trim() ?? "";
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        var shown = query.Length == 0 ? all
            : all.Where(x => compare.IndexOf(x.Character.Name, query, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0).ToList();
        historyCount.Text = query.Length == 0 ? all.Count.ToString() : $"{shown.Count} / {all.Count}";

        if (all.Count == 0) { historyPanel.Children.Add(T("aucun personnage consulté pour l'instant", 13, Tok.Faint).WithMargin(12, 7)); return; }
        if (shown.Count == 0) { historyPanel.Children.Add(T($"aucun personnage consulté ne correspond à « {query} »", 13, Tok.Faint).WithMargin(12, 7)); return; }
        ComputeOverviewScores(shown.Select(x => x.Character).ToList());
        foreach (var (s, viewedAt) in shown)
        {
            var date = T(viewedAt.ToLocalTime().ToString("dd/MM HH:mm"), 13, Tok.Muted, mono: true);
            DockPanel.SetDock(date, Dock.Right);
            var known = overviewScores.TryGetValue(s.Path, out var sc) && sc.FetchedAtUtc == s.FetchedAtUtc && sc.Mode == prefs.Mode;
            var score = Tip(T(known ? sc.Score.ToString() : "··", 13, known ? Tok.Score(sc.Score) : Tok.Faint, FontWeight.SemiBold, mono: true),
                known ? $"Score global {ScreenViews.ModeLabel(prefs.Mode)} : {sc.Score}/100 · stats {sc.Stats} · équipement {sc.Gear} · compétences {sc.Skills} · Daevanion {sc.Daevanion}" : "calcul du score…");
            score.Width = 34;
            score.TextAlignment = TextAlignment.Right;
            score.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(score, Dock.Right);
            var isCurrent = current is not null && s.Name == current.Name && s.ServerName == current.ServerName && s.Region == current.Region;
            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    T(s.Name, 13, isCurrent ? Accent.Main : null, isCurrent ? FontWeight.SemiBold : FontWeight.Normal),
                    T($"— {db.FindClass(s.ClassCode)?.Name ?? s.ClassCode} · niv. {s.Level} · {s.ServerName} ({s.Region}) · obj {s.ItemLevel}", 13, Tok.Muted),
                },
            };
            var saved = s;
            var row = Clickable(new DockPanel { Children = { date, score, name } }, () => OpenSaved(saved), hover: Tok.HoverStrong, padding: new Thickness(12, 7));
            ToolTip.SetTip(row, $"Consulté le {viewedAt.ToLocalTime():dd/MM/yyyy à HH:mm} · importé le {s.FetchedAtUtc.ToLocalTime():dd/MM/yyyy à HH:mm}");
            historyPanel.Children.Add(row);
        }
    }

    // Vue d'ensemble : score de chaque personnage enregistré (vue en cours), calculé en arrière-plan et gardé en mémoire.
    sealed record OverviewScore(DateTime FetchedAtUtc, GameMode Mode, int Score, int Stats, int Gear, int Skills, int Daevanion);
    readonly Dictionary<string, OverviewScore> overviewScores = [];
    bool overviewRunning;

    void ComputeOverviewScores(List<SavedCharacter> characters)
    {
        var mode = prefs.Mode;
        var missing = characters.Where(c => !overviewScores.TryGetValue(c.Path, out var sc) || sc.FetchedAtUtc != c.FetchedAtUtc || sc.Mode != mode).ToList();
        if (missing.Count == 0 || overviewRunning) return;
        overviewRunning = true;
        var data = db;
        var collection = pets.Collection;
        Task.Run(() =>
        {
            foreach (var c in missing)
            {
                try
                {
                    var snapshot = store.Load(c.Path);
                    var scores = new Advisor(data).Analyze(snapshot, mode, store.LoadInputs(snapshot), collection).Scores;
                    var result = new OverviewScore(c.FetchedAtUtc, mode, scores.Overall, scores.Stats, scores.Gear, scores.Skills, scores.Daevanion);
                    Dispatcher.UIThread.Post(() => overviewScores[c.Path] = result);
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or InvalidOperationException) { /* fiche illisible : pas de score */ }
            }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            overviewRunning = false;
            if (state.Screen == Screen.Import || report is null) RenderHistory();
        }));
    }

    async void ScheduleSearch()
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        importError = null;
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
            await ImportAsync(best.Name, (client, progress) => client.Fetch(db, RegionCode, best.ServerId, best.CharacterId, progress), best.CharacterId);
        else
            await ImportAsync(name, async (client, progress) => (await client.FetchByName(db, RegionCode, name, ServerFilter, progress)).Snapshot);
    }

    async Task RefreshAsync()
    {
        if (current is null) { ShowImport(); return; }
        var c = current;
        await ImportAsync(c.Name, (client, progress) => client.Fetch(db, c.Region, c.ServerId, c.CharacterId, progress));
    }

    /// <param name="key">Identifiant du résultat cliqué (sa ligne affiche le chargement) ; sinon une ligne au nom tapé.</param>
    async Task ImportAsync(string name, Func<ArmoryClient, IProgress<string>, Task<CharacterSnapshot>> fetch, string? key = null)
    {
        if (loading is not null) return; // un double clic ne relance pas l'import
        loading = key ?? $"name:{name}";
        loadStep = "connexion au site officiel…";
        importError = null;
        RenderHits();
        SetStatus($"Import de {name}…");
        try
        {
            using var client = ArmoryClient.Create();
            var snapshot = await fetch(client, new Progress<string>(m =>
            {
                SetStatus(m);
                loadStep = m;
                if (loadText is not null) loadText.Text = m;
            }));
            var path = store.Save(snapshot);
            prefs = prefs with { LastFile = path, Name = search.Text ?? snapshot.Name, Region = snapshot.Region };
            prefs.Save();
            SetStatus($"importé depuis le site officiel");
            loading = null;
            Open(snapshot, path);
        }
        catch (ArmoryException ex)
        {
            importError = ex.Message;
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            if (loading is not null)
            {
                loading = null;
                RenderHits();
            }
        }
    }

    /// <summary>Rapport de la vue en cours en Markdown, dans le fichier choisi.</summary>
    async Task ExportAsync()
    {
        if (report is null || current is null) { SetStatus("Ouvre d'abord un personnage.", error: true); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Exporter le rapport",
            SuggestedFileName = $"{current.Name}-{(prefs.Mode == GameMode.PvE ? "jce" : "jcj")}-{DateTime.Now:yyyy-MM-dd}.md",
            DefaultExtension = "md",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(ReportMarkdown.Write(report, db, inputs));
            SetStatus($"rapport exporté : {file.Name}");
        }
        catch (IOException ex) { SetStatus($"Export impossible : {ex.Message}", error: true); }
    }

    /// <summary>Seul envoi réseau fait sans demande explicite : désactivable (politique de confidentialité, README).</summary>
    void ToggleAutoUpdate()
    {
        prefs = prefs with { AutoUpdate = !prefs.AutoUpdate };
        prefs.Save();
        SetStatus(prefs.AutoUpdate ? "vérification automatique des mises à jour activée" : "vérification automatique désactivée : « Aide → Vérifier les mises à jour » reste disponible");
        Render();
    }

    async Task CheckUpdateAsync(bool manual)
    {
        if (installing || !manual && !prefs.AutoUpdate) return;
        if (Updater.Current is null)
        {
            if (manual) SetStatus("build locale (publier.cmd) : mets-la à jour avec git pull puis publier.cmd");
            return;
        }
        if (manual) SetStatus("Recherche d'une mise à jour sur Aion 2 Todo…");
        try { update = await Updater.Check(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (manual) SetStatus($"Aion 2 Todo ne répond pas : {ex.Message}", error: true);
            return;
        }
        if (manual && update is null) SetStatus($"Aion 2 Builder est à jour ({Updater.CurrentLabel})");
        Render();
    }

    async Task InstallUpdateAsync()
    {
        if (update is not { } release || installing) return;
        installing = true;
        Render();
        try
        {
            await Updater.Install(release, new Progress<string>(m => SetStatus(m)));
            Close(); // la nouvelle version est lancée
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            installing = false;
            SetStatus($"Mise à jour impossible : {ex.Message}", error: true);
            Render();
        }
    }

    /// <summary>Familiers : null = relire avec la clé enregistrée, "" = oublier la clé, sinon relier avec cette clé.</summary>
    async Task PetsAsync(string? key)
    {
        if (key is "")
        {
            petStore.Forget();
            pets = petStore.Load();
            SetStatus("clé Aion 2 Todo oubliée");
            Recompute();
            return;
        }
        SetStatus("Lecture des familiers sur Aion 2 Todo… (le site peut mettre une minute à se réveiller)");
        try
        {
            await petStore.Refresh(key);
            pets = petStore.Load();
            SetStatus("familiers à jour");
            Recompute();
        }
        catch (ArmoryException ex) { SetStatus(ex.Message, error: true); }
    }

    void OpenSaved(SavedCharacter s)
    {
        if (loading is not null) return; // l'import en cours ouvrira son personnage
        if (TryLoad(s.Path) is not { } snapshot) return;
        prefs = prefs with { LastFile = s.Path };
        prefs.Save();
        SetStatus("");
        Open(snapshot, s.Path);
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
