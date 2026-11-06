using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Un objet d'une recette : sa clé de la liste des prix (numéro du jeu, « name:… », ou « npc:… »), son nom, sa quantité ;
/// Npc = prix à l'unité chez un PNJ (objet acheté, jamais revendu à l'hôtel), sinon null.
/// </summary>
sealed record RecipePart(string Key, string Name, int Qty, long? Npc = null);

/// <summary>Une recette notée sur Aion 2 Todo : objet fabriqué (Qty par fabrication), ingrédients, coût en Kinah, note, auteur.</summary>
/// <param name="Bound">L'objet fabriqué est lié d'âme à qui le fabrique : il ne se revend pas, on ne juge que ce qu'il coûte.</param>
sealed record CraftRecipe(int Id, RecipePart Output, List<RecipePart> Inputs, long Kinah, string? Note, DateTime At, string? By, bool Mine, bool Bound = false);

/// <summary>Recette envoyée à Aion 2 Todo : chaque objet par sa clé de la liste, sinon par son nom ; Npc = prix chez le PNJ.</summary>
sealed record RecipePartSend(string? Key, string? Name, int Qty, long? Npc = null);
sealed record RecipeSend(RecipePartSend Output, List<RecipePartSend> Inputs, long Kinah, string? Note, bool Bound = false);

/// <summary>Le formulaire « Noter une recette », gardé tel quel d'un affichage à l'autre.</summary>
sealed class CraftDraft
{
    public string Output { get; set; } = "";
    public string OutputQty { get; set; } = "1";
    public List<CraftDraftLine> Inputs { get; } = [new(), new()];
    public string Kinah { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>L'objet fabriqué sera lié d'âme (lu sur la capture, ou coché).</summary>
    public bool Bound { get; set; }
}

sealed class CraftDraftLine
{
    public string Name { get; set; } = "";
    public string Qty { get; set; } = "1";
    /// <summary>Acheté chez un PNJ : son prix à l'unité (NpcPrice) remplace le prix de l'hôtel.</summary>
    public bool Npc { get; set; }
    public string NpcPrice { get; set; } = "";
}

/// <summary>
/// Une recette chiffrée pour UNE fabrication, aux derniers prix du marché montré. Sell : vente du fabriqué, taxe déduite ;
/// Inputs : ce que rapporteraient ses ingrédients de l'hôtel vendus tels quels (taxe déduite) ; Buy : les mêmes achetés à
/// l'hôtel ; Npc : achats chez le PNJ ; Fee : coût en Kinah de la fabrication. Chained : ingrédients sans prix chiffrés par leur
/// propre recette ; Oldest : le plus ancien prix utilisé. Uncounted : ingrédients sans prix ni recette notée, comptés 0 (le coût
/// est alors un minimum) ; NoPrice : eux, plus l'objet fabriqué s'il se vend mais n'a pas de prix. ForSelf : l'objet fabriqué ne
/// se vend pas (lié d'âme, ou objet fabriqué seulement : catégorie « Fabrication ») ; Sell est alors null.
/// </summary>
sealed record CraftCosting(long? Sell, long? Inputs, long? Buy, long Npc, long Fee, List<string> NoPrice, List<string> Chained, DateTime? Oldest,
    List<string> Uncounted, bool ForSelf);

/// <summary>
/// Une recette jugée. Gain : fabriquer avec tes ingrédients plutôt que les vendre tels quels (négatif : vends-les) ; Flip :
/// acheter les ingrédients à l'hôtel, fabriquer et vendre ; CanMake : fabrications possibles avec ton stock (null sans
/// inventaire), les objets du PNJ achetés au besoin ; Limit : l'ingrédient (ou les Kinah) qui manque le premier.
/// </summary>
sealed record CraftPlan(CraftRecipe Recipe, MarketItem? Item, CraftCosting C, int? CanMake, string? Limit, List<(RecipePart Part, MarketItem? Item, long? Have)> Parts)
{
    public long? Cost => C.Inputs is { } i ? i + C.Npc + C.Fee : null;
    /// <summary>Tous les ingrédients ont un prix (ou une recette chiffrée) : le coût est exact, pas un minimum.</summary>
    public bool Complete => C.Uncounted.Count == 0;
    public long? Gain => C.Sell is { } s && Complete && Cost is { } c ? s - c : null;
    public long? Flip => C.Sell is { } s && Complete && C.Buy is { } b ? s - b - C.Npc - C.Fee : null;
    public long? Total => Gain is { } g && CanMake is { } n ? g * n : null;
    /// <summary>Kinah dépensés par fabrication : achats au PNJ et coût de fabrication.</summary>
    public long Spend => C.Npc + C.Fee;
}

/// <summary>
/// Chiffrage des recettes : dernier prix du marché (taxe déduite à la vente), prix du PNJ, et, pour un ingrédient sans prix
/// qu'une autre recette fabrique, le coût de cette recette (la moins chère, 3 niveaux au plus, sans boucle).
/// </summary>
sealed class CraftBook(MarketData data, Dictionary<string, MarketPrice> latest, double tax, List<CraftRecipe> recipes)
{
    public double Tax => tax;

    public MarketItem? ItemOf(RecipePart p) =>
        data.Items.FirstOrDefault(i => i.Key == p.Key) ?? data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(p.Name));

    public MarketPrice? PriceOf(RecipePart p) =>
        latest.TryGetValue(p.Key, out var x) ? x : ItemOf(p) is { } i && latest.TryGetValue(i.Key, out var y) ? y : null;

    static bool Same(RecipePart a, RecipePart b) => a.Key == b.Key || Market.Fold(a.Name) == Market.Fold(b.Name);

    public CraftCosting Cost(CraftRecipe rc) => Cost(rc, []);

    CraftCosting Cost(CraftRecipe rc, HashSet<int> path)
    {
        path = [.. path, rc.Id];
        var noPrice = new List<string>();
        var chained = new List<string>();
        DateTime? oldest = null;
        void Seen(DateTime at) { if (oldest is null || at < oldest) oldest = at; }

        long? sell = null;
        // Lié d'âme à la fabrication, ou objet qui ne se fabrique que pour soi (liste « Fabrication ») : il ne se revend pas, même
        // si un objet du même nom a un prix à l'hôtel.
        var forSelf = rc.Bound || ItemOf(rc.Output)?.Category.StartsWith("Fabrication") == true;
        if (forSelf) { }
        else if (PriceOf(rc.Output) is { } op) { sell = (long)Math.Round(op.Price * rc.Output.Qty * (1 - tax)); Seen(op.At); }
        else noPrice.Add(rc.Output.Name);

        long inputs = 0, buy = 0, npc = 0;
        var uncounted = new List<string>();
        foreach (var p in rc.Inputs)
        {
            if (p.Npc is { } unit) { npc += unit * p.Qty; continue; }
            if (PriceOf(p) is { } ip)
            {
                inputs += (long)Math.Round(ip.Price * p.Qty * (1 - tax));
                buy += ip.Price * p.Qty;
                Seen(ip.At);
                continue;
            }
            // Sans prix : chiffré par la recette qui le fabrique le moins cher (ingrédients vendus tels quels, PNJ, fabrication).
            // (la plus complète d'abord : une recette dont tout a un prix passe devant une moins chère qui en oublie)
            var sub = path.Count > 3 ? default : recipes.Where(r => !path.Contains(r.Id) && Same(r.Output, p))
                .Select(r => (R: (CraftRecipe?)r, C: (CraftCosting?)Cost(r, path)))
                .OrderBy(x => x.C!.Uncounted.Count)
                .ThenBy(x => (x.C!.Inputs!.Value + x.C.Npc + x.C.Fee) / (double)x.R!.Output.Qty)
                .FirstOrDefault();
            if (sub.R is not { } sr || sub.C is not { } sc)
            {
                // Ni prix ni recette : compté 0 (souvent un objet qui ne se vend pas, comme la pièce de transfert) ; le coût est un minimum.
                noPrice.Add(p.Name);
                uncounted.Add(p.Name);
                continue;
            }
            var share = (double)p.Qty / sr.Output.Qty;
            inputs += (long)Math.Round((sc.Inputs!.Value + sc.Npc + sc.Fee) * share);
            buy += (long)Math.Round((sc.Buy!.Value + sc.Npc + sc.Fee) * share);
            chained.Add(p.Name);
            foreach (var u in sc.Uncounted) if (!uncounted.Contains(u)) uncounted.Add(u);
            if (sc.Oldest is { } so) Seen(so);
        }
        return new CraftCosting(sell, inputs, buy, npc, rc.Kinah, noPrice, chained, oldest, uncounted, forSelf);
    }

    /// <summary>
    /// Ce que je fabriquerais avec ton stock : à chaque tour, la recette qui rapporte le plus en tout (gain par fabrication ×
    /// fabrications possibles) avec ce qui reste ; ses ingrédients sortent du stock et le fabriqué y entre (il peut servir à la
    /// suivante) ; 6 étapes au plus. Seulement les recettes qui utilisent au moins un ingrédient de l'hôtel : celles faites
    /// d'objets du PNJ seuls vont dans « Acheter pour fabriquer ».
    /// </summary>
    public static List<(CraftPlan Plan, int N, long Total)> Proposal(IEnumerable<CraftPlan> plans, Func<RecipePart, long?> haveOf, long kinah)
    {
        var stock = new Dictionary<string, long>();
        long Have(RecipePart p) => stock.TryGetValue(p.Key, out var q) ? q : stock[p.Key] = haveOf(p) ?? 0;
        var useful = plans.Where(p => p.Gain > 0 && p.Recipe.Inputs.Any(i => i.Npc is null)).ToList();
        var steps = new List<(CraftPlan, int, long)>();
        for (var round = 0; round < 6; round++)
        {
            (CraftPlan Plan, int N, long Total)? best = null;
            foreach (var p in useful)
            {
                var n = p.Recipe.Inputs.Where(i => i.Npc is null).Min(i => Have(i) / i.Qty);
                if (p.Spend > 0) n = Math.Min(n, kinah / p.Spend);
                if (n < 1) continue;
                var total = p.Gain!.Value * n;
                if (best is null || total > best.Value.Total) best = (p, (int)Math.Min(n, int.MaxValue), total);
            }
            if (best is not { } b) break;
            foreach (var i in b.Plan.Recipe.Inputs.Where(i => i.Npc is null)) stock[i.Key] = Have(i) - (long)i.Qty * b.N;
            kinah -= b.Plan.Spend * b.N;
            stock[b.Plan.Recipe.Output.Key] = Have(b.Plan.Recipe.Output) + (long)b.Plan.Recipe.Output.Qty * b.N;
            steps.Add(b);
        }
        return steps;
    }
}

sealed partial class ScreenViews
{
    /// <summary>Taxe de l'hôtel des ventes d'un serveur sur une vente (« Taxe actuelle : 10 % » dans la fenêtre Marché).</summary>
    public const double MarketTax = 0.10;

    /// <summary>
    /// Taxe de l'hôtel inter-serveurs : 20 % d'après les notes de version coréennes du 10/12/2025 (« World Brokerage »),
    /// à vérifier en jeu sur les serveurs européens.
    /// </summary>
    public const double WorldMarketTax = 0.20;

    /// <summary>Taxe du marché montré : inter-serveurs (« *eu ») ou hôtel d'un serveur.</summary>
    public static double TaxOf(string market) => market.StartsWith('*') ? WorldMarketTax : MarketTax;

    /// <summary>
    /// Écran Craft : les recettes notées par les joueurs d'Aion 2 Todo, croisées avec ton stock (sac et coffre lus par le Pet
    /// Meter) et les derniers prix de la communauté. En tête, ce que je fabriquerais avec ton stock (étapes, gain en tout) et
    /// ce qui rapporte même en achetant les ingrédients à l'hôtel ; dessous, chaque recette avec son verdict. Aucune source ne
    /// donne les recettes : un joueur les note une fois, pour tous (formulaire en bas), avec le prix des objets du PNJ.
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
        var tax = TaxOf(shown);
        var npcByName = NpcByName(data);
        // Un ingrédient de la liste « Objets du PNJ » s'achète à son prix du PNJ (le prix à jour de la liste), sauf s'il est moins
        // cher à l'hôtel ; l'objet fabriqué garde toujours son prix de l'hôtel.
        var hdvPrices = new CraftBook(data, latest, tax, []);
        RecipePart Priced(RecipePart p)
        {
            if (!npcByName.TryGetValue(Market.Fold(p.Name), out var n)) return p;
            if (p.Npc is null && hdvPrices.PriceOf(p)?.Price is { } h && h < n.Price) return p;
            return p with { Npc = n.Price };
        }
        var recipes = (data.Recipes ?? []).Select(rc => rc with { Inputs = rc.Inputs.Select(Priced).ToList() }).ToList();
        var taxNote = shown.StartsWith('*') ? $"{tax:P0} (notes coréennes, à vérifier en jeu)" : $"{tax:P0}";
        var book = new CraftBook(data, latest, tax, recipes);

        // Ton stock : le perso choisi (comme l'écran Inventaire), sac + coffre + autres rangements, sans les pièces portées.
        var inv = PetMeterInventory.Load();
        var owners = inv?.Chars.Values.OrderByDescending(c => c.At).ToList() ?? [];
        var who = inv is null || owners.Count == 0 ? null : owners.FirstOrDefault(c => c.Character == s.InventoryChar) ?? inv.For(r.Character.Name) ?? owners[0];
        long kinahHave = 0;
        var entries = inv is null || who is null ? null : InventoryEntries(inv, who, days, latest, out kinahHave);
        var byKey = entries?.GroupBy(e => e.Id.ToString()).ToDictionary(g => g.Key, g => g.Sum(e => e.Qty)) ?? [];
        var byName = entries?.Where(e => e.Family != "Sans nom").GroupBy(e => Market.Fold(e.Name)).ToDictionary(g => g.Key, g => g.Sum(e => e.Qty)) ?? [];
        long? HaveOf(RecipePart p) => entries is null ? null : byKey.TryGetValue(p.Key, out var q) ? q : byName.GetValueOrDefault(Market.Fold(p.Name));

        var plans = recipes.Select(rc =>
        {
            var parts = rc.Inputs.Select(p => (Part: p, Item: book.ItemOf(p), Have: HaveOf(p))).ToList();
            var costing = book.Cost(rc);
            int? canMake = null;
            string? limit = null;
            if (entries is not null)
            {
                // Les objets du PNJ s'achètent au besoin : ils ne limitent que par les Kinah.
                var counts = parts.Where(x => x.Part.Npc is null).Select(x => (Name: x.Part.Name, N: (int)Math.Min(int.MaxValue, (x.Have ?? 0) / x.Part.Qty))).ToList();
                var spend = costing.Npc + costing.Fee;
                if (spend > 0) counts.Add(("Kinah", (int)Math.Min(int.MaxValue, kinahHave / spend)));
                if (counts.Count > 0)
                {
                    var min = counts.MinBy(c => c.N);
                    canMake = min.N;
                    limit = min.Name;
                }
            }
            return new CraftPlan(rc, book.ItemOf(rc.Output), costing, canMake, limit, parts);
        }).ToList();

        var blocks = new List<Control?>();
        if (entries is null)
            blocks.Add(T("Ton stock vient du Pet Meter de ce PC (inventory.json, v0.21.0 ou plus, lancé avant d'entrer en jeu). Sans lui, les recettes et leurs " +
                         "gains restent visibles, sans « tu peux en faire » ni proposition.", 12, Tok.Warn, wrap: true));

        var proposal = entries is null ? [] : CraftBook.Proposal(plans, HaveOf, kinahHave);
        var flips = plans.Where(p => p.Flip > 0).OrderByDescending(p => p.Flip).ToList();

        // Les chiffres du haut.
        var makeable = plans.Where(p => p.CanMake > 0).ToList();
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Recettes notées", recipes.Count.ToString(), size: 20, note: "par les joueurs d'Aion 2 Todo, valables pour tous").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Faisables avec ton stock", entries is null ? "—" : makeable.Count.ToString(), makeable.Count > 0 ? Tok.Good : null, 20,
            note: who is null ? "inventaire inconnu sur ce PC" : $"sac et coffre de {who.Character}, relevé {Market.Since(who.At)}").WithMargin(0, 0, 10, 0));
        var proposalSum = proposal.Sum(x => x.Total);
        kpis.Children.Add(Kpi("Gain en fabriquant", entries is null ? "—" : proposalSum > 0 ? $"+{Fmt.Big(proposalSum)}" : "0", proposalSum > 0 ? Tok.Good : null, 20,
            note: entries is null ? "il faut ton stock (Pet Meter)" : proposalSum > 0 ? "Kinah de plus que vendre tes ingrédients, en suivant la proposition"
                : "rien de rentable avec ton stock aux prix connus").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Taxe du Marché", $"{tax:P0}", size: 20, note: $"déduite à chaque vente (fabriqué ou ingrédients) ; prix de {data.Label(shown)}" +
                                                                         (shown.StartsWith('*') ? " ; inter-serveurs : à vérifier en jeu" : "")));
        blocks.Add(kpis);

        if (proposal.Count > 0) blocks.Add(CraftProposalCard(proposal, kinahHave));
        else if (entries is not null && plans.Any(p => p.Gain is not null))
            blocks.Add(Card(T("Aucune recette ne rapporte plus que vendre tes ingrédients tels quels, avec ton stock et les prix connus. Vends-les " +
                              "(écran HdV → « Vendre ou garder ? ») ou note d'autres recettes et d'autres prix.", 12, Tok.Muted, wrap: true), "Ce que je fabriquerais avec ton stock"));
        if (flips.Count > 0) blocks.Add(CraftFlipCard(flips.Take(5).ToList(), flips.Count, data.Label(shown)));

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

        var visible = plans.Where(p => filter switch { "make" => p.CanMake > 0, "gain" => p.Gain > 0 || p.Flip > 0, _ => true })
            .OrderByDescending(p => p.Gain is null ? 0 : 1).ThenByDescending(p => p.Total ?? long.MinValue).ThenByDescending(p => p.Gain ?? long.MinValue)
            .ThenBy(p => p.Recipe.Output.Name, French).ToList();
        var rows = new List<(Control Row, string Text)>();
        const string cols = "*,110,110,110,200,30";
        var lines = new List<Control>();
        var stale = DateTime.UtcNow.AddDays(-7);
        foreach (var p in visible)
        {
            var rc = p.Recipe;
            Control head = MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13, withKind: true);
            if (long.TryParse(rc.Output.Key, out var gameId))
                head = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children =
                    {
                        head,
                        Tip(Clickable(T("AION 2 Hub ↗", 10, Accent.Main, mono: true), () => OpenLink($"https://aion2hub.com/database/items/{gameId}"), null, Tok.Hover, new Thickness(4, 1)),
                            "Fiche de l'objet sur AION 2 Hub (site de fans, en anglais) : sa recette pour la vérifier. Rien n'est repris automatiquement."),
                    },
                };
            // Les ingrédients : quantité par fabrication, ton stock entre parenthèses (en orange s'il n'y en a pas assez),
            // le prix à l'unité des objets du PNJ.
            var parts = new WrapPanel();
            foreach (var x in p.Parts)
            {
                var enough = x.Have is not { } h || h >= x.Part.Qty || x.Part.Npc is not null;
                parts.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0),
                    Children =
                    {
                        T($"{x.Part.Qty} ", 11, Tok.Text2, mono: true),
                        T(x.Part.Name, 11, x.Item?.GameGrade is { } g ? MarketBrush(g) : Tok.Text2),
                        x.Part.Npc is { } unit ? T($" · PNJ {Fmt.Big(unit)}/u", 11, Tok.Muted, mono: true)
                            : x.Have is { } have ? T($" ({Fmt.Big(have)})", 11, enough ? Tok.Muted : Tok.Warn, mono: true) : new Panel(),
                    },
                });
            }
            if (rc.Kinah > 0) parts.Children.Add(T($"+ {Fmt.Big(rc.Kinah)} Kinah", 11, Tok.Text2, mono: true));
            var by = T($"notée par {(rc.Mine ? "toi" : rc.By ?? "un compte supprimé")} {Market.Since(rc.At)}{(rc.Note is { } n ? $" · {n}" : "")}", 10, Tok.Faint, mono: true);
            var left = new StackPanel { Spacing = 2, Children = { head, parts, by } };

            Control make = p.CanMake is { } cm
                ? Tip(T(cm.ToString(), 14, cm > 0 ? Tok.Text : Tok.Faint, FontWeight.SemiBold, mono: true).AlignRight(),
                    (cm > 0 ? $"Avec ton stock : {Fmt.Plural(cm, "fabrication")} ; ensuite il manque : {p.Limit}." : $"Il te manque : {p.Limit}.") +
                    (p.C.Chained.Count > 0 ? $" {string.Join(", ", p.C.Chained)} : à fabriquer d'abord si tu n'en as pas." : "") +
                    (p.C.Npc > 0 ? " Les objets du PNJ sont comptés comme achetés." : ""))
                : T("—", 12, Tok.Faint, mono: true).AlignRight();
            Control Value(long? v, string tip, bool atLeast = false) => v is { } x
                ? Tip(T((atLeast ? "≥ " : "") + Fmt.Big(x), 12, atLeast ? Tok.Warn : Tok.Text2, mono: true).AlignRight(), tip)
                : T(p.C.ForSelf ? "ne se vend pas" : "prix ?", 11, Tok.Faint, mono: true).AlignRight();
            var uncountedText = p.Complete ? "" : $" Pas compté (ni prix ni recette notée) : {string.Join(", ", p.C.Uncounted)} : le coût est un minimum. " +
                                                  "Note leur prix (écran HdV) ou leur recette pour le coût complet.";
            var costTip = p.Cost is null ? "" : $"Pour une fabrication : ingrédients de l'hôtel vendus tels quels {Fmt.Big(p.C.Inputs!.Value)} Kinah (taxe déduite)" +
                (p.C.Chained.Count > 0 ? $", dont {string.Join(", ", p.C.Chained)} chiffré par sa recette" : "") +
                (p.C.Npc > 0 ? $", achats au PNJ {Fmt.Big(p.C.Npc)}" : "") + (p.C.Fee > 0 ? $", fabrication {Fmt.Big(p.C.Fee)}" : "") + "." + uncountedText;

            Control verdict;
            if (p.Gain is { } gain)
            {
                var detail = $"Pour une fabrication, taxe du Marché ({taxNote}) déduite : vendre {rc.Output.Name}{(rc.Output.Qty > 1 ? $" ×{rc.Output.Qty}" : "")} rapporte " +
                             $"{Fmt.Big(p.C.Sell!.Value)} Kinah ; fabriquer coûte {Fmt.Big(p.Cost!.Value)} Kinah (tes ingrédients vendus tels quels" +
                             (p.C.Npc > 0 ? ", les achats au PNJ" : "") + (p.C.Fee > 0 ? ", le coût de fabrication" : "") + ")." +
                             (p.CanMake is > 0 ? $" Avec ton stock ({Fmt.Plural(p.CanMake.Value, "fabrication")}) : {(gain >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(gain) * p.CanMake.Value)} Kinah." : "") +
                             (p.Flip is { } f ? $" En achetant les ingrédients à l'hôtel : {(f >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(f))} par fabrication." : "") +
                             $" Prix : derniers notés sur {data.Label(shown)}" + (p.C.Oldest is { } o ? $", le plus ancien du {o.ToLocalTime():dd/MM}." : ".");
                var verdictLines = new StackPanel
                {
                    Children =
                    {
                        T(gain > 0 ? "fabrique" : "vends les ingrédients", 12, gain > 0 ? Tok.Good : Tok.Warn, FontWeight.SemiBold),
                        T($"{(gain >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(gain))} par fabrication" + (p.Total is { } t && p.CanMake > 0 ? $" · {Fmt.Big(Math.Abs(t))} en tout" : ""), 11, Tok.Muted, mono: true),
                    },
                };
                if (p.C.Oldest < stale) verdictLines.Children.Add(T($"prix du {p.C.Oldest!.Value.ToLocalTime():dd/MM} : à revoir", 10, Tok.Warn, mono: true));
                verdict = Tip(verdictLines, detail);
            }
            else if (p.C.ForSelf && p.Cost is { } boundCost)
            {
                var forSelf = new StackPanel
                {
                    Children =
                    {
                        T(rc.Bound ? "lié d'âme : pour toi" : "pour toi (ne se vend pas)", 12, Tok.Text2, FontWeight.SemiBold),
                        T($"te coûte {(p.Complete ? "" : "au moins ")}{Fmt.Big(boundCost)}", 11, Tok.Muted, mono: true),
                    },
                };
                if (!p.Complete) forSelf.Children.Add(T($"sans prix : {string.Join(", ", p.C.Uncounted.Take(2))}{(p.C.Uncounted.Count > 2 ? "…" : "")}", 10, Tok.Faint, wrap: true));
                verdict = Tip(forSelf,
                    (rc.Bound ? "L'objet fabriqué sera lié d'âme à ton perso" : "Cet objet ne se fabrique que pour soi") +
                    $" : il ne se revend pas, la question est ce qu'il te coûte. Fabriquer coûte {(p.Complete ? "" : "au moins ")}{Fmt.Big(boundCost)} Kinah : " +
                    $"tes ingrédients de l'hôtel vendus tels quels {Fmt.Big(p.C.Inputs!.Value)}" + (p.C.Npc > 0 ? $", achats au PNJ {Fmt.Big(p.C.Npc)}" : "") +
                    (p.C.Fee > 0 ? $", fabrication {Fmt.Big(p.C.Fee)}" : "") + $". Prix : derniers notés sur {data.Label(shown)}" +
                    (p.C.Oldest is { } bo ? $", le plus ancien du {bo.ToLocalTime():dd/MM}." : ".") + uncountedText);
            }
            else if (p.C.Sell is null && p.Cost is { } cost && npcByName.TryGetValue(Market.Fold(rc.Output.Name), out var sold) && sold.Price * rc.Output.Qty < cost)
                // Pas de prix à l'hôtel, mais le PNJ le vend : moins cher de l'acheter que de le fabriquer.
                verdict = Tip(new StackPanel
                {
                    Children =
                    {
                        T("achète-le au PNJ", 12, Tok.Warn, FontWeight.SemiBold),
                        T($"{Fmt.Big(sold.Price * rc.Output.Qty)} au PNJ, {Fmt.Big(cost)} à fabriquer", 11, Tok.Muted, mono: true),
                    },
                }, $"Le PNJ vend {rc.Output.Name} {Fmt.Big(sold.Price)} Kinah l'unité ; le fabriquer coûte {Fmt.Big(cost)} Kinah (tes ingrédients vendus tels quels, " +
                   "achats au PNJ, fabrication). Sans prix noté à l'hôtel, on ne sait pas s'il s'y vend plus cher.");
            else verdict = Tip(T($"prix manquant : {string.Join(", ", p.C.NoPrice.Distinct().Take(3))}", 11, Tok.Faint, wrap: true),
                $"Note le prix de ces objets (écran HdV, sur {data.Label(shown)}), ou leur recette s'ils se fabriquent, pour savoir s'il vaut mieux fabriquer ou vendre. " +
                "Un objet acheté chez un PNJ se note avec son prix dans la recette. En attendant, la colonne Coût compte ce qui a un prix (« ≥ » : un minimum).");

            var recipe = rc;
            var remove = Tip(Clickable(T("✕", 12, Tok.Muted), () => { market.RemoveRecipe(recipe); update(st => st.CraftUndo = (recipe.Id, recipe.Output.Name)); }, null, Tok.Hover, new Thickness(8, 3)),
                "Retirer cette recette (fausse ou en double) : pour tous les joueurs, rétablissable 30 jours.");
            var line = new Border
            {
                Padding = new Thickness(20, 6),
                Child = Columns(cols, left, make, Value(p.Cost, costTip, !p.Complete), Value(p.C.Sell, $"Vente du fabriqué, taxe de {taxNote} déduite."), verdict, remove),
            };
            lines.Add(line);
            rows.Add((line, $"{rc.Output.Name} {p.Item?.Category} {string.Join(' ', rc.Inputs.Select(i => i.Name))} {rc.Note}"));
        }
        var card = TableCard("Recettes", T(filter is null ? Fmt.Plural(visible.Count, "recette") : $"{visible.Count} sur {recipes.Count}", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Fabrique (ingrédients par fabrication, ton stock)", "Tu peux en faire", "Coût", "Fabriqué", "Mieux vaut", ""), lines);
        if (recipes.Count == 0)
            blocks.Add(Card(T("Aucune recette notée pour l'instant. Note la première plus bas : ouvre la fenêtre d'artisanat du jeu et recopie ce qu'elle fabrique, " +
                              "ses ingrédients et le prix des objets achetés chez un PNJ. Elle servira à tous les joueurs d'Aion 2 Todo.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
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
        blocks.Add(NpcCard(data));

        var right = owners.Count > 1
            ? Choice(owners.Select(c => (c.Character, c == who, (Action)(() => update(st => st.InventoryChar = c.Character)))))
            : who is null ? null : (Control)T($"stock de {who.Character}", 11, Tok.Muted, mono: true);
        return Page("Craft", right, blocks.ToArray());
    }

    /// <summary>« Ce que je fabriquerais avec ton stock » : les étapes de la proposition, dans l'ordre, et le gain en tout.</summary>
    Control CraftProposalCard(List<(CraftPlan Plan, int N, long Total)> steps, long kinah)
    {
        var list = new StackPanel { Spacing = 6 };
        var i = 0;
        foreach (var (plan, n, total) in steps)
        {
            var rc = plan.Recipe;
            var uses = rc.Inputs.Where(x => x.Npc is null).Select(x => $"{Fmt.Big((long)x.Qty * n)} {x.Name}").ToList();
            if (plan.C.Npc > 0) uses.Add($"{Fmt.Big(plan.C.Npc * n)} Kinah au PNJ");
            if (plan.C.Fee > 0) uses.Add($"{Fmt.Big(plan.C.Fee * n)} Kinah de fabrication");
            var made = (long)rc.Output.Qty * n;
            list.Children.Add(Columns("26,*,150",
                T($"{++i}.", 13, Tok.Muted, mono: true),
                new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children = { T($"Fabrique {Fmt.Plural(n, "fois", "fois")} ", 13, Tok.Text), MarketItemLine(plan.Item, made > 1 ? $"{rc.Output.Name} (×{Fmt.Big(made)})" : rc.Output.Name, 13) },
                        },
                        T("utilise " + string.Join(", ", uses), 11, Tok.Muted, wrap: true),
                    },
                },
                Tip(T($"+{Fmt.Big(total)}", 14, Tok.Good, FontWeight.SemiBold, mono: true).AlignRight(),
                    $"+{Fmt.Big(plan.Gain!.Value)} Kinah par fabrication par rapport à vendre ces ingrédients tels quels, taxe déduite.")));
        }
        var sum = steps.Sum(x => x.Total);
        list.Children.Add(new Border
        {
            BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0),
            Child = Columns("26,*,150", new Panel(),
                T("En tout, de plus que vendre ces ingrédients tels quels", 12, Tok.Text2),
                T($"+{Fmt.Big(sum)} Kinah", 14, Tok.Good, FontWeight.SemiBold, mono: true).AlignRight()),
        });
        list.Children.Add(T($"Chaque étape prend la recette qui rapporte le plus en tout avec ce qui reste de ton stock ; un objet fabriqué peut servir à l'étape " +
                            $"suivante. Les objets du PNJ sont achetés au besoin, sur tes {Fmt.Big(kinah)} Kinah. Ce qu'il reste ensuite se vend tel quel.", 11, Tok.Faint, wrap: true));
        return Card(list, "Ce que je fabriquerais avec ton stock", null, new Thickness(16, 10, 16, 12));
    }

    /// <summary>« Acheter pour fabriquer » : les recettes qui rapportent même en achetant tous les ingrédients à l'hôtel.</summary>
    Control CraftFlipCard(List<CraftPlan> flips, int count, string marketLabel)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var p in flips)
        {
            var rc = p.Recipe;
            var spent = p.C.Buy!.Value + p.C.Npc + p.C.Fee;
            var margin = spent > 0 ? (double)p.Flip!.Value / spent : 0;
            var bits = new List<string> { $"ingrédients à l'hôtel {Fmt.Big(p.C.Buy.Value)}" };
            if (p.C.Npc > 0) bits.Add($"PNJ {Fmt.Big(p.C.Npc)}");
            if (p.C.Fee > 0) bits.Add($"fabrication {Fmt.Big(p.C.Fee)}");
            list.Children.Add(Columns("*,150",
                new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13),
                        T($"achète : {string.Join(" + ", bits)} → vends {Fmt.Big(p.C.Sell!.Value)} (taxe déduite)", 11, Tok.Muted, wrap: true),
                    },
                },
                Tip(new StackPanel
                {
                    Children =
                    {
                        T($"+{Fmt.Big(p.Flip.Value)}", 14, Tok.Good, FontWeight.SemiBold, mono: true).AlignRight(),
                        T($"{margin:P0} par fabrication", 11, Tok.Muted, mono: true).AlignRight(),
                    },
                }, $"Par fabrication, sur {marketLabel}. Le prix noté d'un ingrédient est le plus bas vu : regarde combien d'annonces restent à ce prix avant d'en acheter beaucoup." +
                   (p.C.Oldest is { } o ? $" Prix le plus ancien : {o.ToLocalTime():dd/MM}." : ""))));
        }
        if (count > flips.Count) list.Children.Add(T($"et {count - flips.Count} de plus (filtre « rentables »)", 11, Tok.Faint, mono: true));
        return Card(list, "Rentable même en achetant les ingrédients", null, new Thickness(16, 10, 16, 12));
    }

    /// <summary>Liste des objets proposés dans une case d'objet (recherche sans accents ni majuscules, aussi par type).</summary>
    AutoCompleteBox ItemPicker(List<MarketItem> items, string text, string placeholder, double width, Action<string> changed)
    {
        // Filtre et modèle AVANT FilterMode et Text : redessiner le formulaire avec un nom déjà tapé (« + ingrédient », « PNJ »,
        // ✕) lance la recherche dès que Text est posé, et Avalonia plante si le filtre « Custom » n'existe pas encore.
        var box = new AutoCompleteBox
        {
            ItemFilter = (search, o) => o is MarketItem i && Market.Matches(Uncut(search), $"{i.Name} {i.Category}"),
            // Une ligne recyclée par Avalonia arrive vide : panneau vide, pas de plantage.
            ItemTemplate = new FuncDataTemplate<MarketItem?>((i, _) => i is null ? new Panel() : MarketItemLine(i, i.Name, 13, withKind: true)),
            ItemsSource = items, FilterMode = AutoCompleteFilterMode.Custom, MinimumPrefixLength = 0, MaxDropDownHeight = 320,
            PlaceholderText = placeholder, Width = width,
            Text = text,
        };
        box.TextChanged += (_, _) => changed(box.Text ?? "");
        return box;
    }

    /// <summary>
    /// Noter une recette : ce qu'elle fabrique (et combien), ses ingrédients et leurs quantités (un objet acheté chez un PNJ avec
    /// son prix à l'unité), son coût en Kinah, une note.
    /// </summary>
    Control RecipeForm(MarketData data)
    {
        var draft = s.CraftDraft;
        var npcByName = NpcByName(data);
        var items = data.Items.OrderByDescending(i => i.Gathered).ThenByDescending(i => i.IsGathering).ThenBy(i => i.Name, French)
            .Concat((data.Npc ?? []).OrderBy(n => n.Name, French).Select(n => new MarketItem(n.Key, n.Name, 0, false, $"PNJ · {n.Tab ?? "marchand"} · {Fmt.Big(n.Price)} Kinah")))
            .DistinctBy(i => Market.Fold(i.Name)).ToList();
        TextBox Qty(string text, Action<string> changed)
        {
            var box = new TextBox { Text = text, Width = 64, HorizontalContentAlignment = HorizontalAlignment.Right, PlaceholderText = "qté" };
            box.TextChanged += (_, _) => changed(box.Text ?? "");
            return box;
        }
        var form = new StackPanel { Spacing = 8 };
        // Remplir depuis une capture de la fenêtre d'artisanat du jeu (lue sur ce PC).
        var latestShot = Clickable(T("lire la dernière capture d'écran", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), () => market.ReadCraft("latest"), Accent.Main, null, new Thickness(12, 4));
        latestShot.CornerRadius = new CornerRadius(3);
        var pickShot = Clickable(T("choisir une capture…", 12, Accent.Main, FontWeight.SemiBold, mono: true), () => market.ReadCraft(null), null, Tok.Hover, new Thickness(8, 4));
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                Tip(latestShot, "Dans le jeu, choisis la recette dans la fenêtre d'artisanat (Forge, Armure, Artisanat, Alchimie…) et fais Impr. écran : " +
                                "le Builder lit l'objet fabriqué et ses matériaux avec leurs quantités, puis remplit le formulaire. Rien n'est noté sans ton clic."),
                pickShot,
                T("remplit le formulaire depuis la fenêtre d'artisanat du jeu", 11, Tok.Muted).WithMargin(0, 5, 0, 0),
            },
        });
        if (s.CraftShotStatus is { } shotStatus) form.Children.Add(T(shotStatus, 11, shotStatus.Contains("À compléter") || shotStatus.StartsWith("Capture refusée") ? Tok.Warn : Tok.Text2, wrap: true));
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
        if (CutHelp(items, draft.Output, name => draft.Output = name) is { } outputHelp) form.Children.Add(outputHelp);
        for (var i = 0; i < draft.Inputs.Count; i++)
        {
            var line = draft.Inputs[i];
            var npc = Tip(Clickable(T(line.Npc ? "☑ PNJ" : "☐ PNJ", 12, line.Npc ? Accent.Main : Tok.Muted, mono: true), () => update(_ =>
            {
                line.Npc = !line.Npc;
                if (line.Npc && line.NpcPrice.Trim().Length == 0 && npcByName.TryGetValue(Market.Fold(line.Name), out var listed)) line.NpcPrice = listed.Price.ToString();
            }), null, Tok.Hover, new Thickness(6, 5)),
                "Coche si cet ingrédient s'achète chez un PNJ (catalyseur…) : note son prix à l'unité. Il ne se revend pas à l'hôtel.");
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    T("Ingrédient", 12, Tok.Text2).WithMargin(0, 6, 0, 0),
                    Qty(line.Qty, t => line.Qty = t),
                    T("×", 12, Tok.Muted).WithMargin(0, 6, 0, 0),
                    ItemPicker(items, line.Name, line.Npc ? "objet du PNJ : catalyseur…" : "ingrédient : Od pur, orichalque…", 320, t => line.Name = t),
                    npc,
                },
            };
            if (line.Npc)
            {
                var price = new TextBox { Text = line.NpcPrice, Width = 120, PlaceholderText = "prix à l'unité" };
                price.TextChanged += (_, _) => line.NpcPrice = price.Text ?? "";
                row.Children.Add(price);
                row.Children.Add(T("Kinah", 11, Tok.Muted).WithMargin(0, 7, 0, 0));
            }
            if (draft.Inputs.Count > 1)
                row.Children.Add(Clickable(T("✕", 12, Tok.Muted), () => update(st => st.CraftDraft.Inputs.Remove(line)), null, Tok.Hover, new Thickness(8, 5)));
            form.Children.Add(row);
            if (CutHelp(items, line.Name, name => line.Name = name) is { } help) form.Children.Add(help);
        }
        var add = Clickable(T("+ ingrédient", 12, Accent.Main, FontWeight.SemiBold, mono: true), () => update(st => { if (st.CraftDraft.Inputs.Count < 10) st.CraftDraft.Inputs.Add(new()); }),
            null, Tok.Hover, new Thickness(6, 3));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(add);

        var kinah = new TextBox { Text = draft.Kinah, Width = 150, PlaceholderText = "coût en Kinah (0)" };
        kinah.TextChanged += (_, _) => draft.Kinah = kinah.Text ?? "";
        var note = new TextBox { Text = draft.Note, Width = 260, PlaceholderText = "note : métier, niveau… (facultatif)" };
        note.TextChanged += (_, _) => draft.Note = note.Text ?? "";
        var bound = Tip(Clickable(T(draft.Bound ? "☑ lié d'âme" : "☐ lié d'âme", 12, draft.Bound ? Accent.Main : Tok.Muted, mono: true),
                () => update(_ => draft.Bound = !draft.Bound), null, Tok.Hover, new Thickness(6, 5)),
            "Coche si la fenêtre dit « l'objet fabriqué sera lié d'âme » : il ne se revend pas, l'écran dira ce qu'il te coûte au lieu de « fabrique ou vends ».");
        async void Submit()
        {
            static int Number(string text, int fallback) => int.TryParse(new string(text.Where(char.IsDigit).ToArray()), out var n) ? n : fallback;
            RecipePartSend Part(string name, int qty, long? npc = null)
            {
                var clean = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var item = data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(clean));
                return new RecipePartSend(item?.Key, item?.Name ?? clean, qty, npc);
            }
            if (draft.Output.Trim().Length < 2) { update(st => st.CraftError = "Choisis ce que la recette fabrique."); return; }
            // Un nom coupé par le jeu (lu sur une capture) entrerait dans la liste des objets sous un faux nom.
            if (draft.Inputs.Select(l => l.Name).Prepend(draft.Output).FirstOrDefault(n => n.Contains('…') || n.TrimEnd().EndsWith("...")) is { } cut)
            {
                update(st => st.CraftError = $"« {cut.Trim()} » : le jeu a coupé ce nom (« … »), je ne peux pas le noter tel quel. Choisis le bon objet juste " +
                                              "sous la case, ou survole-le en jeu pour lire son nom entier et tape-le.");
                return;
            }
            var lines = draft.Inputs.Where(l => l.Name.Trim().Length >= 2).ToList();
            if (lines.FirstOrDefault(l => l.Npc && !long.TryParse(new string(l.NpcPrice.Where(char.IsDigit).ToArray()), out var u) | u < 1) is { } bad)
            {
                update(st => st.CraftError = $"Note le prix à l'unité de « {bad.Name.Trim()} » chez le PNJ.");
                return;
            }
            // Coché « PNJ » : le prix tapé ; sinon un objet de la liste « Objets du PNJ » prend son prix du PNJ.
            var inputs = lines.Select(l => Part(l.Name, Number(l.Qty, 1),
                l.Npc ? long.Parse(new string(l.NpcPrice.Where(char.IsDigit).ToArray()))
                : npcByName.TryGetValue(Market.Fold(l.Name.Trim()), out var listed) ? listed.Price : null)).ToList();
            if (inputs.Count == 0) { update(st => st.CraftError = "Ajoute au moins un ingrédient."); return; }
            var cost = long.TryParse(new string(draft.Kinah.Where(char.IsDigit).ToArray()), out var k) ? k : 0;
            var output = Part(draft.Output, Number(draft.OutputQty, 1));
            var error = await market.AddRecipe(new RecipeSend(output, inputs, cost, draft.Note.Trim().Length > 0 ? draft.Note.Trim() : null, draft.Bound));
            update(st =>
            {
                st.CraftError = error;
                if (error is null) { st.CraftDraft = new CraftDraft(); st.CraftShotStatus = null; }
            });
        }
        var save = Clickable(T("noter la recette", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), Submit, Accent.Main, null, new Thickness(14, 5));
        save.CornerRadius = new CornerRadius(3);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { kinah, note, bound, save } });
        if (s.CraftError is { } err) form.Children.Add(T(err, 12, Tok.Bad, wrap: true));
        form.Children.Add(T("Recopie la fenêtre d'artisanat du jeu : les quantités d'UNE fabrication. Un objet de la liste « Objets du PNJ » (plus bas) prend son prix " +
                            "tout seul ; pour un autre objet acheté chez un marchand, coche « PNJ » et note son prix. La recette vaut pour tous les joueurs d'Aion 2 Todo, " +
                            "avec ton pseudo ; n'importe qui peut retirer une recette fausse. Un objet hors liste entre sous le nom que tu tapes.", 11, Tok.Muted, wrap: true));
        var hub = Clickable(T("chercher une recette sur AION 2 Hub ↗", 11, Accent.Main, mono: true), () => OpenLink("https://aion2hub.com/tools/crafting-calculator"), null, Tok.Hover, new Thickness(4, 2));
        hub.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(Tip(hub, "Calculateur d'artisanat d'AION 2 Hub (site de fans, en anglais, recettes tirées du jeu) : pour lire une recette et la recopier ici. " +
                                   "Le Builder ne reprend rien de ce site sans leur accord."));
        return Card(form, "Noter une recette", null, new Thickness(16, 10, 16, 12));
    }

    /// <summary>Un nom coupé par le jeu (« Épée éclatante du Roi dr… ») sans ses « … », pour chercher les objets qui commencent ainsi.</summary>
    static string? Uncut(string? text) => text?.Trim().TrimEnd('…', '.', ' ');

    static bool IsCut(string name) => name.TrimEnd().EndsWith('…') || name.TrimEnd().EndsWith("...");

    /// <summary>
    /// Sous une case dont le nom est coupé par le jeu : les objets connus qui commencent ainsi (un clic remplace le nom), ou, s'il
    /// n'y en a pas, quoi faire. Null pour un nom entier.
    /// </summary>
    Control? CutHelp(List<MarketItem> items, string name, Action<string> set)
    {
        if (!IsCut(name)) return null;
        var prefix = Market.Fold(Uncut(name));
        var candidates = items.Where(i => Market.Fold(i.Name).StartsWith(prefix)).Select(i => i.Name).Distinct().Take(8).ToList();
        var row = new WrapPanel { Margin = new Thickness(80, -4, 0, 2) };
        row.Children.Add(T(candidates.Count > 0 ? "Nom coupé par le jeu. C'est :" : "Nom coupé par le jeu, et aucun objet connu ne commence ainsi : survole-le en jeu pour lire son nom entier et tape-le.",
            11, Tok.Warn).WithMargin(0, 3, 8, 0));
        foreach (var c in candidates)
        {
            var choice = Clickable(T(c, 11, Accent.Main, mono: true), () => update(_ => set(c)), null, Tok.Hover, new Thickness(6, 2));
            choice.Margin = new Thickness(0, 0, 6, 4);
            row.Children.Add(choice);
        }
        if (candidates.Count > 1) row.Children.Add(T("(au survol en jeu, le nom s'affiche en entier)", 11, Tok.Faint).WithMargin(0, 3, 0, 0));
        return row;
    }

    /// <summary>Liste des objets du PNJ par nom (sans accents ni majuscules).</summary>
    static Dictionary<string, NpcItem> NpcByName(MarketData data) =>
        (data.Npc ?? []).GroupBy(n => Market.Fold(n.Name)).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// « Objets du PNJ » : les prix des marchands, communs à tous les joueurs d'Aion 2 Todo (les mêmes pour tous dans le jeu), par
    /// onglet du marchand ; noter ou corriger un prix (par le nom), retirer un objet faux.
    /// </summary>
    Control NpcCard(MarketData data)
    {
        var list = data.Npc ?? [];
        var body = new StackPanel { Spacing = 8 };
        foreach (var tab in list.GroupBy(n => n.Tab ?? "Autres"))
        {
            body.Children.Add(T(tab.Key, 11, Tok.Muted, FontWeight.SemiBold, mono: true));
            var wrap = new WrapPanel();
            foreach (var n in tab)
            {
                var item = n;
                var who = n.By is null ? "relevé sur les captures des marchands" : $"noté par {(n.Mine ? "toi" : n.By)} {Market.Since(n.At)}";
                wrap.Children.Add(new Border
                {
                    BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3, 2, 3),
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6,
                        Children =
                        {
                            Tip(T(n.Name, 12, Tok.Text), who),
                            T(Fmt.Big(n.Price), 12, Tok.Text2, mono: true),
                            Tip(Clickable(T("✕", 10, Tok.Faint), () => market.RemoveNpc(item), null, Tok.Hover, new Thickness(4, 1)),
                                "Retirer cet objet de la liste (faux ou en double), pour tous. Pour corriger son prix, note-le à nouveau."),
                        },
                    },
                });
            }
            body.Children.Add(wrap);
        }
        if (list.Count == 0) body.Children.Add(T("Aucun objet du PNJ noté.", 12, Tok.Muted));

        var name = new TextBox { Text = s.NpcName, Width = 260, PlaceholderText = "nom complet : Catalyseur, Sel…" };
        name.TextChanged += (_, _) => s.NpcName = name.Text ?? "";
        var price = new TextBox { Text = s.NpcPrice, Width = 130, PlaceholderText = "prix à l'unité" };
        price.TextChanged += (_, _) => s.NpcPrice = price.Text ?? "";
        var tabBox = new TextBox { Text = s.NpcTab, Width = 200, PlaceholderText = "onglet : Consommables… (facultatif)" };
        tabBox.TextChanged += (_, _) => s.NpcTab = tabBox.Text ?? "";
        async void Save()
        {
            var digits = new string(s.NpcPrice.Where(char.IsDigit).ToArray());
            if (s.NpcName.Trim().Length < 2) { update(st => st.NpcError = "Note le nom de l'objet."); return; }
            if (!long.TryParse(digits, out var value) || value < 1) { update(st => st.NpcError = "Note son prix à l'unité chez le PNJ."); return; }
            var error = await market.AddNpc(s.NpcName.Trim(), value, s.NpcTab.Trim().Length > 0 ? s.NpcTab.Trim() : null);
            update(st =>
            {
                st.NpcError = error;
                if (error is null) { st.NpcName = ""; st.NpcPrice = ""; }
            });
        }
        var add = Clickable(T("noter le prix", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true), Save, Accent.Main, null, new Thickness(14, 5));
        add.CornerRadius = new CornerRadius(3);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0), Children = { name, price, tabBox, add } });
        if (s.NpcError is { } err) body.Children.Add(T(err, 12, Tok.Bad, wrap: true));
        body.Children.Add(T("Les prix des marchands sont les mêmes pour tout le monde : un joueur les note une fois, pour tous. Un objet déjà là est corrigé " +
                            "si tu notes le même nom. Le marchand coupe les noms longs (« … ») : note le nom complet, affiché au survol en jeu.", 11, Tok.Muted, wrap: true));

        var toggle = Clickable(T(s.NpcOpen ? "masquer" : $"afficher ({list.Count})", 11, Accent.Main, mono: true), () => update(st => st.NpcOpen = !st.NpcOpen), null, Tok.Hover, new Thickness(6, 2));
        return Card(s.NpcOpen ? body : T($"{Fmt.Plural(list.Count, "objet")} avec leur prix chez le PNJ.", 12, Tok.Muted), $"Objets du PNJ ({list.Count})", toggle,
            new Thickness(16, 10, 16, 12));
    }
}
