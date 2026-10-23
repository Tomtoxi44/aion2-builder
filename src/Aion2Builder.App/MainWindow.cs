using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;
using Aion2Builder.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Dernières saisies, pour rouvrir l'application sur le même personnage.</summary>
sealed record UiPrefs(string Region = "eu", string Server = "", string Name = "", GameMode Mode = GameMode.PvE, string? LastFile = null, int Tab = 0)
{
    static string PathOf => Path.Combine(AppPaths.Root, "ui.json");

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

public sealed class MainWindow : Window
{
    readonly CharacterStore store = new();
    readonly ComboBox region = new() { ItemsSource = ArmoryClient.Regions, MinWidth = 80 };
    readonly ServerCache serverCache = new();
    // Liste filtrable : on tape "az" et Azphel ressort ; vide = recherche sur tous les serveurs.
    readonly AutoCompleteBox server = new()
    {
        PlaceholderText = "Serveur (vide = tous)", Width = 210, MinimumPrefixLength = 0, MaxDropDownHeight = 380,
        FilterMode = AutoCompleteFilterMode.Contains, ItemTemplate = new FuncDataTemplate<ServerInfo>((s, _) => ServerItem(s)),
    };
    readonly TextBox name = new() { PlaceholderText = "Nom du personnage", Width = 190 };
    readonly Button importButton = new() { Content = "Importer depuis le site officiel" };
    readonly ComboBox saved = new() { PlaceholderText = "Personnages enregistrés", MinWidth = 260 };
    readonly ComboBox mode = new() { ItemsSource = new[] { "Vue JcE (PvE)", "Vue JcJ (PvP)" }, SelectedIndex = 0, MinWidth = 150 };
    readonly Button refresh = new() { Content = "Actualiser", IsEnabled = false };
    readonly Button reloadData = new() { Content = "Recharger les données" };
    readonly TextBlock status = Text("", 12, color: Muted);
    readonly Border header = new();
    readonly ContentControl body = new();

    GameDatabase db;
    CharacterSnapshot? current;
    List<SavedCharacter> savedList = [];
    UiPrefs prefs = UiPrefs.Load();
    bool loadingSaved;

    public MainWindow()
    {
        Title = "Aion 2 Builder";
        Width = 1280;
        Height = 860;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        db = LoadData();
        region.SelectedItem = ArmoryClient.Regions.Contains(prefs.Region) ? prefs.Region : "eu";
        server.Text = prefs.Server;
        name.Text = prefs.Name;
        mode.SelectedIndex = prefs.Mode == GameMode.PvP ? 1 : 0;
        server.ItemsSource = serverCache.Load((string?)region.SelectedItem ?? "eu");

        importButton.Click += async (_, _) => await ImportAsync(name.Text, server.Text);
        name.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await ImportAsync(name.Text, server.Text); };
        refresh.Click += async (_, _) => { if (current is not null) await ImportAsync(current.Name, current.ServerName, current.Region); };
        saved.SelectionChanged += (_, _) => { if (!loadingSaved && saved.SelectedIndex >= 0) OpenSaved(savedList[saved.SelectedIndex]); };
        mode.SelectionChanged += (_, _) => { SavePrefs(); Render(); };
        reloadData.Click += async (_, _) => { db = LoadData(); Render(); await LoadServersAsync(force: true); };
        region.SelectionChanged += async (_, _) => { server.Text = ""; SavePrefs(); await LoadServersAsync(force: false); };
        server.GotFocus += (_, _) => server.IsDropDownOpen = true;
        Opened += async (_, _) => await LoadServersAsync(force: false);

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { region, server, name, importButton, saved, refresh, mode, reloadData })
        {
            c.Margin = new Thickness(0, 0, 8, 6);
            toolbar.Children.Add(c);
        }

        var top = Stack(4, toolbar, status, header);
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel
        {
            Children = { new Border { Padding = new Thickness(16, 12, 16, 4), Child = top, [DockPanel.DockProperty] = Dock.Top }, body },
        };

        RefreshSaved();
        var last = prefs.LastFile is not null ? savedList.FindIndex(s => s.Path == prefs.LastFile) : -1;
        if (last >= 0) saved.SelectedIndex = last;
        else Welcome();
    }

    /// <summary>Liste du cache tout de suite, relue sur le site si elle date de plus d'une semaine (ou sur demande).</summary>
    async Task LoadServersAsync(bool force)
    {
        var code = (string?)region.SelectedItem ?? "eu";
        server.ItemsSource = serverCache.Load(code);
        if (!force && !serverCache.IsStale(code)) return;
        try
        {
            using var client = ArmoryClient.Create();
            var list = await serverCache.Get(client, code, force);
            if (((string?)region.SelectedItem ?? "eu") == code) server.ItemsSource = list;
        }
        catch (ArmoryException ex) { SetStatus($"Liste des serveurs indisponible : {ex.Message}", error: true); }
    }

    static Control ServerItem(ServerInfo? s) => new DockPanel
    {
        Children =
        {
            new TextBlock { Text = s?.Race ?? "", FontSize = 11, Foreground = Muted, Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
            new TextBlock { Text = s?.Name ?? "", VerticalAlignment = VerticalAlignment.Center },
        },
    };

    GameDatabase LoadData()
    {
        try
        {
            var data = GameDataStore.Load();
            var custom = GameDataStore.Sources().Where(s => s.Overridden).Select(s => s.File).ToList();
            SetStatus(custom.Count == 0 ? "Données de jeu par défaut." : $"Données personnalisées : {string.Join(", ", custom)} ({AppPaths.DataDir}).");
            return data;
        }
        catch (InvalidDataException ex)
        {
            SetStatus(ex.Message + " — données par défaut utilisées.", error: true);
            return GameDataStore.Load(overrideDir: Path.Combine(AppPaths.Root, "__defaults_only__"));
        }
    }

    GameMode Mode => mode.SelectedIndex == 1 ? GameMode.PvP : GameMode.PvE;

    async Task ImportAsync(string? characterName, string? serverName, string? regionCode = null)
    {
        if (string.IsNullOrWhiteSpace(characterName)) { SetStatus("Indique le nom du personnage.", error: true); return; }
        importButton.IsEnabled = refresh.IsEnabled = false;
        try
        {
            using var client = ArmoryClient.Create();
            var progress = new Progress<string>(m => SetStatus(m));
            var (snapshot, _) = await client.FetchByName(db, regionCode ?? (string?)region.SelectedItem ?? "eu", characterName,
                string.IsNullOrWhiteSpace(serverName) ? null : serverName, progress);
            var path = store.Save(snapshot);
            current = snapshot;
            prefs = prefs with { LastFile = path };
            SavePrefs();
            RefreshSaved(path);
            SetStatus($"Importé depuis le site officiel le {snapshot.FetchedAtUtc.ToLocalTime():dd/MM/yyyy à HH:mm}.");
            Render();
        }
        catch (ArmoryException ex) { SetStatus(ex.Message, error: true); }
        finally
        {
            importButton.IsEnabled = true;
            refresh.IsEnabled = current is not null;
        }
    }

    void OpenSaved(SavedCharacter s)
    {
        try
        {
            current = store.Load(s.Path);
            prefs = prefs with { LastFile = s.Path };
            SavePrefs();
            refresh.IsEnabled = true;
            SetStatus($"Données du {current.FetchedAtUtc.ToLocalTime():dd/MM/yyyy à HH:mm} — « Actualiser » relit le site officiel.");
            Render();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            SetStatus($"Impossible de lire {s.Path} : {ex.Message}", error: true);
        }
    }

    void RefreshSaved(string? select = null)
    {
        loadingSaved = true;
        savedList = store.List();
        saved.ItemsSource = savedList.Select(s => $"{s.Name} · {db.FindClass(s.ClassCode)?.Name ?? s.ClassCode} · {s.ServerName} ({s.Region})").ToList();
        if (select is not null) saved.SelectedIndex = savedList.FindIndex(s => s.Path == select);
        loadingSaved = false;
    }

    void Render()
    {
        if (current is null) return;
        var previousTab = (body.Content as TabControl)?.SelectedIndex ?? prefs.Tab;
        try
        {
            var inputs = store.LoadInputs(current);
            var report = new Advisor(db).Analyze(current, Mode, inputs);
            header.Child = Header(report);
            var snapshot = current;
            var tabs = new ReportView(db, report, inputs, updated => { store.SaveInputs(snapshot, updated); Render(); }).Build(previousTab);
            tabs.SelectionChanged += (_, e) => { if (e.Source == tabs) { prefs = prefs with { Tab = tabs.SelectedIndex }; prefs.Save(); } };
            body.Content = tabs;
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, error: true); }
    }

    Control Header(AdviceReport r)
    {
        var c = r.Character;
        return Card(Row(16,
            Stack(2,
                Text($"{c.Name}", 22, true, wrap: false),
                Text($"{r.Profile.Class.Name} ({r.Profile.Role.Name}) niv. {c.Level} · {c.ServerName} ({c.Region}) · {c.Race}", 12, color: Muted, wrap: false)),
            Stack(2, Text("Niveau d'objet", 11, color: Muted), Text(c.ItemLevel.ToString(), 18, true)),
            Stack(2, Text("Puissance de combat", 11, color: Muted), Text(c.CombatPower.ToString("N0"), 18, true)),
            Stack(2, Text("Score du build", 11, color: Muted), Text($"{r.Scores.Overall}/100", 18, true, ScoreBrush(r.Scores.Overall))),
            Stack(2, Text("Palier", 11, color: Muted), Text(r.Gear.Stage.Name, 14, true))), bottom: 4);
    }

    void Welcome()
    {
        header.Child = null;
        body.Content = Scroll(Stack(8,
            Text("Bienvenue", 22, true),
            Text("Choisis ta région, ton serveur et le nom de ton personnage, puis « Importer depuis le site officiel ».", 14),
            Text("L'application lit ta fiche publique (équipement, compétences, Daevanion) et te dit, pour ta classe, " +
                 "ce qui est optimisé ou non, quelle pièce aller chercher au palier suivant, quelles compétences monter et " +
                 "quelles cases Daevanion ouvrir — en vue JcE ou JcJ.", 13, color: Muted),
            Text($"Les poids, paliers et coûts sont modifiables : copie les fichiers par défaut dans {AppPaths.DataDir} " +
                 "(commande « data export » du CLI) puis « Recharger les données ».", 12, color: Muted)));
    }

    void SavePrefs()
    {
        prefs = prefs with
        {
            Region = (string?)region.SelectedItem ?? "eu",
            Server = server.Text ?? "",
            Name = name.Text ?? "",
            Mode = Mode,
        };
        prefs.Save();
    }

    void SetStatus(string text, bool error = false)
    {
        status.Text = text;
        status.Foreground = error ? Bad : Muted;
    }
}
