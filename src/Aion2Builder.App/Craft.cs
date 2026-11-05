using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Un objet d'une recette : sa clé de la liste des prix (numéro du jeu, ou « name:… »), son nom, sa quantité.</summary>
sealed record RecipePart(string Key, string Name, int Qty);

/// <summary>Une recette notée sur Aion 2 Todo : objet fabriqué (Qty par fabrication), ingrédients, coût en Kinah, note, auteur.</summary>
sealed record CraftRecipe(int Id, RecipePart Output, List<RecipePart> Inputs, long Kinah, string? Note, DateTime At, string? By, bool Mine);

/// <summary>Recette envoyée à Aion 2 Todo : chaque objet par sa clé de la liste, sinon par son nom.</summary>
sealed record RecipePartSend(string? Key, string? Name, int Qty);
sealed record RecipeSend(RecipePartSend Output, List<RecipePartSend> Inputs, long Kinah, string? Note);

/// <summary>Le formulaire « Noter une recette », gardé tel quel d'un affichage à l'autre.</summary>
sealed class CraftDraft
{
    public string Output { get; set; } = "";
    public string OutputQty { get; set; } = "1";
    public List<CraftDraftLine> Inputs { get; } = [new(), new()];
    public string Kinah { get; set; } = "";
    public string Note { get; set; } = "";
}

sealed class CraftDraftLine
{
    public string Name { get; set; } = "";
    public string Qty { get; set; } = "1";
}

/// <summary>
/// Une recette jugée : prix (dernier noté sur le marché montré) de ce qu'elle fabrique et de ses ingrédients, pour UNE
/// fabrication ; Gain : ce que fabriquer rapporte de plus que vendre les ingrédients tels quels, taxe du Marché déduite des
/// deux côtés et coût en Kinah compris (négatif : mieux vaut vendre les ingrédients) ; CanMake : fabrications possibles avec
/// ton stock (null sans inventaire) ; Limit : l'ingrédient (ou les Kinah) qui manque le premier.
/// </summary>
sealed record CraftPlan(CraftRecipe Recipe, MarketItem? Item, long? OutValue, long? InValue, List<string> NoPrice, int? CanMake, string? Limit,
    List<(RecipePart Part, MarketItem? Item, long? Have)> Parts)
{
    public long? Gain => OutValue is { } o && InValue is { } i ? (long)Math.Round((o - i) * (1 - ScreenViews.MarketTax)) - Recipe.Kinah : null;
    public long? Total => Gain is { } g && CanMake is { } n ? g * n : null;
}

sealed partial class ScreenViews
{
    /// <summary>Taxe de l'hôtel des ventes du jeu sur une vente (« Taxe actuelle : 10 % » dans la fenêtre Marché).</summary>
    public const double MarketTax = 0.10;

    /// <summary>
    /// Écran Craft : les recettes notées par les joueurs d'Aion 2 Todo, croisées avec ton stock (sac et coffre lus par le Pet
    /// Meter) et les derniers prix de la communauté : combien tu peux en fabriquer, et s'il vaut mieux fabriquer ou vendre les
    /// ingrédients. Aucune source ne donne les recettes : un joueur les note une fois, pour tous (formulaire en bas).
    /// </summary>
    Control CraftScreen(IReadOnlyList<GatherDay> days)
    {
        var data = market.Data;
        if (!market.Linked)
            return Page("Craft", null, Card(T("Les recettes et les prix viennent d'Aion 2 Todo : colle ta clé de synchro dans l'écran Familiers.", 13, Tok.Muted, wrap: true)));
        if (data is null)
            return Page("Craft", null, Card(T(market.Status ?? "Lecture des recettes et des prix sur Aion 2 Todo…", 13, Tok.Muted, wrap: true)));
        var shown = MarketShown(data);
        var latest = data.Latest(shown);
        var recipes = data.Recipes ?? [];

        // Ton stock : le perso choisi (comme l'écran Inventaire), sac + coffre + autres rangements, sans les pièces portées.
        var inv = PetMeterInventory.Load();
        var owners = inv?.Chars.Values.OrderByDescending(c => c.At).ToList() ?? [];
        var who = inv is null || owners.Count == 0 ? null : owners.FirstOrDefault(c => c.Character == s.InventoryChar) ?? inv.For(r.Character.Name) ?? owners[0];
        long kinahHave = 0;
        var entries = inv is null || who is null ? null : InventoryEntries(inv, who, days, latest, out kinahHave);
        var byKey = entries?.GroupBy(e => e.Id.ToString()).ToDictionary(g => g.Key, g => g.Sum(e => e.Qty)) ?? [];
        var byName = entries?.Where(e => e.Family != "Sans nom").GroupBy(e => Market.Fold(e.Name)).ToDictionary(g => g.Key, g => g.Sum(e => e.Qty)) ?? [];

        MarketItem? ItemOf(RecipePart p) => data.Items.FirstOrDefault(i => i.Key == p.Key) ?? data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(p.Name));
        long? PriceOf(RecipePart p) => latest.TryGetValue(p.Key, out var x) ? x.Price : ItemOf(p) is { } i && latest.TryGetValue(i.Key, out var y) ? y.Price : null;
        long? HaveOf(RecipePart p) => entries is null ? null : byKey.TryGetValue(p.Key, out var q) ? q : byName.GetValueOrDefault(Market.Fold(p.Name));

        var plans = recipes.Select(rc =>
        {
            var parts = rc.Inputs.Select(p => (Part: p, Item: ItemOf(p), Have: HaveOf(p))).ToList();
            var noPrice = new List<string>();
            long? outValue = PriceOf(rc.Output) is { } op ? op * rc.Output.Qty : null;
            if (outValue is null) noPrice.Add(rc.Output.Name);
            long inSum = 0;
            var inMissing = false;
            foreach (var p in rc.Inputs)
                if (PriceOf(p) is { } ip) inSum += ip * p.Qty;
                else { noPrice.Add(p.Name); inMissing = true; }
            var inValue = inMissing ? (long?)null : inSum;
            int? canMake = null;
            string? limit = null;
            if (entries is not null)
            {
                var counts = parts.Select(x => (Name: x.Part.Name, N: (int)Math.Min(int.MaxValue, (x.Have ?? 0) / x.Part.Qty))).ToList();
                if (rc.Kinah > 0) counts.Add(("Kinah", (int)Math.Min(int.MaxValue, kinahHave / rc.Kinah)));
                var min = counts.MinBy(c => c.N);
                canMake = min.N;
                limit = min.Name;
            }
            return new CraftPlan(rc, ItemOf(rc.Output), outValue, inValue, noPrice, canMake, limit, parts);
        }).ToList();

        var blocks = new List<Control?>();
        if (entries is null)
            blocks.Add(T("Ton stock vient du Pet Meter de ce PC (inventory.json, v0.21.0 ou plus, lancé avant d'entrer en jeu). Sans lui, les recettes et leurs " +
                         "gains restent visibles, sans « tu peux en faire ».", 12, Tok.Warn, wrap: true));

        // Les chiffres du haut.
        var makeable = plans.Where(p => p.CanMake > 0).ToList();
        var best = plans.Where(p => p.Gain > 0).MaxBy(p => p.Total ?? 0);
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Recettes notées", recipes.Count.ToString(), size: 20, note: "par les joueurs d'Aion 2 Todo, valables pour tous").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Faisables avec ton stock", entries is null ? "—" : makeable.Count.ToString(), makeable.Count > 0 ? Tok.Good : null, 20,
            note: who is null ? "inventaire inconnu sur ce PC" : $"sac et coffre de {who.Character}, relevé {Market.Since(who.At)}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Le plus rentable à fabriquer", best is null ? "—" : best.Recipe.Output.Name, best is null ? null : Tok.Good, 16,
            note: best is null ? "aucune recette ne rapporte plus que vendre ses ingrédients (avec les prix connus)"
                : best.Total is > 0 ? $"+{Fmt.Big(best.Total.Value)} Kinah avec ton stock ({Fmt.Plural(best.CanMake!.Value, "fabrication")})"
                : $"+{Fmt.Big(best.Gain!.Value)} Kinah par fabrication").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Taxe du Marché", $"{MarketTax:P0}", size: 20, note: $"déduite des deux côtés : vendre le fabriqué ou les ingrédients ; prix de {data.Label(shown)}"));
        blocks.Add(kpis);

        if (s.CraftUndo is { } undo)
        {
            var back = Clickable(T("rétablir", 12, Accent.Main, FontWeight.SemiBold, mono: true), () => { market.RestoreRecipe(undo.Id); update(st => st.CraftUndo = null); }, null, Tok.Hover, new Thickness(6, 2));
            blocks.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T($"Recette retirée : {undo.Name}.", 12, Tok.Muted).WithMargin(0, 2, 0, 0), back } });
        }

        // Filtres : texte cherché (sans redessiner), toutes / faisables / rentables.
        var query = new TextBox { Text = s.CraftQuery ?? "", PlaceholderText = "chercher : potion, od, parchemin…", Width = 260 };
        var filter = s.CraftFilter;
        blocks.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                query,
                Choice(new (string, string?)[] { ("toutes", null), ("faisables", "make"), ("rentables", "gain") }
                    .Select(x => (x.Item1, filter == x.Item2, (Action)(() => update(st => st.CraftFilter = x.Item2))))),
            },
        });

        var visible = plans.Where(p => filter switch { "make" => p.CanMake > 0, "gain" => p.Gain > 0, _ => true })
            .OrderByDescending(p => p.Gain is null ? 0 : 1).ThenByDescending(p => p.Total ?? long.MinValue).ThenByDescending(p => p.Gain ?? long.MinValue)
            .ThenBy(p => p.Recipe.Output.Name, French).ToList();
        var rows = new List<(Control Row, string Text)>();
        const string cols = "*,125,110,110,190,30";
        var lines = new List<Control>();
        foreach (var p in visible)
        {
            var rc = p.Recipe;
            var head = MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13, withKind: true);
            // Les ingrédients : quantité par fabrication, ton stock entre parenthèses (en orange s'il n'y en a pas assez).
            var parts = new WrapPanel();
            foreach (var x in p.Parts)
            {
                var enough = x.Have is not { } h || h >= x.Part.Qty;
                parts.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0),
                    Children =
                    {
                        T($"{x.Part.Qty} ", 11, Tok.Text2, mono: true),
                        T(x.Part.Name, 11, x.Item?.GameGrade is { } g ? MarketBrush(g) : Tok.Text2),
                        x.Have is { } have ? T($" ({Fmt.Big(have)})", 11, enough ? Tok.Muted : Tok.Warn, mono: true) : new Panel(),
                    },
                });
            }
            if (rc.Kinah > 0) parts.Children.Add(T($"+ {Fmt.Big(rc.Kinah)} Kinah", 11, Tok.Text2, mono: true));
            var by = T($"notée par {(rc.Mine ? "toi" : rc.By ?? "un compte supprimé")} {Market.Since(rc.At)}{(rc.Note is { } n ? $" · {n}" : "")}", 10, Tok.Faint, mono: true);
            var left = new StackPanel { Spacing = 2, Children = { head, parts, by } };

            Control make = p.CanMake is { } cm
                ? Tip(T(cm.ToString(), 14, cm > 0 ? Tok.Text : Tok.Faint, FontWeight.SemiBold, mono: true).AlignRight(),
                    cm > 0 ? $"Avec ton stock : {Fmt.Plural(cm, "fabrication")} ; ensuite il manque : {p.Limit}." : $"Il te manque : {p.Limit}.")
                : T("—", 12, Tok.Faint, mono: true).AlignRight();
            Control Value(long? v) => v is { } x ? T(Fmt.Big(x), 12, Tok.Text2, mono: true).AlignRight() : T("prix ?", 11, Tok.Faint, mono: true).AlignRight();

            Control verdict;
            if (p.Gain is { } gain)
            {
                var detail = $"Pour une fabrication, taxe du Marché ({MarketTax:P0}) déduite : vendre {rc.Output.Name}{(rc.Output.Qty > 1 ? $" ×{rc.Output.Qty}" : "")} rapporte " +
                             $"{Fmt.Big((long)(p.OutValue!.Value * (1 - MarketTax)))} Kinah, vendre les ingrédients {Fmt.Big((long)(p.InValue!.Value * (1 - MarketTax)))} Kinah" +
                             (rc.Kinah > 0 ? $", et la fabrication coûte {Fmt.Big(rc.Kinah)} Kinah." : ".") +
                             (p.CanMake is > 0 ? $" Avec ton stock ({Fmt.Plural(p.CanMake.Value, "fabrication")}) : {(gain >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(gain) * p.CanMake.Value)} Kinah." : "") +
                             $" Prix : derniers notés sur {data.Label(shown)}.";
                verdict = Tip(new StackPanel
                {
                    Children =
                    {
                        T(gain > 0 ? "fabrique" : "vends les ingrédients", 12, gain > 0 ? Tok.Good : Tok.Warn, FontWeight.SemiBold),
                        T($"+{Fmt.Big(Math.Abs(gain))} par fabrication" + (p.Total is { } t && p.CanMake > 0 ? $" · {Fmt.Big(Math.Abs(t))} en tout" : ""), 11, Tok.Muted, mono: true),
                    },
                }, detail);
            }
            else verdict = Tip(T($"prix manquant : {string.Join(", ", p.NoPrice.Distinct().Take(3))}", 11, Tok.Faint, wrap: true),
                $"Note le prix de ces objets (écran HdV, sur {data.Label(shown)}) pour savoir s'il vaut mieux fabriquer ou vendre.");

            var recipe = rc;
            var remove = Tip(Clickable(T("✕", 12, Tok.Muted), () => { market.RemoveRecipe(recipe); update(st => st.CraftUndo = (recipe.Id, recipe.Output.Name)); }, null, Tok.Hover, new Thickness(8, 3)),
                "Retirer cette recette (fausse ou en double) : pour tous les joueurs, rétablissable 30 jours.");
            var line = new Border
            {
                Padding = new Thickness(20, 6),
                Child = Columns(cols, left, make, Value(p.InValue), Value(p.OutValue), verdict, remove),
            };
            lines.Add(line);
            rows.Add((line, $"{rc.Output.Name} {p.Item?.Category} {string.Join(' ', rc.Inputs.Select(i => i.Name))} {rc.Note}"));
        }
        var card = TableCard("Recettes", T(filter is null ? Fmt.Plural(visible.Count, "recette") : $"{visible.Count} sur {recipes.Count}", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Fabrique (ingrédients par fabrication, ton stock)", "Tu peux en faire", "Ingrédients", "Fabriqué", "Mieux vaut", ""), lines);
        if (recipes.Count == 0)
            blocks.Add(Card(T("Aucune recette notée pour l'instant. Note la première plus bas : ouvre la fenêtre d'artisanat du jeu et recopie ce qu'elle fabrique " +
                              "et ses ingrédients. Elle servira à tous les joueurs d'Aion 2 Todo.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
        else
        {
            blocks.Add(card);
            var none = T("Aucune recette ne correspond.", 12, Tok.Muted);
            void Filter()
            {
                var count = 0;
                foreach (var (row, text) in rows) { row.IsVisible = Market.Matches(query.Text, text); if (row.IsVisible) count++; }
                none.IsVisible = count == 0;
            }
            query.TextChanged += (_, _) => { s.CraftQuery = query.Text; Filter(); };
            blocks.Add(none);
            Filter();
        }
        blocks.Add(RecipeForm(data));

        var right = owners.Count > 1
            ? Choice(owners.Select(c => (c.Character, c == who, (Action)(() => update(st => st.InventoryChar = c.Character)))))
            : who is null ? null : (Control)T($"stock de {who.Character}", 11, Tok.Muted, mono: true);
        return Page("Craft", right, blocks.ToArray());
    }

    /// <summary>Liste des objets proposés dans une case d'objet (recherche sans accents ni majuscules, aussi par type).</summary>
    AutoCompleteBox ItemPicker(List<MarketItem> items, string text, string placeholder, double width, Action<string> changed)
    {
        var box = new AutoCompleteBox
        {
            ItemsSource = items, FilterMode = AutoCompleteFilterMode.Custom, Text = text, MinimumPrefixLength = 0, MaxDropDownHeight = 320,
            PlaceholderText = placeholder, Width = width,
            ItemFilter = (search, o) => o is MarketItem i && Market.Matches(search, $"{i.Name} {i.Category}"),
            // Une ligne recyclée par Avalonia arrive vide : panneau vide, pas de plantage.
            ItemTemplate = new FuncDataTemplate<MarketItem?>((i, _) => i is null ? new Panel() : MarketItemLine(i, i.Name, 13, withKind: true)),
        };
        box.TextChanged += (_, _) => changed(box.Text ?? "");
        return box;
    }

    /// <summary>Noter une recette : ce qu'elle fabrique (et combien), ses ingrédients et leurs quantités, son coût en Kinah, une note.</summary>
    Control RecipeForm(MarketData data)
    {
        var draft = s.CraftDraft;
        var items = data.Items.OrderByDescending(i => i.Gathered).ThenByDescending(i => i.IsGathering).ThenBy(i => i.Name, French)
            .DistinctBy(i => Market.Fold(i.Name)).ToList();
        TextBox Qty(string text, Action<string> changed)
        {
            var box = new TextBox { Text = text, Width = 64, HorizontalContentAlignment = HorizontalAlignment.Right, PlaceholderText = "qté" };
            box.TextChanged += (_, _) => changed(box.Text ?? "");
            return box;
        }
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                T("Fabrique", 12, Tok.Text2).WithMargin(0, 6, 0, 0),
                ItemPicker(items, draft.Output, "objet fabriqué : potion, parchemin…", 320, t => draft.Output = t),
                T("×", 12, Tok.Muted).WithMargin(0, 6, 0, 0),
                Qty(draft.OutputQty, t => draft.OutputQty = t),
                T("par fabrication", 11, Tok.Muted).WithMargin(0, 7, 0, 0),
            },
        });
        for (var i = 0; i < draft.Inputs.Count; i++)
        {
            var line = draft.Inputs[i];
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    T("Ingrédient", 12, Tok.Text2).WithMargin(0, 6, 0, 0),
                    Qty(line.Qty, t => line.Qty = t),
                    T("×", 12, Tok.Muted).WithMargin(0, 6, 0, 0),
                    ItemPicker(items, line.Name, "ingrédient : Od pur, orichalque…", 320, t => line.Name = t),
                },
            };
            if (draft.Inputs.Count > 1)
                row.Children.Add(Clickable(T("✕", 12, Tok.Muted), () => update(st => st.CraftDraft.Inputs.Remove(line)), null, Tok.Hover, new Thickness(8, 5)));
            form.Children.Add(row);
        }
        var add = Clickable(T("+ ingrédient", 12, Accent.Main, FontWeight.SemiBold, mono: true), () => update(st => { if (st.CraftDraft.Inputs.Count < 10) st.CraftDraft.Inputs.Add(new()); }),
            null, Tok.Hover, new Thickness(6, 3));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(add);

        var kinah = new TextBox { Text = draft.Kinah, Width = 150, PlaceholderText = "coût en Kinah (0)" };
        kinah.TextChanged += (_, _) => draft.Kinah = kinah.Text ?? "";
        var note = new TextBox { Text = draft.Note, Width = 260, PlaceholderText = "note : métier, niveau… (facultatif)" };
        note.TextChanged += (_, _) => draft.Note = note.Text ?? "";
        async void Submit()
        {
            static int Number(string text, int fallback) => int.TryParse(new string(text.Where(char.IsDigit).ToArray()), out var n) ? n : fallback;
            RecipePartSend Part(string name, int qty)
            {
                var clean = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var item = data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(clean));
                return new RecipePartSend(item?.Key, item?.Name ?? clean, qty);
            }
            if (draft.Output.Trim().Length < 2) { update(st => st.CraftError = "Choisis ce que la recette fabrique."); return; }
            var inputs = draft.Inputs.Where(l => l.Name.Trim().Length >= 2).Select(l => Part(l.Name, Number(l.Qty, 1))).ToList();
            if (inputs.Count == 0) { update(st => st.CraftError = "Ajoute au moins un ingrédient."); return; }
            var cost = long.TryParse(new string(draft.Kinah.Where(char.IsDigit).ToArray()), out var k) ? k : 0;
            var output = Part(draft.Output, Number(draft.OutputQty, 1));
            var error = await market.AddRecipe(new RecipeSend(output, inputs, cost, draft.Note.Trim().Length > 0 ? draft.Note.Trim() : null));
            update(st =>
            {
                st.CraftError = error;
                if (error is null) st.CraftDraft = new CraftDraft();
            });
        }
        var save = Clickable(T("noter la recette", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), Submit, Accent.Main, null, new Thickness(14, 5));
        save.CornerRadius = new CornerRadius(3);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { kinah, note, save } });
        if (s.CraftError is { } err) form.Children.Add(T(err, 12, Tok.Bad, wrap: true));
        form.Children.Add(T("Recopie la fenêtre d'artisanat du jeu : les quantités d'UNE fabrication. La recette vaut pour tous les joueurs d'Aion 2 Todo, avec ton pseudo ; " +
                            "n'importe qui peut retirer une recette fausse. Un objet hors liste entre sous le nom que tu tapes.", 11, Tok.Muted, wrap: true));
        return Card(form, "Noter une recette", null, new Thickness(16, 10, 16, 12));
    }
}
