using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Craft : les recettes de ta faction seulement (le jeu a une version élyséenne et une version asmodienne de presque chaque
/// recette, aux ingrédients différents), et la carte « Simuler une série » de la page d'une recette (N fabrications : coût,
/// vente, gain, et le prix plancher sous lequel la vente fait perdre de l'argent, taxe comprise).
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>Race du personnage d'après le site officiel : 1 Élyséens, 2 Asmodiens, 0 inconnue.</summary>
    static int RaceOf(string? race)
    {
        var f = Market.Fold(race);
        return f.StartsWith("elys") ? 1 : f.StartsWith("asmo") ? 2 : 0;
    }

    /// <summary>
    /// Faction d'une recette officielle : le 2e chiffre de son numéro à 9 chiffres (1 Élyséens ou commune, 2 Asmodiens) ; relevé
    /// sur les ingrédients (Poivre de Verteron / d'Altgard, Aria / Azpha, dragon blanc / noir). 0 pour une recette notée par un joueur.
    /// </summary>
    static int RecipeRace(CraftRecipe rc) => rc.Official && rc.Id is >= 100_000_000 and <= 999_999_999 ? rc.Id / 10_000_000 % 10 : 0;

    /// <summary>
    /// Les recettes officielles que ce personnage peut faire. Élyséens : tout sauf les asmodiennes. Asmodiens : les leurs, plus
    /// celles marquées 1 qui n'ont pas de version asmodienne (même numéro au 2e chiffre près, ou même objet fabriqué) : les
    /// recettes communes. Race inconnue : toutes.
    /// </summary>
    static List<CraftRecipe> OfficialForRace(IReadOnlyList<CraftRecipe> official, int race)
    {
        if (race == 1) return official.Where(rc => RecipeRace(rc) != 2).ToList();
        if (race != 2) return official.ToList();
        var ids = official.Select(rc => rc.Id).ToHashSet();
        var theirs = official.Where(rc => RecipeRace(rc) == 2).Select(rc => Market.Fold(rc.Output.Name)).ToHashSet();
        return official.Where(rc => RecipeRace(rc) != 1 || !ids.Contains(rc.Id + 10_000_000) && !theirs.Contains(Market.Fold(rc.Output.Name))).ToList();
    }

    /// <summary>Recettes de l'écran Craft pour le personnage affiché : officielles de sa faction, et celles notées par les joueurs.</summary>
    List<CraftRecipe> RecipesForMe(MarketData data) =>
        MergeRecipes(data.Recipes ?? [], OfficialForRace(Market.OfficialRecipes, RaceOf(r.Character.Race)));

    static long? Num(string? text) =>
        long.TryParse(new string((text ?? "").Where(char.IsDigit).ToArray()), out var v) ? v : null;

    /// <summary>
    /// « Simuler une série » : un nombre de fabrications et un prix de vente à l'unité (le dernier relevé par défaut) donnent le
    /// coût en achetant tout, ce qu'il reste à acheter une fois ton stock déduit, la vente taxe déduite, le gain, et le prix
    /// plancher : le prix affiché à l'hôtel sous lequel la vente ne rembourse plus le coût. Les chiffres se recalculent à la frappe.
    /// </summary>
    Control CraftSimCard(CraftModel m, CraftPlan p)
    {
        var rc = p.Recipe;
        if (s.CraftSim.Recipe != rc.Id) s.CraftSim = (rc.Id, s.CraftSim.Qty, "");  // la quantité reste, le prix repart du dernier relevé
        var parts = p.Parts.Where(x => !CraftSkipped(rc.Id, x.Part))
            .Select(x => (x.Part, x.Have, Unit: m.Book.CostOf(rc, x.Part).Unit)).ToList();
        var missing = parts.Where(x => x.Unit is null).Select(x => x.Part.Name).ToList();
        // Une fabrication, tout acheté : chaque ingrédient à son prix (hôtel, Od, PNJ ou coût de sa recette), plus le coût de fabrication.
        var perCraft = parts.Sum(x => (x.Unit ?? 0) * x.Part.Qty) + p.C.Fee;
        var last = p.C.ForSelf ? null : m.Book.PriceOf(rc.Output)?.Price;
        var keep = rc.Output.Qty * (1 - m.Tax);  // ce qu'une fabrication vendue 1 Kinah l'unité laisse, taxe déduite
        long? floor = p.C.ForSelf || perCraft <= 0 ? null : (long)Math.Ceiling(perCraft / keep);

        var qty = new TextBox { Text = s.CraftSim.Qty, Width = 90, PlaceholderText = "10" };
        var price = new TextBox { Text = s.CraftSim.Price, Width = 130, PlaceholderText = last is { } lp ? Fmt.Big(lp) : "prix à l'unité", IsEnabled = !p.C.ForSelf };

        (StackPanel Panel, TextBlock Value, TextBlock Note) Cell(string label)
        {
            var value = T("—", 18, Tok.Text, FontWeight.SemiBold, mono: true);
            var note = T("", 11, Tok.Muted, wrap: true);
            return (new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 14, 0), Children = { T(label, 10, Tok.Muted, mono: true), value, note } }, value, note);
        }
        var cost = Cell("COÛT, TOUT ACHETÉ");
        var pocket = Cell("À ACHETER, STOCK DÉDUIT");
        var sell = Cell("VENTE, FRAIS DÉDUITS");
        var gain = Cell("GAIN");
        var low = Cell("PRIX PLANCHER, L'UNITÉ");
        var needs = T("", 12, Tok.Text2, wrap: true).WithLineHeight(1.45);

        void Recalc()
        {
            var n = Math.Min(Num(qty.Text) ?? 0, 1_000_000);
            var unit = Num(price.Text) ?? last;
            var atLeast = missing.Count > 0 ? "≥ " : "";
            cost.Value.Text = n > 0 ? atLeast + Fmt.Big(perCraft * n) : "—";
            cost.Note.Text = n > 0 ? $"{Fmt.Big(perCraft)} par fabrication" + (missing.Count > 0 ? $" ; sans prix, comptés 0 : {string.Join(", ", missing)}" : "") : "indique un nombre de fabrications";
            // Ce qu'il reste à acheter : pour chaque ingrédient, le besoin moins ton stock.
            if (!m.HasStock) { pocket.Value.Text = "—"; pocket.Note.Text = "il faut ton stock (Pet Meter)"; }
            else if (n > 0)
            {
                var toBuy = parts.Sum(x => Math.Max(0, (long)x.Part.Qty * n - (x.Have ?? 0)) * (x.Unit ?? 0)) + p.C.Fee * n;
                pocket.Value.Text = atLeast + Fmt.Big(toBuy);
                pocket.Note.Text = toBuy < perCraft * n ? $"ton stock couvre {Fmt.Big(perCraft * n - toBuy)} Kinah d'ingrédients" : "ton stock n'a aucun de ces ingrédients";
            }
            else { pocket.Value.Text = "—"; pocket.Note.Text = ""; }

            if (p.C.ForSelf)
            {
                sell.Value.Text = "ne se vend pas";
                sell.Note.Text = rc.Bound ? "lié d'âme à qui le fabrique" : "objet fabriqué seulement";
                gain.Value.Text = "—"; gain.Note.Text = "";
                low.Value.Text = "—"; low.Note.Text = "";
            }
            else
            {
                long? income = n > 0 && unit is { } u ? (long)Math.Round(u * keep * n) : null;
                sell.Value.Text = income is { } i ? Fmt.Big(i) : unit is null ? "prix ?" : "—";
                sell.Note.Text = unit is { } shown
                    ? $"{Fmt.Big(n * rc.Output.Qty)} × {Fmt.Big(shown)}, frais de vente de {m.TaxNote} déduits" + (Num(price.Text) is null ? $" (dernier prix de {m.Data.Label(m.Shown)})" : "")
                    : $"pas de prix noté sur {m.Data.Label(m.Shown)} : indique ton prix de vente";
                if (income is { } inc)
                {
                    var g = inc - perCraft * n;
                    gain.Value.Text = $"{(g >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(g))}";
                    gain.Value.Foreground = g > 0 ? Tok.Good : g < 0 ? Tok.Bad : Tok.Text;
                    gain.Note.Text = $"{(g >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(g) / n)} par fabrication, en achetant tout";
                }
                else { gain.Value.Text = "—"; gain.Value.Foreground = Tok.Text; gain.Note.Text = ""; }
                low.Value.Text = floor is { } f ? atLeast + Fmt.Big(f) : "—";
                low.Value.Foreground = floor is { } fl && unit is { } up ? up >= fl ? Tok.Good : Tok.Bad : Tok.Text;
                low.Note.Text = floor is { } f2
                    ? unit is { } u2 ? u2 >= f2 ? $"ton prix est {Fmt.Big(u2 - f2)} au-dessus : tu gagnes" : $"ton prix est {Fmt.Big(f2 - u2)} en dessous : tu perds"
                                    : "en dessous, la vente ne rembourse pas le coût"
                    : "";
            }
            needs.Text = n <= 0 ? "" : $"Pour {Fmt.Plural((int)Math.Min(n, int.MaxValue), "fabrication")} : " + string.Join(" · ", parts.Select(x =>
            {
                var need = (long)x.Part.Qty * n;
                var have = m.HasStock ? x.Have ?? 0 : (long?)null;
                return $"{Fmt.Big(need)} {x.Part.Name}" + (x.Part.Npc is not null ? " (PNJ)"
                    : have is { } h ? h >= need ? " (tu les as)" : $" (tu en as {Fmt.Big(h)}, à acheter {Fmt.Big(need - h)})" : "");
            })) + (p.C.Fee > 0 ? $" · {Fmt.Big(p.C.Fee * n)} Kinah de fabrication" : "") + ".";
        }
        qty.TextChanged += (_, _) => { s.CraftSim = (rc.Id, qty.Text ?? "", s.CraftSim.Price); Recalc(); };
        price.TextChanged += (_, _) => { s.CraftSim = (rc.Id, s.CraftSim.Qty, price.Text ?? ""); Recalc(); };
        Recalc();

        var inputs = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                T("Fabrications", 13, weight: FontWeight.Medium).WithMargin(0, 6, 0, 0), qty,
                T("Vendu à l'unité", 13, weight: FontWeight.Medium).WithMargin(14, 6, 0, 0), price,
                T(p.C.ForSelf ? "" : last is { } l ? $"Kinah · vide : le dernier prix de {m.Data.Label(m.Shown)}, {Fmt.Big(l)}" : "Kinah", 11, Tok.Muted).WithMargin(0, 8, 0, 0),
            },
        };
        var cells = new Avalonia.Controls.Primitives.UniformGrid { Columns = 5 };
        foreach (var c in new[] { cost, pocket, sell, gain, low }) cells.Children.Add(c.Panel);
        var body = new StackPanel { Spacing = 12, Children = { inputs, cells, needs } };
        if (!p.C.ForSelf && floor is not null)
            body.Children.Add(T($"Prix plancher : le coût d'une fabrication tout acheté ({Fmt.Big(perCraft)}) divisé par ce que la vente te laisse " +
                                $"({(rc.Output.Qty > 1 ? $"{rc.Output.Qty} objets, " : "")}frais de vente de {m.TaxNote} déduits : {SaleCostDetail(m.Shown)}). Ces frais suivent le prix : " +
                                "la taille du lot n'y change rien, mais chaque remise en vente repaie les frais d'enregistrement.", 11, Tok.Muted, wrap: true).WithLineHeight(1.45));
        return Card(body, "Simuler une série", T("coût, vente et prix plancher pour N fabrications", 11, Tok.Muted, mono: true), new Thickness(16, 12, 16, 12));
    }
}
