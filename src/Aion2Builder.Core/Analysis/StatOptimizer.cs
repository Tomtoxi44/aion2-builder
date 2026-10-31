using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Ce qu'il faut faire d'une ligne (option d'âme ou pierre).</summary>
public enum OptAdvice { Keep, Change, Fill, Locked, Skip }

/// <param name="Kind">« âme » (option de gravure d'âme, stat ou niveau de compétence) ou « pierre ».</param>
/// <param name="Current">La ligne telle qu'elle est (« CRIT 29 ») ; vide pour un emplacement de pierre libre.</param>
/// <param name="Target">Stat à viser à la place ; null pour garder la ligne.</param>
/// <param name="TargetValue">Valeur attendue de la stat visée (comme tes autres lignes de la pièce, ou tes pierres du même grade).</param>
/// <param name="Alternatives">Autres stats qui feraient presque aussi bien : un tirage au hasard peut aussi tomber dessus.</param>
/// <param name="Gain">Ce que l'échange rapporte (sert à l'ordre), 0 pour une ligne gardée.</param>
public sealed record OptLine(string Kind, string Current, string? StatKey, OptAdvice Advice, string? Target, double TargetValue,
    List<string> Alternatives, double Gain, string Why);

/// <summary>Une pièce : ses options d'âme et ses pierres, chacune avec son conseil.</summary>
public sealed record OptItem(string Slot, string SlotName, EquippedItem Item, double? Rate, string? Note, List<OptLine> Soul, List<OptLine> Stones)
{
    public double Gain => Soul.Concat(Stones).Sum(l => l.Gain);
    public int Changes => Soul.Concat(Stones).Count(l => l.Advice is OptAdvice.Change or OptAdvice.Fill);
}

/// <summary>Une stat : aujourd'hui, la cible (guides ou joueurs), le plafond, et après les échanges conseillés.</summary>
public sealed record OptStat(StatDef Def, double Now, double After, double? Target, string? TargetLabel, double? Cap, double Weight);

/// <summary>Plan d'optimisation : pièce par pièce, et les stats avant → après ; plus les compétences à un niveau d'une spécialisation.</summary>
public sealed record OptimizationPlan(List<OptItem> Items, List<OptStat> Stats, List<string> SkillNotes);

/// <summary>
/// Optimisation des stats de l'équipement, toutes pièces ensemble. Chaque stat vaut son poids pour la classe, entièrement
/// jusqu'à sa cible (la prochaine valeur des guides de la classe, sinon la moyenne des joueurs comparés), à 35 % au-delà, et
/// plus du tout au-delà de son plafond. Chaque échange possible est essayé — une option d'âme (pièce gardée, lien d'âme sous
/// 100 %) vers une stat possible sur ce type de pièce, une pierre vers une autre stat du même grade, un emplacement de pierre
/// libre — avec la valeur des autres lignes de la pièce ; le meilleur est retenu, les totaux refaits, et on recommence tant
/// qu'un échange rapporte au moins un quart d'une ligne de la stat la plus utile. Ainsi, une stat qui atteint sa cible
/// n'est plus visée sur les pièces suivantes : on ne met pas du CRIT partout. Les valeurs sont celles d'aujourd'hui (le
/// lien d'âme les fera monter) ; une synchronisation tombe au hasard parmi les options de la pièce.
/// </summary>
public sealed class StatOptimizer(GameDatabase db)
{
    const double AboveTargetSlope = 0.35;
    const double MinGainShare = 0.25;
    const double AlternativeShare = 0.6;

    /// <summary>Une ligne qu'on peut changer : ce qu'elle apporte aujourd'hui, et ce que chaque stat apporterait à sa place.</summary>
    sealed class Move
    {
        public required int Item;
        public required string Kind;
        public required int Index;
        public string? Remove;
        public double RemoveValue;
        public double PrimaryValue;
        public required Dictionary<string, double> Candidates;
        public string? Target;
        public double Gain;
        public List<string> Alternatives = [];
    }

    public OptimizationPlan Plan(AdviceReport r)
    {
        var p = r.Profile;
        var sp = r.Stats;
        var cmp = sp.Comparison;
        var parser = new EffectParser(db);
        var rules = db.Progression.Engraving;
        var byKey = sp.Stats.ToDictionary(x => x.Def.Key);
        var totals = sp.Stats.ToDictionary(x => x.Def.Key, x => x.BenchValue);
        var now = new Dictionary<string, double>(totals);
        double Unit(StatDef d) => d.Kind == StatKind.Percent ? Math.Max(1e-9, d.PercentUnit) : Math.Max(1e-9, d.Unit);

        // Cible de chaque stat : la prochaine valeur des guides pas atteinte, au moins la moyenne des joueurs comparés ; une fois
        // toutes atteintes, la plus haute. Plafond : la valeur des guides au-delà de laquelle la stat ne sert plus.
        var goals = new Dictionary<string, (double? Target, string? Label, double? Cap)>();
        foreach (var x in sp.Stats)
        {
            var targets = cmp.TargetsFor(x);
            var cap = targets.FirstOrDefault(t => t.Cap)?.Value;
            var next = cmp.NextTarget(x) is { Cap: false } n ? n : null;
            double? avg = cmp.Peers is { Count: > 0 } peers ? ClassBenchmark.Sample(peers, x.Def.Key).Average() : null;
            var reached = targets.LastOrDefault(t => !t.Cap && t.Value <= x.BenchValue + 1e-9);
            double? target = next?.Value ?? reached?.Value;
            string? label = next?.Label ?? reached?.Label;
            if (avg is { } a && (target is null || a > target)) { target = a; label = "moyenne des joueurs"; }
            goals[x.Def.Key] = (target, label, cap);
        }
        double Piece(string key, double x)
        {
            var (t, _, c) = goals.GetValueOrDefault(key);
            var top = c ?? double.PositiveInfinity;
            var knee = Math.Min(t ?? double.PositiveInfinity, top);
            return Math.Min(x, knee) + AboveTargetSlope * Math.Max(0, Math.Min(x, top) - knee);
        }
        double Value(string key, double x) => byKey.TryGetValue(key, out var s) ? p.Weight(key) * Piece(key, x) / Unit(s.Def) : 0;
        // Une stat principale (Force…) : ce qu'elle augmente, un peu moins, sans plafond.
        double PrimaryValue(Effect e) => db.Stat(e.StatKey!) is { Category: StatCategory.Primary } d
            ? (d.Proxy is { } proxy ? p.Weight(proxy) * 0.8 : 0.3) * e.Value / Math.Max(1e-9, d.Unit) : 0;
        // Ce qu'une ligne ajoute à sa stat, à l'échelle des totaux (le % pour une stat en %, la valeur plate sinon).
        double Contribution(Effect e)
        {
            if (!e.IsStat || !byKey.TryGetValue(e.StatKey!, out var s)) return 0;
            return s.Def.Kind == StatKind.Percent ? (e.IsPercent ? e.Value : 0) : e.IsPercent ? e.Value / 100 * s.Flat : e.Value;
        }
        double LineBudget(Effect e) => byKey.TryGetValue(e.StatKey ?? "", out var s) ? Contribution(e) / Unit(s.Def) : 0;
        static double Median(IEnumerable<double> values)
        {
            var list = values.Where(v => v > 0).OrderBy(v => v).ToList();
            return list.Count == 0 ? 0 : list[list.Count / 2];
        }

        var available = GearRoadmap.AvailableTier(db, r.Character.ItemLevel);
        var slots = r.Gear.Slots.Where(s => s.Item is { HasDetail: true } && (s.Item.SoulLineCount > 0 || s.Item.MagicStoneSlots > 0)).ToList();
        var soul = slots.Select(s => s.Item!.SubStats.Select(parser.Recognize).ToList()).ToList();
        var stones = slots.Select(s => s.Item!.Stones.Select(x => (Effect: parser.Recognize(x.Effect), x.Grade)).ToList()).ToList();
        var globalLine = Median(soul.SelectMany(l => l).Select(LineBudget));
        // Options vues sur tes propres pièces, par type de pièce : elles complètent les listes relevées ailleurs.
        var observed = slots.Select((s, i) => (s.Item!.Category, Keys: soul[i].Where(e => e.IsStat).Select(e => e.StatKey!)))
            .Where(x => x.Category is not null).GroupBy(x => x.Category!).ToDictionary(g => g.Key, g => g.SelectMany(x => x.Keys).ToHashSet());
        // Pierres : la valeur d'une stat à un grade, vue sur tes pierres ; sinon convertie depuis les pierres du même grade.
        var stoneSeen = stones.SelectMany(l => l).Where(x => x.Effect.IsStat).GroupBy(x => (x.Effect.StatKey!, x.Grade))
            .ToDictionary(g => g.Key, g => g.Max(x => Contribution(x.Effect)));
        var gradeBudget = stones.SelectMany(l => l).GroupBy(x => x.Grade).ToDictionary(g => g.Key, g => Median(g.Select(x => LineBudget(x.Effect))));
        double StoneValue(string key, string grade) => stoneSeen.TryGetValue((key, grade), out var v) && v > 0 ? v
            : gradeBudget.GetValueOrDefault(grade) * Unit(byKey[key].Def);

        var moves = new List<Move>();
        var notes = new string?[slots.Count];
        var locked = new bool[slots.Count];
        var skipped = new bool[slots.Count];
        var keepStones = new bool[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var item = slot.Item!;
            var replaced = slot.Status == SlotStatus.Replace;
            skipped[i] = replaced;
            locked[i] = item.SoulLineCount > 0 && (item.SoulBindRate ?? 0) >= 100;
            keepStones[i] = !replaced && GearRoadmap.TierSource(db, slot.Slot, slot.Group, item, available) is null;
            notes[i] = replaced ? "Pièce à remplacer : n'y dépense rien, garde tes matériaux pour la suivante."
                : locked[i] ? "Lien d'âme à 100 % : options verrouillées, elles ne se changent plus."
                : item.SoulLineCount == 0 ? null
                : !keepStones[i] ? "Pièce provisoire (le palier accessible la remplacera) : ses pierres seraient perdues au transfert ; ses options d'âme, elles, suivent un transfert."
                : null;

            // Options d'âme modifiables : stats possibles sur ce type de pièce, absentes de la pièce, à la valeur de ses lignes.
            if (item.SoulLineCount > 0 && !replaced && !locked[i])
            {
                var pool = new HashSet<string>(item.Category is { } cat && rules.SoulPools.TryGetValue(cat, out var known) ? known : slot.Slot == "MainHand" ? rules.WeaponPool : []);
                if (item.Category is { } category && observed.TryGetValue(category, out var mine)) pool.UnionWith(mine);
                pool.RemoveWhere(k => !byKey.ContainsKey(k));
                var budget = Median(soul[i].Select(LineBudget)) is var m && m > 0 ? m : globalLine;
                Dictionary<string, double> Candidates() => pool.ToDictionary(k => k, k => budget * Unit(byKey[k].Def));
                for (var j = 0; j < soul[i].Count; j++)
                {
                    var e = soul[i][j];
                    moves.Add(new Move
                    {
                        Item = i, Kind = "âme", Index = j, Remove = e.IsStat && byKey.ContainsKey(e.StatKey!) ? e.StatKey : null,
                        RemoveValue = Contribution(e), PrimaryValue = e.IsStat ? PrimaryValue(e) : 0, Candidates = Candidates(),
                    });
                }
                // Niveaux de compétence (lignes d'âme aussi) : une compétence peu utile pour toi est une ligne perdue.
                var bonuses = r.Skills.GearBonuses.Where(b => !b.Arcana && b.Slot == slot.Slot).ToList();
                for (var j = 0; j < bonuses.Count; j++)
                    if (!bonuses[j].Useful)
                        moves.Add(new Move { Item = i, Kind = "compétence", Index = j, Candidates = Candidates() });
            }
            // Pierres : sur une pièce gardée, chaque pierre peut changer de stat (même grade), un emplacement libre se remplit.
            if (keepStones[i] && item.MagicStoneSlots > 0)
            {
                for (var j = 0; j < stones[i].Count; j++)
                {
                    var (e, grade) = stones[i][j];
                    moves.Add(new Move
                    {
                        Item = i, Kind = "pierre", Index = j, Remove = e.IsStat && byKey.ContainsKey(e.StatKey!) ? e.StatKey : null,
                        RemoveValue = Contribution(e), Candidates = rules.StonePool.Where(byKey.ContainsKey).ToDictionary(k => k, k => StoneValue(k, grade)),
                    });
                }
                var best = stones[i].Select(x => x.Grade).OrderByDescending(g => db.Grade(g).Rank).FirstOrDefault() ?? "Rare";
                for (var j = stones[i].Count; j < item.MagicStoneSlots; j++)
                    moves.Add(new Move { Item = i, Kind = "pierre", Index = j, Candidates = rules.StonePool.Where(byKey.ContainsKey).ToDictionary(k => k, k => StoneValue(k, best)) });
            }
        }

        // Le plus rentable d'abord, totaux refaits après chaque échange.
        // Seuil : un quart d'une ligne de la stat la plus utile (une pierre vaut bien moins qu'une option d'âme : son seuil aussi).
        var topWeight = sp.Stats.Select(x => p.Weight(x.Def.Key)).DefaultIfEmpty(1).Max();
        var globalStone = Median(stones.SelectMany(l => l).Select(x => LineBudget(x.Effect)));
        double MinGain(Move m) => MinGainShare * topWeight * Math.Max(m.Kind == "pierre" ? globalStone : globalLine, 1e-6);
        double Delta(Move m, string target, double add)
        {
            var before = 0.0;
            var after = 0.0;
            foreach (var key in new[] { m.Remove, target }.OfType<string>().Distinct())
            {
                var x = totals[key];
                var y = x - (key == m.Remove ? m.RemoveValue : 0) + (key == target ? add : 0);
                before += Value(key, x);
                after += Value(key, y);
            }
            return after - before - m.PrimaryValue;
        }
        bool Present(Move m, string key) => m.Kind != "pierre" && moves.Any(o => o.Item == m.Item && o.Kind == "âme" && (o.Target ?? (o == m ? null : o.Remove)) == key)
                                            || (m.Kind != "pierre" && soul[m.Item].Any(e => e.StatKey == key && key != m.Remove));
        var open = new List<Move>(moves);
        while (open.Count > 0)
        {
            var best = open.SelectMany(m => m.Candidates.Where(c => c.Key != m.Remove && !Present(m, c.Key)).Select(c => (Move: m, Key: c.Key, Gain: Delta(m, c.Key, c.Value))))
                .Where(x => x.Gain >= MinGain(x.Move)).OrderByDescending(x => x.Gain / MinGain(x.Move)).FirstOrDefault();
            if (best.Move is null) break;
            var move = best.Move;
            move.Alternatives = move.Candidates.Where(c => c.Key != move.Remove && c.Key != best.Key && !Present(move, c.Key))
                .Select(c => (c.Key, Gain: Delta(move, c.Key, c.Value))).Where(x => x.Gain >= AlternativeShare * best.Gain)
                .OrderByDescending(x => x.Gain).Take(3).Select(x => x.Key).ToList();
            if (move.Remove is { } removed) totals[removed] -= move.RemoveValue;
            totals[best.Key] += move.Candidates[best.Key];
            move.Target = best.Key;
            move.Gain = best.Gain;
            open.Remove(move);
        }

        // Pièce par pièce : chaque ligne gardée ou à changer, et pourquoi.
        string Show(string key, double value) => byKey[key].Def.Kind == StatKind.Percent ? $"{Fmt.Num(Math.Round(value, 1))} %" : Fmt.Num(Math.Round(value));
        string Why(string key)
        {
            var (t, label, c) = goals.GetValueOrDefault(key);
            var x = now[key];
            if (c is { } cap && x > cap * 1.02) return $"{db.StatName(key)} au-delà de son plafond ({Show(key, x)} pour {Show(key, cap)})";
            if (t is { } target && x < target) return $"il te manque {Show(key, target - x)} de {db.StatName(key)} pour {Show(key, target)} ({label})";
            return $"{db.StatName(key)} utile pour ta classe";
        }
        var items = new List<OptItem>();
        for (var i = 0; i < slots.Count; i++)
        {
            var item = slots[i].Item!;
            var soulLines = new List<OptLine>();
            for (var j = 0; j < soul[i].Count; j++)
            {
                var e = soul[i][j];
                var m = moves.FirstOrDefault(x => x.Item == i && x.Kind == "âme" && x.Index == j);
                soulLines.Add(Line("âme", e.Raw, e.StatKey, m, skipped[i] ? OptAdvice.Skip : locked[i] ? OptAdvice.Locked : OptAdvice.Keep,
                    e.IsStat && byKey.ContainsKey(e.StatKey!) ? $"garde : {Why(e.StatKey!).ToLowerInvariant()}" : e.IsStat ? "garde : stat principale, elle augmente une stat utile" : "option non reconnue"));
            }
            var bonuses = r.Skills.GearBonuses.Where(b => !b.Arcana && b.Slot == slots[i].Slot).ToList();
            for (var j = 0; j < bonuses.Count; j++)
            {
                var b = bonuses[j];
                var m = moves.FirstOrDefault(x => x.Item == i && x.Kind == "compétence" && x.Index == j);
                soulLines.Add(Line("âme", $"{b.SkillName} +{b.Levels}", null, m, skipped[i] ? OptAdvice.Skip : locked[i] ? OptAdvice.Locked : OptAdvice.Keep,
                    b.Useful ? $"garde : {b.Verdict}" : $"compétence peu utile pour toi : {b.Verdict}"));
            }
            var stoneLines = new List<OptLine>();
            for (var j = 0; j < Math.Max(item.MagicStoneSlots, stones[i].Count); j++)
            {
                var m = moves.FirstOrDefault(x => x.Item == i && x.Kind == "pierre" && x.Index == j);
                var current = j < stones[i].Count ? stones[i][j] : default;
                stoneLines.Add(Line("pierre", current.Effect is { } se ? $"{se.Raw} ({db.Grade(current.Grade).Name.Split(' ')[0].ToLowerInvariant()})" : "", current.Effect?.StatKey, m,
                    skipped[i] || !keepStones[i] ? OptAdvice.Skip : OptAdvice.Keep,
                    current.Effect is { IsStat: true } ce && byKey.ContainsKey(ce.StatKey!) ? $"garde : {Why(ce.StatKey!).ToLowerInvariant()}" : "garde"));
            }
            items.Add(new OptItem(slots[i].Slot, slots[i].SlotName, item, item.SoulLineCount > 0 ? item.SoulBindRate : null, notes[i], soulLines, stoneLines));
        }

        OptLine Line(string kind, string current, string? key, Move? m, OptAdvice idle, string keepWhy)
        {
            if (m?.Target is not { } target)
                return new(kind, current, key, current.Length == 0 && idle == OptAdvice.Keep ? OptAdvice.Skip : idle, null, 0, [], 0,
                    idle switch { OptAdvice.Locked => "verrouillée (lien d'âme à 100 %)", OptAdvice.Skip => current.Length == 0 ? "emplacement libre" : "pièce à remplacer ou provisoire", _ => keepWhy });
            var why = (m.Remove is { } r2 && goals.GetValueOrDefault(r2).Cap is { } cap && now[r2] > cap * 1.02 ? $"{db.StatName(r2)} déjà au-delà de son plafond ; "
                       : m.Remove is null && current.Length > 0 && kind == "âme" ? "ligne qui ne te sert pas ; " : "") + Why(target);
            return new(kind, current, key, current.Length == 0 ? OptAdvice.Fill : OptAdvice.Change, target, m.Candidates[target], m.Alternatives, m.Gain, why);
        }

        // Stats : avant → après, celles qui comptent ou qui bougent.
        var touched = moves.Where(m => m.Target is not null).SelectMany(m => new[] { m.Target!, m.Remove }).OfType<string>().ToHashSet();
        var statRows = sp.Stats.Where(x => p.Weight(x.Def.Key) >= 0.6 || touched.Contains(x.Def.Key) || goals[x.Def.Key].Cap is not null && now[x.Def.Key] > goals[x.Def.Key].Cap)
            .Select(x => new OptStat(x.Def, now[x.Def.Key], totals[x.Def.Key], goals[x.Def.Key].Target, goals[x.Def.Key].Label, goals[x.Def.Key].Cap, p.Weight(x.Def.Key)))
            .OrderByDescending(x => x.Weight).ToList();

        // Compétences importantes à un niveau d'une spécialisation : une ligne d'âme « +1 » la débloquerait.
        var specLevels = new[] { 8, 12, 16, 20 };
        var skillNotes = r.Skills.Skills.Where(a => a.Skill.Acquired && a.Priority >= 0.85 && specLevels.Contains(a.Total + 1))
            .OrderByDescending(a => a.Priority).Select(a => $"{a.Skill.Name} est au niveau {a.Total} : une option d'âme « {a.Skill.Name} +1 » la ferait passer au niveau {a.Total + 1} (nouvelle spécialisation).")
            .ToList();

        return new OptimizationPlan(items.OrderByDescending(x => x.Gain).ThenBy(x => x.SlotName).ToList(), statRows, skillNotes);
    }
}
