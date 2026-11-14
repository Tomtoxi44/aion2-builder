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

    /// <summary>« Tous mes persos » dans le choix du perso (Inventaire, Craft) : les stocks réunis, avec qui a quoi.</summary>
    const string AllChars = "*";

    /// <summary>Le rangement sans le perso (« Tinifixi : sac » → « sac »), pour les filtres de l'inventaire réuni.</summary>
    static string BarePlace(string place) => place.LastIndexOf(" : ", StringComparison.Ordinal) is >= 0 and var i ? place[(i + 3)..] : place;

    /// <summary>Le perso d'une pile de l'inventaire réuni (« Tinifixi : sac » → « Tinifixi ») ; null pour l'inventaire d'un seul perso.</summary>
    static string? PlaceOwner(string place) => place.LastIndexOf(" : ", StringComparison.Ordinal) is >= 0 and var i ? place[..i] : null;

    /// <summary>Les objets de tous tes persos réunis : une ligne par objet, chaque pile marquée de son perso (« Tinifixi : sac »).</summary>
    List<InventoryEntry> InventoryEntriesAll(InventoryFile inv, IReadOnlyList<CharacterInventory> owners, IReadOnlyList<GatherDay> days, Dictionary<string, MarketPrice> latest, out long kinah)
    {
        kinah = 0;
        var all = new List<InventoryEntry>();
        foreach (var c in owners)
        {
            all.AddRange(InventoryEntries(inv, c, days, latest, out var k)
                .Select(e => e with { Where = e.Where.Select(w => ($"{c.Character} : {w.Place}", w.Slot, w.Qty)).ToList() }));
            kinah += k;
        }
        return all.GroupBy(e => e.Id).Select(g => g.First() with { Qty = g.Sum(e => e.Qty), Where = g.SelectMany(e => e.Where).ToList() }).ToList();
    }

    /// <summary>Le stock de tous les persos lus par le Pet Meter, présenté comme celui d'un seul.</summary>
    static CharacterInventory EveryChar(IReadOnlyList<CharacterInventory> owners) => new("tous tes persos", owners.Min(c => c.At), null, []);

    /// <summary>
    /// Les objets d'un perso (sac, coffre, autres rangements ; sans les pièces portées ni les Kinah, comptés à part), toutes piles
    /// réunies, nommés (ressources de récolte, liste d'Aion 2 Todo) et au dernier prix connu. Partagé par les écrans Inventaire et Craft.
    /// </summary>
    List<InventoryEntry> InventoryEntries(InventoryFile inv, CharacterInventory who, IReadOnlyList<GatherDay> days, Dictionary<string, MarketPrice> latest, out long kinah)
    {
        var named = market.Data?.Items.Where(i => long.TryParse(i.Key, out _)).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var stacks = who.Items.Where(i => i.Qty > 0 && inv.ContainerName(i.Cont) != "porté").ToList();
        kinah = stacks.Where(i => i.Item == KinahItem).Sum(i => i.Qty);
        return stacks.Where(i => i.Item != KinahItem).GroupBy(i => i.Item).Select(g =>
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
    }

    /// <summary>L'inventaire calculé une fois par affichage (barre de gauche, page, inspecteur) : le perso choisi, ses objets nommés et chiffrés.</summary>
    /// <param name="All">Tous tes persos réunis (chaque pile dit sur quel perso elle est).</param>
    sealed record InventoryModel(InventoryFile Inv, List<CharacterInventory> Owners, CharacterInventory Who, List<InventoryEntry> Entries, long Kinah,
        MarketData? Data, string? Shown, bool All = false);

    InventoryModel? inventoryModel;
    bool inventoryModelDone;

    InventoryModel? Inventorying()
    {
        if (inventoryModelDone) return inventoryModel;
        inventoryModelDone = true;
        if (PetMeterInventory.Load() is not { Chars.Count: > 0 } inv) return null;
        var days = PetMeterGathering.Merge(accountGather.Days, PetMeterGathering.Load());
        var owners = inv.Chars.Values.OrderByDescending(c => c.At).ToList();
        var all = s.InventoryChar == AllChars && owners.Count > 1;
        var who = all ? EveryChar(owners) : owners.FirstOrDefault(c => c.Character == s.InventoryChar) ?? inv.For(r.Character.Name) ?? owners[0];
        var data = market.Data;
        var shown = data is null ? null : MarketShown(data);
        var latest = data is null || shown is null ? [] : data.Latest(shown);
        long kinah;
        var entries = all ? InventoryEntriesAll(inv, owners, days, latest, out kinah) : InventoryEntries(inv, who, days, latest, out kinah);
        return inventoryModel = new InventoryModel(inv, owners, who, entries, kinah, data, shown, all);
    }

    static string FamilyIcon(string family) => family switch
    {
        "Récolte" => Icons.Gem,
        "Équipement" => Icons.Shield,
        "Sans nom" => Icons.Card,
        _ => Icons.Box,
    };

    /// <summary>Barre de gauche de l'écran Inventaire : tout, puis chaque famille (nombre d'objets, valeur connue), les objets à nommer.</summary>
    SidePanel? InventorySide()
    {
        if (Inventorying() is not { } m) return null;
        var body = new StackPanel();
        var total = m.Entries.Where(e => e.Value is not null).Sum(e => e.Value!.Value);
        body.Children.Add(SideItem(Icons.Grid, "Tout", $"{m.Entries.Count}", Tok.Text2, total > 0 ? $"≈ {Short(total)} Kinah aux prix connus" : "sac, coffre et rangements",
            s.InventoryFamily is null, () => update(st => { st.InventoryFamily = null; st.InventoryItem = null; })));
        body.Children.Add(SideGroup("Familles"));
        foreach (var family in InventoryFamilies)
        {
            var list = m.Entries.Where(e => e.Family == family).ToList();
            if (list.Count == 0) continue;
            var value = list.Where(e => e.Value is not null).Sum(e => e.Value!.Value);
            var f = family;
            body.Children.Add(SideItem(FamilyIcon(family), family == "Sans nom" ? "À nommer" : family, $"{list.Count}", family == "Sans nom" ? Tok.Warn : Tok.Text2,
                family == "Sans nom" ? "le jeu ne donne que leur numéro ; un nom vaut pour tous" : value > 0 ? $"≈ {Short(value)} Kinah aux prix connus" : "sans prix connu",
                s.InventoryFamily == family, () => update(st => { st.InventoryFamily = f; st.InventoryItem = null; })));
        }
        return new SidePanel(m.All ? "Inventaire · tous" : $"Inventaire · {m.Who.Character}", null, body);
    }

    /// <summary>
    /// Écran Inventaire : tout ton sac et ton coffre tels que le Pet Meter les a lus à ton entrée en jeu (inventory.json, sur ce PC),
    /// par familles (barre de gauche), avec la couleur de rareté du jeu, le rangement, la quantité et la valeur au dernier prix de la
    /// communauté ; l'objet choisi dans l'inspecteur (où il est, son prix, les recettes qui l'utilisent). Un objet sans nom se nomme
    /// ici, pour tous. Les pièces portées restent dans l'écran Équipement.
    /// </summary>
    Control InventoryScreen()
    {
        if (Inventorying() is not { } m)
            return Page("Inventaire", null, Card(T("Ton inventaire vient du Pet Meter de ce PC (v0.21.0 ou plus). Lance-le avant d'entrer en jeu : le jeu " +
                "envoie alors tout ton sac et ton coffre, et le Pet Meter les garde sur ce PC (inventory.json). Lancé après l'entrée en jeu, il ne voit que " +
                "ce qui change ensuite. Rien n'est envoyé.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        var (who, entries, data, shown) = (m.Who, m.Entries, m.Data, m.Shown);
        var family = s.InventoryFamily is { } fam && entries.Any(e => e.Family == fam) ? fam : null;
        // Filtre de rangement : sac, coffre, ou « autres » (arcanes rangées, rangements).
        var place = s.InventoryPlace;
        static string PlaceOf((string Place, int Slot, long Qty) w) => BarePlace(w.Place) is var bare && bare is "sac" or "coffre" ? bare : "autres";
        var visible = entries.Where(e => family is null || e.Family == family).Where(e => place is null || e.Where.Any(w => PlaceOf(w) == place)).ToList();

        var blocks = new List<Control?>();
        if (!market.Linked)
            blocks.Add(T("Sans ta clé Aion 2 Todo (écran Familiers), seuls les noms des ressources de récolte sont connus, et aucun prix.", 12, Tok.Warn, wrap: true));
        else if (data is null)
            blocks.Add(T(market.Status ?? "Lecture des noms et des prix sur Aion 2 Todo…", 12, Tok.Muted, wrap: true));

        // Chiffres du haut : ceux de la famille ouverte (ou de tout l'inventaire).
        var scope = entries.Where(e => family is null || e.Family == family).ToList();
        var priced = scope.Where(e => e.Value is not null).ToList();
        var unnamed = entries.Count(e => e.Family == "Sans nom");
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi(family is null ? "Objets différents" : family == "Sans nom" ? "À nommer" : family, scope.Count.ToString(), size: 20,
            note: $"{Fmt.Big(scope.Sum(e => e.Qty))} en tout : sac ({scope.Count(e => e.Where.Any(w => BarePlace(w.Place) == "sac"))}), coffre ({scope.Count(e => e.Where.Any(w => BarePlace(w.Place) == "coffre"))})").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Valeur connue", priced.Count == 0 ? "—" : $"≈ {Fmt.Big(priced.Sum(e => e.Value!.Value))}", priced.Count == 0 ? null : Tok.Good, 20,
            note: shown is null ? "aucun prix sans ta clé Aion 2 Todo"
                : $"Kinah, au dernier prix noté sur {data!.Label(shown)} ; {Fmt.Plural(scope.Count - priced.Count, "objet")} sans prix, pas compté{(scope.Count - priced.Count > 1 ? "s" : "")}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Kinah", m.Kinah > 0 ? Fmt.Big(m.Kinah) : "—", size: 20, note: m.All ? $"sur tes {m.Owners.Count} persos : " + string.Join(", ", m.Owners.Select(c => c.Character)) : $"dans ton sac, relevé {Market.Since(who.At)}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Sans nom", unnamed.ToString(), unnamed > 0 ? Tok.Warn : null, 20,
            note: unnamed > 0 ? "le jeu ne donne que leur numéro : nomme-les (barre de gauche → « À nommer »)" : "tous tes objets ont un nom"));
        blocks.Add(kpis);

        // Filtres : texte cherché (sans redessiner l'écran) et rangement.
        var query = new TextBox { Text = s.InventoryQuery ?? "", PlaceholderText = "chercher : od, potion, anneau, n° …", Width = 260 };
        blocks.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                query,
                Choice(new[] { ("sac et coffre", place is null, (Action)(() => update(st => st.InventoryPlace = null))) }
                    .Concat(new[] { "sac", "coffre", "autres" }.Where(p => entries.Any(e => e.Where.Any(w => PlaceOf(w) == p)))
                        .Select(p => (p, place == p, (Action)(() => update(st => st.InventoryPlace = p)))))),
            },
        });

        var selected = InventorySelected(m);
        var rows = new List<(Control Row, string Text, Control Card)>();
        // Tous tes persos : la colonne du rangement dit aussi sur quel perso, elle s'élargit.
        var cols = m.All ? "*,290,100,110,130" : "*,190,100,110,130";
        foreach (var fam2 in InventoryFamilies.Where(f => f != "Sans nom"))
        {
            var list = visible.Where(e => e.Family == fam2).OrderByDescending(e => e.Value ?? -1).ThenByDescending(e => e.Qty).ThenBy(e => e.Name, StringComparer.CurrentCulture).ToList();
            if (list.Count == 0) continue;
            var lines = new List<Control>();
            foreach (var e in list)
            {
                // Tous tes persos : « Tinifixi : sac ×6, coffre ×374 · Tarnafix : sac ×3 ».
                var whereText = m.All
                    ? string.Join(" · ", e.Where.GroupBy(w => PlaceOwner(w.Place)).Select(o => $"{o.Key} : " +
                        string.Join(", ", o.GroupBy(w => BarePlace(w.Place)).Select(g => $"{g.Key} ×{Fmt.Big(g.Sum(w => w.Qty))}"))))
                    : string.Join(" · ", e.Where.GroupBy(w => w.Place).Select(g => $"{g.Key} ×{Fmt.Big(g.Sum(w => w.Qty))}"));
                var id = e.Id;
                var line = TableRow(Columns(cols, MarketItemLine(e.Item, e.Name, 13, withKind: fam2 != "Récolte"),
                        T(whereText, 12, Tok.Text2, wrap: m.All),
                        T(Fmt.Big(e.Qty), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                        e.Price is { } pr ? T(Fmt.Big(pr), 12, Tok.Text2, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight(),
                        e.Value is { } v ? T(Fmt.Big(v), 13, Tok.Good, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight()),
                    selected?.Id == id, () => update(st => st.InventoryItem = id));
                lines.Add(line);
                rows.Add((line, $"{e.Name} {e.Item?.Category} {e.Id} {whereText}", null!));
            }
            var famValue = list.Where(e => e.Value is not null).Sum(e => e.Value!.Value);
            var card = TableCard(fam2, T($"{Fmt.Plural(list.Count, "objet")}" + (famValue > 0 ? $" · ≈ {Fmt.Big(famValue)} Kinah" : "") + " · clic : le détail à droite", 11, Tok.Muted, mono: true),
                TableHeader(cols, "Objet", "Rangement", "Quantité", "Prix unitaire", "Valeur"), lines);
            for (var i = rows.Count - list.Count; i < rows.Count; i++) rows[i] = rows[i] with { Card = card };
            blocks.Add(card);
        }

        // Objets sans nom : le jeu ne donne que leur numéro ; un nom donné ici vaut pour tous les joueurs (liste des prix comprise).
        var nameless = visible.Where(e => e.Family == "Sans nom").OrderByDescending(e => e.Qty).ToList();
        if (nameless.Count > 0)
        {
            const string ncols = "130,190,90,*,90";
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
            var card = TableCard("À nommer", T("le nom vaut pour tous les joueurs", 11, Tok.Muted, mono: true),
                TableHeader(ncols, "Numéro", "Rangement", "Quantité", "Nom du jeu", ""), lines);
            for (var i = rows.Count - nameless.Count; i < rows.Count; i++) rows[i] = rows[i] with { Card = card };
            if (s.InventoryNameStatus is { } status)
                blocks.Add(T(status, 12, status.Contains("nommé pour tous") ? Tok.Good : Tok.Warn, wrap: true));
            blocks.Add(card);
            blocks.Add(T("Ouvre ton sac dans le jeu : la case donne l'objet (comptée de gauche à droite, ligne par ligne). Il faut que ton Pet Meter " +
                         "(v0.24.0 ou plus) ait fait sa synchro du jour avec Aion 2 Todo depuis que l'objet est dans ton sac.", 11, Tok.Muted, wrap: true));
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

        var right = m.Owners.Count > 1
            ? Tip(Choice(m.Owners.Select(c => (c.Character, !m.All && c == who, (Action)(() => update(st => { st.InventoryChar = c.Character; st.InventoryItem = null; }))))
                .Append(("tous", m.All, (Action)(() => update(st => { st.InventoryChar = AllChars; st.InventoryItem = null; }))))),
                "Un perso, ou « tous » : les sacs et coffres de tous tes persos réunis, chaque ligne disant sur quel perso est l'objet.")
            : (Control)T($"relevé {Market.Since(who.At)}", 11, Tok.Muted, mono: true);
        return Page($"Inventaire · {who.Character}" + (family is null ? "" : $" · {(family == "Sans nom" ? "à nommer" : family.ToLowerInvariant())}"), right, blocks.ToArray());
    }

    /// <summary>L'objet choisi dans l'inventaire, sinon le plus précieux de la famille ouverte.</summary>
    InventoryEntry? InventorySelected(InventoryModel m)
    {
        var scope = m.Entries.Where(e => s.InventoryFamily is null || e.Family == s.InventoryFamily).ToList();
        return scope.FirstOrDefault(e => e.Id == s.InventoryItem)
               ?? scope.Where(e => e.Family != "Sans nom").OrderByDescending(e => e.Value ?? -1).ThenByDescending(e => e.Qty).FirstOrDefault();
    }

    /// <summary>Inspecteur de l'écran Inventaire : l'objet choisi, où il est rangé (case par case), son prix, sa valeur, les recettes qui l'utilisent.</summary>
    Control? InventoryInspector()
    {
        if (Inventorying() is not { } m) return null;
        var panel = new StackPanel { Children = { SectionHeader("Inspecteur · objet") } };
        if (InventorySelected(m) is not { } e) return panel;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        panel.Children.Add(body);
        body.Children.Add(ItemTitle(e.Item, e.Name));
        var latest = m.Data is not null && m.Shown is not null ? m.Data.Latest(m.Shown).GetValueOrDefault(e.Id.ToString()) : null;
        body.Children.Add(KeyValue(
        [
            ("quantité", Fmt.Big(e.Qty), Tok.Text),
            ("prix unitaire", latest is null ? "pas de prix noté" : $"{Fmt.Big(latest.Price)} · {Market.Since(latest.At)}", latest is null ? Tok.Faint : null),
            ("valeur", e.Value is { } v ? $"≈ {Fmt.Big(v)} Kinah" : "—", e.Value is null ? Tok.Faint : Tok.Good),
            ("marché", m.Shown is null ? "—" : m.Data!.Label(m.Shown), null),
            ("numéro du jeu", e.Id.ToString(), Tok.Muted),
        ]));
        var where = new StackPanel { Spacing = 3, Children = { Label("Où il est") } };
        foreach (var g in e.Where.GroupBy(w => w.Place))
            where.Children.Add(T($"{g.Key} ×{Fmt.Big(g.Sum(w => w.Qty))} : case{(g.Count() > 1 ? "s" : "")} {string.Join(", ", g.Select(w => w.Slot + 1))}", 12, Tok.Text2, wrap: true));
        where.Children.Add(T("Cases comptées de gauche à droite, ligne par ligne, comme dans le jeu.", 11, Tok.Faint, wrap: true));
        body.Children.Add(where);
        if (m.Data is { } data && UsedInRecipes(data, e.Id.ToString(), e.Name) is { } uses) body.Children.Add(uses);
        if (latest is not null)
        {
            var key = e.Id.ToString();
            var link = Clickable(T("voir ses prix dans l'écran HdV", 12, Accent.Main), () => update(st =>
            {
                st.Screen = Screen.Market;
                st.MarketPage = MarketPage.Prices;
                st.MarketItem = key;
            }), null, Tok.Hover, new Thickness(4, 2));
            link.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(link);
        }
        if (e.Family == "Sans nom")
            body.Children.Add(Callout("Le jeu ne donne que son numéro : regarde la case dans ton sac et tape son nom dans le tableau « À nommer ». Le nom vaut pour tous les joueurs.", Tok.Warn));
        return panel;
    }
}
