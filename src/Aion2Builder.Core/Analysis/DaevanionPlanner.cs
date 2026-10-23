using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Planifie les prochaines cases Daevanion. Une case ne s'ouvre qu'à côté d'une case ouverte (voisinage
/// haut/bas/gauche/droite à partir du centre) : on cherche donc, pour chaque cible, le chemin le moins cher
/// depuis la zone ouverte, et on retient le meilleur rapport valeur du chemin / coût du chemin.
/// Chaque réserve de points (plateaux communs, Ariel en JcE, Azphel en JcJ) est planifiée séparément.
/// </summary>
public sealed class DaevanionPlanner(GameDatabase db, StatAnalyzer stats)
{
    const string NormalPool = "Normal";
    static readonly (int R, int C)[] Neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    public string Pool(DaevanionBoard b) => db.Settings.Daevanion.BoardPools.GetValueOrDefault(b.Name, NormalPool);
    public static bool Relevant(string pool, GameMode mode) => pool == NormalPool || pool == mode.ToString();
    public string PoolName(string pool) => db.Settings.Daevanion.PoolNames.GetValueOrDefault(pool, pool);
    public double NodeCost(DaevanionNode n) => Math.Max(0.1, db.Settings.Daevanion.NodeCost.GetValueOrDefault(n.Grade, 1));

    sealed record Context(ResolvedProfile Profile, StatProfile Stats, Dictionary<string, SkillInfo> Skills, HashSet<string> Core);

    double NodeValue(DaevanionNode n, Context ctx)
    {
        var s = db.Settings.Daevanion;
        var v = 0.0;
        foreach (var e in n.Effects)
        {
            if (e.IsStat) v += stats.EffectValue(e, ctx.Stats, ctx.Profile);
            else if (e.IsSkill && ctx.Skills.TryGetValue(GameDatabase.NormalizeLabel(e.SkillName!), out var skill))
            {
                var used = skill.Acquired && (!skill.IsActive || skill.Equipped);
                var mult = ctx.Core.Contains(GameDatabase.NormalizeLabel(skill.Name)) ? s.CoreSkillMultiplier : 1;
                v += e.Value * (used ? s.SkillNodeValue * mult : skill.Acquired ? s.UnequippedSkillNodeValue : 0);
            }
        }
        return v;
    }

    public DaevanionPlan Plan(CharacterSnapshot c, ResolvedProfile p, StatProfile sp)
    {
        var ctx = new Context(p, sp,
            c.Skills.GroupBy(s => GameDatabase.NormalizeLabel(s.Name)).ToDictionary(g => g.Key, g => g.First()),
            new SkillAdvisor(db).CoreSet(p));
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
            return new BoardSummary(b, pool, Relevant(pool, p.Mode), closed.Sum(n => values[n.Id]), closed.Sum(NodeCost), reachable);
        }).ToList();

        var steps = new List<PlannedNode>();
        foreach (var pool in boards.Select(Pool).Distinct().Where(x => Relevant(x, p.Mode)).OrderBy(x => x == NormalPool ? 0 : 1))
        {
            var poolBoards = boards.Where(b => Pool(b) == pool).ToList();
            var planned = 0;
            while (planned < settings.PlanLength)
            {
                (DaevanionBoard Board, List<DaevanionNode> Path, double Ratio, double Value)? best = null;
                foreach (var b in poolBoards)
                    foreach (var (path, value, cost) in Candidates(grids[b.Id], open[b.Id], values, settings.MaxPathLength))
                    {
                        var ratio = value / cost;
                        if (ratio > 1e-6 && (best is null || ratio > best.Value.Ratio + 1e-9
                                              || Math.Abs(ratio - best.Value.Ratio) < 1e-9 && value > best.Value.Value))
                            best = (b, path, ratio, value);
                    }
                if (best is not { } pick) break;

                var target = pick.Path[^1];
                foreach (var n in pick.Path)
                {
                    open[pick.Board.Id].Add((n.Row, n.Col));
                    var passage = n != target && values[n.Id] < 0.5 * NodeCost(n) * pick.Ratio;
                    steps.Add(new PlannedNode(steps.Count + 1, pool, pick.Board, n, values[n.Id], NodeCost(n), passage));
                    planned++;
                }
            }
        }
        return new DaevanionPlan(steps, summaries);
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
        foreach (var group in plan.ByPool)
        {
            var first = group.Take(6).ToList();
            findings.Add(new Finding(Area.Daevanion, Severity.Advice, $"Prochaines cases — {PoolName(group.Key)}",
                string.Join(" · ", first.Select(s => $"{s.Board.Name} : {s.Effects}{(s.IsPassage ? " (passage)" : "")}")),
                $"Ouvre-les dans cet ordre ({group.Count()} cases planifiées) : meilleur gain par point dépensé, chemins d'accès compris.",
                40 + 10 * first.Where(s => !s.IsPassage).Sum(s => s.Value) / Math.Max(1, first.Sum(s => s.Cost))));
        }

        var ranked = plan.Boards.Where(b => b.Relevant && b.Pool == NormalPool && b.Reachable > 0).OrderByDescending(b => b.ValuePerCost).ToList();
        if (ranked.Count >= 2 && ranked[0].ValuePerCost > 1.3 * ranked[1].ValuePerCost)
            findings.Add(new Finding(Area.Daevanion, Severity.Advice, $"Plateau le plus rentable : {ranked[0].Board.Name}",
                $"Reste à prendre : {Fmt.Num(ranked[0].ValuePerCost * 100)} pts de valeur pour 100 points dépensés, contre {Fmt.Num(ranked[1].ValuePerCost * 100)} sur {ranked[1].Board.Name}.",
                $"Mets tes points Daevanion communs d'abord sur {ranked[0].Board.Name}.", 35));

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
