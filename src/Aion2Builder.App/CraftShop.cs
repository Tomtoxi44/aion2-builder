using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Craft → « Courses » : les séries gardées depuis « Simuler une série » (10 Chop steak, 5 Bœuf Wellington…) additionnées en une
/// seule liste d'achats : ce qu'il faut de chaque ingrédient, ton stock retiré une seule fois, l'hôtel et le PNJ séparés, le total
/// à payer, et le tout en texte à copier. Aucune donnée nouvelle : recettes, prix et stock déjà connus.
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>Une ligne des courses : l'ingrédient, combien il en faut, ce que ton stock couvre, son prix à l'unité et d'où il vient.</summary>
    /// <param name="Od">Acheté sous forme d'Od du même rang (moins cher), puis transmuté 1 pour 1 : le nom de cet Od.</param>
    sealed record ShopLine(RecipePart Part, MarketItem? Item, long Need, long Have, long? Unit, PartSource Source, string? Od, List<string> For)
    {
        public long Buy => Math.Max(0, Need - Have);
        public long Total => Buy * (Unit ?? 0);
        /// <summary>Ce qu'on achète vraiment : l'Od à transmuter, sinon l'ingrédient.</summary>
        public string Bought => Od is null ? Part.Name : $"{Od} (transmuté en {Part.Name})";
    }

    /// <summary>Un ingrédient que tu fabriques d'abord, par sa recette : combien il en faut, ce que ton stock en a déjà, combien de fabrications.</summary>
    sealed record ShopStep(CraftRecipe Recipe, long Qty, long FromStock, long Crafts);

    /// <summary>Les courses calculées : les séries gardées, celles dont la recette n'existe plus, les achats, les fabrications d'abord, les Kinah de fabrication.</summary>
    sealed record ShopModel(List<(CraftPlan Plan, int N)> Runs, List<int> Gone, List<ShopLine> Lines, List<ShopStep> Steps, long Fees)
    {
        public long AtMarket => Lines.Where(l => l.Source is PartSource.Market or PartSource.Od).Sum(l => l.Total);
        public long AtNpc => Lines.Where(l => l.Source == PartSource.Npc).Sum(l => l.Total);
        public long Total => AtMarket + AtNpc + Fees;
        public List<ShopLine> NoPrice => Lines.Where(l => l.Unit is null && l.Buy > 0).ToList();
    }

    /// <summary>Garde une série dans les courses (n fabrications ; 0 la retire).</summary>
    void KeepRun(int recipe, long n) => update(st =>
    {
        if (n <= 0) st.CraftRuns.Remove(recipe);
        else st.CraftRuns[recipe] = (int)Math.Min(n, 1_000_000);
    });

    /// <summary>
    /// Une fabrication en achetant tout : chaque ingrédient à son prix (hôtel, Od, PNJ ou coût de sa recette) plus le coût de
    /// fabrication ; Partial : des ingrédients sans prix comptent 0 (le coût est un minimum).
    /// </summary>
    static (long PerCraft, bool Partial) BuyAll(CraftModel m, CraftPlan p)
    {
        var sum = p.C.Fee;
        var partial = false;
        foreach (var part in p.Recipe.Inputs)
        {
            if (m.Book.CostOf(p.Recipe, part).Unit is { } unit) sum += unit * part.Qty;
            else partial = true;
        }
        return (sum, partial);
    }

    /// <summary>
    /// Un craft aux derniers prix, en achetant tout : son coût, le prix de vente à l'unité, le gain par fabrication (frais de vente
    /// déduits) et le prix plancher (le prix à l'unité sous lequel la vente ne rembourse plus le coût).
    /// </summary>
    sealed record CraftNow(long PerCraft, bool Partial, long? Unit, long? Gain, long? Floor);

    static CraftNow NowOf(CraftModel m, CraftPlan p)
    {
        var (perCraft, partial) = BuyAll(m, p);
        if (p.C.ForSelf) return new(perCraft, partial, null, null, null);
        var keep = p.Recipe.Output.Qty * (1 - m.Tax);
        var unit = m.Book.PriceOf(p.Recipe.Output)?.Price;
        return new(perCraft, partial, unit, unit is { } u ? (long)Math.Round(u * keep) - perCraft : null, perCraft > 0 ? (long)Math.Ceiling(perCraft / keep) : null);
    }

    ShopModel? shopModel;

    /// <summary>
    /// Les courses de toutes les séries gardées, calculées une fois par affichage. Un ingrédient chiffré par sa recette (sans prix,
    /// ou « fabriquer plutôt ») est remplacé par les ingrédients de cette recette, 3 niveaux au plus. Ton stock n'est retiré qu'une
    /// fois : d'abord par les ingrédients à fabriquer, puis par les achats.
    /// </summary>
    ShopModel Shopping(CraftModel m)
    {
        if (shopModel is not null) return shopModel;
        var runs = new List<(CraftPlan Plan, int N)>();
        var gone = new List<int>();
        foreach (var (id, n) in s.CraftRuns.OrderBy(x => x.Key))
        {
            if (m.Plans.FirstOrDefault(p => p.Recipe.Id == id) is { } plan) runs.Add((plan, n));
            else gone.Add(id);
        }
        var stock = new Dictionary<string, long>();
        long Take(RecipePart p, long want)
        {
            if (!stock.TryGetValue(p.Key, out var left)) left = m.HaveOf(p) ?? 0;
            var used = Math.Clamp(want, 0, Math.Max(0, left));
            stock[p.Key] = left - used;
            return used;
        }
        var needs = new Dictionary<string, (RecipePart Part, long Need, PartCost Cost, List<string> For)>();
        var steps = new Dictionary<int, ShopStep>();
        long fees = 0;
        void Need(CraftRecipe rc, long n, string made, int depth)
        {
            fees += rc.Kinah * n;
            foreach (var part in rc.Inputs)
            {
                var qty = (long)part.Qty * n;
                var cost = m.Book.CostOf(rc, part);
                if (cost.Source == PartSource.Recipe && cost.Via is { } sub && depth < 3)
                {
                    var have = Take(part, qty);
                    var crafts = (long)Math.Ceiling((qty - have) / (double)sub.Output.Qty);
                    steps[sub.Id] = steps.TryGetValue(sub.Id, out var step)
                        ? step with { Qty = step.Qty + qty, FromStock = step.FromStock + have, Crafts = step.Crafts + crafts }
                        : new ShopStep(sub, qty, have, crafts);
                    if (crafts > 0) Need(sub, crafts, made, depth + 1);
                    continue;
                }
                if (needs.TryGetValue(part.Key, out var line))
                {
                    if (!line.For.Contains(made)) line.For.Add(made);
                    needs[part.Key] = line with { Need = line.Need + qty };
                }
                else needs[part.Key] = (part, qty, cost, [made]);
            }
        }
        foreach (var (plan, n) in runs) Need(plan.Recipe, n, plan.Recipe.Output.Name, 0);
        var lines = needs.Values.Select(x =>
        {
            // Ton stock de l'ingrédient d'abord ; acheté en Od, ton stock de cet Od compte aussi (il se transmute 1 pour 1).
            var have = Take(x.Part, x.Need);
            if (x.Cost.Source == PartSource.Od && m.Book.OdFor(x.Part) is { } od) have += Take(new RecipePart(od.Od.Key, od.Od.Name, 1), x.Need - have);
            return new ShopLine(x.Part, m.Book.ItemOf(x.Part), x.Need, have, x.Cost.Unit, x.Cost.Source, x.Cost.Source == PartSource.Od ? x.Cost.Od : null, x.For);
        }).OrderByDescending(l => l.Total).ThenBy(l => l.Part.Name, French).ToList();
        return shopModel = new ShopModel(runs, gone, lines, steps.Values.OrderBy(x => x.Recipe.Output.Name, French).ToList(), fees);
    }

    /// <summary>Les courses en texte, à coller (Discord, bloc-notes) : les séries, l'hôtel, le PNJ, la fabrication, le total.</summary>
    string ShoppingText(CraftModel m, ShopModel shop)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Courses Aion 2 : " + string.Join(", ", shop.Runs.Select(x => $"{Fmt.Big(x.N)} × {x.Plan.Recipe.Output.Name}")));
        void Section(string title, IEnumerable<ShopLine> lines)
        {
            var list = lines.Where(l => l.Buy > 0).ToList();
            if (list.Count == 0) return;
            text.AppendLine().AppendLine(title);
            foreach (var l in list)
                text.AppendLine($"- {Fmt.Big(l.Buy)} × {l.Bought}" + (l.Unit is { } u ? $" à {Fmt.Big(u)} = {Fmt.Big(l.Total)}" : " (pas de prix noté)"));
        }
        Section($"Hôtel des ventes ({m.Data.Label(m.Shown)}) : {Fmt.Big(shop.AtMarket)} Kinah", shop.Lines.Where(l => l.Source is not PartSource.Npc));
        Section($"PNJ : {Fmt.Big(shop.AtNpc)} Kinah", shop.Lines.Where(l => l.Source == PartSource.Npc));
        if (shop.Steps.Any(x => x.Crafts > 0))
        {
            text.AppendLine().AppendLine("À fabriquer d'abord");
            foreach (var step in shop.Steps.Where(x => x.Crafts > 0))
                text.AppendLine($"- {Fmt.Big(step.Crafts)} × {step.Recipe.Output.Name}" + (step.Recipe.Output.Qty > 1 ? $" (×{step.Recipe.Output.Qty} par fabrication)" : ""));
        }
        var owned = shop.Lines.Where(l => l.Have > 0).ToList();
        if (owned.Count > 0)
            text.AppendLine().AppendLine("Déjà dans ton stock : " + string.Join(", ", owned.Select(l => $"{Fmt.Big(l.Have)} {l.Part.Name}")));
        if (shop.Fees > 0) text.AppendLine().AppendLine($"Kinah de fabrication : {Fmt.Big(shop.Fees)}");
        text.AppendLine().Append($"Total à payer : {Fmt.Big(shop.Total)} Kinah" + (shop.NoPrice.Count > 0 ? " au moins (des ingrédients n'ont pas de prix)" : ""));
        return text.ToString();
    }

    /// <summary>La page « Courses » : les séries (nombre modifiable), les chiffres, les achats à l'hôtel et au PNJ, ce qui se fabrique d'abord.</summary>
    Control CraftShoppingPage(CraftModel m, Control? right)
    {
        var shop = Shopping(m);
        var blocks = new List<Control?>();
        if (shop.Runs.Count == 0 && shop.Gone.Count == 0)
        {
            blocks.Add(Card(T("Aucune série gardée. Ouvre une recette, indique un nombre de fabrications dans « Simuler une série », puis clique « garder dans les " +
                              "courses ». Garde plusieurs séries : cette page additionne leurs ingrédients, retire ton stock, sépare l'hôtel des ventes du PNJ " +
                              "et donne le total à payer.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5), "Tes courses"));
            return Page("Craft · courses", right, blocks.ToArray());
        }
        var label = m.Data.Label(m.Shown);
        var atLeast = shop.NoPrice.Count > 0 ? "≥ " : "";

        // Les chiffres : ce qu'il reste à payer une fois ton stock retiré.
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("À payer en tout", atLeast + Fmt.Big(shop.Total), size: 20,
            note: (m.HasStock ? "ton stock déjà retiré" : "sans ton stock (Pet Meter) : tout est compté acheté") +
                  (shop.NoPrice.Count > 0 ? " ; un minimum : des ingrédients n'ont pas de prix" : "")).WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Hôtel des ventes", atLeast + Fmt.Big(shop.AtMarket), size: 20, note: $"aux derniers prix de {label}").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("PNJ", Fmt.Big(shop.AtNpc), size: 20, note: "objets achetés chez un marchand").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Fabrication", Fmt.Big(shop.Fees), size: 20, note: "Kinah demandés par l'artisanat"));
        blocks.Add(kpis);

        // Copier, vider.
        var copyLabel = T("copier la liste", 12, Accent.Main, FontWeight.SemiBold, mono: true);
        Control? copy = null;
        copy = Tip(Clickable(copyLabel, async () =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is not { } clip) return;
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clip, ShoppingText(m, shop));
            copyLabel.Text = "✓ liste copiée";
        }, null, Tok.Hover, new Thickness(10, 4)), "Copie les courses en texte : à coller dans un bloc-notes ou sur Discord, pour les avoir sous les yeux en jeu.");
        var clear = Tip(Clickable(T("vider les courses", 12, Tok.Muted, mono: true), () => update(st => st.CraftRuns.Clear()), null, Tok.Hover, new Thickness(10, 4)),
            "Retire toutes les séries gardées.");
        blocks.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, clear } });

        // Les séries : le nombre se change ici, la croix retire la série.
        const string runCols = "*,120,150,180,130,40";
        var runRows = new List<Control>();
        foreach (var (plan, n) in shop.Runs)
        {
            var rc = plan.Recipe;
            var id = rc.Id;
            var now = NowOf(m, plan);
            var qty = new TextBox { Text = n.ToString(), Width = 84, HorizontalAlignment = HorizontalAlignment.Right };
            void Commit() { if (Num(qty.Text) is { } v && v != n) KeepRun(id, v); else qty.Text = n.ToString(); }
            qty.LostFocus += (_, _) => Commit();
            qty.KeyDown += (_, k) => { if (k.Key == Avalonia.Input.Key.Enter) { k.Handled = true; Commit(); } };
            var name = Tip(Clickable(MarketItemLine(plan.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13), () => OpenRecipe(id), null, Tok.Hover, new Thickness(4, 2)),
                "Ouvrir la recette : ses ingrédients et le calcul.");
            var income = now.Unit is { } u ? (long)Math.Round(u * rc.Output.Qty * (1 - m.Tax) * n) : (long?)null;
            var gain = now.Gain is { } g ? g * n : (long?)null;
            var drop = Tip(Clickable(T("✕", 11, Tok.Muted), () => KeepRun(id, 0), null, Tok.Hover, new Thickness(8, 2)), "Retirer cette série des courses.");
            runRows.Add(new Border
            {
                Padding = new Thickness(16, 5, 20, 5),
                Child = Columns(runCols, name, qty,
                    T((now.Partial ? "≥ " : "") + Fmt.Big(now.PerCraft * n), 13, Tok.Text, mono: true).AlignRight(),
                    plan.C.ForSelf ? T("ne se vend pas", 11, Tok.Muted).AlignRight() : income is { } inc ? T(Fmt.Big(inc), 13, Tok.Text2, mono: true).AlignRight() : T("prix ?", 12, Tok.Faint, mono: true).AlignRight(),
                    gain is { } total ? T($"{(total >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(total))}", 13, total > 0 ? Tok.Good : total < 0 ? Tok.Bad : Tok.Text, FontWeight.SemiBold, mono: true).AlignRight()
                        : T("—", 12, Tok.Faint, mono: true).AlignRight(),
                    drop),
            });
        }
        foreach (var id in shop.Gone)
            runRows.Add(new Border
            {
                Padding = new Thickness(20, 5),
                Child = Columns("*,Auto", T($"Recette n° {id} : elle n'existe plus (retirée, ou d'une autre faction).", 12, Tok.Muted),
                    Clickable(T("retirer", 12, Accent.Main, mono: true), () => KeepRun(id, 0), null, Tok.Hover, new Thickness(8, 2))),
            });
        var sumGain = shop.Runs.Select(x => NowOf(m, x.Plan).Gain is { } g ? g * x.N : (long?)null).ToList();
        var allGain = sumGain.All(g => g is not null) && sumGain.Count > 0 ? sumGain.Sum(g => g!.Value) : (long?)null;
        blocks.Add(TableCard($"Tes séries · {shop.Runs.Count}",
            T(allGain is { } ag ? $"gain en vendant tout : {(ag >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(ag))} Kinah, en achetant tout" : "change un nombre puis Entrée", 11, Tok.Muted, mono: true),
            TableHeader(runCols, "Objet fabriqué", "Fabrications", "Coût, tout acheté", "Vente, frais déduits", "Gain", ""), runRows));

        // Les achats : l'hôtel puis le PNJ ; une ligne par ingrédient, ce que ton stock couvre déjà retiré.
        var cols = m.AllStock ? "*,90,190,100,110,120" : "*,90,110,100,110,120";
        Control? Purchases(string title, string hint, IEnumerable<ShopLine> source, long sum)
        {
            var list = source.Where(l => l.Buy > 0).ToList();
            if (list.Count == 0) return null;
            var rows = new List<Control>();
            foreach (var l in list)
            {
                var name = new StackPanel { Spacing = 1 };
                name.Children.Add(MarketItemLine(l.Item, l.Part.Name, 13));
                var note = (l.Od is { } od ? $"achète {Fmt.Big(l.Buy)} {od} et transmute-les 1 pour 1 (moins cher que la ressource) · " : "") + "pour " + string.Join(", ", l.For);
                name.Children.Add(T(note, 11, l.Od is null ? Tok.Muted : Tok.Good, wrap: true).WithMargin(11, 0, 0, 0));
                Control have = !m.HasStock ? T("—", 12, Tok.Faint, mono: true).AlignRight()
                    : l.Have == 0 ? T("0", 12, Tok.Faint, mono: true).AlignRight()
                    : m.WhereOf(l.Part) is { } on ? Tip(T(Fmt.Big(l.Have), 13, Tok.Text2, mono: true).AlignRight(), $"Sur tes persos : {on}.")
                    : T(Fmt.Big(l.Have), 13, Tok.Text2, mono: true).AlignRight();
                rows.Add(new Border
                {
                    Padding = new Thickness(20, 5), BorderBrush = Tok.RowLine, BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = Columns(cols, name,
                        T(Fmt.Big(l.Need), 13, Tok.Text2, mono: true).AlignRight(), have,
                        T(Fmt.Big(l.Buy), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                        l.Unit is { } u ? T(Fmt.Big(u), 12, Tok.Text2, mono: true).AlignRight() : T("pas de prix", 11, Tok.Warn).AlignRight(),
                        l.Unit is null ? T("?", 13, Tok.Warn, mono: true).AlignRight() : T(Fmt.Big(l.Total), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight()),
                });
            }
            return TableCard(title, T($"{Fmt.Plural(list.Count, "objet")} · {Fmt.Big(sum)} Kinah · {hint}", 11, Tok.Muted, mono: true),
                TableHeader(cols, "Ingrédient", "Besoin", m.AllStock ? "Stock, tous tes persos" : "Ton stock", "À acheter", "Prix unitaire", "Total"), rows);
        }
        blocks.Add(Purchases("À acheter à l'hôtel des ventes", $"prix de {label}", shop.Lines.Where(l => l.Source is not PartSource.Npc), shop.AtMarket));
        blocks.Add(Purchases("À acheter chez le PNJ", "prix du marchand", shop.Lines.Where(l => l.Source == PartSource.Npc), shop.AtNpc));
        if (shop.Lines.All(l => l.Buy == 0) && shop.Lines.Count > 0)
            blocks.Add(Callout("Ton stock couvre tous les ingrédients : rien à acheter" + (shop.Fees > 0 ? $", il reste {Fmt.Big(shop.Fees)} Kinah de fabrication." : "."), Tok.Good));
        if (shop.NoPrice.Count > 0)
            blocks.Add(Callout($"Sans prix noté sur {label}, comptés 0 dans le total : {string.Join(", ", shop.NoPrice.Select(l => l.Part.Name))}. Note leur prix dans l'écran HdV.", Tok.Warn));

        // Les ingrédients qui se fabriquent : à faire avant la série.
        var steps = shop.Steps.Where(x => x.Crafts > 0).ToList();
        if (steps.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var step in steps)
            {
                var id = step.Recipe.Id;
                var made = step.Crafts * step.Recipe.Output.Qty;
                list.Children.Add(Clickable(Columns("*,Auto",
                        T($"{Fmt.Big(step.Crafts)} × {step.Recipe.Output.Name}" + (step.Recipe.Output.Qty > 1 ? $" (×{step.Recipe.Output.Qty} par fabrication, soit {Fmt.Big(made)})" : ""), 13, Tok.Text),
                        T($"il en faut {Fmt.Big(step.Qty)}" + (step.FromStock > 0 ? $", tu en as {Fmt.Big(step.FromStock)}" : "") + " · ses ingrédients sont dans les achats ›", 11, Tok.Muted)),
                    () => OpenRecipe(id), null, Tok.Hover, new Thickness(6, 3)));
            }
            blocks.Add(Card(list, "À fabriquer d'abord", T("ingrédients chiffrés par leur recette (sans prix, ou « fabriquer plutôt »)", 11, Tok.Muted, mono: true)));
        }

        // Ce que ton stock couvre déjà.
        var owned = shop.Lines.Where(l => l.Have > 0).ToList();
        if (owned.Count > 0)
            blocks.Add(Card(T(string.Join(" · ", owned.Select(l => $"{Fmt.Big(l.Have)} {l.Part.Name}" + (m.WhereOf(l.Part) is { } on ? $" ({on})" : ""))), 12, Tok.Text2, wrap: true).WithLineHeight(1.5),
                "Pris dans ton stock", T(m.AllStock ? "tous tes persos : regroupe-les sur celui qui fabrique" : $"stock de {m.Who?.Character}", 11, Tok.Muted, mono: true)));
        blocks.Add(T("Comment c'est compté : chaque série demande ses ingrédients × le nombre de fabrications ; un même ingrédient demandé par plusieurs séries est additionné, " +
                     "et ton stock n'en est retiré qu'une fois. Un ingrédient coché « déjà à toi » dans sa recette n'y est pas. Les prix sont les derniers notés : " +
                     "acheter une grosse quantité peut les faire monter.", 11, Tok.Muted, wrap: true).WithLineHeight(1.45));
        return Page("Craft · courses", right, blocks.ToArray());
    }

    /// <summary>Sous « Simuler une série » : garder la série dans les courses (ou la mettre à jour, ou la retirer).</summary>
    Control CraftRunButtons(int recipe, Func<long> quantity)
    {
        var kept = s.CraftRuns.TryGetValue(recipe, out var n) ? n : (int?)null;
        Control Button(string text, IBrush color, Action act, string tip)
        {
            var b = Clickable(T(text, 12, color, FontWeight.SemiBold, mono: true), act, null, Tok.Hover, new Thickness(12, 4));
            b.CornerRadius = new CornerRadius(3);
            b.BorderBrush = color;
            b.BorderThickness = new Thickness(1);
            return Tip(b, tip);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Button(kept is null ? "garder dans les courses" : "mettre à jour les courses", Accent.Main, () => { if (quantity() is > 0 and var q) KeepRun(recipe, q); },
            "Garde cette série (ce nombre de fabrications) dans la page « Courses » : elle additionne les ingrédients de toutes tes séries, retire ton stock et donne le total à payer."));
        if (kept is { } k)
        {
            row.Children.Add(T($"dans les courses : {Fmt.Plural(k, "fabrication")}", 11, Tok.Good, mono: true).WithMargin(4, 0, 0, 0));
            row.Children.Add(Button("voir les courses", Tok.Text2, () => update(st => st.CraftPage = CraftPage.Shopping), "Ouvrir la page « Courses »."));
            row.Children.Add(Button("retirer", Tok.Muted, () => KeepRun(recipe, 0), "Retirer cette série des courses."));
        }
        return row;
    }
}
