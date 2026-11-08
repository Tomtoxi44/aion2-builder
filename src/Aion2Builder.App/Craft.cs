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
    /// <summary>Correction d'une recette notée : son numéro (null : nouvelle recette).</summary>
    public int? Replaces { get; set; }
    /// <summary>Couleur (rang) de l'objet fabriqué choisie à la main : elle remplace celle lue, pour tous.</summary>
    public string? Grade { get; set; }

    /// <summary>Le formulaire rempli avec une recette notée, pour la corriger.</summary>
    public static CraftDraft From(CraftRecipe rc)
    {
        var draft = new CraftDraft
        {
            Output = rc.Output.Name, OutputQty = rc.Output.Qty.ToString(), Kinah = rc.Kinah > 0 ? rc.Kinah.ToString() : "",
            Note = rc.Note ?? "", Bound = rc.Bound, Replaces = rc.Id,
        };
        draft.Inputs.Clear();
        draft.Inputs.AddRange(rc.Inputs.Select(p => new CraftDraftLine
        {
            Name = p.Name, Qty = p.Qty.ToString(), Npc = p.Npc is not null, NpcPrice = p.Npc?.ToString() ?? "",
        }));
        return draft;
    }
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

/// <summary>D'où vient le prix d'un ingrédient : l'hôtel, un PNJ, la recette qui le fabrique, ou rien (compté 0).</summary>
enum PartSource { Market, Npc, Recipe, None }

/// <summary>
/// Un ingrédient chiffré pour une fabrication : d'où vient son prix ; Unit : prix à l'unité (hôtel, PNJ, ou coût par unité de sa
/// recette) ; Value : ce qu'il pèse dans le coût (vendu tel quel, taxe déduite ; PNJ : acheté) ; Buy : acheté à l'hôtel ; At : date
/// du prix (le plus ancien de sa recette) ; Via : la recette qui le chiffre ; Uncounted : ses ingrédients sans prix.
/// </summary>
sealed record PartCost(PartSource Source, long? Unit, long Value, long Buy, DateTime? At, CraftRecipe? Via, List<string> Uncounted);

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

    /// <summary>Ce qu'un ingrédient pèse dans le coût d'UNE fabrication (pour le tableau des ingrédients d'une recette).</summary>
    public PartCost CostOf(CraftRecipe rc, RecipePart p) => CostOf(p, [rc.Id]);

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
        var forSelf = ForSelf(rc);
        if (forSelf) { }
        else if (PriceOf(rc.Output) is { } op) { sell = (long)Math.Round(op.Price * rc.Output.Qty * (1 - tax)); Seen(op.At); }
        else noPrice.Add(rc.Output.Name);

        long inputs = 0, buy = 0, npc = 0;
        var uncounted = new List<string>();
        foreach (var p in rc.Inputs)
        {
            var c = CostOf(p, path);
            switch (c.Source)
            {
                case PartSource.Npc: npc += c.Value; break;
                case PartSource.None:
                    noPrice.Add(p.Name);
                    uncounted.Add(p.Name);
                    break;
                default:
                    inputs += c.Value;
                    buy += c.Buy;
                    if (c.Source == PartSource.Recipe) chained.Add(p.Name);
                    foreach (var u in c.Uncounted) if (!uncounted.Contains(u)) uncounted.Add(u);
                    if (c.At is { } at) Seen(at);
                    break;
            }
        }
        return new CraftCosting(sell, inputs, buy, npc, rc.Kinah, noPrice, chained, oldest, uncounted, forSelf);
    }

    /// <summary>Lié d'âme à la fabrication, ou objet de la liste « Fabrication » : il ne se revend pas.</summary>
    public bool ForSelf(CraftRecipe rc) => rc.Bound || ItemOf(rc.Output)?.Category.StartsWith("Fabrication") == true;

    PartCost CostOf(RecipePart p, HashSet<int> path)
    {
        if (p.Npc is { } unit) return new PartCost(PartSource.Npc, unit, unit * p.Qty, unit * p.Qty, null, null, []);
        if (PriceOf(p) is { } ip)
            return new PartCost(PartSource.Market, ip.Price, (long)Math.Round(ip.Price * p.Qty * (1 - tax)), ip.Price * p.Qty, ip.At, null, []);
        // Sans prix : chiffré par la recette qui le fabrique le moins cher (ingrédients vendus tels quels, PNJ, fabrication).
        // (la plus complète d'abord : une recette dont tout a un prix passe devant une moins chère qui en oublie)
        var sub = path.Count > 3 ? default : recipes.Where(r => !path.Contains(r.Id) && Same(r.Output, p))
            .Select(r => (R: (CraftRecipe?)r, C: (CraftCosting?)Cost(r, path)))
            .OrderBy(x => x.C!.Uncounted.Count)
            .ThenBy(x => (x.C!.Inputs!.Value + x.C.Npc + x.C.Fee) / (double)x.R!.Output.Qty)
            .FirstOrDefault();
        // Ni prix ni recette : compté 0 (souvent un objet qui ne se vend pas, comme la pièce de transfert) ; le coût est un minimum.
        if (sub.R is not { } sr || sub.C is not { } sc) return new PartCost(PartSource.None, null, 0, 0, null, null, [p.Name]);
        var share = (double)p.Qty / sr.Output.Qty;
        var value = (long)Math.Round((sc.Inputs!.Value + sc.Npc + sc.Fee) * share);
        return new PartCost(PartSource.Recipe, (long)Math.Round(value / (double)p.Qty), value,
            (long)Math.Round((sc.Buy!.Value + sc.Npc + sc.Fee) * share), sc.Oldest, sr, sc.Uncounted);
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
    /// L'écran Craft calculé une fois par affichage (barre de gauche et page) : les recettes notées par les joueurs d'Aion 2
    /// Todo, croisées avec ton stock (sac et coffre lus par le Pet Meter) et les derniers prix de la communauté.
    /// </summary>
    sealed record CraftModel(MarketData Data, string Shown, double Tax, string TaxNote, CraftBook Book, Dictionary<string, NpcItem> NpcByName,
        List<CraftPlan> Plans, List<CharacterInventory> Owners, CharacterInventory? Who, bool HasStock, long Kinah,
        List<(CraftPlan Plan, int N, long Total)> Proposal, List<CraftPlan> Flips);

    CraftModel? craftModel;
    bool craftModelDone;

    /// <summary>Ingrédient sorti du coût d'une recette par un clic (tu l'as déjà, comme l'arme précédente).</summary>
    bool CraftSkipped(int recipe, RecipePart p) => s.CraftSkip.Contains($"{recipe}:{p.Key}");

    CraftModel? Crafting()
    {
        if (craftModelDone) return craftModel;
        craftModelDone = true;
        if (!market.Linked || market.Data is not { } data) return null;
        var days = PetMeterGathering.Merge(accountGather.Days, PetMeterGathering.Load());
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
        var full = (data.Recipes ?? []).Select(rc => rc with { Inputs = rc.Inputs.Select(Priced).ToList() }).ToList();
        // Ingrédients sortis du coût : ni dans le coût, ni dans « tu peux en faire » ; ils restent affichés, barrés, dans la recette.
        var recipes = full.Select(rc => rc with { Inputs = rc.Inputs.Where(p => !CraftSkipped(rc.Id, p)).ToList() }).ToList();
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

        var plans = full.Zip(recipes, (shownRc, rc) =>
        {
            var parts = shownRc.Inputs.Select(p => (Part: p, Item: book.ItemOf(p), Have: HaveOf(p))).ToList();
            var costing = book.Cost(rc);
            int? canMake = null;
            string? limit = null;
            if (entries is not null)
            {
                // Les objets du PNJ s'achètent au besoin : ils ne limitent que par les Kinah.
                var counts = parts.Where(x => x.Part.Npc is null && !CraftSkipped(rc.Id, x.Part))
                    .Select(x => (Name: x.Part.Name, N: (int)Math.Min(int.MaxValue, (x.Have ?? 0) / x.Part.Qty))).ToList();
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
        })
            // Les rentables d'abord (gain en tout avec ton stock, puis par fabrication), puis le reste par nom.
            .OrderByDescending(p => p.Gain is null ? 0 : 1).ThenByDescending(p => p.Total ?? long.MinValue).ThenByDescending(p => p.Gain ?? long.MinValue)
            .ThenBy(p => p.Recipe.Output.Name, French).ToList();
        var proposal = entries is null ? [] : CraftBook.Proposal(plans, HaveOf, kinahHave);
        var flips = plans.Where(p => p.Flip > 0).OrderByDescending(p => p.Flip).ToList();
        return craftModel = new CraftModel(data, shown, tax, taxNote, book, npcByName, plans, owners, who, entries is not null, kinahHave, proposal, flips);
    }

    enum CraftCall { Make, SellInputs, ForSelf, BuyNpc, NoPrice }

    /// <summary>Le verdict d'une recette : quoi faire, en un mot, et sa couleur.</summary>
    static (CraftCall Call, string Word, IBrush Color) CallOf(CraftPlan p, CraftModel m)
    {
        if (p.Gain is { } g) return g > 0 ? (CraftCall.Make, "fabrique", Tok.Good) : (CraftCall.SellInputs, "vends les ingrédients", Tok.Warn);
        if (p.C.ForSelf && p.Cost is not null) return (CraftCall.ForSelf, p.Recipe.Bound ? "lié d'âme : pour toi" : "pour toi", Tok.Text2);
        if (NpcPriceOf(p, m) is { } npc && p.Cost is { } cost && npc < cost) return (CraftCall.BuyNpc, "achète-le au PNJ", Tok.Warn);
        return (CraftCall.NoPrice, "prix manquant", Tok.Faint);
    }

    /// <summary>Prix chez le PNJ de ce que la recette fabrique (pour une fabrication), s'il n'a pas de prix à l'hôtel.</summary>
    static long? NpcPriceOf(CraftPlan p, CraftModel m) =>
        p.C.Sell is null && m.NpcByName.TryGetValue(Market.Fold(p.Recipe.Output.Name), out var sold) ? sold.Price * p.Recipe.Output.Qty : null;

    /// <summary>Kinah en court pour la barre de gauche : 146 k, 77,1 M.</summary>
    static string Short(long v)
    {
        var a = Math.Abs(v);
        return a >= 1_000_000_000 ? $"{Fmt.Num(Math.Round(v / 1e9, 1))} Md" : a >= 1_000_000 ? $"{Fmt.Num(Math.Round(v / 1e6, 1))} M"
            : a >= 10_000 ? $"{Fmt.Num(Math.Round(v / 1e3))} k" : Fmt.Big(v);
    }

    /// <summary>Barre de gauche de l'écran Craft : la vue d'ensemble, chaque recette avec son verdict, puis noter une recette et les prix des PNJ.</summary>
    SidePanel? CraftSide()
    {
        if (Crafting() is not { } m) return null;
        var body = new StackPanel();
        var sum = m.Proposal.Sum(x => x.Total);
        body.Children.Add(SideItem(Icons.Grid, "Vue d'ensemble", sum > 0 ? $"+{Short(sum)}" : null, Tok.Good,
            m.HasStock ? sum > 0 ? "ce que je fabriquerais avec ton stock" : "rien de rentable avec ton stock" : "toutes les recettes et leur verdict",
            s.CraftPage == CraftPage.Overview, () => update(st => st.CraftPage = CraftPage.Overview)));
        body.Children.Add(SideGroup($"Recettes · {m.Plans.Count}"));
        foreach (var p in m.Plans)
        {
            var (call, _, color) = CallOf(p, m);
            var (badge, preview) = call switch
            {
                CraftCall.Make => ($"+{Short(p.Gain!.Value)}", p.CanMake is > 0 ? $"fabrique · {p.CanMake} possible{(p.CanMake > 1 ? "s" : "")} avec ton stock" : "fabrique · par fabrication"),
                CraftCall.SellInputs => ($"−{Short(-p.Gain!.Value)}", "vends plutôt les ingrédients"),
                CraftCall.ForSelf => ((p.Complete ? "" : "≥ ") + Short(p.Cost!.Value), p.Recipe.Bound ? "lié d'âme · son coût pour toi" : "ne se vend pas · son coût pour toi"),
                CraftCall.BuyNpc => ("PNJ", "moins cher chez le PNJ"),
                _ => ("?", $"prix manquant : {string.Join(", ", p.C.NoPrice.Distinct().Take(2))}"),
            };
            var id = p.Recipe.Id;
            body.Children.Add(SideItem(null, p.Recipe.Output.Name, badge, color, preview, s.CraftPage == CraftPage.Recipe && s.CraftRecipe == id,
                () => update(st => { st.CraftPage = CraftPage.Recipe; st.CraftRecipe = id; })));
        }
        if (m.Plans.Count == 0) body.Children.Add(T("Aucune recette notée pour l'instant.", 12, Tok.Muted).WithMargin(14, 6));
        body.Children.Add(SideGroup("Ajouter"));
        body.Children.Add(SideItem(Icons.Card, "Noter une recette", null, null, "depuis une capture de la fenêtre d'artisanat, ou à la main",
            s.CraftPage == CraftPage.New && s.CraftDraft.Replaces is null, () => update(st =>
            {
                if (st.CraftDraft.Replaces is not null) { st.CraftDraft = new CraftDraft(); st.CraftError = null; }
                st.CraftPage = CraftPage.New;
            })));
        var npc = m.Data.Npc?.Count ?? 0;
        body.Children.Add(SideItem(Icons.List, "Prix des PNJ", $"{npc}", Tok.Text2, "objets achetés chez un marchand (catalyseurs…)",
            s.CraftPage == CraftPage.Npc, () => update(st => st.CraftPage = CraftPage.Npc)));
        return new SidePanel("Craft", null, body);
    }

    /// <summary>
    /// Écran Craft : la vue d'ensemble (ce que je fabriquerais avec ton stock, ce qui rapporte même en achetant les ingrédients,
    /// toutes les recettes avec leur verdict), la page d'une recette (ses ingrédients, le calcul, le verdict), noter une recette,
    /// les prix des PNJ. Aucune source ne donne les recettes : un joueur les note une fois, pour tous.
    /// </summary>
    Control CraftScreen()
    {
        if (!market.Linked)
            return Page("Craft", null, Card(T("Les recettes et les prix viennent d'Aion 2 Todo : colle ta clé de synchro dans l'écran Familiers.", 13, Tok.Muted, wrap: true)));
        if (Crafting() is not { } m)
            return Page("Craft", null, Card(T(market.Status ?? "Lecture des recettes et des prix sur Aion 2 Todo…", 13, Tok.Muted, wrap: true)));
        var right = m.Owners.Count > 1
            ? Choice(m.Owners.Select(c => (c.Character, c == m.Who, (Action)(() => update(st => st.InventoryChar = c.Character)))))
            : m.Who is null ? null : (Control)T($"stock de {m.Who.Character}", 11, Tok.Muted, mono: true);
        return s.CraftPage switch
        {
            CraftPage.Recipe when m.Plans.FirstOrDefault(p => p.Recipe.Id == s.CraftRecipe) is { } plan => CraftRecipePage(m, plan, right),
            CraftPage.New => Page(s.CraftDraft.Replaces is null ? "Craft · noter une recette" : "Craft · corriger une recette", right, RecipeForm(m.Data)),
            CraftPage.Npc => Page("Craft · prix des PNJ", right, NpcCard(m.Data)),
            _ => CraftOverview(m, right),
        };
    }

    /// <summary>Ce qui suit « Craft » dans le fil d'Ariane.</summary>
    string CraftCrumb() => s.CraftPage switch
    {
        CraftPage.Recipe => Crafting()?.Plans.FirstOrDefault(p => p.Recipe.Id == s.CraftRecipe)?.Recipe.Output.Name ?? "Vue d'ensemble",
        CraftPage.New => s.CraftDraft.Replaces is null ? "Noter une recette" : "Corriger une recette",
        CraftPage.Npc => "Prix des PNJ",
        _ => "Vue d'ensemble",
    };

    void OpenRecipe(int id) => update(st => { st.CraftPage = CraftPage.Recipe; st.CraftRecipe = id; });

    /// <summary>Vue d'ensemble : les chiffres, la proposition avec ton stock, les achats rentables, et toutes les recettes en une ligne chacune.</summary>
    Control CraftOverview(CraftModel m, Control? right)
    {
        var data = m.Data;
        var blocks = new List<Control?>();
        if (!m.HasStock)
            blocks.Add(T("Ton stock vient du Pet Meter de ce PC (inventory.json, v0.21.0 ou plus, lancé avant d'entrer en jeu). Sans lui, les recettes et leurs " +
                         "gains restent visibles, sans « tu peux en faire » ni proposition.", 12, Tok.Warn, wrap: true));

        var makeable = m.Plans.Count(p => p.CanMake > 0);
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Recettes notées", m.Plans.Count.ToString(), size: 20, note: "par les joueurs d'Aion 2 Todo, valables pour tous").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Faisables avec ton stock", m.HasStock ? makeable.ToString() : "—", makeable > 0 ? Tok.Good : null, 20,
            note: m.Who is null ? "inventaire inconnu sur ce PC" : $"sac et coffre de {m.Who.Character}, relevé {Market.Since(m.Who.At)}").WithMargin(0, 0, 10, 0));
        var proposalSum = m.Proposal.Sum(x => x.Total);
        kpis.Children.Add(Kpi("Gain en fabriquant", !m.HasStock ? "—" : proposalSum > 0 ? $"+{Fmt.Big(proposalSum)}" : "0", proposalSum > 0 ? Tok.Good : null, 20,
            note: !m.HasStock ? "il faut ton stock (Pet Meter)" : proposalSum > 0 ? "Kinah de plus que vendre tes ingrédients, en suivant la proposition"
                : "rien de rentable avec ton stock aux prix connus").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Taxe du Marché", $"{m.Tax:P0}", size: 20, note: $"déduite à chaque vente ; prix de {data.Label(m.Shown)}" +
                                                                         (m.Shown.StartsWith('*') ? " ; inter-serveurs : à vérifier en jeu" : "")));
        blocks.Add(kpis);

        if (m.Proposal.Count > 0) blocks.Add(CraftProposalCard(m.Proposal, m.Kinah));
        else if (m.HasStock && m.Plans.Any(p => p.Gain is not null))
            blocks.Add(Card(T("Aucune recette ne rapporte plus que vendre tes ingrédients tels quels, avec ton stock et les prix connus. Vends-les " +
                              "(écran HdV → « Vendre ou garder ? ») ou note d'autres recettes et d'autres prix.", 12, Tok.Muted, wrap: true), "Ce que je fabriquerais avec ton stock"));
        if (m.Flips.Count > 0) blocks.Add(CraftFlipCard(m.Flips.Take(5).ToList(), m.Flips.Count, data.Label(m.Shown)));

        if (s.CraftUndo is { } undo)
        {
            var back = Clickable(T("rétablir", 12, Accent.Main, FontWeight.SemiBold, mono: true), () => { market.RestoreRecipe(undo.Id); update(st => st.CraftUndo = null); }, null, Tok.Hover, new Thickness(6, 2));
            blocks.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { T($"Recette retirée : {undo.Name}.", 12, Tok.Muted).WithMargin(0, 2, 0, 0), back } });
        }

        if (m.Plans.Count == 0)
        {
            blocks.Add(Card(T("Aucune recette notée pour l'instant. Note la première (barre de gauche → « Noter une recette ») : ouvre la fenêtre d'artisanat du jeu " +
                              "et fais une capture d'écran, le Builder la lit. Elle servira à tous les joueurs d'Aion 2 Todo.", 13, Tok.Muted, wrap: true).WithLineHeight(1.5)));
            return Page("Craft · vue d'ensemble", right, blocks.ToArray());
        }

        // Toutes les recettes, une ligne chacune : le verdict en mots et en chiffres ; un clic ouvre la recette (ingrédients et calcul).
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
        var visible = m.Plans.Where(p => filter switch { "make" => p.CanMake > 0, "gain" => p.Gain > 0 || p.Flip > 0, _ => true }).ToList();
        const string cols = "*,190,170,120";
        var rows = new List<(Control Row, string Text)>();
        foreach (var p in visible)
        {
            var rc = p.Recipe;
            var (call, word, color) = CallOf(p, m);
            Control result = call switch
            {
                CraftCall.Make or CraftCall.SellInputs => Tip(T($"{(p.Gain >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(p.Gain!.Value))}", 13, color, FontWeight.SemiBold, mono: true).AlignRight(),
                    "Par fabrication : vendre le fabriqué (taxe déduite) moins ce que rapporteraient ses ingrédients vendus tels quels, achats au PNJ et coût de fabrication compris."),
                CraftCall.ForSelf => Tip(T($"coûte {(p.Complete ? "" : "≥ ")}{Fmt.Big(p.Cost!.Value)}", 12, Tok.Text2, mono: true).AlignRight(),
                    "Ne se revend pas : ce qu'il te coûte, c'est-à-dire ce que tes ingrédients rapporteraient vendus tels quels, plus les achats au PNJ et le coût de fabrication." +
                    (p.Complete ? "" : " « ≥ » : des ingrédients n'ont pas de prix, le coût est un minimum.")),
                CraftCall.BuyNpc => T($"PNJ {Fmt.Big(NpcPriceOf(p, m)!.Value)}", 12, Tok.Text2, mono: true).AlignRight(),
                _ => T("—", 12, Tok.Faint, mono: true).AlignRight(),
            };
            var make = p.CanMake is { } cm ? T(cm.ToString(), 13, cm > 0 ? Tok.Text : Tok.Faint, FontWeight.SemiBold, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight();
            var id = rc.Id;
            var row = TableRow(Columns(cols, MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13, withKind: true),
                T(word, 12, color, FontWeight.SemiBold), result, make), false, () => OpenRecipe(id));
            rows.Add((row, $"{rc.Output.Name} {p.Item?.Category} {string.Join(' ', p.Parts.Select(i => i.Part.Name))} {rc.Note}"));
        }
        var none = T("Aucune recette ne correspond.", 12, Tok.Muted).WithMargin(20, 8, 20, 8);
        void Filter()
        {
            var count = 0;
            foreach (var (row, text) in rows) { row.IsVisible = Market.Matches(query.Text, text); if (row.IsVisible) count++; }
            none.IsVisible = count == 0;
        }
        query.TextChanged += (_, _) => { s.CraftQuery = query.Text; Filter(); };
        Filter();
        blocks.Add(TableCard("Recettes", T((filter is null ? Fmt.Plural(visible.Count, "recette") : $"{visible.Count} sur {m.Plans.Count}") + " · clic : ingrédients et calcul", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Objet fabriqué", "Conseil", "Par fabrication", "Tu peux en faire"), rows.Select(x => x.Row).Append(none)));
        return Page("Craft · vue d'ensemble", right, blocks.ToArray());
    }

    /// <summary>
    /// La page d'une recette : l'objet fabriqué, les quatre chiffres (coût, vente, gain, fabrications possibles), le verdict en une
    /// phrase, puis chaque ingrédient (quantité, ton stock, prix, ce qu'il pèse dans le coût ; décoché = déjà à toi, hors du coût).
    /// </summary>
    Control CraftRecipePage(CraftModel m, CraftPlan p, Control? right)
    {
        var rc = p.Recipe;
        var data = m.Data;
        var (call, word, color) = CallOf(p, m);
        var blocks = new List<Control?>();
        var stale = DateTime.UtcNow.AddDays(-7);

        // L'objet fabriqué, qui a noté la recette, les liens.
        var head = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty} par fabrication" : rc.Output.Name, 18, withKind: true).WithMargin(0, 0, 16, 0));
        head.Children.Add(T($"notée par {(rc.Mine ? "toi" : rc.By ?? "un compte supprimé")} {Market.Since(rc.At)}{(rc.Note is { } n ? $" · {n}" : "")}", 11, Tok.Muted, mono: true).WithMargin(0, 5, 12, 0));
        if (long.TryParse(rc.Output.Key, out var gameId))
            head.Children.Add(Tip(Clickable(T("fiche AION 2 Hub ↗", 11, Accent.Main, mono: true), () => OpenLink($"https://aion2hub.com/database/items/{gameId}"), null, Tok.Hover, new Thickness(6, 2)),
                "Fiche de l'objet sur AION 2 Hub (site de fans, en anglais) : sa recette pour la vérifier. Rien n'est repris automatiquement."));
        var recipe = rc;
        Control Button(string text, IBrush color, Action act, string tip)
        {
            var b = Clickable(T(text, 12, color, FontWeight.SemiBold, mono: true), act, null, Tok.Hover, new Thickness(12, 4));
            b.CornerRadius = new CornerRadius(3);
            b.BorderBrush = color;
            b.BorderThickness = new Thickness(1);
            return Tip(b, tip);
        }
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 0),
            Children =
            {
                Button("modifier", Accent.Main, () => update(st =>
                    {
                        st.CraftDraft = CraftDraft.From(recipe);
                        st.CraftError = null;
                        st.CraftShotStatus = null;
                        st.CraftPage = CraftPage.New;
                    }),
                    "Corriger cette recette (ingrédient, quantité, coût, note…) : la version corrigée est notée avec ton pseudo, " +
                    "l'ancienne passe dans les retirées et reste rétablissable 30 jours."),
                Button("retirer", Tok.Bad, () =>
                    {
                        market.RemoveRecipe(recipe);
                        update(st => { st.CraftUndo = (recipe.Id, recipe.Output.Name); st.CraftPage = CraftPage.Overview; });
                    },
                    "Retirer cette recette (fausse ou en double) : pour tous les joueurs, rétablissable 30 jours (« rétablir » sur la vue d'ensemble)."),
            },
        });
        // Couleur de l'objet fabriqué, si elle vient d'une capture (mal lue parfois) : à choisir, pour tous.
        if (p.Item is { } made && Market.GradeEditable(made))
            head.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 2, 0, 0),
                Children =
                {
                    T(made.Grade is null ? "couleur pas lue :" : "couleur", 11, made.Grade is null ? Tok.Warn : Tok.Muted, mono: true).WithMargin(0, 4, 0, 0),
                    GradePicker(made.Grade, g => market.SetGrade(made.Key, g)),
                },
            });
        blocks.Add(head);

        // Les quatre chiffres d'une fabrication.
        var cost = p.Cost;
        var kpis = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        kpis.Children.Add(Kpi("Coût d'une fabrication", cost is { } c ? (p.Complete ? "" : "≥ ") + Fmt.Big(c) : "—", p.Complete ? null : Tok.Warn, 20,
            note: p.Complete ? "détail dans le tableau des ingrédients" : "un minimum : des ingrédients n'ont pas de prix").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Vente du fabriqué", p.C.Sell is { } sell ? Fmt.Big(sell) : p.C.ForSelf ? "ne se vend pas" : "prix ?", p.C.Sell is null ? Tok.Muted : null, 20,
            note: p.C.Sell is not null ? $"au dernier prix de {data.Label(m.Shown)}, taxe de {m.TaxNote} déduite"
                : p.C.ForSelf ? rc.Bound ? "lié d'âme à qui le fabrique" : "objet fabriqué seulement"
                : $"pas de prix noté sur {data.Label(m.Shown)} (écran HdV)").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Gain par fabrication", p.Gain is { } g ? $"{(g >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(g))}" : "—", p.Gain is { } gc ? gc > 0 ? Tok.Good : Tok.Warn : Tok.Muted, 20,
            note: p.Gain is not null ? "vente moins coût : de plus qu'en vendant les ingrédients tels quels"
                : p.C.ForSelf ? "rien à gagner : la question est ce qu'il te coûte" : "il faut tous les prix").WithMargin(0, 0, 10, 0));
        kpis.Children.Add(Kpi("Tu peux en faire", p.CanMake is { } cm ? cm.ToString() : "—", p.CanMake is > 0 ? Tok.Good : null, 20,
            note: p.CanMake is null ? "il faut ton stock (Pet Meter)" : p.CanMake > 0 ? $"avec ton stock ; ensuite il manque : {p.Limit}" : $"il te manque : {p.Limit}"));
        blocks.Add(kpis);

        // Le verdict en une phrase.
        var output = rc.Output.Qty > 1 ? $"{rc.Output.Qty} {rc.Output.Name}" : rc.Output.Name;
        var verdict = call switch
        {
            CraftCall.Make => $"Fabrique : vendre {output} rapporte {Fmt.Big(p.Gain!.Value)} Kinah de plus que vendre ses ingrédients tels quels." +
                              (p.Total is > 0 && p.CanMake is > 0 ? $" Avec ton stock ({Fmt.Plural(p.CanMake.Value, "fabrication")}) : +{Fmt.Big(p.Total.Value)} Kinah." : ""),
            CraftCall.SellInputs => $"Vends plutôt les ingrédients : fabriquer {output} te fait perdre {Fmt.Big(-p.Gain!.Value)} Kinah par fabrication, par rapport à les vendre tels quels.",
            CraftCall.ForSelf => $"{(rc.Bound ? "Lié d'âme" : "Ne se vend pas")} : la question est ce qu'il te coûte. Le fabriquer revient à {(p.Complete ? "" : "au moins ")}" +
                                 $"{Fmt.Big(cost!.Value)} Kinah : ce que tes ingrédients rapporteraient vendus tels quels, plus les achats au PNJ et le coût de fabrication.",
            CraftCall.BuyNpc => $"Achète-le au PNJ : {Fmt.Big(NpcPriceOf(p, m)!.Value)} Kinah, contre {Fmt.Big(cost!.Value)} pour le fabriquer. Sans prix noté à l'hôtel, " +
                                "on ne sait pas s'il s'y vend plus cher.",
            _ => $"Il manque des prix pour juger : {string.Join(", ", p.C.NoPrice.Distinct())}. Note-les dans l'écran HdV (sur {data.Label(m.Shown)}), ou la recette " +
                 "de ceux qui se fabriquent.",
        };
        if (p.Flip is > 0) verdict += $" Même en achetant tous les ingrédients à l'hôtel, il reste +{Fmt.Big(p.Flip.Value)} par fabrication.";
        if (!p.Complete && call != CraftCall.NoPrice)
            verdict += $" Sans prix ni recette, comptés 0 : {string.Join(", ", p.C.Uncounted)}.";
        if (p.C.Oldest < stale) verdict += $" Le plus ancien des prix utilisés date du {p.C.Oldest!.Value.ToLocalTime():dd/MM} : à revoir.";
        blocks.Add(Callout(verdict, call == CraftCall.NoPrice ? Tok.Muted : color));

        // Les ingrédients d'une fabrication.
        const string cols = "56,*,70,120,130,140";
        var lines = new List<Control>();
        foreach (var x in p.Parts)
        {
            var part = x.Part;
            var off = CraftSkipped(rc.Id, part);
            var pc = m.Book.CostOf(rc, part);
            var skipKey = $"{rc.Id}:{part.Key}";
            var box = Tip(Clickable(Tick(!off), () => update(st => { if (!st.CraftSkip.Remove(skipKey)) st.CraftSkip.Add(skipKey); }),
                    null, Tok.Hover, new Thickness(10, 6)),
                off ? $"{part.Name} n'est pas compté dans le coût (tu l'as déjà). Clic : le compter à nouveau."
                    : $"Décoche si tu as déjà {part.Name} (ton arme précédente, par exemple) : il sort du coût et de « tu peux en faire ». Seulement pour toi.");
            var name = T(part.Name, 13, off ? Tok.Faint : x.Item?.GameGrade is { } gr ? MarketBrush(gr) : Tok.Text, FontWeight.Medium);
            if (off) name.TextDecorations = TextDecorations.Strikethrough;
            var (source, sourceColor) = off ? ("déjà à toi : pas compté", Tok.Faint) : pc.Source switch
            {
                PartSource.Npc => ("acheté au PNJ", Tok.Muted),
                PartSource.Market => ($"prix de l'hôtel, noté {Market.Since(pc.At!.Value)}", pc.At < stale ? Tok.Warn : Tok.Muted),
                PartSource.Recipe => ($"sans prix : chiffré par sa recette ({pc.Via!.Output.Name})", Tok.Muted),
                _ => ("ni prix ni recette : compté 0", Tok.Warn),
            };
            Control stock = off ? T("", 12)
                : part.Npc is not null ? T("au besoin", 11, Tok.Muted).AlignRight()
                : x.Have is not { } have ? T("—", 12, Tok.Faint, mono: true).AlignRight()
                : have >= part.Qty ? T(Fmt.Big(have), 13, Tok.Text2, mono: true).AlignRight()
                : Tip(T(Fmt.Big(have), 13, Tok.Warn, FontWeight.SemiBold, mono: true).AlignRight(), $"Il t'en manque {Fmt.Big(part.Qty - have)} pour une fabrication.");
            var unit = pc.Unit is { } u ? T(Fmt.Big(u), 13, off ? Tok.Faint : Tok.Text2, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight();
            Control weight = off ? T("0", 13, Tok.Faint, mono: true).AlignRight()
                : pc.Source == PartSource.None ? T("0 ?", 13, Tok.Warn, mono: true).AlignRight()
                : Tip(T(Fmt.Big(pc.Value), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(), pc.Source switch
                {
                    PartSource.Market => $"{part.Qty} × {Fmt.Big(pc.Unit!.Value)} vendus tels quels, taxe de {m.TaxNote} déduite : ce que tu « dépenses » en les fabriquant au lieu de les vendre.",
                    PartSource.Npc => $"{part.Qty} × {Fmt.Big(pc.Unit!.Value)} Kinah chez le PNJ.",
                    _ => $"Coût de sa recette ({pc.Via!.Output.Name}) pour {part.Qty} : ce que ses ingrédients rapporteraient vendus tels quels, PNJ et fabrication compris.",
                });
            lines.Add(new Border
            {
                Padding = new Thickness(12, 6, 20, 6),
                Child = Columns(cols, box,
                    new StackPanel { Spacing = 1, Children = { name, T(source, 11, sourceColor) } },
                    T($"×{part.Qty}", 13, off ? Tok.Faint : Tok.Text2, mono: true).AlignRight(), stock, unit, weight),
            });
        }
        Control Sum(string label, string value, IBrush? valueColor = null, bool strong = false) => new Border
        {
            Padding = new Thickness(12, 6, 20, 6), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Columns(cols, new Panel(), T(label, 13, strong ? Tok.Text : Tok.Text2, strong ? FontWeight.SemiBold : FontWeight.Normal), new Panel(), new Panel(), new Panel(),
                T(value, strong ? 14 : 13, valueColor ?? Tok.Text, strong ? FontWeight.SemiBold : FontWeight.Normal, mono: true).AlignRight()),
        };
        if (p.C.Fee > 0) lines.Add(Sum("Coût de fabrication (Kinah demandés par l'artisanat)", Fmt.Big(p.C.Fee)));
        if (cost is { } total) lines.Add(Sum("Coût d'une fabrication", (p.Complete ? "" : "≥ ") + Fmt.Big(total), p.Complete ? null : Tok.Warn, strong: true));
        var skippedCount = p.Parts.Count(x => CraftSkipped(rc.Id, x.Part));
        blocks.Add(TableCard("Ingrédients · pour une fabrication", T(skippedCount > 0 ? $"{Fmt.Plural(skippedCount, "ingrédient")} déjà à toi, hors du coût" : "décoche ce que tu as déjà", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Compté", "Ingrédient", "Qté", "Ton stock", "Prix unitaire", "Dans le coût"), lines));
        blocks.Add(T($"Comment le coût est compté : un ingrédient de l'hôtel vaut ce qu'il te rapporterait vendu tel quel (prix × quantité, taxe de {m.TaxNote} déduite), " +
                     "puisque le fabriquer, c'est renoncer à le vendre ; un objet du PNJ vaut son prix d'achat ; un ingrédient sans prix qu'une autre recette fabrique vaut le " +
                     $"coût de cette recette. Prix : derniers notés sur {data.Label(m.Shown)} par les joueurs d'Aion 2 Todo.", 11, Tok.Muted, wrap: true).WithLineHeight(1.45));
        return Page($"Craft · {rc.Output.Name}", right, blocks.ToArray());
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
            var id = rc.Id;
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
                            Children =
                            {
                                T($"Fabrique {Fmt.Plural(n, "fois", "fois")} ", 13, Tok.Text),
                                Tip(Clickable(MarketItemLine(plan.Item, made > 1 ? $"{rc.Output.Name} (×{Fmt.Big(made)})" : rc.Output.Name, 13), () => OpenRecipe(id), null, Tok.Hover, new Thickness(3, 0)),
                                    "Ouvrir la recette : ses ingrédients et le calcul."),
                            },
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
            var id = rc.Id;
            var name = Tip(Clickable(MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13), () => OpenRecipe(id), null, Tok.Hover, new Thickness(3, 0)),
                "Ouvrir la recette : ses ingrédients et le calcul.");
            name.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(Columns("*,150",
                new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        name,
                        T($"achète : {string.Join(" + ", bits)} → vends {Fmt.Big(p.C.Sell!.Value)} (taxe déduite)", 11, Tok.Muted, wrap: true),
                    },
                },
                Tip(new StackPanel
                {
                    Children =
                    {
                        T($"+{Fmt.Big(p.Flip!.Value)}", 14, Tok.Good, FontWeight.SemiBold, mono: true).AlignRight(),
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
        // Couleur de l'objet fabriqué (rang) : lue sur la capture, elle se trompe parfois ; celle d'un objet du jeu ne change pas.
        var outputKnown = data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(draft.Output));
        if (outputKnown is null || Market.GradeEditable(outputKnown))
            form.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    T("Couleur", 12, Tok.Text2).WithMargin(0, 4, 0, 0),
                    GradePicker(draft.Grade ?? outputKnown?.Grade, g => update(_ => draft.Grade = g)),
                    T("de l'objet fabriqué, si elle est fausse ou pas lue (vaut pour tous)", 11, Tok.Muted).WithMargin(0, 4, 0, 0),
                },
            });
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
            var send = new RecipeSend(output, inputs, cost, draft.Note.Trim().Length > 0 ? draft.Note.Trim() : null, draft.Bound);
            var replaces = draft.Replaces;
            var chosen = draft.Grade;
            var error = replaces is { } id ? await market.ReplaceRecipe(id, send) : await market.AddRecipe(send);
            // Couleur choisie : sur l'objet de la liste, ou sur celui que la recette vient d'y faire entrer sous son nom.
            if (error is null && chosen is not null)
            {
                var made = data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(output.Name ?? ""));
                if (made is null || (Market.GradeEditable(made) && made.Grade != chosen))
                    market.SetGrade(made?.Key ?? "name:" + Market.Fold(output.Name), chosen);
            }
            update(st =>
            {
                st.CraftError = error;
                if (error is not null) return;
                st.CraftDraft = new CraftDraft();
                st.CraftShotStatus = null;
                if (replaces is not null) st.CraftPage = CraftPage.Overview;
            });
        }
        var save = Clickable(T(draft.Replaces is null ? "noter la recette" : "enregistrer la correction", 12, Tok.OnAccent, FontWeight.SemiBold, mono: true),
            Submit, Accent.Main, null, new Thickness(14, 5));
        save.CornerRadius = new CornerRadius(3);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { kinah, note, bound, save } };
        if (draft.Replaces is { } editing)
        {
            var cancel = Clickable(T("annuler", 12, Accent.Main, FontWeight.SemiBold, mono: true), () => update(st =>
            {
                st.CraftDraft = new CraftDraft();
                st.CraftError = null;
                st.CraftPage = CraftPage.Recipe;
                st.CraftRecipe = editing;
            }), null, Tok.Hover, new Thickness(12, 5));
            cancel.CornerRadius = new CornerRadius(3);
            cancel.BorderBrush = Accent.Main;
            cancel.BorderThickness = new Thickness(1);
            actions.Children.Add(cancel);
        }
        form.Children.Add(actions);
        if (s.CraftError is { } err) form.Children.Add(T(err, 12, Tok.Bad, wrap: true));
        form.Children.Add(T("Recopie la fenêtre d'artisanat du jeu : les quantités d'UNE fabrication. Un objet de la liste « Objets du PNJ » (plus bas) prend son prix " +
                            "tout seul ; pour un autre objet acheté chez un marchand, coche « PNJ » et note son prix. La recette vaut pour tous les joueurs d'Aion 2 Todo, " +
                            "avec ton pseudo ; n'importe qui peut retirer une recette fausse. Un objet hors liste entre sous le nom que tu tapes.", 11, Tok.Muted, wrap: true));
        var hub = Clickable(T("chercher une recette sur AION 2 Hub ↗", 11, Accent.Main, mono: true), () => OpenLink("https://aion2hub.com/tools/crafting-calculator"), null, Tok.Hover, new Thickness(4, 2));
        hub.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(Tip(hub, "Calculateur d'artisanat d'AION 2 Hub (site de fans, en anglais, recettes tirées du jeu) : pour lire une recette et la recopier ici. " +
                                   "Le Builder ne reprend rien de ce site sans leur accord."));
        if (draft.Replaces is not null)
            form.Children.Insert(0, Callout("Correction d'une recette notée : la version corrigée sera notée avec ton pseudo, l'ancienne passera dans les " +
                                            "retirées (rétablissable 30 jours).", Accent.Main));
        return Card(form, draft.Replaces is null ? "Noter une recette" : "Corriger la recette", null, new Thickness(16, 10, 16, 12));
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

        return Card(body, $"Objets du PNJ ({list.Count})", T("prix à l'unité, les mêmes pour tous", 11, Tok.Muted, mono: true), new Thickness(16, 10, 16, 12));
    }
}
