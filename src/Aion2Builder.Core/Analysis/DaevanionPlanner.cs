using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Daevanion. Une case ne s'ouvre qu'à côté d'une case ouverte (haut/bas/gauche/droite, depuis le centre) et coûte
/// 1, 2, 3 ou 4 points selon sa couleur. Chaque réserve (plateaux communs, Ariel en JcE, Azphel en JcJ) est traitée
/// à part, avec deux plans :
/// - réorganisation : la meilleure répartition des points déjà dépensés (+ non dépensés) en repartant du centre,
///   comparée au placement actuel → cases à retirer et cases à prendre ;
/// - prochaines cases : où mettre les points à venir sans rien retirer.
/// </summary>
public sealed class DaevanionPlanner(GameDatabase db, StatAnalyzer stats)
{
    public const string NormalPool = "Normal";
    static readonly (int R, int C)[] Neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    public string Pool(DaevanionBoard b) => db.Settings.Daevanion.BoardPools.GetValueOrDefault(b.Name, NormalPool);
    public static bool Relevant(string pool, GameMode mode) => pool == NormalPool || pool == mode.ToString();
    public string PoolName(string pool) => db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool, pool);
    public double NodeCost(DaevanionNode n) => n.IsStart ? 0 : Math.Max(0.1, db.Settings.Daevanion.NodeCost.GetValueOrDefault(n.Grade, 1));

    sealed record Context(ResolvedProfile Profile, StatProfile Stats, Dictionary<string, SkillInfo> Skills, SkillAdvisor Skill);

    double NodeValue(DaevanionNode n, Context ctx)
    {
        var s = db.Settings.Daevanion;
        var v = 0.0;
        foreach (var e in n.Effects)
        {
            if (e.IsStat) v += stats.EffectValue(e, ctx.Stats, ctx.Profile);
            else if (e.IsSkill && ctx.Skills.TryGetValue(GameDatabase.NormalizeLabel(e.SkillName!), out var skill) && skill.Acquired)
            {
                // Un niveau de compétence vaut selon l'importance de la compétence dans la rotation de la classe.
                var priority = ctx.Skill.Priority(skill, ctx.Profile);
                v += e.Value * s.SkillNodeValue * priority * (priority >= 0.85 ? s.CoreSkillMultiplier : 1);
            }
        }
        return v;
    }

    public DaevanionPlan Plan(CharacterSnapshot c, ResolvedProfile p, StatProfile sp, PlayerInputs inputs)
    {
        var ctx = new Context(p, sp,
            c.Skills.GroupBy(s => GameDatabase.NormalizeLabel(s.Name)).ToDictionary(g => g.Key, g => g.First()),
            new SkillAdvisor(db));
        var settings = db.Settings.Daevanion;
        var boards = c.Boards.Where(b => b.Nodes.Count > 0).ToList();
        var open = boards.ToDictionary(b => b.Id, b => b.Nodes.Where(n => n.Open || n.IsStart).Select(n => (n.Row, n.Col)).ToHashSet());
        var grids = boards.ToDictionary(b => b.Id, b => b.Nodes.GroupBy(n => (n.Row, n.Col)).ToDictionary(g => g.Key, g => g.First()));
        var values = boards.SelectMany(b => b.Nodes).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => NodeValue(g.First(), ctx));

        var summaries = boards.Select(b =>
        {
            var closed = b.Nodes.Where(n => !n.Open && !n.IsStart).ToList();
            var reachable = closed.Count(n => Neighbors.Any(d => open[b.Id].Contains((n.Row + d.R, n.Col + d.C))));
            var pool = Pool(b);
            return new BoardSummary(b, pool, Relevant(pool, p.Mode), closed.Sum(n => values[n.Id]), closed.Sum(NodeCost), reachable,
                (int)b.Nodes.Where(n => n.Open).Sum(NodeCost), (int)b.Nodes.Sum(NodeCost));
        }).ToList();

        var pools = boards.Select(Pool).Distinct().OrderBy(x => x == NormalPool ? 0 : 1).ToList();
        var budgets = pools.Select(pool =>
        {
            var poolBoards = summaries.Where(s => s.Pool == pool).ToList();
            return new PoolBudget(pool, poolBoards.Sum(b => b.Spent), inputs.UnspentFor(pool), poolBoards.Sum(b => b.Total));
        }).ToList();

        var steps = new List<PlannedNode>();
        var respecs = new List<RespecPlan>();
        foreach (var pool in pools.Where(x => Relevant(x, p.Mode)))
        {
            var poolBoards = boards.Where(b => Pool(b) == pool).ToList();
            var budget = budgets.First(b => b.Pool == pool);

            // Prochaines cases à partir du placement actuel ; "faisable" tant que les points non dépensés suffisent.
            var current = poolBoards.ToDictionary(b => b.Id, b => new HashSet<(int, int)>(open[b.Id]));
            var cumulative = 0.0;
            var order = 0;
            foreach (var (board, node, passage) in Grow(poolBoards, grids, current, values, double.PositiveInfinity, settings.PlanLength))
            {
                cumulative += NodeCost(node);
                steps.Add(new PlannedNode(++order, pool, board, node, values[node.Id], NodeCost(node), passage, cumulative <= budget.Unspent));
            }

            respecs.Add(Respec(pool, poolBoards, grids, values, budget));
        }
        return new DaevanionPlan(steps, summaries, budgets, respecs);
    }

    /// <summary>Meilleure répartition du budget en repartant du centre, puis comparaison avec le placement actuel.</summary>
    RespecPlan Respec(string pool, List<DaevanionBoard> boards, Dictionary<int, Dictionary<(int, int), DaevanionNode>> grids,
        Dictionary<long, double> values, PoolBudget budget)
    {
        var starts = boards.ToDictionary(b => b.Id, b => b.Nodes.Where(n => n.IsStart).Select(n => (n.Row, n.Col)).ToHashSet());
        var chosen = boards.ToDictionary(b => b.Id, b => new HashSet<(int, int)>(starts[b.Id]));
        var added = new List<(DaevanionBoard Board, DaevanionNode Node)>();
        double Cost() => boards.Sum(b => chosen[b.Id].Sum(cell => NodeCost(grids[b.Id][cell])));
        double Value() => boards.Sum(b => chosen[b.Id].Sum(cell => values[grids[b.Id][cell].Id]));

        foreach (var (board, node, _) in Grow(boards, grids, chosen, values, budget.Budget, int.MaxValue))
            added.Add((board, node));

        // Amélioration locale : on retire la case la moins rentable qui ne coupe pas le chemin, on redépense, on garde si c'est mieux.
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var before = Value();
            var removable = boards.SelectMany(b => chosen[b.Id].Where(cell => !starts[b.Id].Contains(cell) && KeepsConnected(chosen[b.Id], starts[b.Id], cell))
                    .Select(cell => (Board: b, Cell: cell, Node: grids[b.Id][cell])))
                .OrderBy(x => values[x.Node.Id] / NodeCost(x.Node)).FirstOrDefault();
            if (removable.Node is null) break;
            chosen[removable.Board.Id].Remove(removable.Cell);
            var trial = Grow(boards, grids, chosen, values, budget.Budget - Cost(), int.MaxValue).ToList();
            if (Value() > before + 1e-6 && trial.All(t => t.Node.Id != removable.Node.Id))
            {
                added.RemoveAll(a => a.Node.Id == removable.Node.Id);
                added.AddRange(trial.Select(t => (t.Board, t.Node)));
                continue;
            }
            // Pas mieux : on annule.
            foreach (var t in trial) chosen[t.Board.Id].Remove((t.Node.Row, t.Node.Col));
            chosen[removable.Board.Id].Add(removable.Cell);
            break;
        }

        var optimal = boards.SelectMany(b => chosen[b.Id].Select(cell => grids[b.Id][cell].Id)).ToHashSet();
        var currentNodes = boards.SelectMany(b => b.Nodes.Where(n => n.Open && !n.IsStart).Select(n => (Board: b, Node: n))).ToList();
        var currentValue = currentNodes.Sum(x => values[x.Node.Id]);
        var remove = currentNodes.Where(x => !optimal.Contains(x.Node.Id))
            .Select(x => new NodeRef(x.Board, x.Node, values[x.Node.Id], NodeCost(x.Node))).OrderBy(x => x.Value / x.Cost).ToList();
        var step = 0;
        var add = added.Where(a => !a.Node.Open && optimal.Contains(a.Node.Id))
            .Select(a => new PlannedNode(++step, pool, a.Board, a.Node, values[a.Node.Id], NodeCost(a.Node), false, true)).ToList();
        return new RespecPlan(pool, budget.Budget, currentValue, Value(), remove, add, optimal);
    }

    static bool KeepsConnected(HashSet<(int R, int C)> set, HashSet<(int R, int C)> starts, (int R, int C) without)
    {
        var seen = new HashSet<(int, int)>(starts);
        var queue = new Queue<(int R, int C)>(starts);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var (dr, dc) in Neighbors)
            {
                var v = (u.R + dr, u.C + dc);
                if (v != without && set.Contains(v) && seen.Add(v)) queue.Enqueue(v);
            }
        }
        return seen.Count == set.Count - 1;
    }

    /// <summary>
    /// Ajoute des cases tant que le budget le permet : à chaque tour, le chemin (depuis la zone ouverte) au meilleur
    /// rapport valeur / coût, chemins d'accès compris. Modifie <paramref name="open"/>.
    /// </summary>
    IEnumerable<(DaevanionBoard Board, DaevanionNode Node, bool Passage)> Grow(List<DaevanionBoard> boards,
        Dictionary<int, Dictionary<(int, int), DaevanionNode>> grids, Dictionary<int, HashSet<(int, int)>> open,
        Dictionary<long, double> values, double budget, int maxNodes)
    {
        var spent = 0.0;
        var count = 0;
        while (count < maxNodes)
        {
            (DaevanionBoard Board, List<DaevanionNode> Path, double Ratio, double Value)? best = null;
            foreach (var b in boards)
                foreach (var (path, value, cost) in Candidates(grids[b.Id], open[b.Id], values, db.Settings.Daevanion.MaxPathLength))
                {
                    if (spent + cost > budget + 1e-9) continue;
                    var ratio = value / cost;
                    if (ratio > 1e-6 && (best is null || ratio > best.Value.Ratio + 1e-9
                                          || Math.Abs(ratio - best.Value.Ratio) < 1e-9 && value > best.Value.Value))
                        best = (b, path, ratio, value);
                }
            if (best is not { } pick) yield break;

            var target = pick.Path[^1];
            foreach (var n in pick.Path)
            {
                open[pick.Board.Id].Add((n.Row, n.Col));
                spent += NodeCost(n);
                count++;
                yield return (pick.Board, n, n != target && values[n.Id] < 0.5 * NodeCost(n) * pick.Ratio);
            }
        }
    }

    /// <summary>Dijkstra depuis toutes les cases ouvertes : chemin le moins cher vers chaque case fermée atteignable.</summary>
    IEnumerable<(List<DaevanionNode> Path, double Value, double Cost)> Candidates(
        Dictionary<(int, int), DaevanionNode> grid, HashSet<(int, int)> open, Dictionary<long, double> values, int maxLength)
    {
        var dist = new Dictionary<(int, int), double>();
        var length = new Dictionary<(int, int), int>();
        var prev = new Dictionary<(int, int), (int, int)>();
        var queue = new PriorityQueue<(int R, int C), double>();
        foreach (var cell in open) { dist[cell] = 0; length[cell] = 0; queue.Enqueue(cell, 0); }

        while (queue.TryDequeue(out var u, out var d))
        {
            if (d > dist[u] + 1e-12) continue;
            foreach (var (dr, dc) in Neighbors)
            {
                var v = (u.R + dr, u.C + dc);
                if (open.Contains(v) || !grid.TryGetValue(v, out var node)) continue;
                var len = length[u] + 1;
                if (len > maxLength) continue;
                // Coût réel, réduit d'au plus 1 % selon la valeur pour départager deux chemins de même coût.
                var nd = d + NodeCost(node) * (1 - 0.01 * Math.Clamp(values[node.Id], 0, 1));
                if (dist.TryGetValue(v, out var old) && old <= nd) continue;
                dist[v] = nd; length[v] = len; prev[v] = u; queue.Enqueue(v, nd);
            }
        }

        foreach (var cell in dist.Keys.Where(k => !open.Contains(k)))
        {
            var path = new List<DaevanionNode>();
            for (var cur = cell; !open.Contains(cur); cur = prev[cur]) path.Add(grid[cur]);
            path.Reverse();
            yield return (path, path.Sum(n => values[n.Id]), path.Sum(NodeCost));
        }
    }

    public List<Finding> Findings(DaevanionPlan plan, CharacterSnapshot c, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        var settings = db.Settings.Daevanion;

        foreach (var b in plan.Budgets.Where(b => Relevant(b.Pool, p.Mode)))
            findings.Add(new Finding(Area.Daevanion, Severity.Info, $"{PoolName(b.Pool)} : {b.Spent} dépensés + {b.Unspent} non dépensés",
                $"Tout remplir coûte {b.FillCost} points (cases à 1, 2, 3 ou 4 points selon leur couleur). " +
                (b.Pool == NormalPool ? settings.PointSources : "") +
                (b.Unspent == 0 ? " Indique tes points non dépensés (onglet Daevanion ou --dp/--ariel/--azphel) pour un plan exact." : "")));

        foreach (var r in plan.Respecs.Where(r => r.Remove.Count > 0 && r.Gain >= settings.RespecMinGain))
            findings.Add(new Finding(Area.Daevanion, r.Gain >= 0.1 ? Severity.Important : Severity.Advice,
                $"{PoolName(r.Pool)} : réorganisation conseillée (+{Fmt.Pct(r.Gain)} de valeur)",
                $"Avec les mêmes {r.Budget} points, un autre chemin rapporte plus pour un {p.Class.Name} en {GameDatabase.ModeName(p.Mode)}. " +
                $"À retirer ({r.Remove.Count} cases, {Fmt.Num(r.Remove.Sum(x => x.Cost))} points) : " +
                string.Join(" · ", r.Remove.Take(8).Select(x => $"{x.Board.Name} ligne {x.Node.Row} col. {x.Node.Col} {x.Effects}")) +
                (r.Remove.Count > 8 ? " …" : "") + ". À prendre à la place : " +
                string.Join(" · ", r.Add.Take(8).Select(x => $"{x.Board.Name} {x.Effects}")) + (r.Add.Count > 8 ? " …" : "") + ".",
                "Si tu réinitialises ce plateau, reprends dans l'ordre de l'onglet Daevanion (cases rouges = à retirer).",
                40 + 100 * Math.Min(r.Gain, 0.5)));
        foreach (var r in plan.Respecs.Where(r => r.Remove.Count == 0 || r.Gain < settings.RespecMinGain))
            findings.Add(new Finding(Area.Daevanion, Severity.Info, $"{PoolName(r.Pool)} : placement actuel proche de l'optimal",
                r.Remove.Count == 0
                    ? "Aucune case ouverte n'est à retirer : continue simplement le plan des prochaines cases."
                    : $"Une réorganisation ne rapporterait que {Fmt.Pct(Math.Max(0, r.Gain))} : pas la peine de réinitialiser."));

        foreach (var group in plan.ByPool)
        {
            var first = group.Take(6).ToList();
            var affordable = group.Count(s => s.Affordable);
            findings.Add(new Finding(Area.Daevanion, Severity.Advice, $"Prochaines cases — {PoolName(group.Key)}",
                string.Join(" · ", first.Select(s => $"{s.Board.Name} : {s.Effects}{(s.IsPassage ? " (passage)" : "")}")),
                affordable > 0
                    ? $"Tes points non dépensés couvrent les {affordable} premières : ouvre-les dans cet ordre."
                    : "À ouvrir dans cet ordre au fur et à mesure de tes points : meilleur gain par point, chemins d'accès compris.",
                40 + 10 * first.Where(s => !s.IsPassage).Sum(s => s.Value) / Math.Max(1, first.Sum(s => s.Cost))));
        }

        var modePool = p.Mode.ToString();
        var modeBoards = c.Boards.Where(b => Pool(b) == modePool).ToList();
        if (p.Mode == GameMode.PvE && modeBoards.All(b => !b.Unlocked || b.Nodes.Count == 0))
            findings.Add(new Finding(Area.Daevanion, Severity.Info, "Plateau JcE (Ariel) non disponible",
                "Il se débloque plus tard ; ses cristaux JcE viennent surtout de l'Épreuve de l'ascension hebdomadaire et des quêtes journalières / hebdomadaires."));
        if (p.Mode == GameMode.PvP)
        {
            if (modeBoards.Count == 0 || modeBoards.All(b => !b.Unlocked))
                findings.Add(new Finding(Area.Daevanion, Severity.Info, "Plateau JcJ (Azphel) non disponible",
                    "Il se débloque plus tard ; ses cristaux JcJ viennent des champs de bataille hebdomadaires et du monolithe Abyssal."));
            else if (modeBoards.All(b => b.OpenNodes == 0))
                findings.Add(new Finding(Area.Daevanion, Severity.Important, "Azphel (JcJ) : aucune case ouverte",
                    "C'est le plateau des stats JcJ. Les cristaux JcJ viennent des champs de bataille (jusqu'à 3 fois par semaine), du monolithe Abyssal et de la boutique JcJ.",
                    "Fais tes champs de bataille hebdomadaires et ouvre les premières cases d'Azphel.", 60));
        }
        return findings;
    }
}
