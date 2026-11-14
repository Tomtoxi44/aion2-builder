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
/// passent ensuite par des fabrications par transfert, qui partent de la pièce du palier d'avant et ne ratent pas. La page compte,
/// pour un but, les essais de chaque recette (au mieux, en moyenne, et en comptant large) et les ressources, comparées à ton stock.
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

    /// <summary>Stock d'un objet de la chaîne (une version éclatante déjà dans ton sac compte).</summary>
    static double ChainHave(ChainBook book, CraftModel m, string key) =>
        m.HaveOf(book.Parts.TryGetValue(key, out var part) ? part : new RecipePart("", book.Names.GetValueOrDefault(key, key), 1)) ?? 0;

    /// <summary>
    /// Les essais de chaque recette pour un but. lucky : au mieux (rien ne rate, chaque combo sort du premier coup) ; sinon en
    /// moyenne : une recette dont on veut le combo est relancée 1 / (réussite × combo × réussite du combo) fois, et, quand un
    /// transfert rattrape le résultat normal, 1 / (chance d'obtenir l'un ou l'autre) fois plus la part de transferts.
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
            return new SimItem(Compile(main), Compile(then), combo, key == goal ? 0 : ChainHave(book, m, key));
        }).ToArray();

        var crafts = new int[recipes.Count];
        var used = new int[mats.Count];
        var left = new double[sim.Length];
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
            for (var i = 0; i < sim.Length; i++) left[i] = sim[i].Have;
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
    sealed record SimItem(SimRecipe? Main, SimRecipe? Then, bool Combo, double Have);

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

    /// <summary>La page « Chaîne d'un équipement ».</summary>
    Control CraftChainPage(CraftModel m, Control? right)
    {
        var book = Chains(m);
        if (book.Lines.Count == 0)
            return Page("Craft · chaîne d'un équipement", right, Card(T(
                "Les chaînes d'équipement arrivent avec les recettes officielles et leurs chances de fabrication, lues sur Aion 2 Todo. " +
                "Elles ne sont pas encore là : le site est peut-être injoignable. Relance le Builder dans un moment.", 13, Tok.Muted, wrap: true)));

        MarketItem? ItemOf(string name) => m.Book.ItemOf(new RecipePart("", name, 1)) ?? Market.ReferenceOf(name);
        string KeyOf(CraftPlan p) => Market.Fold(p.Recipe.Output.Name);
        var line = book.Lines.FirstOrDefault(l => KeyOf(l[0]) == s.CraftChainLine) ?? book.Lines[0];
        var lineKey = KeyOf(line[0]);

        // Les buts de la ligne : chaque version éclatante, et les pièces Roi dragon elles-mêmes.
        var goals = new List<(string Key, string Name, CraftPlan Plan)>();
        foreach (var p in line)
        {
            var combo = p.Recipe.ComboName is { } c ? Market.Fold(c) : null;
            if (p.Recipe.Transfer is not null || combo is not null && book.MadeBy.ContainsKey(combo)) goals.Add((KeyOf(p), p.Recipe.Output.Name, p));
            if (combo is not null) goals.Add((combo, p.Recipe.ComboName!, p));
        }
        if (goals.Count == 0) goals.Add((KeyOf(line[^1]), line[^1].Recipe.Output.Name, line[^1]));
        var goal = goals.FirstOrDefault(g => g.Key == s.CraftChainGoal);
        if (goal.Key is null) goal = goals[^1];
        var view = ChainFor(book, m, goal.Key);
        var steps = line.Take(line.IndexOf(goal.Plan) + 1).ToList();
        var job = line[0].Recipe.Profession ?? "";

        // Quelle pièce, puis jusqu'où.
        string KindOf(List<CraftPlan> l) => Market.ReferenceOf(l[0].Recipe.Output.Name)?.Category is { Length: > 0 } kind ? kind : l[0].Recipe.Output.Name;
        var kinds = book.Lines.GroupBy(KindOf).ToDictionary(g => g.Key, g => g.Count());
        var pick = Choice(book.Lines.Select(l =>
        {
            var key = KeyOf(l[0]);
            return (kinds[KindOf(l)] > 1 ? l[0].Recipe.Output.Name : KindOf(l), key == lineKey, (Action)(() => update(st => { st.CraftChainLine = key; st.CraftChainGoal = null; })));
        }), wrap: true);
        var until = new WrapPanel();
        foreach (var g in goals)
        {
            var key = g.Key;
            var on = key == goal.Key;
            var chip = Clickable(MarketItemLine(ItemOf(g.Name), g.Name, 12), () => update(st => { st.CraftChainLine = lineKey; st.CraftChainGoal = key; }),
                on ? Accent.Dim : Tok.Panel, on ? null : Tok.Hover, new Thickness(8, 4));
            chip.CornerRadius = new CornerRadius(Skin.Radius);
            chip.BorderBrush = on ? Accent.Main : Tok.Border;
            chip.BorderThickness = new Thickness(1);
            until.Children.Add(chip.WithMargin(0, 0, 6, 6));
        }
        var choose = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Spacing = 6, Children = { Label("Quelle pièce"), pick } },
                new StackPanel { Spacing = 6, Children = { Label("Jusqu'où · ton but"), until } },
            },
        });

        // Les chiffres clés.
        var first = steps[0];
        var mean = view.Mean.Total;
        var kpis = new WrapPanel();
        kpis.Children.Add(Tip(Kpi("Fabrications en moyenne", "≈ " + ChainCount(mean), null, 22,
            $"dont {ChainCount(view.Mean.Crafts.GetValueOrDefault(first.Recipe.Id))} × {first.Recipe.Output.Name}"),
            "Le nombre moyen d'essais, tous paliers confondus, pour obtenir 1 fois ton but : chaque palier est relancé jusqu'à ce que son combo sorte, " +
            "et chaque essai consomme la pièce éclatante du palier d'avant.").WithMargin(0, 0, 10, 10));
        kpis.Children.Add(Tip(Kpi("Au mieux", ChainCount(view.Best.Total), Tok.Good, 22, "si rien ne rate et si chaque combo sort du premier coup"),
            "Le minimum possible : une fabrication par palier.").WithMargin(0, 0, 10, 10));
        kpis.Children.Add(Tip(Kpi("En comptant large", ChainCount(view.WideTotal), Tok.Warn, 22, "9 fois sur 10, il n'en faut pas plus"),
            "La chaîne est jouée 2 000 fois au hasard avec les chances du jeu : dans 9 parties sur 10, le but est atteint avec ce nombre d'essais ou moins. " +
            "Une fois sur 10, il en faut davantage.").WithMargin(0, 0, 10, 10));

        // Les ressources : besoin au mieux, en moyenne, en comptant large, ton stock.
        var order = new List<string>();
        foreach (var bill in new[] { view.Mean, view.Wide, view.Best })
            foreach (var key in bill.Raw.Keys)
                if (!order.Contains(key)) order.Add(key);
        order.Reverse();
        var rows = order.Select(key =>
        {
            var part = book.Parts[key];
            return (Key: key, Part: part, Best: view.Best.Raw.GetValueOrDefault(key), Mean: view.Mean.Raw.GetValueOrDefault(key),
                Wide: Math.Max(view.Wide.Raw.GetValueOrDefault(key), view.Mean.Raw.GetValueOrDefault(key)), Have: m.HaveOf(part) ?? 0);
        }).ToList();
        static long Up(double v) => (long)Math.Ceiling(v - 1e-6);
        var shortMean = rows.Count(x => x.Have < Up(x.Mean));
        var shortWide = rows.Count(x => x.Have < Up(x.Wide));
        if (m.HasStock)
            kpis.Children.Add(Tip(Kpi("Ton stock", shortMean == 0 ? shortWide == 0 ? "tout y est" : "ça passe" : $"il manque {Fmt.Plural(shortMean, "ressource")}",
                shortMean == 0 ? shortWide == 0 ? Tok.Good : Tok.Warn : Tok.Bad, 18,
                shortMean > 0 ? $"sur {rows.Count}, pour le besoin moyen" : shortWide > 0 ? $"en moyenne oui ; en comptant large, {Fmt.Plural(shortWide, "ressource")} à compléter" : "même en comptant large"),
                "Ton stock comparé à ce que la chaîne demande, du premier palier à ton but. Le détail est dans le tableau des ressources.").WithMargin(0, 0, 10, 10));

        // Les paliers, du blanc au but.
        const string stepCols = "*,120,80,130,190";
        var stepRows = new List<Control>();
        Control Count(CraftPlan p)
        {
            var id = p.Recipe.Id;
            var n = view.Mean.Crafts.GetValueOrDefault(id);
            if (n <= 0) return Tip(T("0", 13, Tok.Faint, mono: true).AlignRight(), "Aucun essai à faire : ton stock couvre déjà ce que ce palier fournit.");
            return new StackPanel
            {
                Children =
                {
                    T("≈ " + ChainCount(n), 14, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                    T($"mieux {ChainCount(view.Best.Crafts.GetValueOrDefault(id))} · large {ChainCount(Math.Max(n, view.Wide.Crafts.GetValueOrDefault(id)))}", 11, Tok.Muted, mono: true).AlignRight(),
                },
            };
        }
        string Inputs(CraftPlan p) => string.Join(", ", p.Recipe.Inputs.Select(i => i.Qty > 1 ? $"{i.Qty} {i.Name}" : i.Name));
        void Step(CraftPlan p, bool rescue)
        {
            var rc = p.Recipe;
            var o = OddsOf(rc, m.Mastery);
            var name = new StackPanel { Spacing = 2, Children = { MarketItemLine(ItemOf(rc.Output.Name), rc.Output.Name, 13) } };
            if (rescue) name.Children.Insert(0, T("si le combo ne sort pas, le transfert le rattrape :", 11, Tok.Muted, wrap: true).WithMargin(11, 0, 0, 0));
            if (rc.ComboName is { } result)
                name.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(11, 0, 0, 0),
                    Children = { T("combo →", 11, Tok.Muted, mono: true), T(result, 12, MarketBrush(ItemOf(result)?.GameGrade), FontWeight.Medium, wrap: true) },
                });
            name.Children.Add(T("par essai : " + (rc.Transfer is { } source ? $"{source} (la pièce de départ), " : "") + Inputs(p), 11, Tok.Muted, wrap: true).WithMargin(11, 0, 0, 0));

            Control mastery;
            if (o.Transfer) mastery = T("transfert", 12, Tok.Text2, mono: true);
            else
            {
                var mine = m.Mastery.TryGetValue(rc.Profession ?? "", out var lv) ? lv : (int?)null;
                var low = mine is { } have && rc.Mastery is { } need && have < need;
                mastery = new StackPanel
                {
                    Children =
                    {
                        T($"{rc.Profession} {rc.Mastery}", 12, low ? Tok.Bad : Tok.Text2, mono: true),
                        T(mine is { } my ? low ? $"tu as {my} : hors de portée" : $"tu as {my}" : "la tienne : inconnue", 11, low ? Tok.Bad : Tok.Muted, wrap: true),
                    },
                };
            }
            Control success = o.Transfer ? Tip(T("ne rate pas", 12, Tok.Good, mono: true).AlignRight(), "Une fabrication par transfert refond la pièce que tu as déjà : elle réussit toujours.")
                : o.Success is { } ok ? Tip(T(ChainPct(ok), 13, ok >= 0.9 ? Tok.Good : ok >= 0.8 ? Tok.Text : Tok.Warn, FontWeight.SemiBold, mono: true).AlignRight(),
                    "Chance que la fabrication réussisse. Ratée, tous ses ingrédients sont perdus, la pièce éclatante du palier d'avant comprise.")
                : Tip(T("?", 13, Tok.Faint, mono: true).AlignRight(), "Le jeu ne donne pas la chance de réussite de cette recette : comptée à 100 %.");
            Control combo = rc.Combo <= 0 ? T("—", 12, Tok.Faint, mono: true).AlignRight()
                : Tip(new StackPanel
                {
                    Children =
                    {
                        T($"{rc.Combo} %", 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                        T($"{ChainPct(o.ComboYield)} des essais", 11, Tok.Muted, mono: true).AlignRight(),
                    },
                }, o.Transfer ? $"Une fois sur {Fmt.Num(100.0 / rc.Combo)}, le transfert donne directement la version éclatante."
                    : $"Chance de combo après une fabrication réussie. Le combo est une fabrication à part, qui peut rater à son tour : " +
                      $"réussite {ChainPct(o.Success ?? 1)} × combo {rc.Combo} % × réussite du combo {ChainPct(o.ComboSuccess ?? o.Success ?? 1)} = {ChainPct(o.ComboYield)} des essais donnent l'éclatant.");
            var id = rc.Id;
            var count = Count(p);
            foreach (var cell in new[] { mastery, success, combo, count }) cell.VerticalAlignment = VerticalAlignment.Top;
            stepRows.Add(TableRow(Columns(stepCols, name, mastery, success, combo, count), false, () => OpenRecipe(id)));
        }
        foreach (var p in steps)
        {
            Step(p, false);
            if (p.Recipe.ComboName is { } result && book.Ways(Market.Fold(result)).Then is { } then && view.Mean.Crafts.GetValueOrDefault(then.Recipe.Id) > 0)
                Step(then, true);
        }
        var stepCard = TableCard($"Les paliers · de « {first.Recipe.Output.Name} » à « {goal.Name} »",
            T("clic : la recette", 11, Tok.Muted, mono: true),
            TableHeader(stepCols, "Tu fabriques", "Maîtrise", "Réussite", "Combo", "Essais"), stepRows);

        // Le tableau des ressources.
        const string cols = "*,90,100,100,100,150";
        long buy = 0;
        var noPrice = 0;
        var resRows = new List<Control>();
        foreach (var x in rows)
        {
            long best = Up(x.Best), need = Up(x.Mean), wide = Up(x.Wide), have = (long)x.Have;
            var unit = x.Part.Npc ?? m.Book.PriceOf(x.Part)?.Price;
            if (have < need) { if (unit is { } u) buy += (need - have) * u; else noPrice++; }
            Control state = !m.HasStock ? T("—", 12, Tok.Faint, mono: true).AlignRight()
                : have >= wide ? Tip(T("assez", 12, Tok.Good, FontWeight.SemiBold, mono: true).AlignRight(), "Ton stock couvre même le besoin compté large.")
                : have >= need ? Tip(T($"assez en moyenne", 12, Tok.Warn, mono: true).AlignRight(), $"Pour être tranquille (9 fois sur 10), il en faudrait {Fmt.Big(wide - have)} de plus.")
                : Tip(T($"manque {Fmt.Big(need - have)}", 12, Tok.Bad, FontWeight.SemiBold, mono: true).AlignRight(),
                    $"Pour le besoin moyen. En comptant large, il en manque {Fmt.Big(wide - have)}." + (unit is { } u2 ? $" Prix noté : {Fmt.Big(u2)} Kinah pièce." : " Pas de prix noté."));
            var where = m.WhereOf(x.Part);
            var name = x.Part.Name;
            resRows.Add(TableRow(Columns(cols, MarketItemLine(m.Book.ItemOf(x.Part) ?? Market.ReferenceOf(name), name, 13),
                T(Fmt.Big(best), 13, Tok.Text2, mono: true).AlignRight(),
                T(Fmt.Big(need), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                T(Fmt.Big(wide), 13, Tok.Text2, mono: true).AlignRight(),
                !m.HasStock ? T("—", 12, Tok.Faint, mono: true).AlignRight()
                    : Tip(T(Fmt.Big(have), 13, have > 0 ? Tok.Text : Tok.Faint, mono: true).AlignRight(), where is null ? "Dans ton stock." : $"Dans ton stock : {where}."),
                state), false, () => OpenUses(name)));
        }
        if (m.HasStock && (buy > 0 || noPrice > 0))
            resRows.Add(T((buy > 0 ? $"Ce qui manque pour le besoin moyen coûte environ {Fmt.Big(buy)} Kinah aux prix notés" : "Ce qui manque n'a pas de prix noté") +
                          (buy > 0 && noPrice > 0 ? $" ({Fmt.Plural(noPrice, "ressource")} sans prix non comptée{(noPrice > 1 ? "s" : "")})." : "."), 11, Tok.Muted, wrap: true).WithMargin(20, 8, 20, 10));
        var resCard = TableCard($"Les ressources, tous paliers additionnés · {rows.Count}",
            T(m.AllStock ? "stock de tous tes persos" : m.Who is null ? "stock inconnu sur ce PC : il faut le Pet Meter" : $"stock de {m.Who.Character}", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Ressource", "Au mieux", "En moyenne", "Large", "Ton stock", "Verdict"), resRows);

        // Les matériaux à fabriquer d'abord.
        Control? makeCard = null;
        if (view.Mean.Makes.Count > 0)
        {
            const string makeCols = "*,110,110,130,150";
            var makeRows = new List<Control>();
            foreach (var (key, make) in view.Mean.Makes)
            {
                var rc = make.Plan.Recipe;
                var o = OddsOf(rc, m.Mastery);
                var id = rc.Id;
                makeRows.Add(TableRow(Columns(makeCols, MarketItemLine(make.Plan.Item ?? Market.ReferenceOf(rc.Output.Name), rc.Output.Name, 13),
                    T(Fmt.Big(Up(make.Need)), 13, Tok.Text, mono: true).AlignRight(),
                    m.HasStock ? T(Fmt.Big(make.FromStock), 13, make.FromStock > 0 ? Tok.Text : Tok.Faint, mono: true).AlignRight() : T("—", 12, Tok.Faint, mono: true).AlignRight(),
                    T(make.Crafts > 0 ? "≈ " + ChainCount(make.Crafts) : "0", 13, make.Crafts > 0 ? Tok.Text : Tok.Faint, FontWeight.SemiBold, mono: true).AlignRight(),
                    T($"{rc.Profession} {rc.Mastery}" + (o.Success is { } ok ? $" · {ChainPct(ok)}" : ""), 12, Tok.Text2, mono: true)), false, () => OpenRecipe(id)));
            }
            makeCard = TableCard("À fabriquer d'abord", T("leurs ingrédients sont déjà dans les ressources ci-dessus", 11, Tok.Muted, mono: true),
                TableHeader(makeCols, "Matériau", "Besoin moyen", "Pris au stock", "Fabrications", "Recette"), makeRows);
        }

        // Comment ça marche, en clair.
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
        var how = Card(rules, "Comment ça marche");

        var unknown = !steps.Any(p => p.Recipe.Transfer is null && OddsOf(p.Recipe, m.Mastery).Known);
        var hint = T((unknown && job.Length > 0 ? $"Ta maîtrise en {job} n'est pas renseignée : les chances sont celles du niveau de chaque recette, les plus basses. Tape-la ci-dessous pour les vraies. " : "") +
                     "Les nombres sont pour obtenir 1 fois ton but. Une pièce éclatante déjà dans ton stock est comptée.", 12, Tok.Muted, wrap: true).WithLineHeight(1.45);
        return Page("Craft · chaîne d'un équipement", right, choose, hint, CraftMasteryRow(m), kpis, stepCard, resCard, makeCard, how);
    }
}
