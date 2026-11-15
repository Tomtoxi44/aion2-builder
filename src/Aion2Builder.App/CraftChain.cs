using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Craft · « Chaîne d'un équipement » : du premier objet blanc au dernier palier du jeu. Une pièce d'équipement se fabrique par
/// paliers : la fabrication d'un palier peut rater (tout est perdu), et, réussie, elle a une chance de combo (25 %) qui donne la
/// version « éclatante », du rang au-dessus ; c'est cette version que demande la recette du palier suivant. Les paliers Roi dragon
/// passent ensuite par des fabrications par transfert, qui partent de la pièce du palier d'avant et ne ratent pas. La page repère
/// la pièce que tu portes ou que tu as en stock (ton palier) et compte, de là jusqu'à ton but, les essais de chaque recette (en
/// moyenne, au mieux ou en comptant large) et les ressources : ce qu'il faut, ton stock, ce qui manque et ce que coûte le manque.
/// Chances relevées sur MetaBot.GG (servies par le site avec les recettes officielles) ; ordre des transferts déduit des paliers.
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>
    /// Les chances d'une recette à ta maîtrise. Success : la fabrication normale réussit ; Combo : chance (en %) qu'une fabrication
    /// réussie déclenche le combo ; ComboSuccess : la fabrication combo réussit à son tour. Un transfert ne rate pas. Known : ta
    /// maîtrise du métier est renseignée (sinon les chances sont celles du niveau de la recette, le minimum).
    /// </summary>
    readonly record struct CraftOdds(double? Success, double? ComboSuccess, int Combo, bool Known, bool Transfer)
    {
        double S => Success ?? 1;
        /// <summary>Part des fabrications qui donnent le résultat combo.</summary>
        public double ComboYield => Combo <= 0 ? 0 : S * Combo / 100.0 * (Transfer ? 1 : ComboSuccess ?? S);
        /// <summary>Part des fabrications qui donnent l'objet normal.</summary>
        public double NormalYield => S * (1 - Math.Max(0, Combo) / 100.0);
    }

    static CraftOdds OddsOf(CraftRecipe rc, IReadOnlyDictionary<string, int> mastery)
    {
        if (rc.Transfer is not null) return new(1, 1, rc.Combo, true, true);
        var mine = rc.Profession is { } job && mastery.TryGetValue(job, out var level) ? level : (int?)null;
        var gap = mine is { } have && rc.Mastery is { } need ? Math.Max(0, have - need) : 0;
        double? At(string? curve) =>
            curve is not null && Market.CraftCurves.TryGetValue(curve, out var c) && c.Length > 0 ? c[Math.Min(gap, c.Length - 1)] / 100 : null;
        return new(At(rc.RateNormal), At(rc.RateCombo), rc.Combo, mine is not null, false);
    }

    /// <summary>
    /// Les recettes d'équipement reliées entre elles. ComboOf : la recette dont le combo donne l'objet ; MadeBy : la recette
    /// d'équipement qui le fabrique (fabrication ou transfert) ; Material : la recette d'un matériau qui se fabrique (lingot, corne
    /// raffinée) ; Lines : chaque ligne d'équipement, ses recettes du blanc au dernier palier. Tous les noms sont pliés.
    /// </summary>
    sealed class ChainBook
    {
        public Dictionary<string, CraftPlan> ComboOf { get; } = [];
        public Dictionary<string, CraftPlan> MadeBy { get; } = [];
        public Dictionary<string, CraftPlan> Material { get; } = [];
        public Dictionary<int, List<(string Key, RecipePart Part)>> Inputs { get; } = [];
        public Dictionary<string, RecipePart> Parts { get; } = [];
        public Dictionary<string, string> Names { get; } = [];
        public List<List<CraftPlan>> Lines { get; } = [];

        /// <summary>Un objet de la chaîne (il sort d'une recette d'équipement), pas un matériau.</summary>
        public bool IsLink(string key) => ComboOf.ContainsKey(key) || MadeBy.ContainsKey(key);

        /// <summary>
        /// Comment obtenir un objet de la chaîne. Main : la recette qu'on lance ; Combo : l'objet est son résultat combo ; Then : le
        /// transfert qui change son résultat normal en cet objet quand le combo ne sort pas.
        /// </summary>
        public (CraftPlan? Main, CraftPlan? Then, bool Combo) Ways(string key)
        {
            if (!ComboOf.TryGetValue(key, out var c)) return (MadeBy.GetValueOrDefault(key), null, false);
            var then = MadeBy.TryGetValue(key, out var t) && t.Recipe.Transfer is { } src && Market.Fold(src) == Market.Fold(c.Recipe.Output.Name) ? t : null;
            return (c, then, true);
        }
    }

    /// <summary>Ordre des catégories du jeu : armes, armures, bijoux.</summary>
    static readonly string[] ChainKinds =
    [
        "Épées", "Espadons", "Dagues", "Masses", "Garde-bras", "Arcs", "Bâtons", "Grimoires", "Orbes",
        "Casques", "Épaulettes", "Torses", "Gants", "Bas", "Chaussures", "Capes", "Colliers", "Boucles d'oreilles", "Anneaux", "Bracelets",
    ];

    (CraftModel Model, ChainBook Book)? chainBook;

    ChainBook Chains(CraftModel m)
    {
        if (chainBook is { } kept && ReferenceEquals(kept.Model, m)) return kept.Book;
        var book = new ChainBook();
        var gear = m.Plans.Where(p => p.Recipe.Official && (p.Recipe.ComboName is not null || p.Recipe.Transfer is not null)).ToList();
        foreach (var p in gear)
        {
            if (p.Recipe.ComboName is { } combo)
            {
                var key = Market.Fold(combo);
                book.ComboOf.TryAdd(key, p);
                book.Names.TryAdd(key, combo);
            }
            var made = Market.Fold(p.Recipe.Output.Name);
            book.MadeBy.TryAdd(made, p);
            book.Names.TryAdd(made, p.Recipe.Output.Name);
        }
        // Un matériau fabriqué : sa recette officielle, celle à ta portée d'abord.
        foreach (var p in m.Plans.Where(p => p.Recipe.Official && p.Recipe.ComboName is null && p.Recipe.Transfer is null).OrderBy(p => p.Locked is not null))
        {
            var key = Market.Fold(p.Recipe.Output.Name);
            if (!book.IsLink(key)) book.Material.TryAdd(key, p);
        }
        foreach (var p in gear.Concat(book.Material.Values))
        {
            var inputs = p.Recipe.Inputs.Select(i => (Key: Market.Fold(i.Name), Part: i)).ToList();
            book.Inputs[p.Recipe.Id] = inputs;
            foreach (var (key, part) in inputs) book.Parts.TryAdd(key, part);
        }
        // Une pièce qui n'est l'ingrédient d'aucune recette (le dernier palier, la pièce de départ d'un transfert) garde son numéro.
        foreach (var p in gear) book.Parts.TryAdd(Market.Fold(p.Recipe.Output.Name), p.Recipe.Output with { Qty = 1 });

        // Les lignes : du blanc (aucun objet de la chaîne dans ses ingrédients), puis la recette qui demande son éclatant, etc.
        var users = new Dictionary<string, List<CraftPlan>>();
        foreach (var p in gear)
            foreach (var (key, _) in book.Inputs[p.Recipe.Id])
                if (book.ComboOf.ContainsKey(key))
                {
                    if (!users.TryGetValue(key, out var list)) users[key] = list = [];
                    list.Add(p);
                }
        var byTransfer = gear.Where(p => p.Recipe.Transfer is not null).GroupBy(p => Market.Fold(p.Recipe.Transfer!)).ToDictionary(g => g.Key, g => g.First());
        foreach (var root in gear.Where(p => p.Recipe.Transfer is null && p.Recipe.ComboName is not null && !book.Inputs[p.Recipe.Id].Any(i => book.IsLink(i.Key))))
        {
            var line = new List<CraftPlan> { root };
            while (line[^1].Recipe.ComboName is { } result && line.Count < 12)
            {
                var key = Market.Fold(result);
                var next = (users.TryGetValue(key, out var list) ? list.FirstOrDefault(p => p.Recipe.Transfer is null) : null) ?? byTransfer.GetValueOrDefault(key);
                if (next is null || line.Contains(next)) break;
                line.Add(next);
            }
            book.Lines.Add(line);
        }
        int Rank(List<CraftPlan> line)
        {
            var at = Array.IndexOf(ChainKinds, Market.ReferenceOf(line[0].Recipe.Output.Name)?.Category ?? "");
            return at < 0 ? ChainKinds.Length : at;
        }
        book.Lines.Sort((a, b) => Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) : French.Compare(a[0].Recipe.Output.Name, b[0].Recipe.Output.Name));
        chainBook = (m, book);
        return book;
    }

    /// <summary>Les objets de la chaîne dont dépend un but, le but d'abord, chacun après tout ce qui le demande.</summary>
    static List<string> ChainOrder(ChainBook book, string goal)
    {
        var seen = new HashSet<string>();
        var post = new List<string>();
        void Visit(string key, int depth)
        {
            if (depth > 24 || !seen.Add(key)) return;
            var (main, then, _) = book.Ways(key);
            foreach (var p in new[] { main, then })
            {
                if (p is null) continue;
                foreach (var (input, _) in book.Inputs[p.Recipe.Id])
                    if (book.IsLink(input)) Visit(input, depth + 1);
                if (ReferenceEquals(p, main) && p.Recipe.Transfer is { } src) Visit(Market.Fold(src), depth + 1);
            }
            post.Add(key);
        }
        Visit(goal, 0);
        post.Reverse();
        return post;
    }

    /// <summary>
    /// Ce qu'il faut pour un but. Crafts : essais par recette ; Needs : matériaux demandés par ces essais ; Raw : les mêmes une fois
    /// les matériaux fabriqués (lingot, corne raffinée) remplacés par leurs propres ingrédients, ton stock déduit ; Makes : ces
    /// matériaux fabriqués (besoin, pris dans ton stock, fabrications).
    /// </summary>
    sealed class ChainBill
    {
        public Dictionary<int, double> Crafts { get; } = [];
        public Dictionary<string, double> Needs { get; } = [];
        public Dictionary<string, double> Raw { get; } = [];
        public Dictionary<string, (CraftPlan Plan, double Need, double FromStock, double Crafts)> Makes { get; } = [];
        public double Total => Crafts.Values.Sum();
    }

    /// <summary>Ce que tu as d'un objet de la chaîne : dans ton stock, et sur toi (la pièce que tu portes est celle dont tu repars).</summary>
    static double ChainHave(ChainBook book, CraftModel m, string key)
    {
        var part = book.Parts.TryGetValue(key, out var known) ? known : new RecipePart("", book.Names.GetValueOrDefault(key, key), 1);
        return (m.HaveOf(part) ?? 0) + m.WornOf(part);
    }

    /// <summary>
    /// Où tu en es sur une ligne : le palier le plus haut dont tu as la pièce, portée ou en stock. Bright : c'est sa version
    /// éclatante (le palier est fini) ; sinon sa version normale. Worn : tu la portes ; By : sur quel perso.
    /// </summary>
    sealed record ChainSpot(int Tier, bool Bright, string Name, bool Worn, string? By);

    static ChainSpot? SpotOf(ChainBook book, CraftModel m, List<CraftPlan> line)
    {
        for (var i = line.Count - 1; i >= 0; i--)
        {
            var rc = line[i].Recipe;
            foreach (var (name, bright) in new (string? Name, bool Bright)[] { (rc.ComboName, true), (rc.Output.Name, false) })
            {
                if (name is null) continue;
                var part = book.Parts.TryGetValue(Market.Fold(name), out var known) ? known : new RecipePart("", name, 1);
                var worn = m.WornOf(part);
                if (worn + (m.HaveOf(part) ?? 0) > 0) return new ChainSpot(i, bright, name, worn > 0, m.WornBy(part));
            }
        }
        return null;
    }

    /// <summary>
    /// Les essais de chaque recette pour un but. lucky : au mieux (rien ne rate, chaque combo sort du premier coup) ; sinon en
    /// moyenne : une recette dont on veut le combo est relancée 1 / (réussite × combo × réussite du combo) fois, et, quand un
    /// transfert rattrape le résultat normal, 1 / (chance d'obtenir l'un ou l'autre) fois plus la part de transferts. La pièce
    /// normale d'un palier Roi dragon que tu as déjà passe directement par ce transfert, sans refaire le palier.
    /// </summary>
    static ChainBill Spread(ChainBook book, CraftModel m, string goal, bool lucky)
    {
        var bill = new ChainBill();
        var want = new Dictionary<string, double> { [goal] = 1 };
        void Attempt(CraftPlan p, double n, bool withSource)
        {
            bill.Crafts[p.Recipe.Id] = bill.Crafts.GetValueOrDefault(p.Recipe.Id) + n;
            foreach (var (key, part) in book.Inputs[p.Recipe.Id])
            {
                var to = book.IsLink(key) ? want : bill.Needs;
                to[key] = to.GetValueOrDefault(key) + n * part.Qty;
            }
            if (withSource && p.Recipe.Transfer is { } src)
            {
                var key = Market.Fold(src);
                want[key] = want.GetValueOrDefault(key) + n;
            }
        }
        foreach (var key in ChainOrder(book, goal))
        {
            var need = want.GetValueOrDefault(key);
            if (key != goal) need = Math.Max(0, need - ChainHave(book, m, key));
            if (need <= 0) continue;
            var (main, then, combo) = book.Ways(key);
            if (main is null) continue;
            var o = OddsOf(main.Recipe, m.Mastery);
            if (combo && then is not null)
            {
                var spare = Math.Min(need, ChainHave(book, m, Market.Fold(main.Recipe.Output.Name)));
                if (spare > 0)
                {
                    Attempt(then, spare, false);
                    need -= spare;
                }
                if (need <= 0) continue;
            }
            if (combo)
            {
                var yes = lucky ? 1 : o.ComboYield;
                var normal = lucky || then is null ? 0 : o.NormalYield;
                if (yes + normal <= 0) continue;
                var tries = need / (yes + normal);
                Attempt(main, tries, true);
                if (then is not null && normal > 0) Attempt(then, tries * normal, false);
            }
            else
            {
                var yes = lucky ? 1 : o.ComboYield + o.NormalYield;
                if (yes > 0) Attempt(main, need / yes, true);
            }
        }
        return bill;
    }

    /// <summary>
    /// Les mêmes essais « en comptant large » : la chaîne est jouée 2 000 fois au hasard (toujours la même suite de tirages), et
    /// chaque nombre est celui que 9 parties sur 10 ne dépassent pas. Total : le nombre d'essais en tout, compté de la même façon.
    /// </summary>
    static (ChainBill Bill, double Total) Prudent(ChainBook book, CraftModel m, string goal)
    {
        const int runs = 2000;
        var items = ChainOrder(book, goal);
        var itemAt = items.Select((key, at) => (key, at)).ToDictionary(x => x.key, x => x.at);
        var mats = new List<string>();
        var matAt = new Dictionary<string, int>();
        var recipes = new List<SimRecipe>();
        var recipeAt = new Dictionary<int, SimRecipe>();
        SimRecipe? Compile(CraftPlan? p)
        {
            if (p is null) return null;
            if (recipeAt.TryGetValue(p.Recipe.Id, out var done)) return done;
            var o = OddsOf(p.Recipe, m.Mastery);
            var links = new List<(int, int)>();
            var uses = new List<(int, int)>();
            foreach (var (key, part) in book.Inputs[p.Recipe.Id])
            {
                if (book.IsLink(key)) { if (itemAt.TryGetValue(key, out var at)) links.Add((at, part.Qty)); }
                else
                {
                    if (!matAt.TryGetValue(key, out var at)) { matAt[key] = at = mats.Count; mats.Add(key); }
                    uses.Add((at, part.Qty));
                }
            }
            var r = new SimRecipe(recipes.Count, p.Recipe.Id, links.ToArray(), uses.ToArray(),
                p.Recipe.Transfer is { } src && itemAt.TryGetValue(Market.Fold(src), out var source) ? source : -1, o.ComboYield, o.NormalYield);
            recipes.Add(r);
            recipeAt[p.Recipe.Id] = r;
            return r;
        }
        var sim = items.Select(key =>
        {
            var (main, then, combo) = book.Ways(key);
            return new SimItem(Compile(main), Compile(then), combo, key == goal ? 0 : ChainHave(book, m, key),
                combo && then is not null && main is not null ? ChainHave(book, m, Market.Fold(main.Recipe.Output.Name)) : 0);
        }).ToArray();

        var crafts = new int[recipes.Count];
        var used = new int[mats.Count];
        var left = new double[sim.Length];
        var spare = new double[sim.Length];
        var rng = new Random(20261114);
        int Try(SimRecipe r, bool withSource)
        {
            crafts[r.At]++;
            foreach (var (item, qty) in r.Links)
                for (var i = 0; i < qty; i++) Get(item);
            foreach (var (mat, qty) in r.Mats) used[mat] += qty;
            if (withSource && r.Source >= 0) Get(r.Source);
            var u = rng.NextDouble();
            return u < r.Combo ? 2 : u < r.Combo + r.Normal ? 1 : 0;
        }
        void Get(int at)
        {
            var it = sim[at];
            if (left[at] >= 1) { left[at]--; return; }
            if (it.Main is null) return;
            // La pièce normale du palier, déjà en main : le transfert la change en éclatante, sans refaire le palier.
            if (it.Then is not null && spare[at] >= 1) { spare[at]--; Try(it.Then, false); return; }
            // Rien ne peut donner l'objet (combo voulu sans chance de combo ni transfert) : pas de boucle sans fin.
            if (it.Combo ? it.Main.Combo <= 0 && (it.Then is null || it.Main.Normal <= 0) : it.Main.Combo + it.Main.Normal <= 0) return;
            for (var guard = 0; guard < 5000; guard++)
            {
                var got = Try(it.Main, true);
                if (!it.Combo) { if (got != 0) return; }
                else if (got == 2) return;
                else if (got == 1 && it.Then is not null) { Try(it.Then, false); return; }
            }
        }
        var craftRuns = new int[recipes.Count][];
        for (var i = 0; i < craftRuns.Length; i++) craftRuns[i] = new int[runs];
        var matRuns = new int[mats.Count][];
        for (var i = 0; i < matRuns.Length; i++) matRuns[i] = new int[runs];
        var totals = new int[runs];
        for (var run = 0; run < runs; run++)
        {
            Array.Clear(crafts);
            Array.Clear(used);
            for (var i = 0; i < sim.Length; i++) { left[i] = sim[i].Have; spare[i] = sim[i].Spare; }
            Get(itemAt[goal]);
            for (var i = 0; i < crafts.Length; i++) { craftRuns[i][run] = crafts[i]; totals[run] += crafts[i]; }
            for (var i = 0; i < used.Length; i++) matRuns[i][run] = used[i];
        }
        static double Nine(int[] values)
        {
            Array.Sort(values);
            return values[(int)Math.Ceiling(values.Length * 0.9) - 1];
        }
        var bill = new ChainBill();
        foreach (var r in recipes) bill.Crafts[r.Id] = Nine(craftRuns[r.At]);
        for (var i = 0; i < mats.Count; i++) bill.Needs[mats[i]] = Nine(matRuns[i]);
        return (bill, Nine(totals));
    }

    /// <summary>Une recette de la chaîne prête à être jouée : objets de la chaîne et matériaux consommés par essai, objet de départ d'un transfert, chances.</summary>
    sealed record SimRecipe(int At, int Id, (int Item, int Qty)[] Links, (int Mat, int Qty)[] Mats, int Source, double Combo, double Normal);
    /// <summary>Un objet de la chaîne prêt à être joué. Have : ce que tu en as ; Spare : ce que tu as de la pièce normale que le transfert change en cet objet.</summary>
    sealed record SimItem(SimRecipe? Main, SimRecipe? Then, bool Combo, double Have, double Spare);

    /// <summary>
    /// Les matériaux qui se fabriquent sont remplacés par leurs ingrédients : ton stock du matériau sert d'abord, le reste est
    /// fabriqué (en moyenne : compté avec la chance de réussite de sa recette), jusqu'à 4 niveaux. Une recette hors de ta maîtrise
    /// n'est pas développée : le matériau reste à trouver.
    /// </summary>
    static void Expand(ChainBook book, CraftModel m, ChainBill bill, bool lucky)
    {
        var left = new Dictionary<string, double>();
        void Raw(string key, RecipePart part, double qty, int depth)
        {
            if (qty <= 0) return;
            if (depth < 4 && book.Material.TryGetValue(key, out var p) && p.Locked is null)
            {
                if (!left.TryGetValue(key, out var have)) have = m.HaveOf(part) ?? 0;
                var take = Math.Min(qty, have);
                left[key] = have - take;
                var rest = qty - take;
                var o = OddsOf(p.Recipe, m.Mastery);
                var yes = lucky ? 1 : Math.Max(0.01, o.ComboYield + o.NormalYield);
                var crafts = rest / (Math.Max(1, p.Recipe.Output.Qty) * yes);
                var before = bill.Makes.GetValueOrDefault(key);
                bill.Makes[key] = (p, before.Need + qty, before.FromStock + take, before.Crafts + crafts);
                if (crafts > 0)
                    foreach (var (input, inputPart) in book.Inputs[p.Recipe.Id]) Raw(input, inputPart, crafts * inputPart.Qty, depth + 1);
                return;
            }
            bill.Raw[key] = bill.Raw.GetValueOrDefault(key) + qty;
        }
        foreach (var (key, qty) in bill.Needs) Raw(key, book.Parts[key], qty, 0);
    }

    /// <summary>Tout ce que la page montre pour un but : les trois comptes et, par ressource, les trois besoins et ton stock.</summary>
    sealed record ChainView(string Goal, ChainBill Best, ChainBill Mean, ChainBill Wide, double WideTotal);

    (CraftModel Model, string Goal, ChainView View)? chainView;

    ChainView ChainFor(ChainBook book, CraftModel m, string goal)
    {
        if (chainView is { } kept && ReferenceEquals(kept.Model, m) && kept.Goal == goal) return kept.View;
        var best = Spread(book, m, goal, true);
        var mean = Spread(book, m, goal, false);
        var (wide, wideTotal) = Prudent(book, m, goal);
        Expand(book, m, best, true);
        Expand(book, m, mean, false);
        Expand(book, m, wide, false);
        var view = new ChainView(goal, best, mean, wide, wideTotal);
        chainView = (m, goal, view);
        return view;
    }

    void OpenChain(string line, string goal) => update(st => { st.Screen = Screen.Craft; st.CraftPage = CraftPage.Chain; st.CraftChainLine = line; st.CraftChainGoal = goal; });

    /// <summary>La ligne d'équipement d'une recette (celle qui la contient, ou dont elle est le transfert vers l'éclatant) et le but qu'elle sert.</summary>
    (string Line, string Goal)? ChainOf(CraftModel m, CraftRecipe rc)
    {
        var book = Chains(m);
        var made = Market.Fold(rc.Output.Name);
        foreach (var line in book.Lines)
            foreach (var p in line)
            {
                var combo = p.Recipe.ComboName is { } c ? Market.Fold(c) : null;
                if (p.Recipe.Id == rc.Id) return (Market.Fold(line[0].Recipe.Output.Name), combo ?? made);
                if (combo == made) return (Market.Fold(line[0].Recipe.Output.Name), made);
            }
        return null;
    }

    static string ChainPct(double ratio) => $"{Fmt.Num(Math.Round(ratio * 1000) / 10)} %";
    static string ChainCount(double v) => v < 10 ? Fmt.Num(Math.Round(v * 10) / 10) : Fmt.Big(Math.Round(v));

    /// <summary>Une ligne pour la page d'une recette : sa chance de réussite, son combo et ce qu'il donne, et le lien vers sa chaîne.</summary>
    Control? CraftOddsLine(CraftModel m, CraftRecipe rc)
    {
        if (!rc.Official || rc.RateNormal is null && rc.Transfer is null && rc.Combo <= 0) return null;
        var o = OddsOf(rc, m.Mastery);
        var words = new List<string>();
        if (o.Transfer) words.Add($"fabrication par transfert : part de « {rc.Transfer} », ne rate pas");
        else if (o.Success is { } ok) words.Add($"réussite {ChainPct(ok)}" + (o.Known ? "" : " au niveau de la recette (ta maîtrise n'est pas renseignée)"));
        if (rc.Combo > 0)
            words.Add($"combo {rc.Combo} %" + (rc.ComboName is { } name ? $" → {name}" : " (meilleur résultat)") +
                      (o.Success is not null ? $", soit {ChainPct(o.ComboYield)} des essais" : ""));
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Tip(T(string.Join(" · ", words), 12, Tok.Text2, wrap: true).WithMargin(0, 0, 10, 0),
            "Une fabrication peut rater : tous les ingrédients sont perdus. Réussie, elle a une chance de combo, qui remplace le résultat par un meilleur ; " +
            "la fabrication combo a sa propre chance de réussir. Chances du jeu selon l'écart entre ta maîtrise et celle de la recette (relevées sur MetaBot.GG)."));
        if (ChainOf(m, rc) is { } chain)
            row.Children.Add(Clickable(T("voir toute la chaîne ›", 12, Accent.Main), () => OpenChain(chain.Line, chain.Goal), null, Tok.Hover, new Thickness(6, 2)));
        return row;
    }

    /// <summary>
    /// La page « Chaîne d'un équipement » : la pièce et le but (deux listes), où tu en es (la pièce que tu portes ou que tu as en
    /// stock marque ton palier, et le compte repart d'elle), le chemin palier par palier, puis la grille des ressources : ce qu'il
    /// faut, ton stock, ce qui manque et ce que coûte le manque, avec les totaux. Le détail (matériaux à fabriquer d'abord, règles
    /// du jeu) est replié dessous.
    /// </summary>
    Control CraftChainPage(CraftModel m, Control? right)
    {
        var book = Chains(m);
        if (book.Lines.Count == 0)
            return Page("Craft · chaîne d'un équipement", right, Card(T(
                "Les chaînes d'équipement arrivent avec les recettes officielles et leurs chances de fabrication, lues sur Aion 2 Todo. " +
                "Elles ne sont pas encore là : le site est peut-être injoignable. Relance le Builder dans un moment.", 13, Tok.Muted, wrap: true)));

        MarketItem? ItemOf(string name) => m.Book.ItemOf(new RecipePart("", name, 1)) ?? Market.ReferenceOf(name);
        string KeyOf(CraftPlan p) => Market.Fold(p.Recipe.Output.Name);
        static long Up(double v) => (long)Math.Ceiling(v - 1e-6);

        // La pièce : celle choisie ; sinon celle dont tu portes déjà un palier, puis celle dont tu en as un en stock, puis la première.
        var spots = book.Lines.Select(l => SpotOf(book, m, l)).ToList();
        var lineAt = book.Lines.FindIndex(l => KeyOf(l[0]) == s.CraftChainLine);
        if (lineAt < 0) lineAt = spots.FindIndex(x => x is { Worn: true });
        if (lineAt < 0) lineAt = spots.FindIndex(x => x is not null);
        if (lineAt < 0) lineAt = 0;
        var line = book.Lines[lineAt];
        var lineKey = KeyOf(line[0]);
        var spot = spots[lineAt];

        // Les buts de la ligne : chaque version éclatante, et les pièces Roi dragon elles-mêmes.
        var goals = new List<(string Key, string Name, CraftPlan Plan)>();
        foreach (var p in line)
        {
            var combo = p.Recipe.ComboName is { } c ? Market.Fold(c) : null;
            if (p.Recipe.Transfer is not null || combo is not null && book.MadeBy.ContainsKey(combo)) goals.Add((KeyOf(p), p.Recipe.Output.Name, p));
            if (combo is not null) goals.Add((combo, p.Recipe.ComboName!, p));
        }
        if (goals.Count == 0) goals.Add((KeyOf(line[^1]), line[^1].Recipe.Output.Name, line[^1]));
        var goalAt = goals.FindIndex(g => g.Key == s.CraftChainGoal);
        if (goalAt < 0) goalAt = goals.Count - 1;
        var goal = goals[goalAt];
        var view = ChainFor(book, m, goal.Key);
        var steps = line.Take(line.IndexOf(goal.Plan) + 1).ToList();
        var job = line[0].Recipe.Profession ?? "";
        var goalBright = goal.Key != KeyOf(goal.Plan);
        // Tu as déjà le but, ou mieux : les chiffres comptent alors une pièce de plus.
        var reached = spot is not null && (spot.Tier >= steps.Count || spot.Tier == steps.Count - 1 && (spot.Bright || !goalBright));

        // Comment compter : en moyenne (par défaut), au mieux, ou large (9 parties sur 10).
        var mode = s.CraftChainCount is "best" or "wide" ? s.CraftChainCount : "mean";
        var bill = mode == "best" ? view.Best : mode == "wide" ? view.Wide : view.Mean;
        var wide = mode == "wide";
        double CraftsOf(int id) => wide ? Math.Max(bill.Crafts.GetValueOrDefault(id), view.Mean.Crafts.GetValueOrDefault(id)) : bill.Crafts.GetValueOrDefault(id);
        double RawOf(string key) => wide ? Math.Max(bill.Raw.GetValueOrDefault(key), view.Mean.Raw.GetValueOrDefault(key)) : bill.Raw.GetValueOrDefault(key);
        var wideTotal = Math.Max(view.WideTotal, view.Mean.Total);
        var total = mode == "best" ? view.Best.Total : wide ? wideTotal : view.Mean.Total;
        string Approx(double v) => (mode == "best" ? "" : "≈ ") + ChainCount(v);

        // ----- Les choix, sur une rangée : la pièce, le but, ta maîtrise du métier, comment compter.
        string KindOf(List<CraftPlan> l) => Market.ReferenceOf(l[0].Recipe.Output.Name)?.Category is { Length: > 0 } kind ? kind : l[0].Recipe.Output.Name;
        var kinds = book.Lines.GroupBy(KindOf).ToDictionary(g => g.Key, g => g.Count());
        string LineName(List<CraftPlan> l) => kinds[KindOf(l)] > 1 ? l[0].Recipe.Output.Name : KindOf(l);
        ComboBox Pick(List<string> names, int at, Action<int> choose)
        {
            var box = new ComboBox { ItemsSource = names, SelectedIndex = at, MinWidth = 240, MaxDropDownHeight = 460, VerticalAlignment = VerticalAlignment.Center };
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && box.SelectedIndex != at) choose(box.SelectedIndex); };
            return box;
        }
        static StackPanel Named(string label, Control control) => new() { Spacing = 5, Margin = new Thickness(0, 0, 20, 6), Children = { Label(label), control } };
        var head = new WrapPanel();
        head.Children.Add(Named("Pièce", Tip(Pick(book.Lines.Select((l, i) => LineName(l) + (spots[i] is { } at ? $"   ·   tu as le palier {at.Tier + 1} sur {l.Count}" : "")).ToList(), lineAt,
            i => { var key = KeyOf(book.Lines[i][0]); update(st => { st.CraftChainLine = key; st.CraftChainGoal = null; }); }),
            "Armes, armures et bijoux qui se fabriquent par paliers. Une pièce dont tu as déjà un palier, sur toi ou en stock, l'indique.")));
        head.Children.Add(Named("Ton but", Tip(Pick(goals.Select(g => g.Name).ToList(), goalAt,
            i => { var key = goals[i].Key; update(st => { st.CraftChainLine = lineKey; st.CraftChainGoal = key; }); }),
            "Jusqu'où tu veux monter. Par défaut : le dernier palier de la pièce.")));
        if (job.Length > 0)
        {
            var known = m.Mastery.ContainsKey(job);
            var field = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { MasteryBox(m, job) } };
            if (!known) field.Children.Add(T("inconnue : chances\nles plus basses", 11, Tok.Warn).WithLineHeight(1.2));
            head.Children.Add(Named($"Ta maîtrise · {job}", Tip(field,
                $"Ton niveau en {job}, comme l'affiche la fenêtre d'artisanat du jeu : le rang (Débutant, puis Spécialiste) et le niveau de 1 à 50, puis Entrée. " +
                "« Spécialiste 50 » est le maximum. Plus ton niveau dépasse celui d'une recette, plus elle réussit. " +
                "Vide : les chances sont celles du niveau de chaque recette, les plus basses.")));
        }
        head.Children.Add(Named("Compter", Tip(Choice(new[] { ("en moyenne", "mean"), ("au mieux", "best"), ("large", "wide") }
            .Select(x => (x.Item1, mode == x.Item2, (Action)(() => update(st => st.CraftChainCount = x.Item2 == "mean" ? null : x.Item2))))),
            "En moyenne : le nombre moyen d'essais avec les chances du jeu. Au mieux : rien ne rate et chaque combo sort du premier coup. " +
            "Large : la chaîne est jouée 2 000 fois au hasard, et 9 fois sur 10 il n'en faut pas plus.")));
        var choose = Card(head, padding: new Thickness(14, 10, 14, 4));

        // ----- Les ressources : ce qu'il faut, ton stock, ce qui manque, ce que coûte le manque.
        var keys = new List<string>();
        foreach (var b in new[] { view.Mean, view.Wide, view.Best })
            foreach (var key in b.Raw.Keys)
                if (!keys.Contains(key)) keys.Add(key);
        keys.Reverse();  // le calcul part du but et descend : les ressources du premier palier d'abord
        var res = keys.Select((key, at) =>
        {
            var part = book.Parts[key];
            long need = Up(RawOf(key)), have = m.HaveOf(part) ?? 0;
            return (At: at, Part: part, Need: need, Have: have, Miss: Math.Max(0, need - have), Unit: part.Npc ?? m.Book.PriceOf(part)?.Price);
        }).Where(x => x.Need > 0).ToList();
        var missing = res.Count(x => x.Miss > 0);
        var buy = res.Sum(x => x.Unit is { } u ? x.Miss * u : 0);
        var full = res.Sum(x => x.Unit is { } u ? x.Need * u : 0);
        var unpriced = res.Where(x => x.Unit is null).ToList();

        // ----- Les chiffres clés : où tu en es, ce qu'il reste à fabriquer, ce qu'il reste à acheter.
        var where = spot is null ? "" : !spot.Worn ? "dans ton stock"
            : "équipée" + (spot.By is { } by && (m.AllStock || !string.Equals(by, r.Character.Name, StringComparison.OrdinalIgnoreCase)) ? $" sur {by}" : "");
        var kpis = new WrapPanel();
        kpis.Children.Add(Tip(Kpi("Où tu en es", spot is null ? "pas commencé" : reached ? "but atteint" : $"palier {spot.Tier + 1} sur {steps.Count}",
            spot is null ? Tok.Text2 : reached ? Tok.Good : Accent.Main, 20,
            spot is null ? "aucune pièce de cette chaîne sur toi ni dans ton stock" : $"{spot.Name}, {where}"),
            "La pièce la plus haute de cette chaîne que tu portes ou que tu as en stock (lue par le Pet Meter, ou sur ta fiche). Le compte repart d'elle : les paliers déjà passés ne sont pas refaits.")
            .WithMargin(0, 0, 10, 10));
        kpis.Children.Add(Tip(Kpi("Fabrications à faire", Approx(total), null, 20,
            mode == "best" ? "si rien ne rate et si chaque combo sort du premier coup"
            : wide ? "9 fois sur 10, il n'en faut pas plus"
            : $"au mieux {ChainCount(view.Best.Total)} · en comptant large {ChainCount(wideTotal)}"),
            "Le nombre d'essais, tous paliers confondus, pour obtenir 1 fois ton but en partant de ce que tu as : chaque palier est relancé jusqu'à ce que son combo sorte, " +
            "et chaque essai consomme la pièce éclatante du palier d'avant.").WithMargin(0, 0, 10, 10));
        kpis.Children.Add(Tip(m.HasStock
            ? Kpi("À acheter", missing == 0 ? "rien" : Short(buy), missing == 0 ? Tok.Good : null, 20,
                missing == 0 ? "ton stock couvre toutes les ressources" : $"Kinah, pour {Fmt.Plural(missing, "ressource")} qui manque{(missing > 1 ? "nt" : "")} sur {res.Count}")
            : Kpi("Tout acheter", Short(full), null, 20, "Kinah ; ton stock est inconnu sur ce PC (il faut le Pet Meter)"),
            "Ce que coûtent, aux derniers prix de l'hôtel des ventes (ou du PNJ), les ressources que ton stock ne couvre pas. Le détail est dans la grille dessous.")
            .WithMargin(0, 0, 10, 10));
        if (m.HasStock)
            kpis.Children.Add(Tip(Kpi("Prix complet", Short(full), null, 20, "Kinah, si tu achetais tout, ton stock compris"),
                "Ce que valent toutes les ressources de la chaîne aux derniers prix : ce que te coûte la pièce, en comptant aussi ce que tu sors de ton stock.")
                .WithMargin(0, 0, 10, 10));
        if (Skin.Comfort)
            pageLead = spot is null || reached
                ? new PageLead($"{goal.Name} : ", Approx(total), Tok.Text, " fabrications à faire.",
                    reached ? $"Tu as déjà {spot!.Name} : les chiffres comptent une pièce de plus." : "Tu n'as encore aucune pièce de cette chaîne : le compte part du premier palier.", []) { Fold = false }
                : new PageLead("Tu es au ", $"palier {spot.Tier + 1} sur {steps.Count}", Accent.Main, $" : il te reste {Approx(total)} fabrications.",
                    $"Tu as {spot.Name} ({where}). Ton but : {goal.Name}." + (m.HasStock && missing > 0 ? $" Ressources à acheter : {Short(buy)} Kinah." : ""), []) { Fold = false };

        // ----- Ton chemin : un palier par ligne, du premier au but ; le palier où tu es est entouré.
        const string pathCols = "22,*,265,130";
        // Ce qu'un palier à venir demande en plus de la pièce dont il part (une autre pièce éclatante d'un palier déjà passé).
        var extra = steps.Where((p, i) => spot is not null && i > spot.Tier && p.Recipe.Transfer is not null)
            .SelectMany(p => book.Inputs[p.Recipe.Id].Where(x => book.IsLink(x.Key)).Select(x => (By: p.Recipe.Output.Name, What: x.Part.Name))).ToList();
        static Border Dot(IBrush color, bool filled, double size) => new()
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = filled ? color : Tok.Panel, BorderBrush = color,
            BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var pathRows = new List<Control>();
        var redo = false;
        for (var i = 0; i < steps.Count; i++)
        {
            var p = steps[i];
            var rc = p.Recipe;
            var o = OddsOf(rc, m.Mastery);
            var last = i == steps.Count - 1;
            // Ce palier doit donner sa version éclatante (le but peut être la pièce Roi dragon elle-même).
            var bright = rc.ComboName is not null && (!last || goalBright);
            var then = bright ? book.Ways(Market.Fold(rc.ComboName!)).Then : null;
            double n = CraftsOf(rc.Id), t = then is null ? 0 : CraftsOf(then.Recipe.Id);
            var todo = n > 0 || t > 0;
            var here = spot is not null && spot.Tier == i;
            var passed = spot is not null && i <= spot.Tier;
            var again = todo && passed && (!here || spot!.Bright);
            redo |= again;

            // Le repère sur le rail : fait (plein, vert), tu es ici (plein, couleur d'accent), à refaire (creux, orange), à faire (creux).
            var mark = here ? Dot(Accent.Main, true, 14) : again ? Dot(Tok.Warn, false, 11) : passed ? Dot(Tok.Good, true, 9) : Dot(last ? Accent.Main : Tok.Muted, false, 11);
            Border Rail(bool show, bool done) => new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Background = done ? Tok.Alpha(Tok.Good, 0.55) : Tok.Border, IsVisible = show };
            var up = Rail(i > 0, passed);
            var down = Rail(!last, spot is not null && i < spot.Tier);
            Grid.SetRow(down, 1);
            Grid.SetRowSpan(mark, 2);
            var rail = new Grid { RowDefinitions = new RowDefinitions("*,*"), Children = { up, down, mark } };

            var names = new WrapPanel();
            names.Children.Add(MarketItemLine(ItemOf(rc.Output.Name), rc.Output.Name, 13));
            if (bright)
            {
                names.Children.Add(T("→", 12, Tok.Muted).WithMargin(8, 0, 8, 0));
                names.Children.Add(T(rc.ComboName!, 13, MarketBrush(ItemOf(rc.ComboName!)?.GameGrade), FontWeight.Medium));
            }
            if (here) names.Children.Add(Badge("tu es ici", Accent.Main).WithMargin(10, 0, 0, 0));
            if (last && !reached) names.Children.Add(Badge("ton but", Tok.Text2).WithMargin(10, 0, 0, 0));
            foreach (var child in names.Children) child.VerticalAlignment = VerticalAlignment.Center;
            var cell = new StackPanel { Spacing = 3, Margin = new Thickness(0, 9), VerticalAlignment = VerticalAlignment.Center, Children = { names } };
            if (here)
                cell.Children.Add(T($"{spot!.Name}, {where}" + (!spot.Bright && bright ? " : il te manque sa version éclatante" : ""), 11, Tok.Text2, wrap: true).WithMargin(11, 0, 0, 0));
            if (again) cell.Children.Add(T("à refaire : il en faut une de plus", 11, Tok.Warn, wrap: true).WithMargin(11, 0, 0, 0));
            var mine = m.Mastery.TryGetValue(rc.Profession ?? "", out var lv) ? lv : (int?)null;
            if (todo && n > 0 && !o.Transfer && mine is { } my && rc.Mastery is { } needed && my < needed)
                cell.Children.Add(T($"{rc.Profession} {MasteryName(needed)} demandé, tu es {MasteryName(my)} : hors de portée", 11, Tok.Bad, wrap: true).WithMargin(11, 0, 0, 0));
            ToolTip.SetTip(names, "Par essai : " + (rc.Transfer is { } source ? $"{source} (la pièce de départ), " : "") +
                string.Join(", ", rc.Inputs.Select(x => x.Qty > 1 ? $"{x.Qty} {x.Name}" : x.Name)) + "." +
                (o.Transfer || rc.Mastery is not { } asked ? "" : $" Maîtrise demandée : {rc.Profession}, {MasteryName(asked)}."));

            Control chance, tries;
            if (!todo)
            {
                chance = passed ? T("fait", 12, Tok.Good, mono: true) : Tip(T("couvert", 12, Tok.Text2, mono: true), "Aucun essai à faire : ce que tu as déjà couvre ce palier.");
                tries = T("", 12);
            }
            else
            {
                // Tu as déjà la pièce normale de ce palier : seul le transfert vers l'éclatante reste.
                var onlyTransfer = n <= 0 || o.Transfer;
                var top = onlyTransfer ? T("transfert : ne rate pas", 12, Tok.Good, mono: true)
                    : o.Success is { } ok ? T($"réussite {ChainPct(ok)}", 12, ok >= 0.9 ? Tok.Good : ok >= 0.8 ? Tok.Text : Tok.Warn, FontWeight.SemiBold, mono: true)
                    : T("réussite inconnue", 12, Tok.Faint, mono: true);
                var odds = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { top } };
                if (bright && rc.Combo > 0 && n > 0)
                    odds.Children.Add(T($"éclatante : {(o.Transfer ? $"{rc.Combo} %" : ChainPct(o.ComboYield))}{(then is not null ? ", sinon transfert" : "")}", 11, Tok.Muted, mono: true));
                chance = Tip(odds, n <= 0 ? "Un transfert refond la pièce que tu as déjà en sa version éclatante : il réussit toujours."
                    : o.Transfer ? "Une fabrication par transfert refond la pièce que tu as déjà : elle réussit toujours et garde ses améliorations." +
                                   (bright && rc.Combo > 0 ? $" Une fois sur {Fmt.Num(100.0 / rc.Combo)}, elle donne directement la version éclatante" + (then is not null ? " ; sinon un second transfert la donne." : ".") : "")
                    : "Chance que la fabrication réussisse. Ratée, tous ses ingrédients sont perdus, la pièce éclatante du palier d'avant comprise." +
                      (bright && rc.Combo > 0 ? $" Réussie, elle a {rc.Combo} % de chances de combo ; le combo est une fabrication à part, qui peut rater à son tour : " +
                          $"{ChainPct(o.Success ?? 1)} × {rc.Combo} % × {ChainPct(o.ComboSuccess ?? o.Success ?? 1)} = {ChainPct(o.ComboYield)} des essais donnent l'éclatante." +
                          (then is not null ? " Si le combo ne sort pas, un transfert change la pièce normale en éclatante." : "") : "") +
                      (o.Known ? "" : " Ta maîtrise n'est pas renseignée : ce sont les chances les plus basses."));
                var count = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                if (n > 0) count.Children.Add(T(Approx(n), 14, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight());
                if (t > 0) count.Children.Add(T($"{(n > 0 ? "+ " : "")}{Approx(t)} transfert{(t >= 2 ? "s" : "")}", n > 0 ? 11 : 13, n > 0 ? Tok.Muted : Tok.Text, n > 0 ? FontWeight.Normal : FontWeight.SemiBold, mono: true).AlignRight());
                tries = count;
            }
            chance.VerticalAlignment = VerticalAlignment.Center;

            var id = rc.Id;
            var row = Clickable(Columns(pathCols, rail, cell, chance, tries), () => OpenRecipe(id), here ? Accent.Dim : null, here ? null : Tok.Hover, new Thickness(here ? 13 : 20, 0));
            if (here)
            {
                row.BorderBrush = Accent.Main;
                row.BorderThickness = new Thickness(1);
                row.CornerRadius = new CornerRadius(Skin.Radius);
                row.Margin = new Thickness(6, 0);
            }
            if (passed && !todo && !here) cell.Opacity = 0.6;
            pathRows.Add(row);
        }
        static Border Band(string text, IBrush color) => new()
        {
            Padding = new Thickness(20, 8), Background = Tok.Alpha(color, 0.09), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = T(text, 12, Tok.Text, wrap: true).WithLineHeight(1.4),
        };
        if (redo && extra.Count > 0)
        {
            var askers = extra.Select(x => $"« {x.By} »").Distinct().ToList();
            pathRows.Insert(0, Band($"{string.Join(" et ", askers)} {(askers.Count > 1 ? "demandent chacun" : "demande")} aussi une autre « {extra[0].What} » : " +
                                    "les paliers marqués « à refaire » sont à repasser pour elle.", Tok.Warn));
        }
        if (reached)
            pathRows.Insert(0, Band($"Tu as déjà {spot!.Name} ({where}) : ce but est atteint. Les chiffres comptent une pièce de plus.", Tok.Good));
        var pathCard = TableCard($"Ton chemin · {KindOf(line)}", T("clic sur un palier : sa recette", 11, Tok.Muted, mono: true),
            TableHeader(pathCols, "", "Palier → version éclatante, que demande le palier suivant", "Chance", "Essais"), pathRows);

        // ----- La grille des ressources, tous paliers additionnés : ce qui manque d'abord (le plus cher en tête), puis ce que tu as.
        const string cols = "*,110,110,110,170";
        static TextBlock Cell(string text, IBrush color, bool bold = false) => T(text, 13, color, bold ? FontWeight.SemiBold : FontWeight.Normal, mono: true).AlignRight();
        var resRows = new List<Control>();
        foreach (var x in res.OrderBy(x => x.Miss > 0 ? 0 : 1).ThenByDescending(x => x.Miss > 0 ? x.Unit is { } u ? x.Miss * u : -1 : 0).ThenBy(x => x.At))
        {
            var name = x.Part.Name;
            var on = m.WhereOf(x.Part);
            Control cost = x.Miss == 0 ? Cell("—", Tok.Faint)
                : x.Unit is { } u ? Tip(Cell(Fmt.Big(x.Miss * u), Tok.Text, true), $"{Fmt.Big(x.Miss)} × {Fmt.Big(u)} Kinah pièce, au {(x.Part.Npc is not null ? "prix du PNJ" : "dernier prix de l'hôtel des ventes")}.")
                : Tip(Cell("pas de prix", Tok.Faint), "Aucun prix noté pour cette ressource : elle ne se vend peut-être pas (objet lié), ou personne ne l'a encore relevée. Elle n'est pas comptée dans les totaux.");
            var row = TableRow(Columns(cols, MarketItemLine(m.Book.ItemOf(x.Part) ?? Market.ReferenceOf(name), name, 13),
                Cell(Fmt.Big(x.Need), Tok.Text, true),
                !m.HasStock ? Cell("—", Tok.Faint) : Tip(Cell(Fmt.Big(x.Have), x.Have > 0 ? Tok.Text2 : Tok.Faint), on is null ? "Dans ton stock (sac, coffre, rangements)." : $"Dans ton stock : {on}."),
                x.Miss == 0 ? Cell("✓", Tok.Good, true) : Cell(Fmt.Big(x.Miss), Tok.Bad, true),
                cost), false, () => OpenUses(name));
            resRows.Add(new Border { BorderBrush = Tok.Alpha(Tok.Border, 0.6), BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
        }
        Border Foot(string label, string note, string value, IBrush color, double size) => new()
        {
            Padding = new Thickness(20, 9), Background = Tok.Chrome, BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Columns("*,Auto", new StackPanel { Spacing = 2, Children = { T(label, 13, Tok.Text, FontWeight.SemiBold), T(note, 11, Tok.Muted, wrap: true) } },
                T(value, size, color, FontWeight.SemiBold, mono: true).AlignRight()),
        };
        var counted = mode == "best" ? "au mieux" : wide ? "en comptant large" : "en moyenne";
        resRows.Add(Foot("Total à acheter",
            !m.HasStock ? "ton stock est inconnu sur ce PC : tout est compté à acheter"
            : missing == 0 ? "ton stock couvre toutes les ressources" : $"{Fmt.Plural(missing, "ressource")} sur {res.Count} à compléter, aux derniers prix",
            $"{Fmt.Big(buy)} Kinah", missing == 0 && m.HasStock ? Tok.Good : Tok.Text, 16));
        if (m.HasStock)
            resRows.Add(Foot("Prix complet de la pièce", "si tu achetais toutes les ressources, celles de ton stock comprises", $"{Fmt.Big(full)} Kinah", Tok.Text2, 14));
        if (m.Book.PriceOf(new RecipePart("", goal.Name, 1))?.Price is { } sold)
            resRows.Add(Foot($"« {goal.Name} » à l'hôtel des ventes", "dernier prix relevé de la pièce finie, pour comparer", $"{Fmt.Big(sold)} Kinah", Tok.Text2, 14));
        if (unpriced.Count > 0)
            resRows.Add(T($"Sans prix noté, donc hors des totaux : {string.Join(", ", unpriced.Select(x => x.Part.Name))}.", 11, Tok.Muted, wrap: true).WithMargin(20, 8, 20, 10));
        var resHead = new Border
        {
            Padding = new Thickness(20, 8), BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Columns(cols, Label("Ressource"), Label("Il faut").AlignRight(), Label("En stock").AlignRight(), Label("Manque").AlignRight(), Label("Coût du manque").AlignRight()),
        };
        var resCard = TableCard($"Ce qu'il te faut · {Fmt.Plural(res.Count, "ressource")}, {counted}",
            m.Who is null ? T("stock inconnu sur ce PC : il faut le Pet Meter", 11, Tok.Muted, mono: true)
                : Tip(T((m.AllStock ? "stock de tous tes persos" : $"stock de {m.Who.Character}") + $", relevé {Market.Since(m.Who.At)}", 11,
                        DateTime.UtcNow - m.Who.At > TimeSpan.FromDays(1) ? Tok.Warn : Tok.Muted, mono: true),
                    "Le Pet Meter relève ton sac et ton coffre quand tu entres en jeu, puis à chaque changement. Lancé après ton entrée en jeu, il ne relève rien : " +
                    "le stock affiché est alors celui de son dernier relevé sur ce PC."),
            resHead, resRows);

        // ----- Le détail, replié : les matériaux à fabriquer d'abord, puis les règles du jeu.
        Border? makeCard = null;
        if (bill.Makes.Count > 0)
        {
            const string makeCols = "*,110,110,130,150";
            var makeRows = new List<Control>();
            foreach (var (_, make) in bill.Makes)
            {
                var rc = make.Plan.Recipe;
                var o = OddsOf(rc, m.Mastery);
                var id = rc.Id;
                makeRows.Add(TableRow(Columns(makeCols, MarketItemLine(make.Plan.Item ?? Market.ReferenceOf(rc.Output.Name), rc.Output.Name, 13),
                    T(Fmt.Big(Up(make.Need)), 13, Tok.Text, mono: true).AlignRight(),
                    m.HasStock ? T(Fmt.Big(make.FromStock), 13, make.FromStock > 0 ? Tok.Text : Tok.Faint, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight(),
                    T(make.Crafts > 0 ? Approx(make.Crafts) : "0", 13, make.Crafts > 0 ? Tok.Text : Tok.Faint, FontWeight.SemiBold, mono: true).AlignRight(),
                    T($"{rc.Profession} {rc.Mastery}" + (o.Success is { } ok ? $" · {ChainPct(ok)}" : ""), 12, Tok.Text2, mono: true)), false, () => OpenRecipe(id)));
            }
            makeCard = TableCard($"Matériaux à fabriquer d'abord · {bill.Makes.Count}", T("leurs ingrédients sont déjà dans la grille ci-dessus", 11, Tok.Muted, mono: true),
                TableHeader(makeCols, "Matériau", "Besoin", "Pris au stock", "Fabrications", "Recette"), makeRows);
            Fold(makeCard, "Matériaux à fabriquer d'abord");
        }

        var rules = new StackPanel { Spacing = 6 };
        foreach (var text in new[]
                 {
                     "Une fabrication peut rater : tous ses ingrédients sont perdus, la pièce éclatante du palier d'avant comprise. Plus ta maîtrise dépasse celle de la recette, plus elle réussit.",
                     "Réussie, elle a 25 % de chances de combo. Le combo donne la version « éclatante », du rang au-dessus (blanc → vert, vert → bleu, bleu → jaune). C'est une fabrication à part, qui peut rater à son tour.",
                     "La recette du palier suivant demande cette version éclatante : sans combo, la pièce normale ne sert pas à monter.",
                     "À partir du Roi dragon, on avance par transfert : la recette refond la pièce que tu as déjà, garde ses améliorations et ne rate pas. Si le combo d'un palier Roi dragon ne sort pas, un transfert donne quand même sa version éclatante, pour quelques matériaux de plus.",
                     "Ne vends pas et ne démonte pas une pièce éclatante : il faudrait retenter le combo.",
                 })
            rules.Children.Add(T("· " + text, 12, Tok.Text2, wrap: true).WithLineHeight(1.45));
        var link = Clickable(T("chances du jeu relevées sur MetaBot.GG ↗", 11, Accent.Main, mono: true), () => OpenLink("https://metabot.gg/fr_FR/aion-2/crafting"), null, Tok.Hover, new Thickness(6, 2));
        link.HorizontalAlignment = HorizontalAlignment.Left;
        rules.Children.Add(Tip(link, "Chance de combo et courbes de réussite de chaque recette, lues dans le client du jeu par MetaBot.GG (site de fans). " +
                                     "L'ordre des transferts (quelle pièce part vers quel palier) est déduit de l'ordre des paliers : le jeu ne le donne pas dans la recette."));
        var how = Card(rules, "Comment marche la fabrication par paliers");
        Fold(how, "Comment marche la fabrication par paliers");

        return Page("Craft · chaîne d'un équipement", right, choose, kpis, pathCard, resCard, makeCard, how);
    }
}
