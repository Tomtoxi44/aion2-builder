using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Objet de la liste du site : ressource de récolte ou pièce d'équipement (numéro du jeu, en texte), ou objet libre (« name:… »).
/// Category : « Récolte », ou la catégorie et le rang d'une pièce (« Épées · jaune ») ; Grade : rang du jeu (Common, Rare, Legend…).
/// </summary>
sealed record MarketItem(string Key, string Name, int Rank, bool Gathered, string Category = "", string? Grade = null)
{
    /// <summary>Texte des listes et de la recherche : le nom seul.</summary>
    public override string ToString() => Name;

    /// <summary>Rang du jeu : celui de la pièce, sinon celui de la ressource (1 blanc, 2 vert, 3 bleu, 4 jaune).</summary>
    public string? GameGrade => Grade ?? Rank switch { 1 => "Common", 2 => "Rare", 3 => "Legend", 4 => "Unique", _ => null };

    public bool IsGathering => Category == "Récolte" || (Category.Length == 0 && Rank > 0);

    /// <summary>Catégorie sans le rang (« Épées »), pour la colonne Type.</summary>
    public string Kind => IsGathering ? "Récolte" : Category.Split(" · ")[0] is { Length: > 0 } k ? k : "noté par son nom";
}
/// <summary>Un prix de l'hôtel des ventes noté par un joueur d'Aion 2 Todo : By = son pseudo (null : compte supprimé), Mine = toi.</summary>
/// Market : où il a été vu, l'hôtel d'un serveur (« Bakarma ») ou l'hôtel inter-serveurs d'une région (« *eu »).
sealed record MarketPrice(int Id, string ItemKey, string ItemName, long Price, DateTime At, string? By, bool Mine, string Market = "*eu");
/// <summary>Un prix retiré ces 30 derniers jours : qui l'avait noté, qui l'a retiré (rétablissable).</summary>
sealed record MarketRemoved(int Id, string ItemKey, string ItemName, long Price, DateTime At, string? By, DateTime RemovedAt, string? RemovedBy, string Market = "*eu");
/// <summary>Un prix à noter : objet de la liste (ItemKey) ou nom libre, avec le rang lu sur la capture pour un objet qui entre dans la liste.</summary>
sealed record PriceLine(string? ItemKey, string ItemName, long Price, string? Grade = null);
/// <summary>Un marché proposé par le site : code, nom affiché, nombre de prix, le tien (tes persos du site y jouent).</summary>
sealed record MarketChoice(string Code, string Label, int Prices, bool Mine);

/// <summary>
/// Un objet vendu par un PNJ marchand (liste commune d'Aion 2 Todo) : clé « npc:… », nom du jeu, prix à l'unité, onglet du
/// marchand ; By = pseudo de qui l'a noté (null : relevé de départ), Mine = toi.
/// </summary>
sealed record NpcItem(string Key, string Name, long Price, string? Tab, string? By, DateTime At, bool Mine);

sealed record MarketData(List<MarketItem> Items, List<MarketPrice> Prices, List<MarketRemoved> Removed, List<MarketChoice>? Markets = null, string? DefaultMarket = null,
    List<CraftRecipe>? Recipes = null, List<NpcItem>? Npc = null)
{
    /// <summary>Dernier prix de chaque objet sur un marché (les prix arrivent du plus ancien au plus récent).</summary>
    public Dictionary<string, MarketPrice> Latest(string market) =>
        Prices.Where(p => p.Market.Equals(market, StringComparison.OrdinalIgnoreCase)).GroupBy(p => p.ItemKey).ToDictionary(g => g.Key, g => g.MaxBy(p => p.At)!);

    public string Label(string market) => Markets?.FirstOrDefault(m => m.Code.Equals(market, StringComparison.OrdinalIgnoreCase))?.Label
                                         ?? (market.StartsWith('*') ? "inter-serveurs" : market);
}

/// <summary>Ce que l'écran Récolte → Prix HdV reçoit de la fenêtre : les prix, l'état de lecture, et les actions.</summary>
sealed record MarketState(MarketData? Data, bool Linked, string? Status, Func<string?, string, long, string, Task<string?>> Add,
    Action<MarketPrice> Remove, Action<MarketRemoved> Restore)
{
    /// <summary>Lignes lues sur des captures de l'hôtel (#65), en attente de ta validation ; ShotStatus : lecture en cours, erreur.</summary>
    public IReadOnlyList<MarketShotLine>? Shot { get; init; }
    public string? ShotStatus { get; init; }
    /// <summary>Lire des captures : null = choisir les fichiers ; « latest » = la dernière capture d'écran de Windows.</summary>
    public Action<string?> ReadShots { get; init; } = _ => { };
    /// <summary>Noter les lignes gardées sur ce marché, puis vider la lecture.</summary>
    public Action<IReadOnlyList<MarketShotLine>, string> SaveShot { get; init; } = (_, _) => { };
    public Action ClearShot { get; init; } = () => { };
    /// <summary>Une ligne lue corrigée à la main (nom, prix) : gardée sans redessiner l'écran.</summary>
    public Action<int, MarketShotLine> EditShot { get; init; } = (_, _) => { };
    /// <summary>Retirer une ligne lue (fausse ou en trop).</summary>
    public Action<int> RemoveShot { get; init; } = _ => { };
    /// <summary>Écran Inventaire : donner son nom du jeu à un objet de ton sac (pour tous) ; null si c'est fait, sinon l'erreur.</summary>
    public Func<long, string, Task<string?>> NameItem { get; init; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Écran Craft : noter une recette (null si c'est fait, sinon l'erreur), la retirer, la rétablir.</summary>
    public Func<RecipeSend, Task<string?>> AddRecipe { get; init; } = _ => Task.FromResult<string?>(null);
    public Func<int, RecipeSend, Task<string?>> ReplaceRecipe { get; init; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Choisir le rang (la couleur) d'un objet noté par les joueurs, par sa clé, pour tous.</summary>
    public Action<string, string?> SetGrade { get; init; } = (_, _) => { };
    /// <summary>Corriger le nom d'un objet noté par son nom (clé, nouveau nom), pour tous ; null si c'est fait, sinon l'erreur.</summary>
    public Func<string, string, Task<string?>> RenameItem { get; init; } = (_, _) => Task.FromResult<string?>(null);
    public Action<CraftRecipe> RemoveRecipe { get; init; } = _ => { };
    public Action<int> RestoreRecipe { get; init; } = _ => { };
    /// <summary>Écran Craft : noter ou corriger le prix d'un objet du PNJ (nom, prix, onglet), pour tous ; le retirer.</summary>
    public Func<string, long, string?, Task<string?>> AddNpc { get; init; } = (_, _, _) => Task.FromResult<string?>(null);
    public Action<NpcItem> RemoveNpc { get; init; } = _ => { };
    /// <summary>Écran Craft : lire une capture de la fenêtre d'artisanat (« latest » : la dernière capture d'écran ; null : choisir).</summary>
    public Action<string?> ReadCraft { get; init; } = _ => { };
}

/// <summary>
/// Prix de l'hôtel des ventes de la communauté Aion 2 Todo (onglet « Prix HdV » du site), lus et notés avec ta clé de synchro :
/// chacun voit les relevés de tous avec leur auteur, et peut retirer un prix faux (rétablissable 30 jours).
/// </summary>
static class Market
{
    public static async Task<MarketData> Load(string key)
    {
        using var http = BugReports.Client(key);
        return JsonSerializer.Deserialize<MarketData>(await http.GetStringAsync("/api/sync/market"), GameDataStore.Json)
               ?? throw new JsonException("réponse vide");
    }

    /// <summary>Null si c'est noté, sinon le message d'erreur (celui du site s'il en donne un).</summary>
    /// <param name="grade">Rang lu sur une capture (couleur du nom), pour un objet qui entre dans la liste.</param>
    public static async Task<string?> Add(string key, string? itemKey, string itemName, long price, string market, string? grade = null)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/prices", new { itemKey, itemName, price, market, grade }));
    }

    /// <summary>
    /// Toutes les lignes d'une capture en un envoi : une erreur par ligne, null si elle est notée. Un envoi par ligne butait
    /// sur la limite du site (60 appels par minute : « erreur 429 » au-delà de 60 lignes).
    /// </summary>
    public static async Task<List<string?>> AddMany(string key, string market, IReadOnlyList<PriceLine> lines)
    {
        using var http = BugReports.Client(key);
        var errors = new List<string?>(lines.Count);
        foreach (var chunk in lines.Chunk(400))
        {
            string? failed;
            try
            {
                var response = await http.PostAsJsonAsync("/api/sync/market/prices/batch", new
                {
                    market,
                    prices = chunk.Select(l => new { itemKey = l.ItemKey, itemName = l.ItemName, price = l.Price, grade = l.Grade }),
                });
                if (response.IsSuccessStatusCode)
                {
                    using (response)
                    using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    {
                        var list = doc.RootElement.GetProperty("errors").EnumerateArray()
                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
                        if (list.Count == chunk.Length) { errors.AddRange(list); continue; }
                    }
                    failed = "réponse du site incomplète";
                }
                else failed = await Error(response);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { failed = $"Aion 2 Todo injoignable ({ex.Message})"; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { failed = "réponse du site illisible"; }
            errors.AddRange(chunk.Select(_ => failed));
        }
        return errors;
    }

    /// <summary>Nommer un objet de ton sac (vu par ton Pet Meter) : le nom vaut pour tous les joueurs.</summary>
    public static async Task<string?> Name(string key, long id, string name)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/names", new { id, name }));
    }

    /// <summary>Recettes d'artisanat communes (écran Craft) : noter, retirer, rétablir.</summary>
    public static async Task<string?> AddRecipe(string key, RecipeSend recipe)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/recipes", recipe));
    }

    /// <summary>
    /// Corriger le nom d'un objet noté par son nom : ses prix, recettes et couleur passent sous le bon nom, réunis avec l'objet de
    /// ce nom s'il existe. Renvoie l'erreur, ou null et la clé de l'objet sous son nouveau nom.
    /// </summary>
    public static async Task<(string? Error, string? Key)> Rename(string key, string itemKey, string name)
    {
        using var http = BugReports.Client(key);
        var response = await http.PostAsJsonAsync("/api/sync/market/rename", new { key = itemKey, name });
        if (!response.IsSuccessStatusCode) return (await Error(response), null);
        using (response)
        {
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return (null, doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null);
            }
            catch (JsonException) { return (null, null); }
        }
    }

    /// <summary>Choisir le rang (la couleur) d'un objet noté par les joueurs, pour tous ; null : la couleur lue sur les captures.</summary>
    public static async Task<string?> SetGrade(string key, string itemKey, string? grade)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/grade", new { key = itemKey, grade }));
    }

    /// <summary>Objet noté par les joueurs (par son nom, ou nommé depuis un sac) : son rang se choisit à la main ; celui d'un objet du jeu vient du jeu.</summary>
    public static bool GradeEditable(MarketItem item) => item.Key.StartsWith("name:", StringComparison.Ordinal) || item.Category == "Nommé par un joueur";

    /// <summary>Corriger une recette : la nouvelle version est notée par toi, l'ancienne passe dans les retirées (rétablissable).</summary>
    public static async Task<string?> ReplaceRecipe(string key, int id, RecipeSend recipe)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PutAsJsonAsync($"/api/sync/market/recipes/{id}", recipe));
    }

    /// <summary>Noms lus sur une capture de la fenêtre d'artisanat : ils entrent dans la liste des objets, pour tous. Renvoie le nombre ajouté.</summary>
    public static async Task<int> AddCrafted(string key, IEnumerable<(string Name, string? Grade, string? Category)> items)
    {
        using var http = BugReports.Client(key);
        var response = await http.PostAsJsonAsync("/api/sync/market/crafted",
            new { items = items.Select(i => new { name = i.Name, grade = i.Grade, category = i.Category }) });
        if (!response.IsSuccessStatusCode) return 0;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("added", out var added) && added.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>Prix des PNJ marchands (communs) : noter ou corriger, retirer.</summary>
    public static async Task<string?> AddNpc(string key, string name, long price, string? tab)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/npc", new { name, price, tab }));
    }

    public static async Task<string?> RemoveNpc(string key, string npcKey)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/npc/remove", new { key = npcKey }));
    }

    public static async Task<string?> RemoveRecipe(string key, int id)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.DeleteAsync($"/api/sync/market/recipes/{id}"));
    }

    public static async Task<string?> RestoreRecipe(string key, int id)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsync($"/api/sync/market/recipes/{id}/restore", null));
    }

    public static async Task<string?> Remove(string key, int id)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.DeleteAsync($"/api/sync/market/prices/{id}"));
    }

    public static async Task<string?> Restore(string key, int id)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsync($"/api/sync/market/prices/{id}/restore", null));
    }

    static async Task<string?> Error(HttpResponseMessage response)
    {
        using (response)
        {
            if (response.IsSuccessStatusCode) return null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } text) return text;
            }
            catch (JsonException) { }
            return (int)response.StatusCode == 401 ? "clé Aion 2 Todo refusée" : $"erreur {(int)response.StatusCode} du site";
        }
    }

    /// <summary>Texte comparable : minuscules, sans accents, espaces simples (« Aria Fraîche » = « aria fraiche »).</summary>
    public static string Fold(string? s)
    {
        // Apostrophes : ’ ‘ ` valent ', et l'espace qui suit est ignoré (« d' Yggdrasil » lu sur une capture = « d'Yggdrasil »).
        s = System.Text.RegularExpressions.Regex.Replace((s ?? "").Replace('’', '\'').Replace('‘', '\'').Replace('`', '\''), @"'\s+", "'");
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Tous les mots cherchés sont dans le texte (sans accents ni majuscules).</summary>
    public static bool Matches(string? search, string text)
    {
        var hay = Fold(text);
        return Fold(search).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
    }

    /// <summary>Couleur du rang en mots, comme dans le jeu.</summary>
    public static string GradeWord(string grade) => grade switch
    {
        "Common" => "blanc", "Rare" => "vert", "Legend" => "bleu", "Unique" => "jaune", "Epic" => "orange", "Mythic" => "rouge",
        "Special" => "turquoise", _ => grade,
    };

    public static string Since(DateTime at)
    {
        var d = DateTime.UtcNow - at.ToUniversalTime();
        return d.TotalMinutes < 1 ? "à l'instant" : d.TotalHours < 1 ? $"il y a {(int)d.TotalMinutes} min"
            : d.TotalDays < 1 ? $"il y a {(int)d.TotalHours} h" : d.TotalDays < 31 ? $"il y a {(int)d.TotalDays} j" : at.ToLocalTime().ToString("dd/MM/yy");
    }
}

sealed partial class ScreenViews
{
    static string By(MarketPrice p) => p.Mine ? "toi" : p.By ?? "un compte supprimé";
    static readonly StringComparer French = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true);

    /// <summary>
    /// Marché montré : celui choisi, sinon le serveur du perso ouvert (son hôtel), sinon le marché par défaut de ton compte, sinon
    /// l'inter-serveurs d'Europe.
    /// </summary>
    string MarketShown(MarketData data)
    {
        var known = data.Markets?.Select(m => m.Code).ToList() ?? [];
        string? Find(string? code) => code is null ? null : known.FirstOrDefault(k => k.Equals(code, StringComparison.OrdinalIgnoreCase));
        return Find(s.MarketMarket) ?? Find(r.Character.ServerName) ?? data.DefaultMarket ?? "*eu";
    }

    static T Right<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Right;
        return control;
    }

    /// <summary>Couleur de rareté du jeu (blanc, vert, bleu, jaune…) ; texte normal sans rang connu.</summary>
    IBrush MarketBrush(string? grade) => grade is null ? Tok.Text : Tok.B(db.Grade(grade).Color);

    static readonly string[] ItemGrades = ["Common", "Rare", "Legend", "Unique", "Epic", "Mythic"];

    /// <summary>Pastilles des rangs du jeu (blanc, vert, bleu, jaune, orange, rouge), celle du rang en cours cerclée ; un clic choisit.</summary>
    Control GradePicker(string? current, Action<string> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var g in ItemGrades)
        {
            var def = db.Grade(g);
            var on = g == current;
            var dot = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Tok.B(def.Color),
                BorderBrush = Tok.Text, BorderThickness = new Thickness(on ? 2 : 0), Opacity = current is null || on ? 1 : 0.55,
            };
            var grade = g;
            row.Children.Add(Tip(Clickable(dot, () => pick(grade), null, Tok.Hover, new Thickness(3)), def.Name));
        }
        return row;
    }

    /// <summary>
    /// Corriger le nom d'un objet noté par son nom (faute de lecture ou de frappe), pour tous ; si le nom corrigé existe déjà, les
    /// deux objets n'en font plus qu'un. Null pour un objet du jeu (son nom vient du jeu).
    /// </summary>
    Control? RenameRow(string key, string name)
    {
        if (!key.StartsWith("name:", StringComparison.Ordinal)) return null;
        var box = new TextBox { Text = name, MinWidth = 0, FontSize = 13 };
        var note = T("Faute de lecture ou de frappe : corrige-le pour tous. Si ce nom existe déjà, les deux objets n'en font plus qu'un " +
                     "(prix, recettes, couleur).", 11, Tok.Muted, wrap: true);
        var busy = false;
        async void Save()
        {
            var text = string.Join(' ', (box.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (busy || text.Length < 2 || text == name) return;
            busy = true;
            note.Text = "Correction en cours…";
            var error = await market.RenameItem(key, text);
            busy = false;
            if (error is not null) { note.Text = error; note.Foreground = Tok.Bad; }
        }
        var save = Clickable(T("corriger", 12, Accent.Main, FontWeight.SemiBold, mono: true), Save, null, Tok.Hover, new Thickness(10, 4));
        save.CornerRadius = new CornerRadius(3);
        save.BorderBrush = Accent.Main;
        save.BorderThickness = new Thickness(1);
        DockPanel.SetDock(save, Dock.Right);
        return new StackPanel
        {
            Spacing = 4,
            Children = { Label("Nom"), new DockPanel { Children = { save.WithMargin(8, 0, 0, 0), box } }, note },
        };
    }

    /// <summary>Couleur d'un objet noté par les joueurs, à choisir (pour tous) ; null pour un objet dont le rang vient du jeu.</summary>
    Control? GradeRow(MarketItem? item)
    {
        if (item is null || !Market.GradeEditable(item)) return null;
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Label("Couleur (rang)"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        GradePicker(item.Grade, g => market.SetGrade(item.Key, g)),
                        T(item.Grade is null ? "pas lue : choisis-la" : db.Grade(item.Grade).Name, 11, Tok.Muted).WithMargin(0, 4, 0, 0),
                    },
                },
                T("Lue à la couleur du nom sur les captures, elle se trompe parfois : ton choix vaut pour tous les joueurs.", 11, Tok.Muted, wrap: true),
            },
        };
    }

    /// <summary>Un objet comme dans l'hôtel du jeu : barre de la couleur de son rang, nom coloré, puis (facultatif) sa catégorie.</summary>
    Control MarketItemLine(MarketItem? item, string name, double size = 13, bool withKind = false)
    {
        var grade = item?.GameGrade;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border
        {
            Width = 3, Height = size + 5, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center,
            Background = grade is null ? Tok.Border : MarketBrush(grade),
        });
        row.Children.Add(T(name, size, MarketBrush(grade), grade is "Unique" or "Epic" or "Mythic" ? FontWeight.SemiBold : FontWeight.Medium));
        if (withKind && item is not null) row.Children.Add(T(item.Kind + (grade is null || item.IsGathering ? "" : $" · {Market.GradeWord(grade)}"), 11, Tok.Muted));
        if (withKind && item?.Gathered == true) row.Children.Add(T("récolté", 10, Tok.Good, mono: true).WithMargin(0, 2, 0, 0));
        return row;
    }

    /// <summary>
    /// « Vendre ou garder ? » (#61) : ton stock de récolte (sac et coffre, lu par le Pet Meter sur ce PC, jamais envoyé) au dernier
    /// prix de la communauté sur ce marché, ressource par ressource, avec la tendance sur 7 jours : un prix qui a monté d'au moins
    /// 15 % est un bon moment pour vendre ; qui a baissé d'autant, mieux vaut garder. Une estimation, d'après les prix notés.
    /// </summary>
    Control? SellOrKeep(MarketData data, string shown, IReadOnlyList<Combat.GatherDay> days)
    {
        if (Aion2Builder.Combat.PetMeterInventory.Load() is not { } inv) return null;
        var who = inv.For(r.Character.Name) ?? inv.Chars.Values.OrderByDescending(c => c.At).FirstOrDefault();
        if (who is null) return null;
        var stock = who.Items.Where(i => Aion2Builder.Combat.GatherCatalog.IsResource(i.Item) && i.Qty > 0 && i.Cont != 11)
            .GroupBy(i => i.Item).ToDictionary(g => g.Key, g => g.Sum(i => i.Qty));
        if (stock.Count == 0) return null;
        var now = DateTime.UtcNow;
        var here = data.Prices.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = new List<(long Id, string Name, MarketItem? Item, long Qty, long Price, long Value, double? Trend)>();
        foreach (var (id, qty) in stock)
        {
            var list = here.Where(p => p.ItemKey == id.ToString()).OrderBy(p => p.At).ToList();
            if (list.Count == 0) continue;
            var last = list[^1];
            // Tendance : le dernier prix face à la moyenne de ceux d'il y a 4 à 10 jours (s'il y en a).
            var before = list.Where(p => p.At.ToUniversalTime() <= now.AddDays(-4) && p.At.ToUniversalTime() >= now.AddDays(-10)).ToList();
            double? trend = before.Count == 0 ? null : (last.Price - before.Average(p => (double)p.Price)) * 100 / before.Average(p => (double)p.Price);
            var item = data.Items.FirstOrDefault(i => i.Key == id.ToString());
            rows.Add((id, item?.Name ?? last.ItemName, item, qty, last.Price, qty * last.Price, trend));
        }
        var body = new StackPanel { Spacing = 8 };
        var unpriced = stock.Count - rows.Count;
        if (rows.Count == 0)
        {
            body.Children.Add(T($"Aucune ressource de ton stock n'a encore de prix sur ce marché ({data.Label(shown)}) : note-les (une capture de l'hôtel suffit).", 12, Tok.Muted, wrap: true));
            return Card(body, "Vendre ou garder ?", null, new Thickness(16, 10, 16, 12));
        }
        var total = rows.Sum(x => x.Value);
        body.Children.Add(T($"Ton stock de {who.Character} (sac et coffre, relevé {Market.Since(who.At)}) vaut ≈ {Fmt.Big(total)} Kinah aux derniers prix de « {data.Label(shown)} »" +
                            (unpriced > 0 ? $" ; {Fmt.Plural(unpriced, "ressource")} sans prix, pas comptée{(unpriced > 1 ? "s" : "")}." : "."), 12, Tok.Text2, wrap: true));
        const string cols = "*,90,110,120,190";
        var table = new StackPanel { Children = { TableHeader(cols, "Ressource", "En stock", "Prix", "Valeur", "Conseil") } };
        foreach (var x in rows.OrderByDescending(x => x.Value).Take(12))
        {
            var (advice, color) = x.Trend switch
            {
                >= 15 => ($"vends : +{x.Trend:0} % en 7 j", Tok.Good),
                <= -15 => ($"garde : {x.Trend:0} % en 7 j", Tok.Warn),
                { } t => ($"stable ({(t >= 0 ? "+" : "")}{t:0} %)", Tok.Muted),
                null => ("pas assez de prix", Tok.Faint),
            };
            table.Children.Add(new Border
            {
                Padding = new Thickness(20, 3),
                Child = Columns(cols, MarketItemLine(x.Item, x.Name), T(Fmt.Big(x.Qty), 12, Tok.Text2, mono: true).AlignRight(),
                    T(Fmt.Big(x.Price), 12, Tok.Text, mono: true).AlignRight(), T(Fmt.Big(x.Value), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                    Tip(T(advice, 12, color), "Dernier prix face à la moyenne des prix notés il y a 4 à 10 jours sur ce marché. Une estimation : plus il y a de prix notés, plus elle est sûre.")),
            });
        }
        body.Children.Add(table);
        return Card(body, "Vendre ou garder ?", T("ton stock, aux prix de la communauté", 11, Tok.Muted, mono: true), new Thickness(16, 10, 16, 12));
    }

    /// <summary>Tuto de l'écran : les trois façons de relever les prix, de la plus rapide (relevé du Pet Meter, touche F9) à la main.</summary>
    static Control HowTo(string marketLabel)
    {
        (string Title, string Text)[] ways =
        [
            ("Relevé avec le Pet Meter · le plus rapide",
             "Dans le Pet Meter : ⋯ → Outils → « Relevé HdV ». En jeu, ouvre le Marché et appuie sur F9 à chaque page (la ligne des titres des colonnes " +
             "doit se voir). La pastille dit ce qui est lu sur chaque capture ; « Envoyer » note tout d'un coup, sans passer par ici. Le Builder doit " +
             "seulement être installé sur ce PC et ouvert une fois."),
            ("Une capture à la fois",
             "Win+Maj+S ou Impr. écran sur le Marché du jeu, puis « dernière capture d'écran » ci-dessus : tu corriges les lignes avant de les noter."),
            ("À la main", "Choisis l'objet, tape son prix unitaire, puis « noter »."),
        ];
        var body = new StackPanel { Spacing = 8 };
        for (var i = 0; i < ways.Length; i++)
            body.Children.Add(Columns("18,*", T($"{i + 1}", 13, Accent.Main, FontWeight.SemiBold, mono: true),
                new StackPanel { Spacing = 2, Children = { T(ways[i].Title, 13, weight: FontWeight.Medium), T(ways[i].Text, 12, Tok.Muted, wrap: true) } }));
        body.Children.Add(T($"Les prix vont sur le marché choisi en haut (« {marketLabel} »), avec ton pseudo. Ce qui est lu : le nom (et son rang, à sa couleur) " +
                            "et le prix unitaire ; sur les annonces d'un objet, le plus bas. Un objet pas encore dans la liste, ou un prix refusé, attend dans " +
                            "« Lire une capture » : vérifie son nom, puis « noter ». Les captures ne quittent pas ton PC.", 11, Tok.Muted, wrap: true));
        return Card(body, "Comment relever les prix", T("3 façons", 11, Tok.Muted, mono: true), new Thickness(16, 10, 16, 12));
    }

    /// <summary>
    /// Prix lus sur une capture de la fenêtre Marché du jeu (#65) : la dernière capture d'écran de Windows, ou des fichiers
    /// choisis ; chaque ligne lue (nom, prix unitaire) se corrige ou se retire (✕), puis « noter » les envoie toutes.
    /// </summary>
    Control ShotCard(MarketData data, string shown)
    {
        var body = new StackPanel { Spacing = 8 };
        Border Act(string text, Action act, bool main = false)
        {
            var b = Clickable(T(text, 12, main ? Tok.OnAccent : Accent.Main, FontWeight.SemiBold, mono: true), act, main ? Accent.Main : null, Tok.Hover, new Thickness(12, 5));
            b.CornerRadius = new CornerRadius(3);
            if (!main) { b.BorderBrush = Accent.Main; b.BorderThickness = new Thickness(1); }
            return b;
        }
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                Tip(Act("dernière capture d'écran", () => market.ReadShots("latest"), market.Shot is null),
                    "La plus récente de tes captures Windows (Impr. écran ou Win+Maj+S : dossier Images\\Captures d'écran ; Win+Alt+Impr. écran : Vidéos\\Captures), de moins de 30 min."),
                Act("choisir des captures…", () => market.ReadShots(null)),
            },
        });
        body.Children.Add(T("Dans le jeu, ouvre le Marché : la liste des objets (onglet Principal) ou les annonces d'un objet, puis fais une capture " +
                            "d'écran en gardant la ligne des titres des colonnes (Objet, Prix de vente…) : c'est elle qui dit où s'arrête chaque nombre, " +
                            "et sans elle la capture est refusée. Le Builder lit chaque ligne (nom, prix unitaire) sur ce PC ; corrige ou retire ce qui est faux avant de noter. " +
                            "Sur les annonces d'un objet, c'est le prix unitaire le plus bas qui est gardé. La capture n'est envoyée nulle part.", 11, Tok.Muted, wrap: true));
        if (market.ShotStatus is { } st) body.Children.Add(T(st, 12, st.StartsWith("Lecture") ? Tok.Muted : Tok.Warn, wrap: true));
        if (market.Shot is { Count: > 0 } lines)
        {
            MarketItem? Known(string name) => data.Items.FirstOrDefault(x => Market.Fold(x.Name) == Market.Fold(name));
            const string cols = "*,150,150,70,34";
            var table = new StackPanel { Spacing = 2, Children = { TableHeader(cols, "Objet (corrige si besoin)", "Prix unitaire", "Sur la capture", "", "") } };
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var index = i;
                var item = Known(line.Name);
                // Rang choisi à la main ; sinon celui de l'objet déjà dans la liste ; sinon celui lu à la couleur du nom sur la capture.
                IBrush BarOf(MarketShotLine l, MarketItem? known) =>
                    (l.GradeChosen ? l.Grade : known?.GameGrade ?? l.Grade) is { } g ? MarketBrush(g) : Tok.Border;
                var bar = new Border { Width = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 8, 4), Background = BarOf(line, item) };
                // Un clic sur la barre : choisir la couleur (objet noté par les joueurs ou nouveau ; celle d'un objet du jeu ne change pas).
                var gradeFlyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                gradeFlyout.Opening += (_, _) =>
                {
                    var known = Known(line.Name);
                    gradeFlyout.Content = known is not null && !Market.GradeEditable(known)
                        ? T("Rang du jeu : il ne se change pas.", 12, Tok.Muted)
                        : GradePicker(line.GradeChosen ? line.Grade : known?.Grade ?? line.Grade, g =>
                        {
                            line = line with { Grade = g, GradeChosen = true };
                            market.EditShot(index, line);
                            bar.Background = BarOf(line, Known(line.Name));
                            gradeFlyout.Hide();
                        });
                };
                FlyoutBase.SetAttachedFlyout(bar, gradeFlyout);
                bar.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                bar.PointerPressed += (_, _) => FlyoutBase.ShowAttachedFlyout(bar);
                ToolTip.SetTip(bar, "Couleur de l'objet (rang) : clique pour la choisir si elle est mal lue.");
                var name = new TextBox { Text = item?.Name ?? line.Name, MinWidth = 0, FontSize = 13 };
                var price = new TextBox { Text = Fmt.Big(line.Price), MinWidth = 0, FontSize = 13, HorizontalContentAlignment = HorizontalAlignment.Right };
                var hint = T(item is null ? "nouveau" : "", 10, Tok.Warn, mono: true);
                void Changed()
                {
                    var text = string.Join(' ', (name.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    var value = long.TryParse(new string((price.Text ?? "").Where(char.IsDigit).ToArray()), out var v) ? v : 0;
                    line = line with { Name = text, Price = value };
                    market.EditShot(index, line);
                    var known = Known(text);
                    hint.Text = known is null ? "nouveau" : "";
                    bar.Background = BarOf(line, known);
                }
                name.TextChanged += (_, _) => Changed();
                price.TextChanged += (_, _) => Changed();
                var seen = line.Listings is not { } n ? "—" : line.Lowest ? $"le plus bas de {Fmt.Plural(n, "annonce")}" : $"{n} en vente";
                var remove = Clickable(T("✕", 12, Tok.Muted), () => market.RemoveShot(index), null, Tok.Hover, new Thickness(8, 3));
                remove.CornerRadius = new CornerRadius(3);
                table.Children.Add(new Border
                {
                    Padding = new Thickness(20, 2),
                    Child = Columns(cols, new DockPanel { Children = { bar, name } }, price.WithMargin(8, 0, 0, 0),
                        Tip(T(seen, 11, Tok.Muted).WithMargin(12, 0, 0, 0), line.Lowest
                            ? "Annonces de cet objet lues sur la capture : le prix unitaire le plus bas est gardé."
                            : "Nombre d'annonces affiché par le jeu (pour information : seul le prix est noté)."),
                        Tip(hint, "Pas encore dans la liste : il y entre sous ce nom (vérifie l'orthographe)."),
                        Tip(remove, "Retirer cette ligne")),
                });
            }
            body.Children.Add(table);
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    Act($"noter {Fmt.Plural(lines.Count, "prix", "prix")}", () => { if (lines.Count > 0) market.SaveShot(lines.ToList(), shown); }, main: true),
                    Act("annuler", () => market.ClearShot()),
                    T($"sur le marché « {data.Label(shown)} », avec ton pseudo", 11, Tok.Muted).WithMargin(0, 5, 0, 0),
                },
            });
        }
        return Card(body, "Lire une capture de l'hôtel des ventes", T("rapide : 10 prix d'un coup", 11, Tok.Muted, mono: true), new Thickness(16, 10, 16, 12));
    }

    /// <summary>
    /// Valeur d'un lot de ressources (numéro → quantité) au dernier prix de la communauté : une ligne cliquable vers Prix HdV,
    /// null sans prix ni clé.
    /// </summary>
    Control? MarketValue(IEnumerable<(long Id, long Qty)> lot, string what)
    {
        if (market.Data is not { } data) return null;
        var shown = MarketShown(data);
        if (LotValue(data.Latest(shown), lot) is not { } lv) return null;
        var (value, missing) = lv;
        var line = T($"≈ {Fmt.Big(value)} Kinah : valeur {what} au dernier prix de la communauté ({data.Label(shown)})" +
                     (missing > 0 ? $" ({Fmt.Plural(missing, "ressource")} sans prix, pas comptée{(missing > 1 ? "s" : "")})" : "") + " · voir les prix",
            12, Tok.Text2, wrap: true);
        return Tip(Clickable(line, () => update(st => st.Screen = Screen.Market), null, Tok.Hover, new Thickness(0, 2)),
            "Prix notés à la main par les joueurs d'Aion 2 Todo (onglet « Prix HdV » du site ou ici) : le dernier relevé de chaque ressource.");
    }

    /// <summary>Valeur d'un lot aux derniers prix, et nombre de ressources sans prix ; null si aucune n'a de prix.</summary>
    static (long Value, int Missing)? LotValue(Dictionary<string, MarketPrice> latest, IEnumerable<(long Id, long Qty)> lot)
    {
        var list = lot.Where(x => x.Qty > 0).ToList();
        var priced = list.Where(x => latest.ContainsKey(x.Id.ToString())).ToList();
        return priced.Count == 0 ? null : (priced.Sum(x => x.Qty * latest[x.Id.ToString()].Price), list.Count - priced.Count);
    }

    /// <summary>Changer de page de l'écran HdV ; la page Courbes trace d'emblée l'objet choisi (sinon le dernier relevé).</summary>
    void MarketGo(MarketPage page, List<MarketPrice> here) => update(st =>
    {
        st.MarketPage = page;
        if (page == MarketPage.Chart && st.MarketSeries.All(k => k is null) && (st.MarketItem ?? here.MaxBy(p => p.At)?.ItemKey) is { } first) st.MarketSeries[0] = first;
    });

    /// <summary>Barre de gauche de l'écran HdV : les derniers prix, les courbes, noter des prix, vendre ou garder, les retraits récents.</summary>
    SidePanel? MarketSide()
    {
        if (!market.Linked || market.Data is not { } data) return null;
        var shown = MarketShown(data);
        var here = data.Prices.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();
        var last = here.MaxBy(p => p.At);
        var body = new StackPanel();
        void Item(string icon, MarketPage page, string title, string? badge, IBrush? color, string preview) =>
            body.Children.Add(SideItem(icon, title, badge, color, preview, s.MarketPage == page, () => MarketGo(page, here)));
        Item(Icons.List, MarketPage.Prices, "Derniers prix", $"{here.Select(p => p.ItemKey).Distinct().Count()}", Tok.Text2,
            last is null ? "aucun prix sur ce marché" : $"dernier relevé {Market.Since(last.At)}");
        Item(Icons.Route, MarketPage.Chart, "Courbes", null, null, "jusqu'à 8 objets sur un graphique");
        var pending = market.Shot?.Count ?? 0;
        Item(Icons.Card, MarketPage.Add, "Noter des prix", pending > 0 ? $"{pending}" : null, Tok.Warn,
            pending > 0 ? $"{Fmt.Plural(pending, "ligne lue", "lignes lues")} à vérifier" : "Pet Meter (F9), capture d'écran ou à la main");
        Item(Icons.Box, MarketPage.Stock, "Vendre ou garder ?", null, null, "ta récolte en stock, aux prix de ce marché");
        var removed = data.Removed.Count(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase));
        if (removed > 0 || s.MarketPage == MarketPage.Removed)
            Item(Icons.Swap, MarketPage.Removed, "Retirés récemment", $"{removed}", Tok.Muted, "rétablir un prix retiré par erreur");
        return new SidePanel($"HdV · {data.Label(shown)}", null, body);
    }

    static string MarketPageTitle(MarketPage page) => page switch
    {
        MarketPage.Chart => "Courbes",
        MarketPage.Add => "Noter des prix",
        MarketPage.Stock => "Vendre ou garder ?",
        MarketPage.Removed => "Retirés récemment",
        _ => "Derniers prix",
    };

    /// <summary>
    /// Écran HdV, présenté comme l'hôtel du jeu : chaque objet à la couleur de son rang (blanc, vert, bleu, jaune). Pages (barre de
    /// gauche) : les derniers prix (chiffres du marché, filtres, un objet par ligne ; l'objet choisi dans l'inspecteur), les
    /// courbes, noter des prix, vendre ou garder, les retraits récents à rétablir.
    /// </summary>
    Control MarketScreen(IReadOnlyList<Combat.GatherDay> days)
    {
        const string title = "Hôtel des ventes";
        if (!market.Linked)
            return Page(title, null, Card(T("Les prix de l'hôtel des ventes sont notés par les joueurs d'Aion 2 Todo et partagés entre eux. " +
                "Pour les voir et en noter ici, colle ta clé Aion 2 Todo dans l'écran Familiers du Builder (sur le site : onglet Familiers → Pet Meter → " +
                "« Afficher ma clé »), ou garde le Pet Meter relié : sa clé est reprise toute seule.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        if (market.Data is not { } data)
            return Page(title, null, T(market.Status ?? "Lecture des prix…", 13, Tok.Muted, wrap: true));

        var blocks = new List<Control?>();
        if (market.Status is { } status) blocks.Add(T(status, 12, Tok.Warn, wrap: true));

        // Le marché : tes serveurs d'abord, puis ceux qui ont des prix, puis une liste de tous les autres.
        var shown = MarketShown(data);
        HdvRelay.NoteMarket(shown, data.Label(shown));  // le relevé du Pet Meter note ses prix sur ce marché
        var choices = data.Markets ?? [new MarketChoice(shown, data.Label(shown), 0, true)];
        var quick = choices.Where(m => m.Mine || m.Prices > 0 || m.Code.Equals(shown, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        var others = new ComboBox
        {
            ItemsSource = choices.Where(m => !quick.Contains(m)).Select(m => m.Label).ToList(), PlaceholderText = "autre serveur…", MinWidth = 170,
        };
        others.SelectionChanged += (_, _) =>
        {
            if (others.SelectedItem is string label && choices.FirstOrDefault(m => m.Label == label) is { } m)
                update(st => { st.MarketMarket = m.Code; st.MarketItem = null; st.MarketConfirm = null; });
        };
        var here = data.Prices.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();
        blocks.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                Tip(T("Marché", 13, weight: FontWeight.Medium).WithMargin(0, 4, 0, 0),
                    "L'hôtel de chaque serveur a ses propres prix ; l'hôtel inter-serveurs réunit tous les serveurs de la région. Choisis celui où tu as vu " +
                    "le prix : l'écran n'affiche que les prix de ce marché, et c'est sur lui que tes prix sont notés."),
                Choice(quick.Select(m => ($"{m.Label}{(m.Prices > 0 ? $" · {m.Prices}" : "")}", m.Code.Equals(shown, StringComparison.OrdinalIgnoreCase),
                    (Action)(() => update(st => { st.MarketMarket = m.Code; st.MarketItem = null; st.MarketConfirm = null; }))))),
                others,
            },
        });
        var pageTitle = $"{title} · {MarketPageTitle(s.MarketPage)}";

        switch (s.MarketPage)
        {
            case MarketPage.Chart:
                return Page(pageTitle, null, blocks.Concat(MarketChartBlocks(data, shown, MarketRows(data, here))).ToArray());
            case MarketPage.Add:
                blocks.Add(MarketPriceForm(data, shown));
                blocks.Add(ShotCard(data, shown));
                blocks.Add(HowTo(data.Label(shown)));
                return Page(pageTitle, null, blocks.ToArray());
            case MarketPage.Stock:
                blocks.Add(SellOrKeep(data, shown, days) ?? Card(T("Ton stock de récolte vient du Pet Meter de ce PC (inventory.json, v0.21.0 ou plus, lancé avant " +
                    "d'entrer en jeu). Sans lui, rien à estimer.", 13, Tok.Muted, wrap: true), "Vendre ou garder ?"));
                return Page(pageTitle, null, blocks.ToArray());
            case MarketPage.Removed:
                blocks.Add(MarketRemovedCard(data, shown));
                return Page(pageTitle, null, blocks.ToArray());
        }

        if (market.Shot is { Count: > 0 } waiting)
            blocks.Add(Clickable(T($"{Fmt.Plural(waiting.Count, "ligne lue", "lignes lues")} sur une capture de l'hôtel attend{(waiting.Count > 1 ? "ent" : "")} ta vérification · ouvrir « Noter des prix »",
                12, Tok.Warn, wrap: true), () => MarketGo(MarketPage.Add, here), null, Tok.Hover, new Thickness(0, 2)));

        // Les chiffres du marché : objets qui ont un prix, relevés de la semaine, le dernier, la valeur de ta récolte du jour.
        var gameToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));
        var todayLot = days.FirstOrDefault(d => d.Date == gameToday)?.Servers is { Count: > 0 } servers
            ? servers.Values.SelectMany(g => g.Items ?? []).Where(i => i.Id is not null).GroupBy(i => (long)i.Id!.Value).Select(g => (g.Key, (long)g.Sum(i => i.Qty))).ToList()
            : [];
        var harvest = LotValue(data.Latest(shown), todayLot);
        var lastPrice = here.MaxBy(p => p.At);
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Objets avec un prix", here.Select(p => p.ItemKey).Distinct().Count().ToString(), size: 20,
            note: $"au moins un prix noté sur {data.Label(shown)}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Prix notés · 7 jours", here.Count(p => p.At.ToUniversalTime() >= DateTime.UtcNow.AddDays(-7)).ToString(), size: 20,
            note: "par tous les joueurs d'Aion 2 Todo, sur ce marché").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Tip(Kpi("Dernier relevé", lastPrice is null ? "—" : Market.Since(lastPrice.At), size: 20,
                note: lastPrice is null ? "aucun prix ici pour l'instant" : $"{lastPrice.ItemName}, par {By(lastPrice)}"),
            lastPrice is null ? "Aucun prix sur ce marché." : $"{lastPrice.ItemName} : {Fmt.Big(lastPrice.Price)} Kinah, par {By(lastPrice)} le {lastPrice.At.ToLocalTime():dd/MM HH:mm}")
            .WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Tip(Kpi("Ta récolte du jour vaut", harvest is { } h ? $"≈ {Fmt.Big(h.Value)} Kinah" : "—", harvest is null ? null : Tok.Good, 20,
                harvest is { } hn ? "au dernier prix noté ici" + (hn.Missing > 0 ? $" ({Fmt.Plural(hn.Missing, "ressource")} sans prix, pas comptée{(hn.Missing > 1 ? "s" : "")})" : "")
                    : "aucune ressource récoltée aujourd'hui n'a encore de prix ici"),
            harvest is { } hv ? "Ce que rapporterait ce que tu as récolté aujourd'hui (jour de jeu en cours) si tu le vendais au dernier prix noté de chaque ressource sur ce marché" +
                                (hv.Missing > 0 ? $" ; {Fmt.Plural(hv.Missing, "ressource")} sans prix ici, pas comptée{(hv.Missing > 1 ? "s" : "")} : note leur prix pour l'avoir en entier." : ".")
                : "Ce que rapporterait ta récolte du jour au prix de la communauté : aucune ressource récoltée aujourd'hui n'a encore de prix sur ce marché."));
        blocks.Add(kpis);

        // Derniers prix (choix « A » du 8 novembre) : rangés par type en groupes repliables (la récolte dépliée d'abord), une
        // seule ligne de filtres (recherche, rang en pastilles) ; au-dessus, ce qui a bougé sur 7 jours (repris de la « B »).
        var byItem = MarketRows(data, here);
        var selected = MarketSelected(byItem);
        if (byItem.Count == 0)
        {
            blocks.Add(Card(T($"Aucun prix noté pour l'instant sur ce marché ({data.Label(shown)}) : note le premier (barre de gauche → « Noter des prix »).", 13, Tok.Muted, wrap: true),
                "Derniers prix de la communauté"));
            return Page(pageTitle, null, blocks.ToArray());
        }

        // Ce qui bouge : le dernier prix face au prix d'il y a 7 jours (le dernier relevé d'avant, sinon le premier de la semaine).
        var since = DateTime.UtcNow.AddDays(-7);
        var moves = byItem.Select(x =>
        {
            var last = x.List[^1];
            var before = x.List.LastOrDefault(q => q.At.ToUniversalTime() < since) ?? x.List.FirstOrDefault(q => q.At < last.At);
            return (x.Key, x.Item, Last: last, Pct: before is { Price: > 0 } bp && bp.At < last.At ? (last.Price - bp.Price) * 100.0 / bp.Price : (double?)null);
        }).Where(x => x.Pct is { } v && Math.Abs(v) >= 1).ToList();
        Control MoveList(IEnumerable<(string Key, MarketItem? Item, MarketPrice Last, double? Pct)> list, string empty)
        {
            var panel = new StackPanel { Spacing = 2 };
            foreach (var (key, item, last, pct) in list)
            {
                // Plus de ×10 en une semaine : sans doute une faute de frappe (un zéro de trop), signalée pour être vérifiée.
                var suspect = pct >= 900;
                var change = suspect ? $"×{Fmt.Big((long)Math.Round(1 + pct!.Value / 100))}" : $"{(pct > 0 ? "+" : "−")}{Math.Abs(pct!.Value):0.#} %";
                Control line = Clickable(Columns("*,90,90", MarketItemLine(item, last.ItemName, 12),
                    T(change, 12, suspect ? Tok.Warn : pct > 0 ? Tok.Good : Tok.Bad, FontWeight.SemiBold, mono: true).AlignRight(),
                    T(Fmt.Big(last.Price), 12, Tok.Text2, mono: true).AlignRight()), () => update(st => { st.MarketItem = key; st.MarketConfirm = null; }), null, Tok.Hover, new Thickness(4, 3));
                panel.Children.Add(suspect
                    ? Tip(line, $"Prix suspect : {change} en 7 jours ({Fmt.Big(last.Price)}, noté par {By(last)}). Peut-être une faute de frappe : vérifie-le, et retire-le dans l'inspecteur s'il est faux.")
                    : line);
            }
            if (panel.Children.Count == 0) panel.Children.Add(T(empty, 12, Tok.Muted, wrap: true));
            return panel;
        }
        if (moves.Count > 0)
            blocks.Add(Columns("*,*",
                Card(MoveList(moves.Where(x => x.Pct > 0).OrderByDescending(x => x.Pct).Take(4), "Rien n'a monté cette semaine."), "En hausse · 7 jours",
                    T("clic : l'objet", 11, Tok.Muted, mono: true)).WithMargin(0, 0, 12, 0),
                Card(MoveList(moves.Where(x => x.Pct < 0).OrderBy(x => x.Pct).Take(4), "Rien n'a baissé cette semaine."), "En baisse · 7 jours",
                    T("clic : l'objet", 11, Tok.Muted, mono: true))));

        // Une seule ligne de filtres : la recherche, le rang en pastilles de couleur (clic à nouveau : tous).
        var query = new TextBox { Text = s.MarketQuery ?? "", PlaceholderText = "filtrer : aria, anneau…", Width = 230 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { query } };
        var present = byItem.Select(x => x.Item?.GameGrade).OfType<string>().Distinct().OrderBy(g => db.Grade(g).Rank).ToList();
        if (present.Count > 1)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            dots.Children.Add(T("rang", 11, Tok.Muted, mono: true).WithMargin(0, 3, 6, 0));
            foreach (var g in present)
            {
                var on = s.MarketGrade == g;
                var dot = new Border
                {
                    Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = MarketBrush(g), BorderBrush = Tok.Text,
                    BorderThickness = new Thickness(on ? 2 : 0), Opacity = s.MarketGrade is null || on ? 1 : 0.4,
                };
                var grade = g;
                dots.Children.Add(Tip(Clickable(dot, () => update(st => st.MarketGrade = on ? null : grade), null, Tok.Hover, new Thickness(3)),
                    on ? "Tous les rangs" : $"Seulement {Market.GradeWord(g)}"));
            }
            filters.Children.Add(dots);
        }
        blocks.Add(filters);

        // Les groupes : type d'objet, comme les onglets de l'hôtel du jeu.
        static string GroupOf((string Key, MarketItem? Item, List<MarketPrice> List) x) =>
            x.Item?.IsGathering == true ? "Récolte"
            : x.Item?.Category.StartsWith("Fabrication") == true ? "Fabrication"
            : x.Item is null || x.Key.StartsWith("name:") || x.Item.Category is "Noté par un joueur" or "Nommé par un joueur" ? "Noté par les joueurs"
            : "Équipement";
        string[] order = ["Récolte", "Équipement", "Fabrication", "Noté par les joueurs"];
        var filtered = byItem.Where(x => s.MarketGrade is null || x.Item?.GameGrade == s.MarketGrade).ToList();
        const string cols = "*,110,80,96";
        var table = new StackPanel();
        var header = TableHeader(cols, "Objet", "Prix", "Évolution", "Tendance");
        var groups = new List<(string Name, Border Head, TextBlock Count, List<(Control Row, string Text)> Rows)>();
        foreach (var name in order)
        {
            var members = filtered.Where(x => GroupOf(x) == name).ToList();
            if (members.Count == 0) continue;
            var open = s.MarketOpen.Contains(name);
            var count = T("", 11, Tok.Muted, mono: true);
            var head = Clickable(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { T(open ? "▾" : "▸", 12, Accent.Main, mono: true), T(name, 12, Accent.Main, FontWeight.SemiBold), count },
            }, () => update(st => { if (!st.MarketOpen.Remove(name)) st.MarketOpen.Add(name); }), Tok.Frame, Tok.Hover, new Thickness(20, 6));
            head.BorderBrush = Tok.RowLine;
            head.BorderThickness = new Thickness(0, 1, 0, 1);
            table.Children.Add(head);
            var rows = new List<(Control Row, string Text)>();
            foreach (var x in members)
            {
                var last = x.List[^1];
                var prev = x.List.Count > 1 ? x.List[^2] : null;
                var d = prev is null ? 0 : last.Price - prev.Price;
                var pct = prev is null || prev.Price == 0 ? 0 : d * 100.0 / prev.Price;
                var grade = x.Item?.GameGrade;
                var spark = x.List.Count > 1
                    ? (Control)new TimeChart(x.List.TakeLast(20).Select(p => new ChartPoint(p.At, p.Price, $"{p.At.ToLocalTime():dd/MM HH:mm} : {Fmt.Big(p.Price)}")).ToList(),
                        v => Fmt.Big(v), grade is null ? Accent.Main : MarketBrush(grade), 22, compact: true) { Width = 90, HorizontalAlignment = HorizontalAlignment.Left }
                    : T("—", 12, Tok.Faint, mono: true);
                var evolution = prev is null ? (Control)T("—", 12, Tok.Muted, mono: true).AlignRight()
                    : Tip(T(d == 0 ? "=" : Math.Abs(pct) < 0.05 ? "≈" : $"{(d > 0 ? "+" : "−")}{Math.Abs(pct):0.#} %", 12, d > 0 ? Tok.Good : d < 0 ? Tok.Bad : Tok.Muted, mono: true).AlignRight(),
                        d == 0 ? $"Même prix qu'au relevé d'avant ({Fmt.Big(prev.Price)})" : $"{(d > 0 ? "+" : "−")}{Fmt.Big(Math.Abs(d))} Kinah depuis le relevé d'avant ({Fmt.Big(prev.Price)}, {prev.At.ToLocalTime():dd/MM})");
                var key = x.Key;
                // Qui a noté et quand : dans la bulle du prix et dans l'inspecteur, plus sur chaque ligne.
                var price = Tip(T(Fmt.Big(last.Price), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(), $"Noté {Market.Since(last.At)} par {By(last)}");
                var row = TableRow(Columns(cols, MarketItemLine(x.Item, last.ItemName, withKind: name is "Équipement" or "Fabrication"), price, evolution, spark),
                    x.Key == selected?.Key, () => update(st => { st.MarketItem = key; st.MarketConfirm = null; }));
                table.Children.Add(row);
                rows.Add((row, $"{last.ItemName} {x.Item?.Category}"));
            }
            groups.Add((name, head, count, rows));
        }
        var none = T("Aucun objet ne correspond à ce filtre.", 12, Tok.Muted).WithMargin(20, 8, 20, 8);
        table.Children.Add(none);
        // Recherche instantanée : un groupe replié s'ouvre tant qu'on cherche ; son titre compte ce qui correspond.
        void Filter()
        {
            var searching = !string.IsNullOrWhiteSpace(query.Text);
            var total = 0;
            foreach (var (name, head, count, rows) in groups)
            {
                var open = searching || s.MarketOpen.Contains(name);
                var matching = 0;
                foreach (var (row, text) in rows)
                {
                    var hit = Market.Matches(query.Text, text);
                    if (hit) matching++;
                    row.IsVisible = open && hit;
                }
                head.IsVisible = matching > 0;
                count.Text = $"{matching}";
                total += matching;
            }
            none.IsVisible = total == 0;
        }
        query.TextChanged += (_, _) => { s.MarketQuery = query.Text; Filter(); };
        Filter();
        blocks.Add(TableCard("Derniers prix de la communauté", T($"{Fmt.Plural(filtered.Count, "objet")} · prix unitaire en Kinah · clic : le détail à droite", 11, Tok.Muted, mono: true),
            header, [table]));
        blocks.Add(T("Prix unitaires notés à la main par les joueurs d'Aion 2 Todo (aucune source publique n'existe), avec le pseudo de leur auteur : " +
                     "les mêmes que l'onglet « Prix HdV » du site. Un prix faux se retire par n'importe qui (inspecteur → « retirer ») et se rétablit pendant 30 jours.",
            11, Tok.Muted, wrap: true).WithLineHeight(1.4));
        return Page(pageTitle, null, blocks.ToArray());
    }

    /// <summary>L'objet choisi dans la liste des prix, sinon le dernier relevé.</summary>
    (string Key, MarketItem? Item, List<MarketPrice> List)? MarketSelected(List<(string Key, MarketItem? Item, List<MarketPrice> List)> byItem)
    {
        if (byItem.Count == 0) return null;
        var selected = byItem.FirstOrDefault(x => x.Key == s.MarketItem);
        return selected.Key is not null ? selected : byItem.MaxBy(x => x.List[^1].At);
    }

    /// <summary>Noter un prix à la main : l'objet (liste avec recherche, sans accents ni majuscules, aussi par type), le prix, « noter ».</summary>
    Control MarketPriceForm(MarketData data, string shown)
    {
        var catalog = data.Items.OrderByDescending(i => i.Gathered).ThenByDescending(i => i.IsGathering).ThenBy(i => i.Name, French)
            .DistinctBy(i => Market.Fold(i.Name)).ToList();
        // Filtre et modèle AVANT FilterMode et Text : un texte déjà là lance la recherche tout de suite, et Avalonia plante si le
        // filtre « Custom » n'est pas encore posé.
        var pick = new AutoCompleteBox
        {
            ItemFilter = (search, o) => o is MarketItem i && Market.Matches(search, $"{i.Name} {i.Category}"),
            // Avalonia rappelle le modèle avec un objet vide quand il recycle une ligne de la liste (en tapant) : ligne vide, pas de plantage.
            ItemTemplate = new FuncDataTemplate<MarketItem?>((i, _) => i is null ? new Panel() : MarketItemLine(i, i.Name, 13, withKind: true)),
            ItemsSource = catalog, FilterMode = AutoCompleteFilterMode.Custom, MinimumPrefixLength = 0, MaxDropDownHeight = 340,
            PlaceholderText = "objet : Od, orichalque, un anneau…", MinWidth = 320,
            Text = s.MarketPick ?? "",
        };
        pick.TextChanged += (_, _) => s.MarketPick = pick.Text;
        var price = new TextBox { Text = s.MarketPriceText ?? "", Width = 150, PlaceholderText = "prix unitaire (Kinah)" };
        price.TextChanged += (_, _) => s.MarketPriceText = price.Text;
        async Task Submit()
        {
            var name = (pick.Text ?? "").Trim();
            var digits = new string((price.Text ?? "").Where(char.IsDigit).ToArray());
            if (name.Length == 0 || !long.TryParse(digits, out var value) || value < 1) { update(st => st.MarketError = "Choisis un objet et tape son prix unitaire."); return; }
            var item = data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(name));
            var error = await market.Add(item?.Key, item?.Name ?? name, value, shown);
            update(st =>
            {
                st.MarketError = error;
                if (error is not null) return;
                st.MarketPriceText = "";
                st.MarketItem = item?.Key ?? st.MarketItem;
            });
        }
        price.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = Submit(); } };
        var save = Clickable(T("noter", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), () => _ = Submit(), Accent.Main, null, new Thickness(14, 5));
        save.CornerRadius = new CornerRadius(3);
        var form = new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { pick, price, save } } } };
        if (s.MarketError is { } err) form.Children.Add(T(err, 12, Tok.Bad, wrap: true));
        form.Children.Add(T($"Noté pour le marché « {data.Label(shown)} », partagé avec tous les joueurs d'Aion 2 Todo avec ton pseudo ; la date est celle où tu le notes. " +
                            "Un objet hors liste est noté sous le nom que tu tapes.", 11, Tok.Muted, wrap: true));
        return Card(form, "Noter un prix à la main", null, new Thickness(16, 10, 16, 12));
    }

    /// <summary>Retirés ces 30 derniers jours sur ce marché : rétablir un prix retiré par erreur.</summary>
    Control MarketRemovedCard(MarketData data, string shown)
    {
        var removed = data.Removed.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();
        if (removed.Count == 0) return Card(T("Aucun prix retiré ces 30 derniers jours sur ce marché.", 13, Tok.Muted), "Retirés récemment");
        var items = data.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        const string remCols = "*,110,260,90";
        return TableCard("Retirés récemment", T("30 derniers jours", 11, Tok.Muted),
            TableHeader(remCols, "Objet", "Prix", "Noté · retiré", ""),
            removed.Select(p => (Control)new Border
            {
                Padding = new Thickness(20, 4),
                Child = Columns(remCols, MarketItemLine(items.GetValueOrDefault(p.ItemKey), p.ItemName), T(Fmt.Big(p.Price), 12, Tok.Text2, mono: true).AlignRight(),
                    T($"par {p.By ?? "un compte supprimé"} le {p.At.ToLocalTime():dd/MM} · retiré par {p.RemovedBy ?? "un compte supprimé"} le {p.RemovedAt.ToLocalTime():dd/MM}", 11, Tok.Muted, wrap: true),
                    Right(Clickable(T("rétablir", 11, Accent.Main, mono: true), () => market.Restore(p), null, Tok.Hover, new Thickness(6, 2)))),
            }));
    }

    /// <summary>Un objet en titre d'inspecteur : barre de la couleur de son rang, nom coloré (sur plusieurs lignes), sa catégorie dessous.</summary>
    Control ItemTitle(MarketItem? item, string name)
    {
        var grade = item?.GameGrade;
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 2, 10, 2), Background = grade is null ? Tok.Border : MarketBrush(grade) };
        DockPanel.SetDock(bar, Dock.Left);
        var text = new StackPanel { Spacing = 3, Children = { T(name, 17, MarketBrush(grade), FontWeight.SemiBold, wrap: true) } };
        if (item is not null)
            text.Children.Add(T(item.Kind + (grade is null || item.IsGathering ? "" : $" · {Market.GradeWord(grade)}") + (item.Gathered ? " · récolté" : ""), 12, Tok.Muted, wrap: true));
        return new DockPanel { Children = { bar, text } };
    }

    /// <summary>Recettes notées qui utilisent cet objet (clic : la recette dans l'écran Craft).</summary>
    Control? UsedInRecipes(MarketData data, string key, string name)
    {
        var uses = (data.Recipes ?? []).Where(rc => rc.Inputs.Any(p => p.Key == key || Market.Fold(p.Name) == Market.Fold(name))).ToList();
        if (uses.Count == 0) return null;
        var list = new StackPanel { Spacing = 2, Children = { Label("Sert à fabriquer") } };
        foreach (var rc in uses.Take(8))
        {
            var id = rc.Id;
            var link = Clickable(T(rc.Output.Name, 12, Accent.Main, wrap: true), () => update(st => { st.Screen = Screen.Craft; st.CraftPage = CraftPage.Recipe; st.CraftRecipe = id; }),
                null, Tok.Hover, new Thickness(4, 2));
            link.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(Tip(link, "Ouvrir la recette dans l'écran Craft"));
        }
        return list;
    }

    /// <summary>Inspecteur de l'écran HdV (page des derniers prix) : l'objet choisi, ses chiffres, sa courbe, chaque relevé (retirer un prix faux).</summary>
    Control? MarketInspector()
    {
        if (s.MarketPage != MarketPage.Prices || !market.Linked || market.Data is not { } data) return null;
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · objet") } };
        var shown = MarketShown(data);
        var here = data.Prices.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();
        if (MarketSelected(MarketRows(data, here)) is not { } selected) return panel;
        var list = selected.List;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        panel.Children.Add(body);
        body.Children.Add(ItemTitle(selected.Item, list[^1].ItemName));
        if (GradeRow(selected.Item) is { } gradeRow) body.Children.Add(gradeRow);
        if (RenameRow(selected.Key, list[^1].ItemName) is { } renameRow) body.Children.Add(renameRow);
        body.Children.Add(KeyValue(
        [
            ("dernier prix", $"{Fmt.Big(list[^1].Price)} · {Market.Since(list[^1].At)}", Tok.Text),
            ("moyenne", Fmt.Big(list.Average(p => (double)p.Price)), null),
            ("plus bas", Fmt.Big(list.Min(p => p.Price)), Tok.Good),
            ("plus haut", Fmt.Big(list.Max(p => p.Price)), Tok.Bad),
            ("relevés", $"{list.Count} sur {data.Label(shown)}", null),
        ]));
        var color = selected.Item?.GameGrade is { } sg ? MarketBrush(sg) : Accent.Main;
        if (list.Count > 1)
            body.Children.Add(new TimeChart(list.Select(p => new ChartPoint(p.At, p.Price, $"{p.At.ToLocalTime():ddd dd/MM HH:mm} : {Fmt.Big(p.Price)} Kinah, par {By(p)}")).ToList(),
                v => Fmt.Big(v), color, 130));
        else body.Children.Add(T("Un seul relevé : la courbe se dessine dès le deuxième.", 12, Tok.Muted, wrap: true));
        if (UsedInRecipes(data, selected.Key, list[^1].ItemName) is { } uses) body.Children.Add(uses);

        var entries = new StackPanel { Spacing = 0, Children = { Label("Relevés · le plus récent d'abord").WithMargin(0, 0, 0, 6) } };
        foreach (var p in Enumerable.Reverse(list).Take(30))
        {
            var price = T(Fmt.Big(p.Price), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight();
            var who = new StackPanel { Spacing = 1, Children = { T(p.At.ToLocalTime().ToString("dd/MM/yy HH:mm"), 11, Tok.Text2, mono: true), T(By(p), 11, p.Mine ? Accent.Main : Tok.Muted) } };
            Control action = s.MarketConfirm == p.Id
                ? new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        Clickable(T("retirer pour tous", 11, Tok.Bad, FontWeight.SemiBold, mono: true), () => { update(st => st.MarketConfirm = null); market.Remove(p); }, null, Tok.Hover, new Thickness(4, 2)),
                        Clickable(T("non", 11, Tok.Muted, mono: true), () => update(st => st.MarketConfirm = null), null, Tok.Hover, new Thickness(4, 2)),
                    },
                }
                : Right(Tip(Clickable(T("retirer", 11, Tok.Muted, mono: true), () => update(st => st.MarketConfirm = p.Id), null, Tok.Hover, new Thickness(4, 2)),
                    "Un prix faux (faute de frappe, farce) : il disparaît pour tout le monde, et se rétablit pendant 30 jours."));
            entries.Children.Add(new Border
            {
                Padding = new Thickness(0, 5), BorderBrush = Tok.RowLine2, BorderThickness = new Thickness(0, 0, 0, 1),
                Child = ColumnsSpaced("*,90,Auto", 8, who, price, action),
            });
        }
        body.Children.Add(entries);
        return panel;
    }
}
