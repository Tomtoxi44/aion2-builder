using System.Net.Http.Json;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Objet de la liste du site : ressource connue (numéro du jeu, en texte) ou objet libre (« name:… »).</summary>
sealed record MarketItem(string Key, string Name, int Rank, bool Gathered);
/// <summary>Un prix de l'hôtel des ventes noté par un joueur d'Aion 2 Todo : By = son pseudo (null : compte supprimé), Mine = toi.</summary>
sealed record MarketPrice(int Id, string ItemKey, string ItemName, long Price, DateTime At, string? By, bool Mine);
/// <summary>Un prix retiré ces 30 derniers jours : qui l'avait noté, qui l'a retiré (rétablissable).</summary>
sealed record MarketRemoved(int Id, string ItemKey, string ItemName, long Price, DateTime At, string? By, DateTime RemovedAt, string? RemovedBy);

sealed record MarketData(List<MarketItem> Items, List<MarketPrice> Prices, List<MarketRemoved> Removed)
{
    /// <summary>Dernier prix de chaque objet (les prix arrivent du plus ancien au plus récent).</summary>
    public Dictionary<string, MarketPrice> Latest { get; } = Prices.GroupBy(p => p.ItemKey).ToDictionary(g => g.Key, g => g.MaxBy(p => p.At)!);
}

/// <summary>Ce que l'écran Récolte → Prix HdV reçoit de la fenêtre : les prix, l'état de lecture, et les actions.</summary>
sealed record MarketState(MarketData? Data, bool Linked, string? Status, Func<string?, string, long, Task<string?>> Add,
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
    public static async Task<string?> Add(string key, string? itemKey, string itemName, long price)
    {
        using var http = BugReports.Client(key);
        return await Error(await http.PostAsJsonAsync("/api/sync/market/prices", new { itemKey, itemName, price }));
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
}

sealed partial class ScreenViews
{
    static string By(MarketPrice p) => p.Mine ? "toi" : p.By ?? "un compte supprimé";

    static T Right<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Right;
        return control;
    }

    /// <summary>
    /// Valeur d'un lot de ressources (numéro → quantité) au dernier prix de la communauté : une ligne cliquable vers Prix HdV,
    /// null sans prix ni clé.
    /// </summary>
    Control? MarketValue(IEnumerable<(long Id, long Qty)> lot, string what)
    {
        if (market.Data is not { } data) return null;
        var list = lot.Where(x => x.Qty > 0).ToList();
        var priced = list.Where(x => data.Latest.ContainsKey(x.Id.ToString())).ToList();
        if (priced.Count == 0) return null;
        var value = priced.Sum(x => x.Qty * data.Latest[x.Id.ToString()].Price);
        var missing = list.Count - priced.Count;
        var line = T($"≈ {Fmt.Big(value)} Kinah : valeur {what} au dernier prix de la communauté" +
                     (missing > 0 ? $" ({Fmt.Plural(missing, "ressource")} sans prix, pas comptée{(missing > 1 ? "s" : "")})" : "") + " · voir les prix",
            12, Tok.Text2, wrap: true);
        return Tip(Clickable(line, () => update(st => st.GatherDay = "prix"), null, Tok.Hover, new Thickness(0, 2)),
            "Prix notés à la main par les joueurs d'Aion 2 Todo (onglet « Prix HdV » du site ou ici) : le dernier relevé de chaque ressource.");
    }

    /// <summary>
    /// Récolte → Prix HdV : noter un prix, les derniers prix de la communauté (auteur, date, évolution, petite courbe), la courbe
    /// d'un objet avec chaque relevé (retirer un prix faux, en deux clics), et les retraits récents à rétablir.
    /// </summary>
    Control MarketScreen(IReadOnlyList<Combat.GatherDay> days)
    {
        var back = Choice([("Récolte", false, () => update(st => st.GatherDay = null)), ("Prix HdV", true, () => { })]);
        if (!market.Linked)
            return Page("Récolte · prix de l'hôtel des ventes", back, Card(T("Les prix de l'hôtel des ventes sont notés par les joueurs d'Aion 2 Todo et partagés entre eux. " +
                "Pour les voir et en noter ici, colle ta clé Aion 2 Todo dans l'écran Familiers du Builder (sur le site : onglet Familiers → Pet Meter → " +
                "« Afficher ma clé »), ou garde le Pet Meter relié : sa clé est reprise toute seule.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        if (market.Data is not { } data)
            return Page("Récolte · prix de l'hôtel des ventes", back, T(market.Status ?? "Lecture des prix…", 13, Tok.Muted, wrap: true));

        var blocks = new List<Control?>
        {
            T("Prix unitaires notés à la main par les joueurs d'Aion 2 Todo (aucune source publique n'existe), avec le pseudo de leur auteur : " +
              "les mêmes que l'onglet « Prix HdV » du site. Un prix faux se retire par n'importe qui et se rétablit pendant 30 jours.", 12, Tok.Muted, wrap: true).WithLineHeight(1.4),
        };
        if (market.Status is { } status) blocks.Add(T(status, 12, Tok.Warn, wrap: true));

        // Noter un prix : l'objet (liste avec recherche, ressources récoltées d'abord), le prix unitaire, puis « noter ».
        var names = data.Items.OrderByDescending(i => i.Gathered).ThenBy(i => i.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), true))
            .Select(i => i.Name).Distinct().ToList();
        var pick = new AutoCompleteBox
        {
            ItemsSource = names, FilterMode = AutoCompleteFilterMode.ContainsOrdinal, Text = s.MarketPick ?? "", MinimumPrefixLength = 0,
            PlaceholderText = "objet : orichalque, Od, Aria…", MinWidth = 280,
        };
        pick.TextChanged += (_, _) => s.MarketPick = pick.Text;
        var price = new TextBox { Text = s.MarketPriceText ?? "", Width = 150, PlaceholderText = "prix unitaire (Kinah)" };
        price.TextChanged += (_, _) => s.MarketPriceText = price.Text;
        async Task Submit()
        {
            var name = (pick.Text ?? "").Trim();
            var digits = new string((price.Text ?? "").Where(char.IsDigit).ToArray());
            if (name.Length == 0 || !long.TryParse(digits, out var value) || value < 1) { update(st => st.MarketError = "Choisis un objet et tape son prix unitaire."); return; }
            var item = data.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            var error = await market.Add(item?.Key, item?.Name ?? name, value);
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
        form.Children.Add(T("Le prix est partagé avec tous les joueurs d'Aion 2 Todo, avec ton pseudo ; la date est celle où tu le notes.", 11, Tok.Muted, wrap: true));
        blocks.Add(Card(form, "Noter un prix vu à l'hôtel des ventes", null, new Thickness(16, 10, 16, 12)));

        // Derniers prix : un objet par ligne ; clic : sa courbe plus bas.
        var byItem = data.Prices.GroupBy(p => p.ItemKey).Select(g => (Key: g.Key, List: g.OrderBy(p => p.At).ToList()))
            .OrderBy(x => x.List[^1].ItemName, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), true)).ToList();
        var selected = byItem.FirstOrDefault(x => x.Key == s.MarketItem);
        if (selected.Key is null && byItem.Count > 0) selected = byItem.MaxBy(x => x.List[^1].At);
        if (byItem.Count == 0)
            blocks.Add(Card(T("Aucun prix noté pour l'instant : note le premier au-dessus.", 13, Tok.Muted), "Derniers prix de la communauté"));
        else
        {
            const string cols = "*,110,100,220,100";
            var rows = byItem.Select(x =>
            {
                var last = x.List[^1];
                var prev = x.List.Count > 1 ? x.List[^2] : null;
                var d = prev is null ? 0 : last.Price - prev.Price;
                var spark = x.List.Count > 1
                    ? (Control)new TimeChart(x.List.TakeLast(20).Select(p => new ChartPoint(p.At, p.Price, $"{p.At.ToLocalTime():dd/MM HH:mm} : {Fmt.Big(p.Price)}")).ToList(),
                        v => Fmt.Big(v), Accent.Main, 22, compact: true) { Width = 90, HorizontalAlignment = HorizontalAlignment.Left }
                    : T("—", 12, Tok.Faint, mono: true);
                return (Control)TableRow(Columns(cols, T(last.ItemName, 13),
                    T(Fmt.Big(last.Price), 12, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                    T(prev is null ? "—" : d == 0 ? "=" : $"{(d > 0 ? "+" : "−")}{Fmt.Big(Math.Abs(d))}", 12, d > 0 ? Tok.Good : d < 0 ? Tok.Bad : Tok.Muted, mono: true).AlignRight(),
                    T($"{last.At.ToLocalTime():dd/MM HH:mm} · {By(last)}" + (x.List.Count > 1 ? $" · {x.List.Count} relevés" : ""), 12, Tok.Muted),
                    spark), x.Key == selected.Key, () => update(st => { st.MarketItem = x.Key; st.MarketConfirm = null; }));
            });
            blocks.Add(TableCard("Derniers prix de la communauté", T(Fmt.Plural(byItem.Count, "objet"), 11, Tok.Muted, mono: true),
                TableHeader(cols, "Objet", "Prix", "Évolution", "Dernier relevé", "Tendance"), rows));
        }

        // L'objet choisi : sa courbe, ses chiffres et chaque relevé (le plus récent d'abord).
        if (selected.Key is not null)
        {
            var list = selected.List;
            var body = new StackPanel { Spacing = 12 };
            var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
            kpis.Children.Add(Kpi("Dernier", Fmt.Big(list[^1].Price)).WithMargin(0, 0, 10, 0));
            kpis.Children.Add(Kpi("Moyenne", Fmt.Big(list.Average(p => (double)p.Price))).WithMargin(0, 0, 10, 0));
            kpis.Children.Add(Kpi("Plus bas", Fmt.Big(list.Min(p => p.Price))).WithMargin(0, 0, 10, 0));
            kpis.Children.Add(Kpi("Plus haut", Fmt.Big(list.Max(p => p.Price))));
            body.Children.Add(kpis);
            if (list.Count > 1)
            {
                var points = list.Select(p => new ChartPoint(p.At, p.Price, $"{p.At.ToLocalTime():ddd dd/MM HH:mm} : {Fmt.Big(p.Price)} Kinah, par {By(p)}")).ToList();
                body.Children.Add(new TimeChart(points, v => Fmt.Big(v), Accent.Main, 150));
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
            blocks.Add(Card(body, $"Prix de {list[^1].ItemName}", T(Fmt.Plural(list.Count, "relevé"), 11, Tok.Muted, mono: true), new Thickness(12, 10, 12, 8)));
        }

        // Retirés ces 30 derniers jours : rétablir un prix retiré par erreur.
        if (data.Removed.Count > 0)
        {
            const string remCols = "*,110,260,90";
            blocks.Add(TableCard("Retirés récemment", T("30 derniers jours", 11, Tok.Muted),
                TableHeader(remCols, "Objet", "Prix", "Noté · retiré", ""),
                data.Removed.Select(p => (Control)new Border
                {
                    Padding = new Thickness(20, 4),
                    Child = Columns(remCols, T(p.ItemName, 13), T(Fmt.Big(p.Price), 12, Tok.Text2, mono: true).AlignRight(),
                        T($"par {p.By ?? "un compte supprimé"} le {p.At.ToLocalTime():dd/MM} · retiré par {p.RemovedBy ?? "un compte supprimé"} le {p.RemovedAt.ToLocalTime():dd/MM}", 11, Tok.Muted, wrap: true),
                        Right(Clickable(T("rétablir", 11, Accent.Main, mono: true), () => market.Restore(p), null, Tok.Hover, new Thickness(6, 2)))),
                })));
        }

        // Ta récolte d'aujourd'hui et ton stock, à ces prix.
        var gameToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));
        if (days.FirstOrDefault(d => d.Date == gameToday)?.Servers is { Count: > 0 } servers)
            blocks.Add(MarketValue(servers.Values.SelectMany(g => g.Items ?? []).Where(i => i.Id is not null).GroupBy(i => (long)i.Id!.Value)
                .Select(g => (g.Key, (long)g.Sum(i => i.Qty))), "de ta récolte du jour"));
        return Page("Récolte · prix de l'hôtel des ventes", back, blocks.ToArray());
    }
}
