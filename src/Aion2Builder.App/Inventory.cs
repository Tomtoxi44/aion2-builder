using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Un objet de l'inventaire, toutes piles réunies : où il est (rangement, case), combien, et son prix connu.</summary>
sealed record InventoryEntry(long Id, string Name, MarketItem? Item, string Family, long Qty, List<(string Place, int Slot, long Qty)> Where, long? Price)
{
    public long? Value => Price is { } p ? p * Qty : null;
}

sealed partial class ScreenViews
{
    /// <summary>Les Kinah sont une pile du sac pour le jeu : comptés à part, pas dans la liste.</summary>
    const long KinahItem = 930100031;

    static readonly string[] InventoryFamilies = ["Récolte", "Équipement", "Autres objets", "Sans nom"];

    /// <summary>
    /// Écran Inventaire : tout ton sac et ton coffre tels que le Pet Meter les a lus à ton entrée en jeu (inventory.json, sur ce PC),
    /// rangés par familles (récolte, équipement, autres objets, objets encore sans nom), avec la couleur de rareté du jeu, le
    /// rangement et la case, la quantité et la valeur au dernier prix de la communauté. Les noms viennent de la liste des
    /// ressources de récolte et de la liste d'Aion 2 Todo (pièces du site officiel, objets nommés par les joueurs) ; un objet
    /// sans nom se nomme ici, pour tous. Les pièces portées restent dans l'écran Équipement. Base du futur planificateur de craft.
    /// </summary>
    Control InventoryScreen(IReadOnlyList<GatherDay> days)
    {
        if (PetMeterInventory.Load() is not { Chars.Count: > 0 } inv)
            return Page("Inventaire", null, Card(T("Ton inventaire vient du Pet Meter de ce PC (v0.21.0 ou plus). Lance-le avant d'entrer en jeu : le jeu " +
                "envoie alors tout ton sac et ton coffre, et le Pet Meter les garde sur ce PC (inventory.json). Lancé après l'entrée en jeu, il ne voit que " +
                "ce qui change ensuite. Rien n'est envoyé.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        var owners = inv.Chars.Values.OrderByDescending(c => c.At).ToList();
        var who = owners.FirstOrDefault(c => c.Character == s.InventoryChar) ?? inv.For(r.Character.Name) ?? owners[0];
        var data = market.Data;
        var named = data?.Items.Where(i => long.TryParse(i.Key, out _)).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var shown = data is null ? null : MarketShown(data);
        var latest = data is null || shown is null ? [] : data.Latest(shown);

        // Les piles du perso, sans les pièces portées ni les Kinah, réunies par objet.
        var stacks = who.Items.Where(i => i.Qty > 0 && inv.ContainerName(i.Cont) != "porté").ToList();
        var kinah = stacks.Where(i => i.Item == KinahItem).Sum(i => i.Qty);
        var entries = stacks.Where(i => i.Item != KinahItem).GroupBy(i => i.Item).Select(g =>
        {
            var qty = g.Sum(i => i.Qty);
            var where = g.Select(i => (inv.ContainerName(i.Cont), i.Slot, i.Qty)).OrderBy(x => x.Item1 == "sac" ? 0 : x.Item1 == "coffre" ? 1 : 2).ThenBy(x => x.Slot).ToList();
            var price = latest.TryGetValue(g.Key.ToString(), out var p) ? p.Price : (long?)null;
            if (GatherCatalog.IsResource(g.Key))
            {
                var res = GatherCatalog.Item(g.Key, qty, days);
                return new InventoryEntry(g.Key, res.Name, new MarketItem(g.Key.ToString(), res.Name, res.Rank, false, "Récolte"), "Récolte", qty, where, price);
            }
            if (named.TryGetValue(g.Key.ToString(), out var item))
                return new InventoryEntry(g.Key, item.Name, item, item.IsGathering ? "Récolte" : item.Category.Contains(" · ") ? "Équipement" : "Autres objets", qty, where, price);
            return new InventoryEntry(g.Key, $"n° {g.Key}", null, "Sans nom", qty, where, price);
        }).ToList();
        // Filtre de rangement : sac, coffre, ou « autres » (arcanes rangées, rangements).
        var place = s.InventoryPlace;
        static string PlaceOf((string Place, int Slot, long Qty) w) => w.Place is "sac" or "coffre" ? w.Place : "autres";
        var visible = place is null ? entries : entries.Where(e => e.Where.Any(w => PlaceOf(w) == place)).ToList();

        var blocks = new List<Control?>();
        if (!market.Linked)
            blocks.Add(T("Sans ta clé Aion 2 Todo (écran Familiers), seuls les noms des ressources de récolte sont connus, et aucun prix.", 12, Tok.Warn, wrap: true));
        else if (data is null)
            blocks.Add(T(market.Status ?? "Lecture des noms et des prix sur Aion 2 Todo…", 12, Tok.Muted, wrap: true));

        // Chiffres du haut : chacun dit ce qu'il compte.
        var priced = entries.Where(e => e.Value is not null).ToList();
        var unnamed = entries.Count(e => e.Family == "Sans nom");
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Objets différents", entries.Count.ToString(), size: 20,
            note: $"{Fmt.Big(entries.Sum(e => e.Qty))} en tout, dans ton sac ({entries.Count(e => e.Where.Any(w => w.Place == "sac"))}) et ton coffre ({entries.Count(e => e.Where.Any(w => w.Place == "coffre"))})").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Valeur connue", priced.Count == 0 ? "—" : $"≈ {Fmt.Big(priced.Sum(e => e.Value!.Value))} Kinah", priced.Count == 0 ? null : Tok.Good, 20,
            note: shown is null ? "aucun prix sans ta clé Aion 2 Todo"
                : $"au dernier prix noté sur {data!.Label(shown)} ; {Fmt.Plural(entries.Count - priced.Count, "objet")} sans prix, pas compté{(entries.Count - priced.Count > 1 ? "s" : "")}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Sans nom", unnamed.ToString(), unnamed > 0 ? Tok.Warn : null, 20,
            note: unnamed > 0 ? "le jeu ne donne que leur numéro : nomme-les plus bas, le nom vaut pour tous" : "tous tes objets ont un nom").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Kinah", kinah > 0 ? Fmt.Big(kinah) : "—", size: 20, note: "dans ton sac, au relevé du Pet Meter"));
        blocks.Add(kpis);

        // Filtres : texte cherché (sans redessiner l'écran) et rangement.
        var query = new TextBox { Text = s.InventoryQuery ?? "", PlaceholderText = "chercher : od, potion, anneau, n° …", Width = 260 };
        blocks.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                query,
                Choice(new[] { ("tout", place is null, (Action)(() => update(st => st.InventoryPlace = null))) }
                    .Concat(new[] { "sac", "coffre", "autres" }.Where(p => entries.Any(e => e.Where.Any(w => PlaceOf(w) == p)))
                        .Select(p => (p, place == p, (Action)(() => update(st => st.InventoryPlace = p)))))),
            },
        });

        var rows = new List<(Control Row, string Text, Control Card)>();
        const string cols = "*,200,90,110,130";
        foreach (var family in InventoryFamilies.Where(f => f != "Sans nom"))
        {
            var list = visible.Where(e => e.Family == family).OrderByDescending(e => e.Value ?? -1).ThenByDescending(e => e.Qty).ThenBy(e => e.Name, StringComparer.CurrentCulture).ToList();
            if (list.Count == 0) continue;
            var lines = new List<Control>();
            foreach (var e in list)
            {
                var whereText = string.Join(" · ", e.Where.GroupBy(w => w.Place).Select(g => $"{g.Key} ×{Fmt.Big(g.Sum(w => w.Qty))}"));
                var slots = string.Join(" ; ", e.Where.GroupBy(w => w.Place).Select(g => $"{g.Key} : case{(g.Count() > 1 ? "s" : "")} {string.Join(", ", g.Select(w => w.Slot + 1))}"));
                var line = new Border
                {
                    Padding = new Thickness(20, 4),
                    Child = Columns(cols, MarketItemLine(e.Item, e.Name, 13, withKind: family != "Récolte"),
                        Tip(T(whereText, 12, Tok.Text2), slots),
                        T(Fmt.Big(e.Qty), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                        e.Price is { } pr ? T(Fmt.Big(pr), 12, Tok.Text2, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight(),
                        e.Value is { } v ? T(Fmt.Big(v), 13, Tok.Good, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight()),
                };
                lines.Add(line);
                rows.Add((line, $"{e.Name} {e.Item?.Category} {e.Id} {whereText}", null!));
            }
            var famValue = list.Where(e => e.Value is not null).Sum(e => e.Value!.Value);
            var card = TableCard(family, T($"{Fmt.Plural(list.Count, "objet")}" + (famValue > 0 ? $" · ≈ {Fmt.Big(famValue)} Kinah" : ""), 11, Tok.Muted, mono: true),
                TableHeader(cols, "Objet", "Rangement", "Quantité", "Prix unitaire", "Valeur"), lines);
            for (var i = rows.Count - list.Count; i < rows.Count; i++) rows[i] = rows[i] with { Card = card };
            blocks.Add(card);
        }

        // Objets sans nom : le jeu ne donne que leur numéro ; un nom donné ici vaut pour tous les joueurs (liste des prix comprise).
        var nameless = visible.Where(e => e.Family == "Sans nom").OrderByDescending(e => e.Qty).ToList();
        if (nameless.Count > 0)
        {
            const string ncols = "130,200,90,*,90";
            var lines = new List<Control>();
            foreach (var e in nameless)
            {
                var id = e.Id;
                var box = new TextBox { Text = s.InventoryNames.GetValueOrDefault(id, ""), PlaceholderText = "nom du jeu (vois-le dans ton sac)", MinWidth = 0 };
                box.TextChanged += (_, _) => s.InventoryNames[id] = box.Text ?? "";
                async void Send()
                {
                    var name = string.Join(' ', (box.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    if (name.Length < 2) { update(st => st.InventoryNameStatus = "Tape le nom du jeu de cet objet (2 caractères au moins)."); return; }
                    var error = await market.NameItem(id, name);
                    update(st =>
                    {
                        st.InventoryNameStatus = error is null ? $"« {name} » : nommé pour tous les joueurs." : $"{name} : {error}";
                        if (error is null) st.InventoryNames.Remove(id);
                    });
                }
                box.KeyDown += (_, k) => { if (k.Key == Avalonia.Input.Key.Enter) { k.Handled = true; Send(); } };
                var go = Clickable(T("nommer", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), Send, Accent.Main, null, new Thickness(12, 4));
                go.CornerRadius = new CornerRadius(3);
                var whereText = string.Join(" · ", e.Where.Select(w => $"{w.Place}, case {w.Slot + 1}"));
                var line = new Border
                {
                    Padding = new Thickness(20, 3),
                    Child = Columns(ncols, T($"n° {id}", 12, Tok.Text2, mono: true), T(whereText, 12, Tok.Text2), T(Fmt.Big(e.Qty), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                        box.WithMargin(12, 0, 0, 0), go),
                };
                lines.Add(line);
                rows.Add((line, $"{e.Name} {whereText}", null!));
            }
            var card = TableCard("Sans nom : à nommer", T("le nom vaut pour tous les joueurs", 11, Tok.Muted, mono: true),
                TableHeader(ncols, "Numéro", "Rangement", "Quantité", "Nom du jeu", ""), lines);
            for (var i = rows.Count - nameless.Count; i < rows.Count; i++) rows[i] = rows[i] with { Card = card };
            if (s.InventoryNameStatus is { } status)
                blocks.Add(T(status, 12, status.Contains("nommé pour tous") ? Tok.Good : Tok.Warn, wrap: true));
            blocks.Add(T("Ouvre ton sac dans le jeu : la case donne l'objet (comptée de gauche à droite, ligne par ligne). Il faut que ton Pet Meter " +
                         "(v0.24.0 ou plus) ait fait sa synchro du jour avec Aion 2 Todo depuis que l'objet est dans ton sac.", 11, Tok.Muted, wrap: true));
            blocks.Add(card);
        }
        if (visible.Count == 0) blocks.Add(T("Rien dans ce rangement.", 13, Tok.Muted));

        var none = T("Aucun objet ne correspond à cette recherche.", 12, Tok.Muted);
        void Filter()
        {
            var shownRows = 0;
            foreach (var (row, text, _) in rows) { row.IsVisible = Market.Matches(query.Text, text); if (row.IsVisible) shownRows++; }
            foreach (var card in rows.Select(x => x.Card).Distinct()) card.IsVisible = rows.Any(x => x.Card == card && x.Row.IsVisible);
            none.IsVisible = shownRows == 0 && rows.Count > 0;
        }
        query.TextChanged += (_, _) => { s.InventoryQuery = query.Text; Filter(); };
        blocks.Add(none);
        Filter();

        var right = owners.Count > 1
            ? Choice(owners.Select(c => (c.Character, c == who, (Action)(() => update(st => st.InventoryChar = c.Character)))))
            : (Control)T($"relevé {Market.Since(who.At)}", 11, Tok.Muted, mono: true);
        return Page($"Inventaire · {who.Character}", right, blocks.ToArray());
    }
}
