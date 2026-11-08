using System.Diagnostics;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.Combat;
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
using Avalonia.Interactivity;
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
    List<string>? Tabs = null, double InspectorWidth = 340, bool InspectorHidden = false, int AutoRefreshHours = 0,
    List<string>? Mine = null, bool BottomHidden = false, double BottomHeight = 190, string BottomTab = "advice", List<string>? RailOrder = null, string GearPage = "Overview", string SkillPage = "Overview", string BuildPage = "Compare", bool SideHidden = false,
    bool TipsSeen = false, int UiVersion = 1, bool DetailsByNeed = false, bool AutoInstall = true, string? AutoInstallFailed = null,
    string? PetMeterExe = null, List<string>? CraftSkip = null)
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
            SettingsSync.OnSaved();
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
    TextBlock? tipHint;

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
    enum Overlay { None, Shortcuts, Settings, Bug }
    Overlay overlay;
    // Actualisation automatique : une seule tentative par personnage et par session.
    readonly HashSet<string> autoRefreshed = [];
    List<ProgressPoint> progress = [];
    readonly CombatStore combatStore = new();
    /// <summary>Analyse du dernier perso faite au lancement (<see cref="Startup"/>), reprise par le premier <see cref="Recompute"/>.</summary>
    Startup.Analysis? startupAnalysis;
    /// <summary>Combats NotMeter importés pour le personnage affiché, le plus récent d'abord.</summary>
    List<(string Path, CombatLog Log)> combats = [];
    string? CurrentKey => currentPath is null ? null : System.IO.Path.GetFileNameWithoutExtension(currentPath);

    public MainWindow()
    {
        Journal.Install();
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

        // Revue d'interface (v2) : le panneau des conseils du bas est fermé par défaut (il répétait « À faire » et Détails) ;
        // un clic sur le compteur de la barre d'état ou Ctrl+J le rouvre.
        if (prefs.UiVersion < 2)
        {
            prefs = prefs with { BottomHidden = true, UiVersion = 2 };
            prefs.Save();
        }
        // Première infobulle ouverte : l'astuce de la barre d'état a servi, elle disparaît pour de bon.
        Ui.TipShown = () =>
        {
            if (prefs.TipsSeen) return;
            prefs = prefs with { TipsSeen = true };
            prefs.Save();
            if (tipHint is not null) tipHint.IsVisible = false;
        };
        // Données, fiches et analyse du dernier perso : préparées pendant le démarrage d'Avalonia (Startup), attendues ici si besoin.
        var prepared = Startup.Take();
        db = LoadData(prepared?.Db);
        pets = prepared?.Pets ?? petStore.Load();
        startupAnalysis = prepared?.First;
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
        // Glissé des entrées du rail : suivi au niveau de la fenêtre, même si un contrôle a déjà traité l'événement.
        AddHandler(PointerMovedEvent, RailMove, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, RailRelease, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { e.Handled = true; ShowOverlay(Overlay.Shortcuts); return; }
            if (e.Key == Key.Escape && overlay != Overlay.None) { e.Handled = true; ShowOverlay(Overlay.None); return; }
            if (e.Key == Key.F5) { e.Handled = true; _ = RefreshAsync(); return; }
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            // Ctrl+1 à Ctrl+9 : les écrans du rail, dans l'ordre choisi.
            if (e.Key is >= Key.D1 and <= Key.D9 && report is not null && e.Key - Key.D1 < RailNav.Count)
            {
                e.Handled = true;
                Navigate(RailNav[e.Key - Key.D1].Screen);
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
                case Key.B: e.Handled = true; ToggleSide(); break;
                case Key.J: e.Handled = true; ToggleBottom(); break;
            }
        };
        Opened += async (_, _) => await LoadServersAsync(force: false);
        // Les écrans se préparent une fois la fenêtre affichée (le premier rendu passe avant).
        Opened += (_, _) =>
        {
            shown = true;
            DispatcherTimer.RunOnce(PrepareScreens, TimeSpan.FromMilliseconds(300));
        };
        Updater.CleanOld();
        Opened += async (_, _) => await CheckUpdateAsync(manual: false);
        Opened += (_, _) => Updater.ConfirmStarted(); // après une mise à jour : l'ancienne version attend ce signal pour se fermer
        // Réglages suivis entre tes PC (Aion 2 Todo) : ceux de l'autre PC s'ils sont plus récents, puis chaque changement d'ici.
        SettingsSync.Saved += () => Dispatcher.UIThread.Post(SettingsSaved);
        Opened += async (_, _) => await PullSettingsAsync();
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => await CheckUpdateAsync(manual: false);
        updateTimer.Start();

        // Onglets de la dernière session (fichiers encore présents), puis le dernier personnage affiché.
        foreach (var path in (prefs.Tabs ?? []).Append(prefs.LastFile).OfType<string>().Distinct())
            if (File.Exists(path) && (prepared?.Snapshots.GetValueOrDefault(path) ?? TryLoad(path)) is { } saved)
            {
                tabs.Add(path);
                opened[path] = saved;
            }
        foreach (var skip in prefs.CraftSkip ?? []) state.CraftSkip.Add(skip);
        var last = prefs.LastFile is not null && opened.ContainsKey(prefs.LastFile) ? prefs.LastFile : tabs.FirstOrDefault();
        if (last is not null)
        {
            state.Screen = Enum.TryParse<Screen>(prefs.Screen, out var screen) ? screen : Screen.Home;
            state.GearPage = Enum.TryParse<GearPage>(prefs.GearPage, out var gearPage) ? gearPage : GearPage.Overview;
            state.ByNeed = prefs.DetailsByNeed;
            state.SkillPage = Enum.TryParse<SkillPage>(prefs.SkillPage, out var skillPage) ? skillPage : SkillPage.Overview;
            state.BuildPage = Enum.TryParse<BuildPage>(prefs.BuildPage, out var buildPage) ? buildPage : BuildPage.Compare;
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

    /// <param name="loaded">Données déjà lues (au lancement, par <see cref="Startup"/>).</param>
    GameDatabase LoadData(GameDatabase? loaded = null)
    {
        try
        {
            var data = loaded ?? GameDataStore.Load();
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

    int Recompute()
    {
        if (current is null) { Render(); return 0; }
        var fresh = 0;
        // Au lancement : l'analyse faite pendant le démarrage d'Avalonia, sur la même fiche et dans le même mode, reprise telle quelle.
        if (startupAnalysis is { } early && ReferenceEquals(early.Character, current) && early.Mode == prefs.Mode)
        {
            inputs = early.Inputs;
            combats = early.Combats;
            fresh = early.Fresh;
            report = early.Report;
        }
        else
        {
            combats = CurrentKey is { } key ? combatStore.List(key) : [];
            // Nouveaux combats du Pet Meter pour ce perso (même nom) : repris tout seuls.
            // (et ceux convertis par une version plus ancienne du Builder, reconvertis).
            if (CurrentKey is { } k)
            {
                fresh = PetMeterFights.Sync(combatStore, k, current.Name, db);
                combats = combatStore.List(k);
            }
            try { report = new Advisor(db).Analyze(current, prefs.Mode, inputs with { MyPeers = peerStore.For(current.ClassCode) }, pets.Collection, combats.Select(x => x.Log).ToList()); }
            catch (InvalidOperationException ex) { report = null; SetStatus(ex.Message, error: true); state.Screen = Screen.Import; }
        }
        startupAnalysis = null;
        if (fresh > 0) Toast($"{fresh} combat{(fresh > 1 ? "s" : "")} du Pet Meter", "Écran Combat : lancers, combos, temps morts, dégâts subis.");
        if (report is not null) store.RecordScores(current, prefs.Mode, report.Scores, ProgressPoint.Todos(report.Findings));
        progress = store.Progress(current);
        Render();
        Dispatcher.UIThread.Post(PrepareScreens, DispatcherPriority.Background);
        return fresh;
    }

    /// <summary>
    /// Bouton ↻ de l'écran Combat : reprend les derniers combats écrits par le Pet Meter (un combat est écrit 15 s après
    /// ton dernier coup), ouvre le plus récent, et dit s'il n'y avait rien de nouveau.
    /// </summary>
    void ReloadCombats()
    {
        if (current is null) return;
        state.CombatIndex = null;
        state.CombatGroup = null;
        state.CombatList = false;
        state.CombatSkill = null;
        if (Recompute() > 0) return;
        var written = PetMeterFights.For(current.Name).Count;
        Toast("Aucun nouveau combat", written == 0
            ? $"Le Pet Meter n'a écrit aucun combat pour {current.Name} : lance-le pendant tes combats (boss, mannequin, JcJ)."
            : "Le Pet Meter écrit un combat 15 s après ton dernier coup : réessaie dans un instant.");
    }

    void Update(Action<UiState> change)
    {
        change(state);
        prefs = prefs with { Screen = state.Screen.ToString(), GearPage = state.GearPage.ToString(), SkillPage = state.SkillPage.ToString(), BuildPage = state.BuildPage.ToString(), DetailsByNeed = state.ByNeed,
            CraftSkip = state.CraftSkip.Count == 0 ? null : [.. state.CraftSkip.Order(StringComparer.Ordinal)] };
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
        if (text.Length > 0 && (journal.Count == 0 || journal[^1].Text != text)) journal.Add((DateTime.Now, text, error));
        if (journal.Count > 300) journal.RemoveRange(0, journal.Count - 300);
        if (statusText is not null) statusText.Text = StatusMessage;
    }

    string StatusMessage => status.Length == 0 ? "" : statusError ? $"⚠ {status}" : status;

    /// <summary>Toasts en bas à droite, gardés d'un rendu à l'autre (le contenu de la fenêtre est reconstruit à chaque rendu).</summary>
    readonly StackPanel toasts = new()
    {
        Spacing = 8, Margin = new Thickness(0, 0, 16, 14), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
    };

    /// <summary>Message discret qui disparaît seul (4 s, 7 s pour une erreur) ; un clic le ferme. Trois au plus à l'écran.</summary>
    void Toast(string title, string? detail = null, bool error = false)
    {
        var body = new StackPanel { Spacing = 2, Children = { T(title, 13, Tok.Text, FontWeight.SemiBold, wrap: true) } };
        if (!string.IsNullOrEmpty(detail)) body.Children.Add(T(detail, 12, Tok.Text2, wrap: true).WithLineHeight(1.4));
        var toast = new Border
        {
            Width = 320, Padding = new Thickness(12, 9), Background = Tok.Panel, CornerRadius = new CornerRadius(4),
            BorderBrush = error ? Tok.Bad : Tok.Good, BorderThickness = new Thickness(3, 0, 0, 0),
            BoxShadow = BoxShadows.Parse("0 6 20 0 #60000000"), Opacity = 0, Cursor = new Cursor(StandardCursorType.Hand),
            Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
            Child = new DockPanel { Children = { T(error ? "⚠" : "✓", 13, error ? Tok.Bad : Tok.Good, FontWeight.Bold).WithMargin(0, 0, 9, 0), body } },
        };
        ToolTip.SetTip(toast, "Clic pour fermer");
        DockPanel.SetDock((Control)((DockPanel)toast.Child).Children[0], Dock.Left);
        void Close()
        {
            if (!toasts.Children.Contains(toast)) return;
            toast.Opacity = 0;
            DispatcherTimer.RunOnce(() => toasts.Children.Remove(toast), TimeSpan.FromMilliseconds(200));
        }
        toast.PointerPressed += (_, e) => { e.Handled = true; Close(); };
        while (toasts.Children.Count >= 3) toasts.Children.RemoveAt(0);
        toasts.Children.Add(toast);
        Dispatcher.UIThread.Post(() => toast.Opacity = 1, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(error ? 7 : 4));
    }

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
        (toasts.Parent as Panel)?.Children.Remove(toasts);
        Grid.SetRow(toasts, 1);
        root.Children.Add(toasts);
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
            Child = overlay switch { Overlay.Shortcuts => ShortcutsCard(), Overlay.Bug => BugCard(), _ => SettingsCard() },
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
        ("Ctrl+B", "afficher / masquer la barre secondaire (sous-pages, combats, plateaux)"),
        ("Ctrl+I", "afficher / masquer l'inspecteur (sa largeur se règle en glissant sa bordure)"), ("Ctrl+J", "afficher / masquer le panneau du bas : conseils et journal"),
        ("Ctrl+E", "exporter le rapport en Markdown"), ("Ctrl+,", "paramètres"), ("F1", "cette aide"), ("Échap", "fermer"),
    ];

    Control ShortcutsCard()
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(18, 14) };
        foreach (var (keys, what) in Shortcuts)
            list.Children.Add(ColumnsSpaced("190,*", 12, T(keys, 12, Accent.Main, FontWeight.SemiBold, mono: true), T(what, 13, Tok.Text2)));
        return new StackPanel { Children = { SectionHeader("Raccourcis clavier"), list } };
    }

    /// <summary>Description du bug : gardée d'un rendu à l'autre (la fenêtre se redessine en entier).</summary>
    readonly TextBox bugBox = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, FontSize = 13, Padding = new Thickness(8, 6),
        PlaceholderText = "Ce que tu faisais, ce que tu attendais, ce que tu as vu…",
    };
    string? bugStatus;
    bool bugSending;

    /// <summary>« Aide → Signaler un bug… » : ta description et le journal anonymisé vers Aion 2 Todo (page Besoins du mainteneur).</summary>
    Control BugCard()
    {
        Detach(bugBox);
        var send = Clickable(T(bugSending ? "envoi…" : "envoyer", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true),
            () => { if (!bugSending) _ = SendBugAsync(); }, Accent.Main, null, new Thickness(14, 4));
        send.CornerRadius = new CornerRadius(3);
        var body = new StackPanel
        {
            Spacing = 10, Margin = new Thickness(18, 14, 18, 18),
            Children =
            {
                T("Que s'est-il passé ? Ce que tu faisais, ce que tu attendais, ce que tu as vu.", 13, weight: FontWeight.Medium, wrap: true),
                new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Background = Tok.Window, Child = bugBox },
                T("Partent avec ta description : la version, Windows, tes réglages d'affichage et la fin du journal d'erreurs du Builder, "
                  + "sans les noms des persos ni ton nom Windows. Avec ta clé Aion 2 Todo, vers le site ; sinon, une copie dans le dossier « rapports » des données.",
                    12, Tok.Muted, wrap: true).WithLineHeight(1.4),
            },
        };
        if (bugStatus is { } st) body.Children.Add(T(st, 12, Accent.Main, FontWeight.SemiBold, wrap: true));
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { send } });
        var close = Clickable(T("fermer · Échap", 11, Tok.Muted, mono: true), () => ShowOverlay(Overlay.None));
        Dispatcher.UIThread.Post(() => bugBox.Focus());
        return new StackPanel { Children = { SectionHeader("Signaler un bug", close), body } };
    }

    async Task SendBugAsync()
    {
        var text = bugBox.Text?.Trim() ?? "";
        if (text.Length == 0) { bugStatus = "Écris d'abord ce qui s'est passé."; Render(); return; }
        bugSending = true; bugStatus = null; Render();
        // Noms à retirer : tes persos enregistrés (fichiers « région-serveur-nom ») et ta saisie.
        var names = Directory.Exists(AppPaths.CharactersDir)
            ? Directory.EnumerateFiles(AppPaths.CharactersDir, "*.json").Select(f => System.IO.Path.GetFileNameWithoutExtension(f).Split('-', 3)).Where(p => p.Length == 3).Select(p => (string?)p[2]).ToList()
            : [];
        var (sent, what) = await BugReports.Send(BugReports.Build(text, prefs, names), AccountKey());
        bugSending = false;
        if (sent)
        {
            bugBox.Text = "";
            bugStatus = null;
            ShowOverlay(Overlay.None);
            SetStatus($"Bug envoyé, merci ! (n° {what})");
        }
        else
        {
            bugStatus = $"Pas envoyé (pas de clé Aion 2 Todo ou pas de réseau) : copie gardée dans {what}";
            Render();
        }
    }

    DispatcherTimer? settingsTimer;

    /// <summary>Réglages enregistrés : envoyés à Aion 2 Todo 3 s après le dernier changement (un glissé enregistre souvent).</summary>
    void SettingsSaved()
    {
        if (AccountKey() is null) return;
        if (settingsTimer is null)
        {
            settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            settingsTimer.Tick += async (_, _) => { settingsTimer.Stop(); await PushSettingsAsync(); };
        }
        settingsTimer.Stop();
        settingsTimer.Start();
    }

    async Task PushSettingsAsync()
    {
        if (AccountKey() is not { } key || SettingsSync.Changed(prefs) is not { } change) return;
        if (await SettingsSync.Push(key, change) is { } remote) ApplyRemoteSettings(remote);
    }

    async Task PullSettingsAsync()
    {
        if (AccountKey() is not { } key) return;
        if (await SettingsSync.Pull(key) is { } remote) ApplyRemoteSettings(remote);
        else SettingsSaved();  // rien de plus récent sur le site : on y met les tiens s'ils n'y sont pas encore
    }

    void ApplyRemoteSettings(System.Text.Json.Nodes.JsonObject remote)
    {
        var mode = prefs.Mode;
        prefs = SettingsSync.Apply(prefs, remote);
        prefs.Save();
        SetStatus("réglages repris de ton autre PC (Aion 2 Todo)");
        if (prefs.Mode != mode) Recompute(); else Render();
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
                Choice("Barre secondaire", "La colonne à côté du rail : sous-pages, combats, plateaux (Ctrl+B).",
                    [("affichée", !prefs.SideHidden, () => Set(prefs with { SideHidden = false })), ("masquée", prefs.SideHidden, () => Set(prefs with { SideHidden = true }))]),
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
                Menu("Données", ("Recharger les données", ReloadData), ("Actualiser les familiers (Aion 2 Todo)", () => _ = PetsAsync(null)), ("Ouvrir le dossier des données", OpenDataFolder),
                    ("Lancer le Pet Meter", () => _ = PetMeterAsync()),
                    ("Choisir l'exe du Pet Meter…", () => _ = ChoosePetMeterAsync())),
                Menu("Aide", ("Raccourcis clavier  F1", () => ShowOverlay(Overlay.Shortcuts)),
                    ("Comment c'est calculé ?", () => Update(st => { st.Screen = report is null ? Screen.Import : Screen.Home; st.HomeHow = true; })),
                    ("Vérifier les mises à jour", () => _ = CheckUpdateAsync(manual: true)),
                    ($"{(prefs.AutoUpdate ? "✓ " : "")}Vérifier automatiquement (au lancement, puis toutes les 6 h)", ToggleAutoUpdate),
                    ($"{(prefs.AutoInstall ? "✓ " : "")}Installer toute seule au lancement", ToggleAutoInstall),
                    ("Signaler un bug…", () => ShowOverlay(Overlay.Bug))),
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

        var nav = RailNav;
        var entries = new List<Border>();
        for (var index = 0; index < nav.Count; index++)
        {
            var (screen, title, shortName) = nav[index];
            var on = state.Screen == screen;
            var score = report is null ? null : ScreenViews.ScoreOf(report, screen);
            Control top = score is { } v ? T(v.ToString(), 13, Tok.Score(v), FontWeight.SemiBold, mono: true).Centered() : RailIcon(screen, on);
            var entry = new Border
            {
                Width = 52, Padding = new Thickness(0, 8), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = on ? Accent.Main : Brushes.Transparent, BorderThickness = new Thickness(2, 0, 0, 0),
                Child = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, Children = { top, T(shortName, 10, on ? Tok.Text : Tok.Muted).Centered() } },
            };
            ToolTip.SetTip(entry, $"{title} · glisse (ou clic droit) pour changer l'ordre");
            RailDrag(entry, index, entries, panel, () => { if (report is not null) Navigate(screen); });
            entries.Add(entry);
            panel.Children.Add(entry);
        }
        return new Border { Background = Tok.Chrome, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    /// <summary>
    /// Barre secondaire, entre le rail et la page (comme l'explorateur d'un éditeur) : sous-pages, combats ou plateaux de
    /// l'écran ouvert. Masquée (bouton ‹ ou Ctrl+B), il n'en reste qu'une bande étroite avec › pour la rouvrir.
    /// </summary>
    Control SideBar(SidePanel side)
    {
        if (prefs.SideHidden)
        {
            var open = Clickable(T("›", 13, Tok.Text2, mono: true).Centered(), ToggleSide, hover: Tok.HoverStrong, padding: new Thickness(0, 9));
            ToolTip.SetTip(open, $"Afficher la barre {side.Title} (Ctrl+B)");
            return new Border { Width = 22, Background = Tok.Frame, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = new StackPanel { Children = { open } } };
        }
        var close = Clickable(T("‹", 12, Tok.Text2, mono: true).Centered(), ToggleSide, hover: Tok.HoverStrong, padding: new Thickness(7, 1));
        close.BorderBrush = Tok.Border;
        close.BorderThickness = new Thickness(1);
        close.CornerRadius = new CornerRadius(3);
        ToolTip.SetTip(close, "Masquer la barre (Ctrl+B)");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (side.Actions is not null) actions.Children.Add(side.Actions);
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        var head = new Border
        {
            Height = 36, Padding = new Thickness(14, 0, 10, 0), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { actions, Label(side.Title) } },
        };
        DockPanel.SetDock(head, Dock.Top);
        var foot = new Border
        {
            Padding = new Thickness(14, 7), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = T("Ctrl+B : masquer la barre", 11, Tok.Muted, mono: true),
        };
        DockPanel.SetDock(foot, Dock.Bottom);
        return new Border
        {
            Width = 232, Background = Tok.Frame, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new DockPanel { Children = { head, foot, Scroll(new Border { Padding = new Thickness(0, 4, 0, 8), Child = side.Body }) } },
        };
    }

    void ToggleSide()
    {
        prefs = prefs with { SideHidden = !prefs.SideHidden };
        prefs.Save();
        Render();
    }

    /// <summary>Ordre par défaut du rail : les écrans notés d'abord (leurs notes se suivent), puis les autres.</summary>
    static readonly Screen[] DefaultRail =
        [Screen.Home, Screen.Stats, Screen.Gear, Screen.Skills, Screen.Daeva, Screen.Pets, Screen.Build, Screen.Combat, Screen.Gather, Screen.Inventory, Screen.Craft, Screen.Market, Screen.Simulate, Screen.Progress, Screen.Details];

    /// <summary>Écrans du rail dans l'ordre choisi (glisser-déposer), les écrans inconnus de cet ordre à leur place par défaut.</summary>
    List<(Screen Screen, string Title, string Short)> RailNav
    {
        get
        {
            var order = (prefs.RailOrder ?? []).Select(s => Enum.TryParse<Screen>(s, out var x) ? x : (Screen?)null).OfType<Screen>()
                .Concat(DefaultRail).Distinct().ToList();
            return order.Select(s => ScreenViews.Nav.FirstOrDefault(n => n.Screen == s)).Where(n => n.Title is not null).ToList();
        }
    }

    /// <summary>
    /// Entrée du rail : un clic l'ouvre ; maintenir et glisser verticalement la déplace (l'entrée suit la souris, l'ordre
    /// est enregistré au relâchement).
    /// </summary>
    void RailDrag(Border entry, int index, List<Border> entries, Panel panel, Action open)
    {
        entry.PointerEntered += (_, _) => { if (railDrag is null) entry.Background = Tok.Hover; };
        entry.PointerExited += (_, _) => { if (railDrag is null) entry.Background = Brushes.Transparent; };
        // L'appui commence le geste ; la fenêtre suit ensuite la souris (RailMove, RailRelease), sans dépendre de la
        // capture de la souris par l'entrée, qui se perdait pendant le glissé.
        entry.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(entry).Properties.IsLeftButtonPressed) return;
            railDrag = new RailGesture(entry, index, entries, e.GetPosition(this).Y, open);
            e.Handled = true;
        };
        entry.ContextFlyout = new MenuFlyout
        {
            ItemsSource = new List<Control>
            {
                RailMenuItem("Monter", () => MoveRailEntry(index, index - 1), index > 0),
                RailMenuItem("Descendre", () => MoveRailEntry(index, index + 1), index < RailNav.Count - 1),
                new Separator(),
                RailMenuItem("Ordre par défaut (écrans notés d'abord)", () => { prefs = prefs with { RailOrder = null }; prefs.Save(); Render(); }, prefs.RailOrder is not null),
            },
        };
    }

    /// <summary>Geste en cours sur une entrée du rail : appui, puis glissé éventuel.</summary>
    sealed class RailGesture(Border entry, int index, List<Border> entries, double startY, Action open)
    {
        public Border Entry { get; } = entry;
        public int Index { get; } = index;
        public List<Border> Entries { get; } = entries;
        public double StartY { get; } = startY;
        public Action Open { get; } = open;
        public double Offset { get; set; }
        public bool Dragging { get; set; }
    }

    RailGesture? railDrag;

    void RailMove(object? sender, PointerEventArgs e)
    {
        if (railDrag is not { } g) return;
        g.Offset = e.GetPosition(this).Y - g.StartY;
        if (!g.Dragging && Math.Abs(g.Offset) < 6) return;
        g.Dragging = true;
        g.Entry.ZIndex = 10;
        g.Entry.Opacity = 0.85;
        g.Entry.Background = Tok.HoverStrong;
        g.Entry.RenderTransform = new TranslateTransform(0, g.Offset);
    }

    void RailRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (railDrag is not { } g) return;
        railDrag = null;
        if (!g.Dragging) { g.Open(); return; }
        // Nouvelle place : avant la première entrée dont le milieu est sous le centre de l'entrée déplacée.
        var center = g.Entry.Bounds.Center.Y + g.Offset;
        MoveRailEntry(g.Index, g.Entries.Count(other => other != g.Entry && other.Bounds.Center.Y < center));
    }

    static MenuItem RailMenuItem(string header, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    void MoveRailEntry(int from, int to)
    {
        var order = RailNav.Select(n => n.Screen.ToString()).ToList();
        if (from < 0 || from >= order.Count) return;
        var moved = order[from];
        order.RemoveAt(from);
        order.Insert(Math.Clamp(to, 0, order.Count), moved);
        prefs = prefs with { RailOrder = order };
        prefs.Save();
        Render();
    }

    /// <summary>
    /// Icône des écrans sans note. Progression : la vraie courbe de la note globale (vue en cours) dès deux imports notés.
    /// </summary>
    Control RailIcon(Screen screen, bool on)
    {
        var color = on ? Accent.Main : Tok.Muted;
        if (screen == Screen.Progress && current is not null)
        {
            var key = prefs.Mode.ToString();
            var scores = progress.Where(p => p.Scores?.ContainsKey(key) == true).Select(p => (double)p.Scores![key].Overall).TakeLast(12).ToList();
            if (scores.Count >= 2)
            {
                double min = scores.Min() - 1, max = scores.Max() + 1;
                var points = scores.Select((v, i) => new Point(1 + 22.0 * i / (scores.Count - 1), 13 - 12 * (v - min) / (max - min))).ToList();
                return new Polyline
                {
                    Points = points, Stroke = on ? Accent.Main : Tok.Score((int)scores[^1]), StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round,
                    Width = 24, Height = 14, HorizontalAlignment = HorizontalAlignment.Center,
                };
            }
        }
        var data = screen switch
        {
            Screen.Progress => "M1,12 L6,7 L10,9 L16,3 L19,5",
            Screen.Build => "M1,2 H17 M1,7 H13 M1,12 H9",
            Screen.Combat => "M2,2 L14,14 M16,2 L4,14 M1,11 L5,15 M17,11 L13,15",
            Screen.Simulate => "M1,8 H6 L11,2 H17 M6,8 L11,14 H17",
            Screen.Details => "M1,2 H3 M6,2 H17 M1,7 H3 M6,7 H17 M1,12 H3 M6,12 H17",
            // Patte : coussinet et quatre doigts.
            Screen.Pets => "M5,11 Q9,6 13,11 Q9,15 5,11 Z M3,6 L3,7 M7,3 L7,4 M11,3 L11,4 M15,6 L15,7",
            // Pioche : manche et fer.
            Screen.Gather => "M3,14 L11,6 M6,3 Q13,1 16,9",
            // Étiquette de prix : l'hôtel des ventes.
            Screen.Market => "M2,2 H9 L16,9 L9,15 L2,8 Z M5,5 L5.6,5.6",
            // Sac : corps et anse.
            Screen.Inventory => "M3,6 H15 L14,15 H4 Z M6,6 Q6,2 9,2 Q12,2 12,6",
            // Marteau : manche et tête.
            Screen.Craft => "M3,15 L10,8 M8,3 L12,1 L17,6 L13,10 Z",
            _ => "M2,7 H16",
        };
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data), Stroke = color, StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Width = 18, Height = 15, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    Control MainArea()
    {
        // Colonne 0 : la barre secondaire de l'écran (sur toute la hauteur, à côté du rail) ; colonne 1 : onglets puis page.
        var grid = new Grid { RowDefinitions = new RowDefinitions(tabs.Count > 0 ? "34,*" : "0,*"), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (tabs.Count > 0)
        {
            var bar = TabBar();
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);
        }
        side = null;
        if (state.Screen is Screen.Gather or Screen.Market or Screen.Inventory or Screen.Craft && report is not null) { _ = LoadAccountGatherAsync(); _ = LoadMarketAsync(); SyncPendingShot(); }
        var page = state.Screen == Screen.Import || report is null ? ImportHost() : Page(report);
        Grid.SetRow(page, 1);
        Grid.SetColumn(page, 1);
        grid.Children.Add(page);
        if (side is not null)
        {
            var bar = SideBar(side);
            Grid.SetRowSpan(bar, 2);
            grid.Children.Add(bar);
        }
        return grid;
    }

    /// <summary>Barre secondaire de l'écran rendu (null : écran d'une seule page).</summary>
    SidePanel? side;

    /// <summary>Fil d'Ariane, puis le contenu et l'inspecteur, séparés par une poignée qui règle la largeur de l'inspecteur.</summary>
    Control Page(AdviceReport r)
    {
        var views = Views(r, state);
        var (content, inspector) = views.Build();
        side = views.Side();
        var grid = new Grid { RowDefinitions = new RowDefinitions("36,*") };
        if (!prefs.BottomHidden)
        {
            // Panneau du bas, comme la liste d'erreurs de Visual Studio : redimensionnable, masquable (Ctrl+J).
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(Math.Clamp(prefs.BottomHeight, 90, 600), GridUnitType.Pixel) { MinHeight = 90, MaxHeight = 600 });
            var splitter = new GridSplitter { Height = 5, Background = Tok.Border, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            ToolTip.SetTip(splitter, "Glisse pour régler la hauteur du panneau (Ctrl+J pour le masquer)");
            splitter.DragCompleted += (_, _) =>
            {
                prefs = prefs with { BottomHeight = Math.Round(grid.RowDefinitions[3].ActualHeight) };
                prefs.Save();
            };
            Grid.SetRow(splitter, 2);
            grid.Children.Add(splitter);
            var bottom = BottomPanel(r, views);
            Grid.SetRow(bottom, 3);
            grid.Children.Add(bottom);
        }
        grid.Children.Add(Breadcrumb(r, inspector is not null, views.Crumb()));
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 420 });
        body.Children.Add(KeepScroll(Scroll(content)));
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

    ScreenViews Views(AdviceReport r, UiState ui)
    {
        var (data, mine, collection) = (db, inputs, pets.Collection);  // ceux de cette analyse, même lus depuis un autre fil
        return new(db, r, inputs, ui, Update, OnInputs, pets.KeyHint, key => _ = PetsAsync(key), progress,
            o => Simulate(data, r, mine, collection, o),
            combats, () => _ = ImportCombatAsync(), DeleteCombat, ReloadCombats, () => _ = RefreshAsync(),
            peerStore.Load(), (name, server) => _ = AddPeerAsync(name, server), RemovePeer, () => _ = ImportInGameAsync(),
            new AccountGather(accountGather ?? [], AccountKey() is not null, accountGatherStatus),
            (name, classCode, anchor) => _ = OpenPlayerAsync(name, classCode, anchor),
            new MarketState(market, AccountKey() is not null, marketStatus, AddPriceAsync,
                p => _ = MarketActionAsync(key => Market.Remove(key, p.Id), $"prix retiré : {p.ItemName}, {Fmt.Big(p.Price)} Kinah"),
                p => _ = MarketActionAsync(key => Market.Restore(key, p.Id), $"prix rétabli : {p.ItemName}, {Fmt.Big(p.Price)} Kinah"))
            {
                Shot = marketShot, ShotStatus = marketShotStatus,
                ReadShots = which => _ = ReadMarketShotsAsync(which),
                SaveShot = (lines, code) => _ = SaveMarketShotAsync(lines, code),
                ClearShot = () => { marketShot = null; marketShotStatus = null; KeepShot(); Render(); },
                EditShot = (i, line) => { if (marketShot is { } l && i < l.Count) l[i] = line; },
                NameItem = NameItemAsync,
                AddRecipe = AddRecipeAsync,
                ReplaceRecipe = ReplaceRecipeAsync,
                RemoveRecipe = rc => _ = MarketActionAsync(key => Market.RemoveRecipe(key, rc.Id), $"recette retirée : {rc.Output.Name} (rétablissable)"),
                RestoreRecipe = id => _ = MarketActionAsync(key => Market.RestoreRecipe(key, id), "recette rétablie"),
                AddNpc = AddNpcAsync,
                ReadCraft = which => _ = ReadCraftShotAsync(which),
                RemoveNpc = item => _ = MarketActionAsync(key => Market.RemoveNpc(key, item.Key), $"objet du PNJ retiré : {item.Name}"),
                RemoveShot = i =>
                {
                    if (marketShot is { } l && i < l.Count) l.RemoveAt(i);
                    if (marketShot is { Count: 0 }) marketShot = null;
                    KeepShot();
                    Render();
                },
            });
    }

    /// <summary>
    /// Simulations « et si » déjà faites, par analyse (une nouvelle analyse = de nouvelles simulations) : les cartes d'arcanes en
    /// lançaient deux complètes à chaque affichage (~300 ms). Partagées avec la préparation en arrière-plan : si elle calcule
    /// déjà celle qu'on demande, on attend son résultat au lieu de la refaire.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AdviceReport, Dictionary<SimulationOptions, Lazy<AdviceReport>>> Simulations = new();

    static AdviceReport Simulate(GameDatabase data, AdviceReport r, PlayerInputs mine, PetCollection? collection, SimulationOptions o)
    {
        var known = Simulations.GetValue(r, _ => []);
        Lazy<AdviceReport> run;
        lock (known)
            if (!known.TryGetValue(o, out run!))
            {
                if (known.Count >= 24) known.Clear();
                known[o] = run = new(() => Simulator.Run(data, r, mine, collection, o));
            }
        return run.Value;
    }

    /// <summary>Analyse dont les écrans ont été préparés (ou sont en train de l'être).</summary>
    AdviceReport? preparedFor;
    CancellationTokenSource? prepareCts;
    bool shown;

    /// <summary>
    /// Calculs des écrans (feuille de route, optimisation, spécialisations, « et si » des arcanes, points des combats…) faits
    /// d'avance dès qu'une analyse est affichée, sur un fil à priorité basse : le premier affichage de chaque écran les trouve
    /// dans les mémos au lieu de les faire (150 à 300 ms). Une seule préparation à la fois ; une nouvelle analyse arrête l'ancienne.
    /// </summary>
    void PrepareScreens()
    {
        if (!shown || report is not { } r || ReferenceEquals(preparedFor, r)) return;
        preparedFor = r;
        prepareCts?.Cancel();
        var cts = prepareCts = new CancellationTokenSource();
        var views = Views(r, new UiState());  // un état à part : la préparation ne touche pas à celui de l'écran
        new Thread(() => views.Prepare(cts.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Préparation des écrans" }.Start();
    }

    ScrollViewer? contentScroll;
    string? contentScrollKey;

    /// <summary>
    /// Chaque rendu reconstruit la page : sur le même écran du même personnage (une section dépliée, une ligne choisie),
    /// le défilement reprend où il était au lieu de remonter en haut.
    /// </summary>
    ScrollViewer KeepScroll(ScrollViewer scroll)
    {
        var key = $"{currentPath}|{state.Screen}|{state.GearPage}|{state.SkillPage}|{state.BuildPage}|{state.CombatIndex}|{state.CombatGroup}|{state.CombatList}|{state.Board}";
        if (contentScroll is { } previous && contentScrollKey == key && previous.Offset != default)
        {
            var offset = previous.Offset;
            void Apply(object? sender, EventArgs e)
            {
                scroll.LayoutUpdated -= Apply;
                scroll.Offset = offset;
            }
            scroll.LayoutUpdated += Apply;
        }
        contentScroll = scroll;
        contentScrollKey = key;
        return scroll;
    }

    DispatcherTimer? shrinkTimer;

    /// <summary>
    /// Quand la fenêtre rétrécit, Avalonia garde dans les colonnes « * » la largeur d'avant si un texte à la ligne la
    /// remplissait : la page déborde à droite. Une fois le redimensionnement fini, tout est reconstruit à la bonne largeur.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ClientSizeProperty || change.GetNewValue<Size>().Width >= change.GetOldValue<Size>().Width) return;
        shrinkTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            shrinkTimer!.Stop();
            Render();
        });
        shrinkTimer.Stop();
        shrinkTimer.Start();
    }

    // Panneau du bas : conseils (filtres par gravité) et journal (messages de l'application).
    readonly List<(DateTime At, string Text, bool Error)> journal = [];
    readonly HashSet<Severity> bottomFilter = [Severity.Critical, Severity.Important, Severity.Advice];

    static (string Icon, IBrush Color) SeverityIcon(Severity s) => s switch
    {
        Severity.Critical => ("✕", Tok.Bad),
        Severity.Important => ("!", Tok.Warn),
        Severity.Advice => ("i", Tok.Info),
        _ => ("·", Tok.Muted),
    };

    void ToggleBottom()
    {
        prefs = prefs with { BottomHidden = !prefs.BottomHidden };
        prefs.Save();
        Render();
    }

    void SetBottomTab(string tab)
    {
        prefs = prefs with { BottomTab = tab, BottomHidden = false };
        prefs.Save();
        Render();
    }

    Control BottomPanel(AdviceReport r, ScreenViews views)
    {
        Border Tab(string key, Control label)
        {
            var on = prefs.BottomTab == key;
            var tab = Clickable(label, () => SetBottomTab(key), on ? Tok.Window : null, on ? null : Tok.Hover, new Thickness(12, 0));
            tab.BorderBrush = on ? Accent.Main : Brushes.Transparent;
            tab.BorderThickness = new Thickness(0, 2, 0, 0);
            return tab;
        }
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        counts.Children.Add(T("Conseils", 12, prefs.BottomTab == "advice" ? Tok.Text : Tok.Muted));
        // Filtres par gravité, comme les compteurs d'erreurs / avertissements / messages de Visual Studio.
        foreach (var sev in new[] { Severity.Critical, Severity.Important, Severity.Advice, Severity.Info })
        {
            var (icon, color) = SeverityIcon(sev);
            var n = r.Findings.Count(f => f.Severity == sev);
            var on = bottomFilter.Contains(sev);
            var chip = Clickable(T($"{icon} {n}", 11, on ? color : Tok.Faint, FontWeight.SemiBold, mono: true), () =>
            {
                if (!bottomFilter.Remove(sev)) bottomFilter.Add(sev);
                SetBottomTab("advice");
            }, on ? Tok.Window : null, Tok.HoverStrong, new Thickness(6, 1));
            chip.CornerRadius = new CornerRadius(3);
            chip.BorderBrush = on ? Tok.Border : Brushes.Transparent;
            chip.BorderThickness = new Thickness(1);
            ToolTip.SetTip(chip, $"{(on ? "Masquer" : "Afficher")} : {Fmt.Severity(sev).ToLowerInvariant()}");
            counts.Children.Add(chip);
        }
        var close = Clickable(T("×", 14, Tok.Muted).Centered(), ToggleBottom, hover: Tok.HoverStrong, padding: new Thickness(10, 0));
        ToolTip.SetTip(close, "Masquer le panneau (Ctrl+J)");
        DockPanel.SetDock(close, Dock.Right);
        var header = new Border
        {
            Height = 30, Background = Tok.Chrome, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    close,
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { Tab("advice", counts), Tab("journal", T($"Journal · {journal.Count}", 12, prefs.BottomTab == "journal" ? Tok.Text : Tok.Muted)) } },
                },
            },
        };

        var list = new StackPanel();
        if (prefs.BottomTab == "journal")
        {
            if (journal.Count == 0) list.Children.Add(T("Rien pour l'instant : imports, mises à jour et erreurs s'inscrivent ici.", 12, Tok.Faint).WithMargin(14, 8));
            foreach (var (at, text, error) in Enumerable.Reverse(journal))
                list.Children.Add(ColumnsSpaced("70,*", 10, T(at.ToString("HH:mm:ss"), 11, Tok.Muted, mono: true), T(text, 12, error ? Tok.Bad : Tok.Text2, wrap: true)).WithMargin(14, 3));
        }
        else
        {
            var shown = r.Findings.Where(f => bottomFilter.Contains(f.Severity)).OrderBy(f => f.Severity).ThenByDescending(f => f.Priority).ToList();
            if (shown.Count == 0) list.Children.Add(T("Aucun conseil avec ces filtres.", 12, Tok.Faint).WithMargin(14, 8));
            foreach (var f in shown)
            {
                var (icon, color) = SeverityIcon(f.Severity);
                var done = inputs.IsDone(f.Title, r.Character.FetchedAtUtc);
                var title = T(f.Title, 12, done ? Tok.Muted : null, FontWeight.Medium);
                if (done) title.TextDecorations = TextDecorations.Strikethrough;
                var finding = f;
                var row = Clickable(ColumnsSpaced("18,2*,3*,110", 10, T(icon, 12, color, FontWeight.SemiBold, mono: true), title,
                        T(f.Action is null ? "" : $"→ {f.Action}", 12, Tok.Muted), T(Fmt.Area(f.Area), 11, Tok.Muted, mono: true)),
                    () => views.Go(finding), hover: Tok.Hover, padding: new Thickness(14, 3));
                ToolTip.SetTip(row, f.Detail);
                list.Children.Add(row);
            }
        }
        var body = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(body, 1);
        return new Border
        {
            Background = Tok.Panel,
            Child = new Grid { RowDefinitions = new RowDefinitions("30,*"), Children = { header, body } },
        };
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
            ToolTip.SetTip(tab, $"{c.Name} — {className} niv. {c.Level} · {c.ServerName} ({c.Region}) · niveau d'objet {c.ItemLevel} · importé le {c.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}" +
                " · clic droit : actualiser, fermer…");
            tab.ContextFlyout = TabMenu(tabPath, c);
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

    /// <summary>Clic droit sur un onglet : actualiser ce personnage (sans devoir l'afficher d'abord), le marquer, fermer.</summary>
    MenuFlyout TabMenu(string path, CharacterSnapshot c)
    {
        var key = System.IO.Path.GetFileNameWithoutExtension(path);
        var mine = (prefs.Mine ?? []).Contains(key);
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        return new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ItemsSource = new List<Control>
            {
                Item("Actualiser depuis le site officiel", () => _ = ImportAsync(c.Name, (client, progress) => client.Fetch(db, c.Region, c.ServerId, c.CharacterId, progress)),
                    loading is null),
                Item(mine ? "Retirer de « Mes personnages »" : "Ajouter à « Mes personnages » ★", () =>
                {
                    var list = (prefs.Mine ?? []).ToList();
                    if (!list.Remove(key)) list.Add(key);
                    prefs = prefs with { Mine = list };
                    prefs.Save();
                    Render();
                }),
                new Separator(),
                Item("Fermer l'onglet", () => CloseTab(path)),
                Item("Fermer les autres onglets", () => { foreach (var other in tabs.Where(t => t != path).ToList()) CloseTab(other); SwitchTab(path); }, tabs.Count > 1),
            },
        };
    }

    Control Breadcrumb(AdviceReport r, bool hasInspector, string? crumb)
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
                T($"{r.Profile.Class.Name} · {r.Profile.Role.Name} · niv. {c.Level}", 12, Tok.Muted, mono: true),
                T($"{c.ServerName} ({c.Region})", 12, Tok.Muted, mono: true),
                Value("niv. d'objet", Fmt.Big(c.ItemLevel)), Value("puissance", Fmt.Big(c.CombatPower)),
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
        // Chaque segment calé à gauche, le dernier remplit le reste : trop long, il se termine par « … » au lieu de passer
        // sous la partie droite.
        var parts = new List<TextBlock> { T(c.Name.ToLowerInvariant(), 12, Tok.Muted, mono: true), T("›", 12, Tok.Muted, mono: true), T(ScreenViews.Title(state.Screen), 12, Tok.Text, mono: true) };
        if (crumb is not null) parts.AddRange([T("›", 12, Tok.Muted, mono: true), T(crumb, 12, Tok.Text, mono: true)]);
        var left = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        foreach (var part in parts)
        {
            part.Margin = new Thickness(0, 0, 8, 0);
            if (part != parts[^1]) DockPanel.SetDock(part, Dock.Left);
            left.Children.Add(part);
        }
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
            if (report is not null)
            {
                // Les conseils, comptés comme partout ailleurs : un clic ouvre ou ferme le panneau du bas.
                var counter = Clickable(T(Fmt.CountsText(report.Findings), 11, Tok.StatusText, FontWeight.SemiBold, mono: true),
                    () => { if (prefs.BottomHidden) SetBottomTab("advice"); else ToggleBottom(); }, hover: Tok.Alpha(Brushes.White, 0.12), padding: new Thickness(6, 0));
                ToolTip.SetTip(counter, "Clic : la liste des conseils en bas de l'écran (Ctrl+J). Importants = critiques et importants ; les infos sont à part.");
                left.Children.Add(counter);
            }
        }
        if (!prefs.TipsSeen)  // tant qu'aucune infobulle n'a été ouverte : dire qu'il y en a
        {
            tipHint = T("astuce : survole ce qui est souligné en pointillés (curseur ?) pour une explication", 11, Tok.StatusText, mono: true);
            left.Children.Add(tipHint);
        }
        statusText = T(StatusMessage, 11, Tok.StatusText, mono: true);
        left.Children.Add(statusText);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(MeterPill());
        if (current is not null) right.Children.Add(T($"importé {current.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}", 11, Tok.StatusText, mono: true));
        right.Children.Add(T(dataSource, 11, Tok.StatusText, mono: true));
        right.Children.Add(T(Updater.CurrentLabel, 11, Tok.StatusText, mono: true));
        DockPanel.SetDock(right, Dock.Right);
        return new Border { Background = Accent.Bar, Padding = new Thickness(12, 0), Child = new DockPanel { Children = { right, left } } };
    }

    // ----- Pet Meter : pastille d'état et lancement -----

    PetMeterLink.Status? meter;
    TextBlock? meterText;
    Border? meterPill;
    DispatcherTimer? meterTimer;
    bool meterStarting;

    /// <summary>
    /// Pastille de la barre d'état : le Pet Meter en route ou non (relu toutes les 5 s), sa version, ta clé Aion 2 Todo, ses
    /// derniers combat et récolte reçus (infobulle) ; un clic le lance s'il est arrêté.
    /// </summary>
    Control MeterPill()
    {
        if (meterTimer is null)
        {
            meterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            meterTimer.Tick += (_, _) => _ = RefreshMeterAsync();
            meterTimer.Start();
            _ = RefreshMeterAsync();
        }
        meterText = T("", 11, Tok.StatusText, FontWeight.SemiBold, mono: true);
        meterPill = Clickable(meterText, () => _ = PetMeterAsync(), hover: Tok.Alpha(Brushes.White, 0.12), padding: new Thickness(6, 0));
        ShowMeter();
        return meterPill;
    }

    async Task RefreshMeterAsync()
    {
        var chosen = prefs.PetMeterExe;
        PetMeterLink.Status status;
        try { status = PetMeterLink.WithKey(await Task.Run(() => PetMeterLink.Read(chosen))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        // Trouvé en cours d'exécution ailleurs que l'exe retenu : c'est celui-là qu'on relancera.
        if (status.Running && status.Exe is { } exe && exe != prefs.PetMeterExe && (prefs.PetMeterExe is null || !File.Exists(prefs.PetMeterExe)))
        {
            prefs = prefs with { PetMeterExe = exe };
            prefs.Save();
        }
        if (status == meter) return;
        meter = status;
        ShowMeter();
    }

    void ShowMeter()
    {
        if (meterText is null || meterPill is null) return;
        var m = meter;
        meterText.Text = m is null ? "Pet Meter…" : m.Running ? $"● Pet Meter{(m.Version is { } v ? $" {v}" : "")}" : meterStarting ? "Pet Meter : lancement…" : "▶ lancer le Pet Meter";
        ToolTip.SetTip(meterPill, MeterTip(m));
    }

    /// <summary>État détaillé (infobulle), une ligne par point.</summary>
    static string MeterTip(PetMeterLink.Status? m) => m is null ? "Pet Meter : lecture de son état…" : string.Join("\n",
        m.Running ? "Pet Meter en route" : m.Exe is null ? "Pet Meter arrêté, introuvable : clic pour choisir son exe (aion2-pet-meter.exe)" : "Pet Meter arrêté : clic pour le lancer",
        $"Version : {m.Version ?? "non lue"}{(m.Version is not null && !m.Running ? " (celle de son dernier fichier)" : "")}",
        $"Exe : {m.Exe ?? "introuvable (Données → Choisir l'exe du Pet Meter…)"}",
        $"Clé Aion 2 Todo : {(m.Key ? "trouvée (familiers et récolte de ton compte)" : "aucune (à coller dans le Pet Meter : ⋯ → Clé de synchro)")}",
        $"Dernier combat reçu : {Ago(m.LastFight) ?? "aucun"}",
        $"Dernière récolte reçue : {Ago(m.LastGather) ?? "aucune"}");

    /// <summary>« 04/11 18:57 (il y a 3 h) ».</summary>
    static string? Ago(DateTime? at) => at is not { } t ? null : $"{t:dd/MM HH:mm} ({(DateTime.Now - t) switch
    {
        var d when d.TotalMinutes < 60 => $"il y a {Math.Max(1, (int)d.TotalMinutes)} min",
        var d when d.TotalHours < 24 => $"il y a {(int)d.TotalHours} h",
        var d => $"il y a {(int)d.TotalDays} j",
    }})";

    /// <summary>Lance le Pet Meter s'il ne tourne pas (exe trouvé, sinon à choisir) ; en route, rappelle son état.</summary>
    async Task PetMeterAsync()
    {
        var status = meter = PetMeterLink.WithKey(await Task.Run(() => PetMeterLink.Read(prefs.PetMeterExe)));
        ShowMeter();
        if (status.Running)
        {
            Toast("Le Pet Meter tourne déjà", $"{status.Version ?? "version non lue"} · clé Aion 2 Todo {(status.Key ? "trouvée" : "absente")} · " +
                $"dernier combat {Ago(status.LastFight) ?? "aucun"} · dernière récolte {Ago(status.LastGather) ?? "aucune"}");
            return;
        }
        if (meterStarting || (status.Exe ?? await ChoosePetMeterAsync()) is not { } exe) return;
        try
        {
            PetMeterLink.Start(exe);
            meterStarting = true;
            SetStatus("Pet Meter lancé");
            Toast("Pet Meter lancé", "Il lit le trafic du jeu pendant que tu joues : ses combats et sa récolte arrivent ici tout seuls.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            SetStatus($"Lancement du Pet Meter impossible : {ex.Message}", error: true);
            Toast("Lancement du Pet Meter impossible", ex.Message, error: true);
            return;
        }
        ShowMeter();
        // Un exe « un seul fichier » met quelques secondes à démarrer : la pastille se met à jour ensuite.
        DispatcherTimer.RunOnce(() => { meterStarting = false; _ = RefreshMeterAsync(); }, TimeSpan.FromSeconds(4));
    }

    async Task<string?> ChoosePetMeterAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Où est le Pet Meter (aion2-pet-meter.exe) ?",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Programme") { Patterns = ["*.exe"] }],
        });
        if (files.FirstOrDefault(f => f.Path.IsFile)?.Path.LocalPath is not { } path) return null;
        prefs = prefs with { PetMeterExe = path };
        prefs.Save();
        _ = RefreshMeterAsync();
        return path;
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

        // Page « nouvel onglet » : la recherche (un perso à toi ou celui d'un pote), puis tes personnages et ceux déjà consultés.
        var palette = new Border
        {
            Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            BoxShadow = BoxShadows.Parse("0 12 40 0 #80000000"),
            Child = new StackPanel { Spacing = 2, Children = { input, Label("Site officiel").WithMargin(12, 8, 12, 4), hitsPanel, new Border { Height = 6 } } },
        };
        var hint = T("Entrée pour importer le meilleur résultat · un clic ouvre le personnage dans un onglet · Ctrl+K pour revenir ici", 12, Tok.Muted, mono: true);
        var list = new Border
        {
            Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Child = new StackPanel { Children = { HistoryHeader(), historyPanel, new Border { Height = 6 } } },
        };
        var page = new StackPanel
        {
            Width = 860, Spacing = 14, Margin = new Thickness(0, 28, 0, 40), HorizontalAlignment = HorizontalAlignment.Center,
            Children = { T("Nouvel onglet", 17, Tok.Text, FontWeight.SemiBold), palette, hint, list },
        };
        return new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
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
        return new DockPanel { Margin = new Thickness(12, 10, 12, 4), Children = { historyCount, Label($"Personnages enregistrés · score {ScreenViews.ModeLabel(prefs.Mode)}") } };
    }

    /// <summary>« ★ » range un personnage dans « Mes personnages » (les autres sont ceux consultés, ceux d'un pote par exemple).</summary>
    void ToggleMine(SavedCharacter s)
    {
        var mine = (prefs.Mine ?? []).ToList();
        if (!mine.Remove(s.Key)) mine.Add(s.Key);
        prefs = prefs with { Mine = mine };
        prefs.Save();
        RenderHistory();
    }

    /// <summary>
    /// Personnages enregistrés en deux groupes : les tiens (★), puis les autres consultés ; du plus récent au plus ancien,
    /// filtrés par le nom tapé (sans tenir compte des majuscules ni des accents).
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

        if (all.Count == 0) { historyPanel.Children.Add(T("aucun personnage pour l'instant : cherche le tien ou celui d'un pote ci-dessus", 13, Tok.Faint).WithMargin(12, 7)); return; }
        if (shown.Count == 0) { historyPanel.Children.Add(T($"aucun personnage enregistré ne correspond à « {query} »", 13, Tok.Faint).WithMargin(12, 7)); return; }
        ComputeOverviewScores(shown.Select(x => x.Character).ToList());

        var mine = (prefs.Mine ?? []).ToHashSet();
        var groups = new[]
        {
            ("Mes personnages ★", shown.Where(x => mine.Contains(x.Character.Key)).ToList()),
            ("Autres personnages consultés", shown.Where(x => !mine.Contains(x.Character.Key)).ToList()),
        };
        foreach (var (title, rows) in groups)
        {
            historyPanel.Children.Add(Label($"{title} · {rows.Count}").WithMargin(12, 10, 12, 4));
            if (rows.Count == 0)
                historyPanel.Children.Add(T(title.StartsWith("Mes", StringComparison.Ordinal)
                    ? "clique ☆ devant un personnage pour le ranger ici" : "aucun", 12, Tok.Faint).WithMargin(12, 4));
            foreach (var (s, viewedAt) in rows) historyPanel.Children.Add(HistoryRow(s, viewedAt, mine.Contains(s.Key)));
        }
    }

    Control HistoryRow(SavedCharacter s, DateTime viewedAt, bool isMine)
    {
        var known = overviewScores.TryGetValue(s.Path, out var sc) && sc.FetchedAtUtc == s.FetchedAtUtc && sc.Mode == prefs.Mode;
        var score = Tip(T(known ? sc.Score.ToString() : "··", 13, known ? Tok.Score(sc.Score) : Tok.Faint, FontWeight.SemiBold, mono: true),
            known ? $"Score global {ScreenViews.ModeLabel(prefs.Mode)} : {sc.Score}/100 · stats {sc.Stats} · équipement {sc.Gear} · compétences {sc.Skills} · Daevanion {sc.Daevanion}" : "calcul du score…");
        score.TextAlignment = TextAlignment.Right;
        var isOpen = tabs.Contains(s.Path);
        var isCurrent = s.Path == currentPath;
        var saved = s;
        var star = Clickable(T(isMine ? "★" : "☆", 15, isMine ? Accent.Main : Tok.Muted).Centered(), () => ToggleMine(saved), hover: Tok.HoverStrong, padding: new Thickness(4, 0));
        star.CornerRadius = new CornerRadius(3);
        ToolTip.SetTip(star, isMine ? "Retirer de « Mes personnages »" : "Ranger dans « Mes personnages »");
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                T(s.Name, 13, isCurrent ? Accent.Main : null, FontWeight.SemiBold),
                T($"{db.FindClass(s.ClassCode)?.Name ?? s.ClassCode} {s.Level}", 12, Tok.Text2, mono: true),
            },
        };
        if (isOpen) name.Children.Add(new Border { Padding = new Thickness(5, 0), CornerRadius = new CornerRadius(3), Background = Accent.Dim, Child = T("ouvert", 10, Accent.Main, mono: true) });
        var row = Clickable(ColumnsSpaced("26,*,150,70,40,110", 8,
                star, name, T($"{s.ServerName} ({s.Region})", 12, Tok.Muted, mono: true), Tip(T($"obj {Fmt.Big(s.ItemLevel)}", 12, Tok.Muted, mono: true), "Niveau d'objet"), score,
                T($"importé {s.FetchedAtUtc.ToLocalTime():dd/MM}", 12, Tok.Muted, mono: true)),
            () => OpenSaved(saved), hover: Tok.HoverStrong, padding: new Thickness(8, 6));
        ToolTip.SetTip(row, $"{(isOpen ? "Revenir à son onglet" : "Ouvrir dans un onglet")} · consulté le {viewedAt.ToLocalTime():dd/MM/yyyy à HH:mm} · importé le {s.FetchedAtUtc.ToLocalTime():dd/MM/yyyy à HH:mm}");
        return row;
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

    readonly PeerStore peerStore = new();

    /// <summary>Ajoute un joueur à ta liste de comparaison : lu sur le site officiel (région de ton perso), mesuré comme toi.</summary>
    async Task AddPeerAsync(string name, string server)
    {
        if (current is null || string.IsNullOrWhiteSpace(name)) return;
        var region = current.Region;
        SetStatus($"Ajout de {name.Trim()} à ta liste…");
        try
        {
            using var client = ArmoryClient.Create();
            var (snapshot, _) = await client.FetchByName(db, region, name.Trim(), string.IsNullOrWhiteSpace(server) ? null : server.Trim(),
                new Progress<string>(m => SetStatus(m)));
            var (player, _) = await Task.Run(() => PeerMeasure.Measure(db, snapshot));
            peerStore.Put(new SavedPeer(snapshot.Name, snapshot.ServerName, region, snapshot.ServerId, snapshot.CharacterId, snapshot.ClassCode, DateTime.UtcNow, player));
            SetStatus($"{snapshot.Name} ajouté à ta liste");
            Toast($"{snapshot.Name} ajouté à ta liste", $"{db.FindClass(snapshot.ClassCode)?.Name} · niveau d'objet {snapshot.ItemLevel}" +
                  (snapshot.ClassCode == current.ClassCode ? "" : " : une autre classe, il servira pour tes persos de cette classe"));
            Recompute();
        }
        catch (ArmoryException ex)
        {
            SetStatus(ex.Message, error: true);
            Toast($"Ajout de {name.Trim()} impossible", ex.Message, error: true);
        }
    }

    /// <summary>
    /// Captures de la fenêtre Stats du jeu : lues sur le PC (OCR de Windows), puis le perso est relu sur le site officiel (même
    /// équipement que sur les captures) et chaque stat lue donne sa part cachée. Ouvre ensuite Stats → Stats en jeu pour vérifier.
    /// </summary>
    async Task ImportInGameAsync()
    {
        if (current is null || report is null) { Toast("Ouvre d'abord un personnage", error: true); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = $"Captures de la fenêtre Stats de {current.Name} (Stats principales, Stats détaillées)",
            AllowMultiple = true,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });
        var paths = files.Where(f => f.Path.IsFile).Select(f => f.Path.LocalPath).ToList();
        if (paths.Count == 0) return;
        SetStatus($"Lecture de {Fmt.Plural(paths.Count, "capture")}…");
        try
        {
            var pages = new List<IReadOnlyList<OcrWord>>();
            foreach (var path in paths) pages.Add(await ScreenshotOcr.Words(path, InGameStatsReader.MinOcrSize));
            var values = InGameStatsReader.Read(db, pages);
            if (values.Count == 0)
            {
                SetStatus("");
                Toast("Aucune stat lue", "Il faut des captures de la fenêtre « Infos sur les stats » du jeu (touche P), onglet Stats principales ou Stats détaillées.", error: true);
                return;
            }
            await RefreshAsync();  // comparer au même équipement que sur les captures
            if (report is null || current is null) return;
            OnInputs(InGameStatsReader.WithInGame(report, inputs, values.ToDictionary(kv => kv.Key, kv => (double?)kv.Value)));
            Update(st => { st.Screen = Screen.Stats; st.Open.Add("stats:ingame"); });
            SetStatus($"{Fmt.Plural(values.Count, "stat lue", "stats lues")} en jeu");
            Toast($"{Fmt.Plural(values.Count, "stat lue", "stats lues")} sur tes captures", "Vérifie-les dans Stats → Stats en jeu : une valeur mal lue se corrige à la main.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message, error: true);
            Toast("Lecture des captures impossible", ex.Message, error: true);
        }
    }

    // Récolte du compte (Aion 2 Todo) : relue à l'ouverture de l'écran Récolte, au plus toutes les 2 minutes.
    List<GatherDay>? accountGather;
    string? accountGatherStatus;
    DateTime accountGatherAt;
    bool accountGatherLoading;

    /// <summary>La clé Aion 2 Todo : celle reliée dans le Builder, sinon celle du Pet Meter de ce PC.</summary>
    /// <summary>
    /// Clé Aion 2 Todo : celle de l'écran Familiers, sinon celle du Pet Meter de ce PC. Une clé que le site refuse (remplacée par
    /// une nouvelle, souvent depuis l'autre PC) est écartée pour la session, et la suivante est essayée.
    /// </summary>
    string? AccountKey() => new[] { pets.Key, PetStore.PetMeterKey() }.FirstOrDefault(k => k is { Length: > 0 } && !refusedKeys.Contains(k));
    readonly HashSet<string> refusedKeys = [];
    const string KeyRefused = "Ta clé Aion 2 Todo est refusée par le site : elle a été remplacée par une nouvelle (peut-être depuis ton autre PC). " +
                              "Recolle la clé actuelle dans l'écran Familiers (le site la montre dans son onglet Familiers → Pet Meter).";

    async Task LoadAccountGatherAsync()
    {
        if (accountGatherLoading || DateTime.UtcNow - accountGatherAt < TimeSpan.FromMinutes(2) || AccountKey() is not { } key) return;
        accountGatherLoading = true;
        accountGatherAt = DateTime.UtcNow;
        if (accountGather is null) accountGatherStatus = "Lecture de la récolte de ton compte sur Aion 2 Todo… (le site peut mettre une minute à se réveiller)";
        try
        {
            using var client = PetSyncClient.Create();
            while (true)
            {
                try { accountGather = await client.Gathering(key); accountGatherStatus = null; break; }
                catch (PetSyncException ex) when (ex.KeyRefused)
                {
                    refusedKeys.Add(key);
                    if (AccountKey() is not { } next) { accountGatherStatus = KeyRefused + " En attendant, seuls les jours de ce PC sont montrés."; break; }
                    key = next;
                }
            }
        }
        catch (Exception ex) when (ex is ArmoryException or System.Text.Json.JsonException)
        {
            accountGatherStatus = $"Récolte du compte illisible ({ex.Message}) : seuls les jours de ce PC sont montrés.";
        }
        finally { accountGatherLoading = false; }
        if (state.Screen is Screen.Gather or Screen.Market or Screen.Inventory or Screen.Craft) Render();
    }

    MarketData? market;
    string? marketStatus;
    List<MarketShotLine>? marketShot;  // lignes lues sur des captures de l'hôtel, en attente de validation
    DateTime pendingStamp = DateTime.MinValue;  // date du fichier des lignes en attente déjà reprise (hdv-pending.json)

    /// <summary>
    /// Lignes en attente gardées sur le disque (HdvRelay.PendingFile) : elles survivent à la fermeture du Builder, et le relevé du
    /// Pet Meter y ajoute les siennes (nom inconnu, prix refusé).
    /// </summary>
    void KeepShot()
    {
        HdvRelay.SavePending(marketShot);
        pendingStamp = File.Exists(HdvRelay.PendingFile) ? File.GetLastWriteTimeUtc(HdvRelay.PendingFile) : default;
    }

    /// <summary>Le fichier des lignes en attente a changé (relevé du Pet Meter, ou lancement) : ses lignes rejoignent « Lire une capture ».</summary>
    void SyncPendingShot()
    {
        var stamp = File.Exists(HdvRelay.PendingFile) ? File.GetLastWriteTimeUtc(HdvRelay.PendingFile) : default;
        if (stamp == pendingStamp) return;
        pendingStamp = stamp;
        if (HdvRelay.LoadPending() is not { Count: > 0 } waiting) return;
        marketShot = (marketShot ?? []).Concat(waiting).GroupBy(l => Market.Fold(l.Name)).Select(g => g.Last()).ToList();
        marketShotStatus = $"{Fmt.Plural(marketShot.Count, "ligne lue attend", "lignes lues attendent")} d'être notée{(marketShot.Count > 1 ? "s" : "")} " +
                           "(relevé du Pet Meter ou capture pas encore notée) : vérifie les noms marqués « nouveau », puis « noter ».";
    }
    string? marketShotStatus;

    /// <summary>
    /// Captures de la fenêtre Marché du jeu lues sur ce PC (OCR de Windows) : « latest » = la plus récente des captures d'écran
    /// de Windows (Images\Captures d'écran, Vidéos\Captures), de moins de 30 min ; null = fichiers choisis. Les lignes lues
    /// attendent ta validation dans l'écran HdV.
    /// </summary>
    /// <summary>La capture d'écran de Windows la plus récente (Images\Captures d'écran, Vidéos\Captures), de moins de 30 min.</summary>
    static FileInfo? LatestScreenshot()
    {
        var folders = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Captures d'écran"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures"),
        };
        return folders.Where(d => Directory.Exists(d))
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles().Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp"))
            .Where(f => DateTime.Now - f.LastWriteTime < TimeSpan.FromMinutes(30)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
    }

    /// <summary>
    /// Écran Craft : lit une capture de la fenêtre d'artisanat du jeu (la dernière capture d'écran, ou un fichier choisi) et
    /// remplit « Noter une recette » ; rien n'est noté sans ton clic.
    /// </summary>
    async Task ReadCraftShotAsync(string? which)
    {
        string? path;
        if (which == "latest")
        {
            path = LatestScreenshot()?.FullName;
            if (path is null)
            {
                Update(st => st.CraftShotStatus = "Aucune capture d'écran de moins de 30 min (Images\\Captures d'écran, Vidéos\\Captures) : fais-en une de la fenêtre " +
                                                  "d'artisanat avec la recette choisie (Impr. écran), ou « choisir une capture… ».");
                return;
            }
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Capture de la fenêtre d'artisanat du jeu", AllowMultiple = false,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }],
            });
            path = files.FirstOrDefault(f => f.Path.IsFile)?.Path.LocalPath;
            if (path is null) return;
        }
        Update(st => st.CraftShotStatus = "Lecture de la capture…");
        try
        {
            var (shot, problem) = await CraftShots.ReadAsync(path);
            if (shot is null) { Update(st => st.CraftShotStatus = "Capture refusée : " + problem); return; }
            Update(st => st.CraftShotStatus = CraftShotFill.Fill(st.CraftDraft, shot, market));
            // Les noms entiers que la liste des objets ne connaît pas (objets fabriqués souvent absents de l'hôtel) y entrent, pour tous.
            var names = CraftShotFill.NewNames(shot, market);
            if (names.Count > 0 && AccountKey() is { } key)
            {
                int added;
                try { added = await Market.AddCrafted(key, names); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { added = 0; }
                if (added > 0)
                {
                    Update(st => st.CraftShotStatus = (st.CraftShotStatus ?? "") +
                        $" {Fmt.Plural(added, "objet ajouté", "objets ajoutés")} à la liste des objets, pour tous : {string.Join(", ", names.Take(6).Select(n => n.Name))}{(names.Count > 6 ? "…" : "")}.");
                    await LoadMarketAsync(force: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Update(st => st.CraftShotStatus = $"Lecture impossible : {ex.Message}");
        }
    }

    async Task ReadMarketShotsAsync(string? which)
    {
        List<string> paths;
        if (which == "latest")
        {
            var latest = LatestScreenshot();
            if (latest is null)
            {
                marketShotStatus = "Aucune capture d'écran de moins de 30 min (Images\\Captures d'écran, Vidéos\\Captures) : fais-en une du Marché du jeu, ou « choisir des captures… ».";
                Render();
                return;
            }
            paths = [latest.FullName];
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Captures de la fenêtre Marché du jeu", AllowMultiple = true,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
            });
            paths = files.Where(f => f.Path.IsFile).Select(f => f.Path.LocalPath).ToList();
            if (paths.Count == 0) return;
        }
        marketShotStatus = $"Lecture de {Fmt.Plural(paths.Count, "capture")}…";
        Render();
        try
        {
            var lines = new List<MarketShotLine>();
            var refused = 0;
            foreach (var path in paths)
            {
                // Image doublée : sur un écran 1080p ou plus petit, le texte du Marché est trop fin pour l'OCR (annonces perdues, accents).
                lines.AddRange(MarketShotReader.Read(await ScreenshotOcr.Words(path, InGameStatsReader.MinOcrSize, minScale: 2, colors: true), out var problem));
                if (problem is not null) refused++;
            }
            // Un même objet sur deux captures : la dernière lecture l'emporte ; les lignes qui attendaient déjà restent.
            marketShot = (marketShot ?? []).Concat(lines).GroupBy(l => Market.Fold(l.Name)).Select(g => g.Last()).ToList();
            marketShotStatus = refused > 0
                ? (paths.Count == 1 ? "Capture refusée : " : $"{Fmt.Plural(refused, "capture refusée", "captures refusées")} sur {paths.Count} : ") + MarketShotReader.NoTitles
                : lines.Count == 0
                    ? "Aucune ligne de prix lue sous les titres des colonnes : il faut la liste des objets ou les annonces d'un objet, avec leurs prix."
                    : null;
            if (marketShot.Count == 0) marketShot = null;
            KeepShot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            marketShotStatus = $"Lecture impossible : {ex.Message}";
            marketShot = null;
        }
        Render();
    }

    async Task SaveMarketShotAsync(IReadOnlyList<MarketShotLine> lines, string marketCode)
    {
        if (AccountKey() is not { } key) { marketShotStatus = "Il faut ta clé Aion 2 Todo (écran Familiers)."; Render(); return; }
        var items = market?.Items ?? [];
        // Un objet pas encore dans la liste entre avec le rang lu à la couleur de son nom (sinon il resterait sans couleur).
        var send = lines.Select(line => items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(line.Name)) is { } item
            ? new PriceLine(item.Key, item.Name, line.Price) : new PriceLine(null, line.Name, line.Price, line.Grade)).ToList();
        marketShotStatus = $"Envoi de {Fmt.Plural(lines.Count, "prix", "prix")} à Aion 2 Todo…";
        Render();
        var results = await Market.AddMany(key, marketCode, send);
        var errors = new List<string>();
        var failed = new List<MarketShotLine>();  // gardées à l'écran pour être corrigées ou renvoyées ; les autres sont notées
        for (var i = 0; i < lines.Count; i++)
        {
            if (results[i] is not { } error) continue;
            errors.Add($"{(lines[i].Name.Length > 0 ? lines[i].Name : "(sans nom)")} : {error}");
            failed.Add(lines[i]);
        }
        var done = lines.Count - failed.Count;
        marketShot = failed.Count == 0 ? null : failed;
        KeepShot();
        // Tout refusé pour la même raison (site injoignable, clé refusée) : une seule ligne.
        marketShotStatus = errors.Count == 0 ? null
            : failed.Count == lines.Count && results.Distinct().Count() == 1 ? $"Aucun prix noté : {results[0]}"
            : string.Join("\n", errors.Take(4)) + (errors.Count > 4 ? $"\n… et {errors.Count - 4} autres" : "");
        SetStatus($"{Fmt.Plural(done, "prix noté", "prix notés")} depuis la capture (partagés avec la communauté)", errors.Count > 0);
        await LoadMarketAsync(force: true);
        Render();
    }
    DateTime marketAt;
    bool marketLoading;

    /// <summary>Prix HdV de la communauté (Aion 2 Todo, avec ta clé) : relus au plus toutes les minutes, ou tout de suite après un changement.</summary>
    async Task LoadMarketAsync(bool force = false)
    {
        if (marketLoading || (!force && DateTime.UtcNow - marketAt < TimeSpan.FromMinutes(1)) || AccountKey() is not { } key) return;
        marketLoading = true;
        marketAt = DateTime.UtcNow;
        if (market is null) marketStatus = "Lecture des prix de la communauté sur Aion 2 Todo… (le site peut mettre une minute à se réveiller)";
        try
        {
            while (true)
            {
                try { market = await Market.Load(key); marketStatus = null; break; }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Journal.Write("prix HdV : clé refusée par le site");
                    refusedKeys.Add(key);
                    if (AccountKey() is not { } next) { marketStatus = KeyRefused; break; }
                    key = next;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            marketStatus = $"Prix de la communauté illisibles ({ex.Message}).";
            Journal.Write("prix HdV : " + ex.Message);
        }
        finally { marketLoading = false; }
        if (state.Screen is Screen.Gather or Screen.Market or Screen.Inventory or Screen.Craft) Render();
    }

    /// <summary>Écran Inventaire : nommer un objet de ton sac pour tous ; la liste des noms est relue aussitôt.</summary>
    async Task<string?> NameItemAsync(long id, string name)
    {
        if (AccountKey() is not { } key) return "Il faut ta clé Aion 2 Todo (écran Familiers).";
        string? error;
        try { error = await Market.Name(key, id, name); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = $"Aion 2 Todo injoignable ({ex.Message})"; }
        if (error is null) await LoadMarketAsync(force: true);
        return error;
    }

    /// <summary>Écran Craft : noter une recette pour tous ; les recettes sont relues aussitôt.</summary>
    async Task<string?> AddRecipeAsync(RecipeSend recipe)
    {
        if (AccountKey() is not { } key) return "Il faut ta clé Aion 2 Todo (écran Familiers).";
        string? error;
        try { error = await Market.AddRecipe(key, recipe); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = $"Aion 2 Todo injoignable ({ex.Message})."; }
        if (error is null)
        {
            SetStatus($"recette notée : {recipe.Output.Name} (partagée avec la communauté)");
            await LoadMarketAsync(force: true);
        }
        return error;
    }

    /// <summary>Écran Craft : corriger une recette pour tous (l'ancienne reste rétablissable) ; les recettes sont relues aussitôt.</summary>
    async Task<string?> ReplaceRecipeAsync(int id, RecipeSend recipe)
    {
        if (AccountKey() is not { } key) return "Il faut ta clé Aion 2 Todo (écran Familiers).";
        string? error;
        try { error = await Market.ReplaceRecipe(key, id, recipe); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = $"Aion 2 Todo injoignable ({ex.Message})."; }
        if (error is null)
        {
            SetStatus($"recette corrigée : {recipe.Output.Name} (l'ancienne version reste dans les retirées 30 jours)");
            await LoadMarketAsync(force: true);
        }
        return error;
    }

    /// <summary>Écran Craft : noter le prix d'un objet du PNJ pour tous ; la liste est relue aussitôt.</summary>
    async Task<string?> AddNpcAsync(string name, long price, string? tab)
    {
        if (AccountKey() is not { } key) return "Il faut ta clé Aion 2 Todo (écran Familiers).";
        string? error;
        try { error = await Market.AddNpc(key, name, price, tab); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = $"Aion 2 Todo injoignable ({ex.Message})."; }
        if (error is null)
        {
            SetStatus($"prix du PNJ noté : {name} (partagé avec la communauté)");
            await LoadMarketAsync(force: true);
        }
        return error;
    }

    async Task<string?> AddPriceAsync(string? itemKey, string itemName, long price, string marketCode)
    {
        if (AccountKey() is not { } key) return "Il faut ta clé Aion 2 Todo (écran Familiers).";
        string? error;
        try { error = await Market.Add(key, itemKey, itemName, price, marketCode); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = $"Aion 2 Todo injoignable ({ex.Message})."; }
        if (error is null)
        {
            SetStatus($"prix noté : {itemName}, {Fmt.Big(price)} Kinah (partagé avec la communauté)");
            await LoadMarketAsync(force: true);
        }
        return error;
    }

    async Task MarketActionAsync(Func<string, Task<string?>> action, string done)
    {
        if (AccountKey() is not { } key) return;
        string? error;
        try { error = await action(key); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = $"Aion 2 Todo injoignable ({ex.Message})"; }
        SetStatus(error ?? done, error is not null);
        await LoadMarketAsync(force: true);
    }

    void RemovePeer(string characterId)
    {
        peerStore.Remove(characterId);
        Recompute();
    }

    async Task RefreshAsync()
    {
        if (current is null) { ShowImport(); return; }
        var c = current;
        await ImportAsync(c.Name, (client, progress) => client.Fetch(db, c.Region, c.ServerId, c.CharacterId, progress));
    }

    /// <summary>
    /// « ouvrir sa fiche » d'un joueur d'un combat : le Pet Meter ne donne que son nom (et sa classe) ; il est cherché dans la
    /// région de ton perso, parmi les noms exacts de sa classe. Un seul : importé et ouvert dans un onglet ; plusieurs serveurs :
    /// un menu sous le lien pour choisir.
    /// </summary>
    async Task OpenPlayerAsync(string name, string? classCode, Control anchor)
    {
        if (current is null || loading is not null) return;
        var region = current.Region;
        SetStatus($"Recherche de {name} en région {region}…");
        List<SearchHit> found;
        try
        {
            using var client = ArmoryClient.Create();
            var hits = await client.Search(region, name);
            found = hits.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (classCode is not null && found.Count(h => h.ClassCode == classCode) is > 0) found = found.Where(h => h.ClassCode == classCode).ToList();
        }
        catch (ArmoryException ex)
        {
            SetStatus(ex.Message, error: true);
            Toast($"Recherche de {name} impossible", ex.Message, error: true);
            return;
        }
        Task Import(SearchHit h) => ImportAsync(h.Name, (client, progress) => client.Fetch(db, region, h.ServerId, h.CharacterId, progress), h.CharacterId);
        switch (found.Count)
        {
            case 0:
                SetStatus($"Aucun personnage « {name} » en région {region}.", error: true);
                Toast($"{name} introuvable", $"Aucun personnage de ce nom en région {region} sur le site officiel.", error: true);
                return;
            case 1:
                await Import(found[0]);
                return;
        }
        SetStatus($"{found.Count} personnages « {name} » : choisis le serveur.");
        new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ItemsSource = found.OrderBy(h => h.ServerId != current.ServerId).ThenBy(h => h.ServerName).Select(h =>
            {
                var item = new MenuItem { Header = $"{h.Name} · {h.ServerName} · {db.FindClass(h.ClassCode ?? "")?.Name ?? "?"} niv. {h.Level}" };
                item.Click += (_, _) => _ = Import(h);
                return item;
            }).ToList(),
        }.ShowAt(anchor);
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
            var previous = store.Find(snapshot.Name, snapshot.ServerName) is { } saved ? TryLoad(saved.Path) : null;
            // Conseils de l'import précédent, refaits avec cette version : l'import qui arrive dira ceux qui ont été réalisés.
            if (previous is not null && previous.FetchedAtUtc != snapshot.FetchedAtUtc)
                try
                {
                    var before = new Advisor(db).Analyze(previous, prefs.Mode, store.LoadInputs(previous), pets.Collection,
                        current?.FileKey == previous.FileKey ? combats.Select(x => x.Log).ToList() : null);
                    store.RecordTodo(previous, prefs.Mode, ProgressPoint.Todos(before.Findings));
                }
                catch (InvalidOperationException) { /* fiche de classe absente : rien à comparer */ }
            var path = store.Save(snapshot);
            prefs = prefs with { LastFile = path, Name = search.Text ?? snapshot.Name, Region = snapshot.Region };
            prefs.Save();
            SetStatus($"importé depuis le site officiel");
            loading = null;
            Open(snapshot, path);
            if (previous is null) Toast($"{snapshot.Name} importé", $"{db.FindClass(snapshot.ClassCode)?.Name} niv. {snapshot.Level} · {snapshot.ServerName}");
            else
            {
                var changes = ProgressPoint.Of(snapshot).ChangesSince(ProgressPoint.Of(previous), db.SlotName);
                Toast($"{snapshot.Name} actualisé", changes.Count == 0 ? "Rien n'a changé depuis le dernier import."
                    : string.Join(" · ", changes.Take(3)) + (changes.Count > 3 ? $" · et {Fmt.Plural(changes.Count - 3, "autre")}" : ""));
            }
        }
        catch (ArmoryException ex)
        {
            importError = ex.Message;
            SetStatus(ex.Message, error: true);
            Toast($"Import de {name} impossible", ex.Message, error: true);
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

    /// <summary>
    /// Captures de la fenêtre « Détails du combat » de NotMeter : lues par l'OCR de Windows, fusionnées en un combat,
    /// enregistrées pour le personnage affiché. Le combat le plus récent alimente ensuite le focus dégâts et les conseils.
    /// </summary>
    async Task ImportCombatAsync()
    {
        if (current is null || CurrentKey is not { } key) { Toast("Ouvre d'abord un personnage", error: true); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = $"Captures NotMeter « Détails du combat » de {current.Name}",
            AllowMultiple = true,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] }],
        });
        var paths = files.Where(f => f.Path.IsFile).Select(f => f.Path.LocalPath).ToList();
        if (paths.Count == 0) return;
        SetStatus($"Lecture de {Fmt.Plural(paths.Count, "capture")}…");
        try
        {
            var pages = new List<IReadOnlyList<OcrWord>>();
            foreach (var path in paths) pages.Add(await ScreenshotOcr.Words(path));
            var log = NotMeterReader.Read(pages);
            if (log.Character.Length > 0 && !string.Equals(log.Character, current.Name, StringComparison.OrdinalIgnoreCase))
            {
                Toast("Capture d'un autre personnage", $"Ces captures sont celles de {log.Character}, pas de {current.Name} : ouvre son onglet pour les importer.", error: true);
                SetStatus("");
                return;
            }
            combatStore.Save(key, log);
            combatStore.Prune(key);
            state.CombatIndex = 0;
            state.CombatGroup = null;
            state.CombatSkill = null;
            Recompute();
            SetStatus($"combat importé : {log.Skills.Count} sorts lus");
            Toast("Combat importé", $"{log.Skills.Count}{(log.SkillCount > 0 ? $"/{log.SkillCount}" : "")} sorts lus · DPS {Fmt.Big(log.Dps)} sur {log.Duration:0} s" +
                (log.Complete || log.SkillCount == 0 ? "" : " · liste partielle : ajoute les captures de la suite de la liste"));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message, error: true);
            Toast("Import du combat impossible", ex.Message, error: true);
        }
    }

    void DeleteCombat(string path)
    {
        combatStore.Delete(path);
        state.CombatIndex = null;
        state.CombatGroup = null;
        state.CombatSkill = null;
        Recompute();
        Toast("Combat supprimé");
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
            Toast("Rapport exporté", file.Name);
        }
        catch (IOException ex)
        {
            SetStatus($"Export impossible : {ex.Message}", error: true);
            Toast("Export impossible", ex.Message, error: true);
        }
    }

    /// <summary>Seul envoi réseau fait sans demande explicite : désactivable (politique de confidentialité, README).</summary>
    void ToggleAutoUpdate()
    {
        prefs = prefs with { AutoUpdate = !prefs.AutoUpdate };
        prefs.Save();
        SetStatus(prefs.AutoUpdate ? "vérification automatique des mises à jour activée" : "vérification automatique désactivée : « Aide → Vérifier les mises à jour » reste disponible");
        Render();
    }

    /// <summary>Mise à jour sans clic (activée par défaut) : décochée, la pastille « ⬆ disponible » attend ton clic comme avant.</summary>
    void ToggleAutoInstall()
    {
        prefs = prefs with { AutoInstall = !prefs.AutoInstall, AutoInstallFailed = null };
        prefs.Save();
        SetStatus(prefs.AutoInstall ? "les mises à jour s'installeront toutes seules au lancement" : "mises à jour à la demande : clique la pastille « ⬆ disponible »");
        Render();
    }

    /// <summary>Seule la vérification du lancement installe d'elle-même : jamais en cours d'utilisation (vérifications toutes les 6 h).</summary>
    bool startupCheckDone;

    async Task CheckUpdateAsync(bool manual)
    {
        if (installing || !manual && !prefs.AutoUpdate) return;
        var atStartup = !manual && !startupCheckDone;  // même si le site ne répond pas : les vérifications suivantes n'installent jamais
        if (!manual) startupCheckDone = true;
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
        // Au lancement, sans clic : une version que Windows a déjà refusée ici n'est pas retentée toute seule (pastille seulement).
        if (atStartup && update is { } found && prefs.AutoInstall && prefs.AutoInstallFailed != found.Version)
        {
            SetStatus($"Nouvelle version {found.Label} : installation automatique (Aide → décocher « Installer toute seule au lancement » pour choisir le moment)");
            await InstallUpdateAsync(auto: true);
            return;
        }
        Render();
    }

    async Task InstallUpdateAsync(bool auto = false)
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
            if (auto) { prefs = prefs with { AutoInstallFailed = release.Version }; prefs.Save(); }
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
            refusedKeys.Clear();
            marketAt = accountGatherAt = default;  // prix et récolte du compte relus avec la nouvelle clé
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
