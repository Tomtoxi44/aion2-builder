using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Craft · « Que fabriquer avec… » : tu donnes le nom d'une ressource, la page liste les recettes qui l'utilisent, ce qu'il en
/// faut par fabrication, combien tu peux en faire avec ton stock, et ce que chacune rapporte. Sans rien taper : tes ressources
/// qui servent à une recette. Tout vient du même calcul que la vue d'ensemble (mêmes recettes, mêmes prix, même stock).
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>Une ressource : un ingrédient d'au moins une recette, ton stock, et chaque recette qui l'utilise (quantité par fabrication).</summary>
    sealed record CraftResource(RecipePart Part, MarketItem? Item, long? Have, List<(CraftPlan Plan, int Qty)> Uses);

    /// <summary>Tous les ingrédients des recettes montrées, regroupés par nom (sans accents ni majuscules).</summary>
    static Dictionary<string, CraftResource> CraftResources(CraftModel m)
    {
        var found = new Dictionary<string, CraftResource>();
        foreach (var p in m.Plans)
            foreach (var g in p.Parts.GroupBy(x => Market.Fold(x.Part.Name)))
            {
                if (!found.TryGetValue(g.Key, out var res))
                {
                    var first = g.First();
                    found[g.Key] = res = new CraftResource(first.Part, first.Item, first.Have, []);
                }
                res.Uses.Add((p, g.Sum(x => x.Part.Qty)));
            }
        return found;
    }

    void OpenUses(string name) => update(st => { st.Screen = Screen.Craft; st.CraftPage = CraftPage.Uses; st.CraftUse = name; });

    /// <summary>Ce que rapporte (ou coûte) une fabrication, comme dans la liste des recettes.</summary>
    static Control CraftResultCell(CraftPlan p, CraftModel m) => CallOf(p, m).Call switch
    {
        CraftCall.Make => Tip(T($"+{Fmt.Big(p.Gain!.Value)}", 13, p.Locked is null ? Tok.Good : Tok.Text2, FontWeight.SemiBold, mono: true).AlignRight(),
            p.Locked is null ? "Par fabrication : de plus que vendre tes ingrédients tels quels, frais de vente déduits."
                : "Ce que la recette rapporterait par fabrication, une fois ta maîtrise montée."),
        CraftCall.SellInputs => Tip(T($"−{Fmt.Big(-p.Gain!.Value)}", 13, Tok.Bad, FontWeight.SemiBold, mono: true).AlignRight(),
            "Par fabrication : vendre le fabriqué (frais de vente déduits) moins ce que rapporteraient ses ingrédients vendus tels quels."),
        CraftCall.ForSelf => Tip(T((p.Complete ? "coût " : "coût ≥ ") + Short(p.Cost!.Value), 12, Tok.Text2, mono: true).AlignRight(),
            $"Ne se revend pas. Ce qu'il te coûte : {Fmt.Big(p.Cost.Value)} Kinah" + (p.Complete ? "." : " au moins : des ingrédients n'ont pas de prix, comptés 0.")),
        CraftCall.BuyNpc => T($"PNJ {Fmt.Big(NpcPriceOf(p, m)!.Value)}", 12, Tok.Text2, mono: true).AlignRight(),
        _ => Tip(T("?", 12, Tok.Faint, mono: true).AlignRight(), "Un prix manque : note-le dans l'écran HdV."),
    };

    /// <summary>La page « Que fabriquer avec… » : la case de recherche, puis le résultat, redessiné à chaque lettre sans perdre la saisie.</summary>
    Control CraftUsesPage(CraftModel m, Control? right)
    {
        var byName = CraftResources(m);
        var all = byName.Values.ToList();
        var query = new TextBox { Text = s.CraftUse ?? "", PlaceholderText = "nom d'une ressource : minerai, herbe, od…", Width = 320 };
        var clear = Clickable(T("effacer", 12, Accent.Main, mono: true), () => query.Text = "", null, Tok.Hover, new Thickness(8, 4));
        var results = new StackPanel { Spacing = 14 };
        var showAll = false;

        // Les recettes d'abord faisables et rentables, puis faisables, puis le reste, les hors de portée en dernier.
        IEnumerable<(CraftPlan Plan, int Qty)> Sorted(IEnumerable<(CraftPlan Plan, int Qty)> uses) => uses
            .OrderBy(u => u.Plan.Locked is not null ? 3 : CallOf(u.Plan, m).Call == CraftCall.Make ? 0 : u.Plan.CanMake > 0 ? 1 : 2)
            .ThenByDescending(u => u.Plan.Gain ?? long.MinValue).ThenBy(u => u.Plan.Recipe.Output.Name, French);
        string Title(CraftRecipe rc) => rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name;
        Control Verdict(CraftPlan p)
        {
            if (p.Locked is { } locked) return T(locked, 11, Tok.Muted, wrap: true);
            var (_, word, color) = CallOf(p, m);
            return T(word, 12, color, FontWeight.SemiBold, wrap: true);
        }
        void Pick(string name) { showAll = false; query.Text = name; }

        // Une ressource : son nom, ton stock et son prix, puis une ligne par recette qui l'utilise.
        Control Section(CraftResource x, bool further)
        {
            var have = x.Have ?? 0;
            var stock = !m.HasStock ? null : have > 0 ? $"tu en as {Fmt.Big(have)}" + (m.WhereOf(x.Part) is { } on ? $" ({on})" : "") : "tu n'en as pas";
            var price = x.Part.Npc is { } npc ? $"{Fmt.Big(npc)} Kinah pièce chez le PNJ"
                : m.Book.PriceOf(x.Part) is { } pr ? $"{Fmt.Big(pr.Price)} Kinah pièce à l'hôtel" : "pas de prix noté";
            var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
            if (stock is not null) info.Children.Add(T(stock, 12, have > 0 ? Tok.Good : Tok.Muted, FontWeight.SemiBold, mono: true));
            info.Children.Add(T(price, 12, Tok.Muted, mono: true));
            info.Children.Add(T(Fmt.Plural(x.Uses.Count, "recette"), 12, Tok.Muted, mono: true));
            var block = new StackPanel { Spacing = 8, Children = { Columns("*,Auto", MarketItemLine(x.Item, x.Part.Name, 15), info) } };

            const string cols = "*,80,150,170,120";
            var rows = new List<Control>();
            var uses = Sorted(x.Uses).ToList();
            // Plusieurs ressources à l'écran : huit recettes chacune, le reste en ouvrant la ressource seule.
            var cap = !further ? 8 : showAll ? uses.Count : 25;
            foreach (var (p, qty) in uses.Take(cap))
            {
                var rc = p.Recipe;
                var name = new StackPanel { Spacing = 1, Children = { MarketItemLine(p.Item, Title(rc), 13) } };
                var others = p.Parts.Where(o => Market.Fold(o.Part.Name) != Market.Fold(x.Part.Name)).ToList();
                if (others.Count > 0)
                    name.Children.Add(T("avec : " + string.Join(", ", others.Take(4).Select(o => o.Part.Qty > 1 ? $"{o.Part.Name} ×{o.Part.Qty}" : o.Part.Name)) +
                                        (others.Count > 4 ? $" et {others.Count - 4} de plus" : ""), 11, Tok.Muted, wrap: true).WithMargin(11, 0, 0, 0));
                var need = Tip(T($"×{Fmt.Big(qty)}", 13, Tok.Text2, mono: true).AlignRight(),
                    !m.HasStock ? "Par fabrication." : have >= qty ? $"Par fabrication. Ton stock de {x.Part.Name} en couvre {Fmt.Big(have / qty)}."
                        : $"Par fabrication. Il t'en manque {Fmt.Big(qty - have)} pour une seule.");
                Control can;
                if (!m.HasStock || p.Locked is not null || p.CanMake is not { } n) can = T("—", 12, Tok.Faint, mono: true).AlignRight();
                else if (n > 0) can = Tip(T(Fmt.Big(n), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(), $"Avec ton stock, tous les ingrédients comptés. Ensuite il manque : {p.Limit}.");
                else
                    can = new StackPanel
                    {
                        Children =
                        {
                            T("0", 13, Tok.Faint, FontWeight.SemiBold, mono: true).AlignRight(),
                            T(Market.Fold(p.Limit) == Market.Fold(x.Part.Name) ? $"il en faut {Fmt.Big(qty)}" : $"manque : {p.Limit}", 11, Tok.Muted, wrap: true).AlignRight(),
                        },
                    };
                var id = rc.Id;
                rows.Add(TableRow(Columns(cols, name, need, can, Verdict(p), CraftResultCell(p, m)), false, () => OpenRecipe(id)));
            }
            if (uses.Count > cap)
            {
                var resource = x.Part.Name;
                rows.Add(Clickable(T($"montrer les {uses.Count - cap} autres recettes", 12, Accent.Main, mono: true),
                    () => { if (further) { showAll = true; Fill(); } else Pick(resource); }, null, Tok.Hover, new Thickness(20, 8)));
            }
            var table = new StackPanel { Children = { TableHeader(cols, "Objet fabriqué", "Il en faut", "Tu peux en faire", "Verdict", "Par fabrication") } };
            foreach (var row in rows) table.Children.Add(row);
            block.Children.Add(Card(table, null, null, new Thickness(0)));

            // Plus loin : les recettes qui utilisent ce que ces recettes fabriquent (un seul niveau de plus).
            if (further)
            {
                var direct = x.Uses.Select(u => u.Plan.Recipe.Id).ToHashSet();
                var next = new List<(CraftPlan Plan, CraftPlan Via, int Qty)>();
                foreach (var u in uses)
                    if (byName.TryGetValue(Market.Fold(u.Plan.Recipe.Output.Name), out var mid))
                        foreach (var v in mid.Uses)
                            if (direct.Add(v.Plan.Recipe.Id)) next.Add((v.Plan, u.Plan, v.Qty));
                if (next.Count > 0)
                {
                    const string cols2 = "*,260,170,120";
                    var table2 = new StackPanel { Children = { TableHeader(cols2, "Objet fabriqué", "En passant par", "Verdict", "Par fabrication") } };
                    foreach (var (p, via, qty) in next.OrderBy(v => v.Plan.Locked is not null).ThenByDescending(v => v.Plan.Gain ?? long.MinValue).Take(12))
                    {
                        var id = p.Recipe.Id;
                        table2.Children.Add(TableRow(Columns(cols2, MarketItemLine(p.Item, Title(p.Recipe), 13),
                            T($"{via.Recipe.Output.Name} ×{Fmt.Big(qty)}", 12, Tok.Text2, wrap: true), Verdict(p), CraftResultCell(p, m)), false, () => OpenRecipe(id)));
                    }
                    if (next.Count > 12) table2.Children.Add(T($"et {Fmt.Plural(next.Count - 12, "autre recette", "autres recettes")}", 11, Tok.Muted, mono: true).WithMargin(20, 6, 20, 8));
                    block.Children.Add(Card(table2, "Plus loin : avec ce que ces recettes fabriquent",
                        T(Fmt.Plural(next.Count, "recette") + " · la ressource sert à fabriquer un de leurs ingrédients", 11, Tok.Muted, mono: true), new Thickness(0)));
                }
            }
            return block;
        }

        // Sans rien taper : tes ressources qui servent à une recette (sans stock connu : les plus utilisées).
        Control Start()
        {
            var mine = all.Where(x => x.Have > 0).ToList();
            var owned = mine.Count > 0;
            var list = (owned ? mine : all).OrderByDescending(x => x.Uses.Count).ThenBy(x => x.Part.Name, French).ToList();
            const string cols = "*,110,90,300";
            var rows = new List<Control>();
            foreach (var x in list.Take(40))
            {
                var best = x.Uses.Where(u => u.Plan.Locked is null && u.Plan.Gain > 0).OrderByDescending(u => u.Plan.Gain).Select(u => u.Plan).FirstOrDefault();
                var name = x.Part.Name;
                rows.Add(TableRow(Columns(cols, MarketItemLine(x.Item, name, 13),
                    m.HasStock ? T(Fmt.Big(x.Have ?? 0), 13, x.Have > 0 ? Tok.Text : Tok.Faint, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight(),
                    T(x.Uses.Count.ToString(), 13, Tok.Text2, mono: true).AlignRight(),
                    best is null ? T("—", 12, Tok.Faint, mono: true) : T($"{best.Recipe.Output.Name}  +{Short(best.Gain!.Value)}", 12, Tok.Good, wrap: true)), false, () => Pick(name)));
            }
            if (list.Count > 40) rows.Add(T($"et {Fmt.Plural(list.Count - 40, "autre ressource", "autres ressources")} : tape son nom.", 11, Tok.Muted, mono: true).WithMargin(20, 6, 20, 8));
            return TableCard(owned ? $"Tes ressources qui servent à une recette · {list.Count}" : $"Les ressources les plus utilisées · {list.Count}",
                T("clic : les recettes de la ressource", 11, Tok.Muted, mono: true),
                TableHeader(cols, "Ressource", "Ton stock", "Recettes", "Le plus rentable"), rows);
        }

        void Fill()
        {
            results.Children.Clear();
            var text = (query.Text ?? "").Trim();
            clear.IsVisible = text.Length > 0;
            if (all.Count == 0)
            {
                results.Children.Add(Card(T("Aucune recette connue pour l'instant : note la première (barre de gauche → « Noter une recette »).", 13, Tok.Muted, wrap: true)));
                return;
            }
            if (text.Length == 0) { results.Children.Add(Start()); return; }
            var hits = all.Where(x => Market.Matches(text, x.Part.Name))
                .OrderByDescending(x => Market.Fold(x.Part.Name) == Market.Fold(text)).ThenByDescending(x => x.Have > 0).ThenByDescending(x => x.Uses.Count)
                .ThenBy(x => x.Part.Name, French).ToList();
            if (hits.Count == 0)
            {
                var none = new StackPanel { Spacing = 8, Children = { T($"Aucune recette connue n'utilise un ingrédient nommé « {text} ».", 13, Tok.Text2, wrap: true) } };
                // Le nom est peut-être celui d'un objet fabriqué : sa recette.
                var made = m.Plans.Where(p => Market.Matches(text, p.Recipe.Output.Name)).Take(5).ToList();
                if (made.Count > 0)
                {
                    none.Children.Add(T("Mais ça se fabrique :", 12, Tok.Muted));
                    foreach (var p in made)
                    {
                        var id = p.Recipe.Id;
                        var link = Clickable(T($"{p.Recipe.Output.Name} ›", 12, Accent.Main, wrap: true), () => OpenRecipe(id), null, Tok.Hover, new Thickness(4, 2));
                        link.HorizontalAlignment = HorizontalAlignment.Left;
                        none.Children.Add(link);
                    }
                }
                results.Children.Add(Card(none));
                return;
            }
            // Le nom exact d'une ressource : elle seule, en entier ; sinon les trois premières, les autres à préciser.
            var exact = Market.Fold(hits[0].Part.Name) == Market.Fold(text);
            var shown = exact ? 1 : Math.Min(3, hits.Count);
            // Les autres noms d'abord, juste sous la case : préciser sans descendre sous les tableaux.
            if (hits.Count > shown)
            {
                var chips = new WrapPanel();
                foreach (var x in hits.Skip(shown).Take(30))
                {
                    var name = x.Part.Name;
                    var chip = Clickable(T(name, 12, Accent.Main), () => Pick(name), Tok.Panel, Tok.Hover, new Thickness(8, 3));
                    chip.CornerRadius = new CornerRadius(Skin.Radius);
                    chip.BorderBrush = Tok.Border;
                    chip.BorderThickness = new Thickness(1);
                    chips.Children.Add(chip.WithMargin(0, 0, 6, 6));
                }
                results.Children.Add(new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Label(exact ? $"Noms proches · {hits.Count - shown}" : $"Autres ressources de ce nom · {hits.Count - shown}"),
                        chips,
                    },
                });
            }
            foreach (var x in hits.Take(shown)) results.Children.Add(Section(x, shown == 1));
        }

        query.TextChanged += (_, _) => { s.CraftUse = query.Text; showAll = false; Fill(); };
        Fill();
        if (string.IsNullOrWhiteSpace(s.CraftUse)) query.AttachedToVisualTree += (_, _) => query.Focus();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { query, clear } };
        var hint = T("Tape le nom d'une ressource : les recettes qui l'utilisent, ce qu'il en faut par fabrication, combien tu peux en faire avec " +
                     (m.AllStock ? "le stock de tous tes persos" : m.Who is null ? "ton stock (inconnu sur ce PC : il faut le Pet Meter)" : $"le stock de {m.Who.Character}") +
                     ", et ce que chacune rapporte aux prix de " + m.Data.Label(m.Shown) + ". Un clic ouvre la recette.", 12, Tok.Muted, wrap: true).WithLineHeight(1.45);
        return Page("Craft · que fabriquer avec…", right, head, hint, results);
    }
}
