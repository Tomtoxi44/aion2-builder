using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Avalonia;
using Avalonia.Controls;
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
/// <summary>Un marché proposé par le site : code, nom affiché, nombre de prix, le tien (tes persos du site y jouent).</summary>
sealed record MarketChoice(string Code, string Label, int Prices, bool Mine);

sealed record MarketData(List<MarketItem> Items, List<MarketPrice> Prices, List<MarketRemoved> Removed, List<MarketChoice>? Markets = null, string? DefaultMarket = null)
{
    /// <summary>Dernier prix de chaque objet sur un marché (les prix arrivent du plus ancien au plus récent).</summary>
    public Dictionary<string, MarketPrice> Latest(string market) =>
        Prices.Where(p => p.Market.Equals(market, StringComparison.OrdinalIgnoreCase)).GroupBy(p => p.ItemKey).ToDictionary(g => g.Key, g => g.MaxBy(p => p.At)!);

    public string Label(string market) => Markets?.FirstOrDefault(m => m.Code.Equals(market, StringComparison.OrdinalIgnoreCase))?.Label
                                         ?? (market.StartsWith('*') ? "inter-serveurs" : market);
}

/// <summary>Ce que l'écran Récolte → Prix HdV reçoit de la fenêtre : les prix, l'état de lecture, et les actions.</summary>
sealed record MarketState(MarketData? Data, bool Linked, string? Status, Func<string?, string, long, string, Task<string?>> Add,
    Action<MarketPrice> Remove, Action<MarketRemoved> Restore);

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
    public static async Task<string?> Add(string key, string? itemKey, string itemName, long price, string market)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/prices", new { itemKey, itemName, price, market }));
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
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
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
        return Tip(Clickable(line, () => update(st => st.GatherDay = "prix"), null, Tok.Hover, new Thickness(0, 2)),
            "Prix notés à la main par les joueurs d'Aion 2 Todo (onglet « Prix HdV » du site ou ici) : le dernier relevé de chaque ressource.");
    }

    /// <summary>Valeur d'un lot aux derniers prix, et nombre de ressources sans prix ; null si aucune n'a de prix.</summary>
    static (long Value, int Missing)? LotValue(Dictionary<string, MarketPrice> latest, IEnumerable<(long Id, long Qty)> lot)
    {
        var list = lot.Where(x => x.Qty > 0).ToList();
        var priced = list.Where(x => latest.ContainsKey(x.Id.ToString())).ToList();
        return priced.Count == 0 ? null : (priced.Sum(x => x.Qty * latest[x.Id.ToString()].Price), list.Count - priced.Count);
    }

    /// <summary>
    /// Récolte → Prix HdV, présenté comme l'hôtel du jeu : chaque objet à la couleur de son rang (blanc, vert, bleu, jaune), les
    /// chiffres du marché en tête, un prix à noter, les derniers prix filtrables (recherche, rang, type), la courbe d'un objet avec
    /// chaque relevé (retirer un prix faux, en deux clics), et les retraits récents à rétablir.
    /// </summary>
    Control MarketScreen(IReadOnlyList<Combat.GatherDay> days)
    {
        var back = Choice([("Récolte", false, () => update(st => st.GatherDay = null)), ("Prix HdV", true, () => { })]);
        const string hint = "L'hôtel de chaque serveur a ses propres prix ; l'hôtel inter-serveurs réunit tous les serveurs de la région. " +
                            "Choisis celui où tu as vu le prix : la page n'affiche que les prix de ce marché.";
        if (!market.Linked)
            return Page("Récolte · prix de l'hôtel des ventes", back, Card(T("Les prix de l'hôtel des ventes sont notés par les joueurs d'Aion 2 Todo et partagés entre eux. " +
                "Pour les voir et en noter ici, colle ta clé Aion 2 Todo dans l'écran Familiers du Builder (sur le site : onglet Familiers → Pet Meter → " +
                "« Afficher ma clé »), ou garde le Pet Meter relié : sa clé est reprise toute seule.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        if (market.Data is not { } data)
            return Page("Récolte · prix de l'hôtel des ventes", back, T(market.Status ?? "Lecture des prix…", 13, Tok.Muted, wrap: true));

        var items = data.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        MarketItem? ItemOf(string key) => items.GetValueOrDefault(key);
        var blocks = new List<Control?>();
        if (market.Status is { } status) blocks.Add(T(status, 12, Tok.Warn, wrap: true));

        // Le marché : tes serveurs d'abord, puis ceux qui ont des prix, puis une liste de tous les autres.
        var shown = MarketShown(data);
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
        blocks.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children =
                    {
                        T("Marché", 13, weight: FontWeight.Medium).WithMargin(0, 4, 0, 0),
                        Choice(quick.Select(m => ($"{m.Label}{(m.Prices > 0 ? $" · {m.Prices}" : "")}", m.Code.Equals(shown, StringComparison.OrdinalIgnoreCase),
                            (Action)(() => update(st => { st.MarketMarket = m.Code; st.MarketItem = null; st.MarketConfirm = null; }))))),
                        others,
                    },
                },
                T(hint, 11, Tok.Muted, wrap: true),
            },
        });
        var here = data.Prices.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();
        var removedHere = data.Removed.Where(p => p.Market.Equals(shown, StringComparison.OrdinalIgnoreCase)).ToList();

        // Les chiffres du marché : objets qui ont un prix, relevés de la semaine, le dernier, la valeur de ta récolte du jour.
        var gameToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));
        var todayLot = days.FirstOrDefault(d => d.Date == gameToday)?.Servers is { Count: > 0 } servers
            ? servers.Values.SelectMany(g => g.Items ?? []).Where(i => i.Id is not null).GroupBy(i => (long)i.Id!.Value).Select(g => (g.Key, (long)g.Sum(i => i.Qty))).ToList()
            : [];
        var harvest = LotValue(data.Latest(shown), todayLot);
        var lastPrice = here.MaxBy(p => p.At);
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Objets avec un prix", here.Select(p => p.ItemKey).Distinct().Count().ToString(), size: 20).WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Relevés · 7 jours", here.Count(p => p.At.ToUniversalTime() >= DateTime.UtcNow.AddDays(-7)).ToString(), size: 20).WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Tip(Kpi("Dernier relevé", lastPrice is null ? "—" : Market.Since(lastPrice.At), size: 20),
            lastPrice is null ? "Aucun prix sur ce marché." : $"{lastPrice.ItemName} : {Fmt.Big(lastPrice.Price)} Kinah, par {By(lastPrice)} le {lastPrice.At.ToLocalTime():dd/MM HH:mm}")
            .WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Tip(Kpi("Ta récolte du jour", harvest is { } h ? $"≈ {Fmt.Big(h.Value)}" : "—", harvest is null ? null : Tok.Good, 20),
            harvest is { } hv ? $"Kinah, au dernier prix de chaque ressource sur ce marché" + (hv.Missing > 0 ? $" ({Fmt.Plural(hv.Missing, "ressource")} sans prix, pas comptée{(hv.Missing > 1 ? "s" : "")})" : "") + "."
                : "Aucune ressource récoltée aujourd'hui n'a encore de prix sur ce marché."));
        blocks.Add(kpis);

        // Noter un prix : l'objet (liste avec recherche, sans accents ni majuscules, aussi par type : « anneau », « épées »), le prix, « noter ».
        var catalog = data.Items.OrderByDescending(i => i.Gathered).ThenByDescending(i => i.IsGathering).ThenBy(i => i.Name, French)
            .DistinctBy(i => Market.Fold(i.Name)).ToList();
        var pick = new AutoCompleteBox
        {
            ItemsSource = catalog, FilterMode = AutoCompleteFilterMode.Custom, Text = s.MarketPick ?? "", MinimumPrefixLength = 0, MaxDropDownHeight = 340,
            PlaceholderText = "objet : Od, orichalque, un anneau…", MinWidth = 320,
            ItemFilter = (search, o) => o is MarketItem i && Market.Matches(search, $"{i.Name} {i.Category}"),
            ItemTemplate = new FuncDataTemplate<MarketItem>((i, _) => MarketItemLine(i, i.Name, 13, withKind: true)),
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
        blocks.Add(Card(form, "Noter un prix vu à l'hôtel des ventes", null, new Thickness(16, 10, 16, 12)));

        // Derniers prix : un objet par ligne, rangés comme l'hôtel du jeu (la récolte par famille, du blanc au jaune, puis
        // l'équipement) ; filtres par rang et par type ; recherche instantanée ; clic : sa courbe plus bas.
        var byItem = here.GroupBy(p => p.ItemKey).Select(g => (Key: g.Key, Item: ItemOf(g.Key), List: g.OrderBy(p => p.At).ToList())).ToList();
        // Famille d'une ressource : son numéro sans le dernier chiffre (Aria, Aria fraîche, pure, éclatante), nommée par la plus commune.
        var family = data.Items.Where(i => i.IsGathering && long.TryParse(i.Key, out _)).GroupBy(i => long.Parse(i.Key) / 10)
            .ToDictionary(g => g.Key, g => g.MinBy(i => i.Rank)!.Name);
        string SortName((string Key, MarketItem? Item, List<MarketPrice> List) x) =>
            x.Item?.IsGathering == true && long.TryParse(x.Key, out var id) && family.TryGetValue(id / 10, out var f) ? f : x.List[^1].ItemName;
        int GradeRank(MarketItem? i) => i?.GameGrade is { } g ? db.Grade(g).Rank : 0;
        var present = byItem.Select(x => x.Item?.GameGrade).OfType<string>().Distinct().OrderBy(g => db.Grade(g).Rank).ToList();
        var kinds = byItem.Select(x => x.Item?.IsGathering == true ? "Récolte" : "Équipement et autres").Distinct().ToList();
        var filtered = byItem
            .Where(x => s.MarketGrade is null || x.Item?.GameGrade == s.MarketGrade)
            .Where(x => s.MarketKind is null || (x.Item?.IsGathering == true ? "Récolte" : "Équipement et autres") == s.MarketKind)
            .OrderByDescending(x => x.Item?.IsGathering == true).ThenBy(SortName, French).ThenBy(x => GradeRank(x.Item)).ThenBy(x => x.List[^1].ItemName, French).ToList();
        var selected = byItem.FirstOrDefault(x => x.Key == s.MarketItem);
        if (selected.Key is null && byItem.Count > 0) selected = byItem.MaxBy(x => x.List[^1].At);
        if (byItem.Count == 0)
            blocks.Add(Card(T($"Aucun prix noté pour l'instant sur ce marché ({data.Label(shown)}) : note le premier au-dessus.", 13, Tok.Muted), "Derniers prix de la communauté"));
        else
        {
            var query = new TextBox { Text = s.MarketQuery ?? "", PlaceholderText = "filtrer : aria, anneau…", Width = 210 };
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { query } };
            if (present.Count > 1)
                filters.Children.Add(Choice(new[] { ("tous les rangs", s.MarketGrade is null, (Action)(() => update(st => st.MarketGrade = null))) }
                    .Concat(present.Select(g => (Market.GradeWord(g), s.MarketGrade == g, (Action)(() => update(st => st.MarketGrade = g)))))));
            if (kinds.Count > 1)
                filters.Children.Add(Choice(new[] { ("tout", s.MarketKind is null, (Action)(() => update(st => st.MarketKind = null))) }
                    .Concat(kinds.Select(k => (k == "Récolte" ? "récolte" : "équipement et autres", s.MarketKind == k, (Action)(() => update(st => st.MarketKind = k)))))));

            const string cols = "*,130,110,80,170,96";
            var rows = new List<(Control Row, string Text)>();
            foreach (var x in filtered)
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
                    : Tip(T(d == 0 ? "=" : $"{(d > 0 ? "+" : "−")}{Math.Abs(pct):0.#} %", 12, d > 0 ? Tok.Good : d < 0 ? Tok.Bad : Tok.Muted, mono: true).AlignRight(),
                        d == 0 ? $"Même prix qu'au relevé d'avant ({Fmt.Big(prev.Price)})" : $"{(d > 0 ? "+" : "−")}{Fmt.Big(Math.Abs(d))} Kinah depuis le relevé d'avant ({Fmt.Big(prev.Price)}, {prev.At.ToLocalTime():dd/MM})");
                var when = Tip(T($"{Market.Since(last.At)} · {By(last)}" + (x.List.Count > 1 ? $" · {x.List.Count}" : ""), 12, Tok.Muted),
                    $"Dernier relevé le {last.At.ToLocalTime():dddd dd/MM à HH:mm}, par {By(last)}" + (x.List.Count > 1 ? $" ; {x.List.Count} relevés sur ce marché" : ""));
                var row = TableRow(Columns(cols, MarketItemLine(x.Item, last.ItemName),
                    T(x.Item?.Kind ?? "noté par son nom", 11, Tok.Muted),
                    T(Fmt.Big(last.Price), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                    evolution, when, spark), x.Key == selected.Key, () => update(st => { st.MarketItem = x.Key; st.MarketConfirm = null; }));
                rows.Add((row, $"{last.ItemName} {x.Item?.Category}"));
            }
            var none = T("Aucun objet ne correspond à ce filtre.", 12, Tok.Muted).WithMargin(20, 8, 20, 8);
            void Filter()
            {
                var shownRows = 0;
                foreach (var (row, text) in rows) { row.IsVisible = Market.Matches(query.Text, text); if (row.IsVisible) shownRows++; }
                none.IsVisible = shownRows == 0;
            }
            query.TextChanged += (_, _) => { s.MarketQuery = query.Text; Filter(); };
            Filter();
            blocks.Add(filters);
            blocks.Add(TableCard("Derniers prix de la communauté", T($"{Fmt.Plural(filtered.Count, "objet")} · prix unitaire en Kinah", 11, Tok.Muted, mono: true),
                TableHeader(cols, "Objet", "Type", "Prix", "Évolution", "Dernier relevé", "Tendance"), rows.Select(x => x.Row).Append((Control)none)));
        }

        // L'objet choisi : sa courbe, ses chiffres et chaque relevé (le plus récent d'abord).
        if (selected.Key is not null)
        {
            var list = selected.List;
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(MarketItemLine(selected.Item, list[^1].ItemName, 17, withKind: true));
            var stats = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
            stats.Children.Add(Kpi("Dernier", Fmt.Big(list[^1].Price)).WithMargin(0, 0, 10, 0));
            stats.Children.Add(Kpi("Moyenne", Fmt.Big(list.Average(p => (double)p.Price))).WithMargin(0, 0, 10, 0));
            stats.Children.Add(Kpi("Plus bas", Fmt.Big(list.Min(p => p.Price)), Tok.Good).WithMargin(0, 0, 10, 0));
            stats.Children.Add(Kpi("Plus haut", Fmt.Big(list.Max(p => p.Price)), Tok.Bad));
            body.Children.Add(stats);
            var color = selected.Item?.GameGrade is { } sg ? MarketBrush(sg) : Accent.Main;
            if (list.Count > 1)
            {
                var points = list.Select(p => new ChartPoint(p.At, p.Price, $"{p.At.ToLocalTime():ddd dd/MM HH:mm} : {Fmt.Big(p.Price)} Kinah, par {By(p)}")).ToList();
                body.Children.Add(new TimeChart(points, v => Fmt.Big(v), color, 150));
            }
            else body.Children.Add(T("Un seul relevé : la courbe se dessine dès le deuxième.", 12, Tok.Muted));

            const string entryCols = "130,*,110,170";
            var entries = new StackPanel { Children = { TableHeader(entryCols, "Date", "Noté par", "Prix", "") } };
            foreach (var p in Enumerable.Reverse(list).Take(30))
            {
                Control action = s.MarketConfirm == p.Id
                    ? new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            Clickable(T("retirer pour tous", 11, Tok.Bad, FontWeight.SemiBold, mono: true), () => { update(st => st.MarketConfirm = null); market.Remove(p); }, null, Tok.Hover, new Thickness(6, 2)),
                            Clickable(T("non", 11, Tok.Muted, mono: true), () => update(st => st.MarketConfirm = null), null, Tok.Hover, new Thickness(6, 2)),
                        },
                    }
                    : Right(Tip(Clickable(T("retirer", 11, Tok.Muted, mono: true), () => update(st => st.MarketConfirm = p.Id), null, Tok.Hover, new Thickness(6, 2)),
                        "Un prix faux (faute de frappe, farce) : il disparaît pour tout le monde, et se rétablit pendant 30 jours."));
                entries.Children.Add(new Border
                {
                    Padding = new Thickness(20, 4),
                    Child = Columns(entryCols, T(p.At.ToLocalTime().ToString("dd/MM/yy HH:mm"), 12, Tok.Text2, mono: true), T(By(p), 12, p.Mine ? Accent.Main : Tok.Text2),
                        T(Fmt.Big(p.Price), 12, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(), action),
                });
            }
            body.Children.Add(entries);
            blocks.Add(Card(body, "Prix de l'objet choisi", T(Fmt.Plural(list.Count, "relevé"), 11, Tok.Muted, mono: true), new Thickness(12, 10, 12, 8)));
        }

        // Retirés ces 30 derniers jours : rétablir un prix retiré par erreur.
        if (removedHere.Count > 0)
        {
            const string remCols = "*,110,260,90";
            blocks.Add(TableCard("Retirés récemment", T("30 derniers jours", 11, Tok.Muted),
                TableHeader(remCols, "Objet", "Prix", "Noté · retiré", ""),
                removedHere.Select(p => (Control)new Border
                {
                    Padding = new Thickness(20, 4),
                    Child = Columns(remCols, MarketItemLine(ItemOf(p.ItemKey), p.ItemName), T(Fmt.Big(p.Price), 12, Tok.Text2, mono: true).AlignRight(),
                        T($"par {p.By ?? "un compte supprimé"} le {p.At.ToLocalTime():dd/MM} · retiré par {p.RemovedBy ?? "un compte supprimé"} le {p.RemovedAt.ToLocalTime():dd/MM}", 11, Tok.Muted, wrap: true),
                        Right(Clickable(T("rétablir", 11, Accent.Main, mono: true), () => market.Restore(p), null, Tok.Hover, new Thickness(6, 2)))),
                })));
        }

        blocks.Add(T("Prix unitaires notés à la main par les joueurs d'Aion 2 Todo (aucune source publique n'existe), avec le pseudo de leur auteur : " +
                     "les mêmes que l'onglet « Prix HdV » du site. Couleurs de rareté du jeu : blanc, vert, bleu, jaune. Un prix faux se retire par " +
                     "n'importe qui et se rétablit pendant 30 jours.", 11, Tok.Muted, wrap: true).WithLineHeight(1.4));
        return Page("Récolte · prix de l'hôtel des ventes", back, blocks.ToArray());
    }
}
