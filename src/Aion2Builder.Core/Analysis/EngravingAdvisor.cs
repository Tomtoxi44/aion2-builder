using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

public enum LineVerdict { Good, Medium, Poor }

/// <summary>Ce qu'il faut faire des gravures d'âme d'une pièce (Skip : pièce à remplacer, rien à y dépenser).</summary>
public enum SoulAction { None, RaiseRate, Sync, Reset, Locked, Skip }

/// <summary>Une ligne de gravure (option d'âme ou pierre) jugée selon l'importance de sa stat pour la classe.</summary>
public sealed record EngravingLine(Effect Effect, string StatName, double Weight, LineVerdict Verdict, string? Grade = null);

public sealed record ItemEngraving(
    string Slot, string SlotName, EquippedItem Item,
    List<EngravingLine> SoulLines, double? SoulBindRate, SoulAction Action, string SoulAdvice, List<string> BetterOptions,
    List<EngravingLine> Stones, int StoneSlots, string StoneKind, string? StoneAdvice, List<string> BetterStones,
    string? StoneUpgrade, double Quality, double Priority)
{
    public bool HasSoulBinding => Item.SoulLineCount > 0;
    public bool NeedsWork => Action is SoulAction.Sync or SoulAction.Reset || StoneAdvice is not null;
    public IEnumerable<EngravingLine> PoorLines => SoulLines.Where(l => l.Verdict == LineVerdict.Poor);
}

public sealed record EngravingPlan(List<ItemEngraving> Items)
{
    public ItemEngraving? For(string slot) => Items.FirstOrDefault(i => i.Slot == slot);
}

/// <summary>
/// Gravures d'âme (options aléatoires d'une pièce, « lien d'âme » de 0 à 100 %) et pierres gravées.
/// Le lien d'âme fait monter les valeurs jusqu'au maximum (100 % = tout au maximum, options verrouillées) ;
/// la synchronisation remplace une ligne (une pièce du même grade est sacrifiée), la réinitialisation relance tout
/// et remet le taux à 0. D'où l'ordre économe : corriger les mauvaises options d'abord, monter le lien d'âme ensuite.
/// </summary>
public sealed class EngravingAdvisor(GameDatabase db)
{
    static readonly Dictionary<string, double> GroupWeight = new() { ["Weapon"] = 1.3, ["Accessory"] = 1.1, ["Armor"] = 1.0, ["Special"] = 0.9 };

    /// <summary>Importance d'une stat pour la classe ; une stat principale (Force…) vaut un peu moins que la stat qu'elle augmente.</summary>
    public double Weight(string? statKey, ResolvedProfile p)
    {
        if (statKey is null) return 0;
        var def = db.Stat(statKey);
        if (def?.Category == StatCategory.Primary) return def.Proxy is { } proxy ? p.Weight(proxy) * 0.8 : 0.3;
        return p.Weight(statKey);
    }

    LineVerdict Verdict(double weight)
    {
        var rules = db.Progression.Engraving;
        return weight >= rules.GoodWeight ? LineVerdict.Good : weight < rules.PoorWeight ? LineVerdict.Poor : LineVerdict.Medium;
    }

    EngravingLine Line(Effect e, ResolvedProfile p, string? grade = null)
    {
        var weight = Weight(e.StatKey, p);
        return new EngravingLine(e, e.StatKey is { } k ? db.StatName(k) : e.Raw, weight, e.StatKey is null ? LineVerdict.Medium : Verdict(weight), grade);
    }

    /// <summary>La stat jugée pour une stat principale : celle qu'elle augmente.</summary>
    string Proxy(string key) => db.Stat(key) is { Category: StatCategory.Primary, Proxy: { } proxy } ? proxy : key;

    /// <summary>Famille d'une stat (une stat principale prend celle de la stat qu'elle augmente).</summary>
    StatCategory? Category(string key) => db.Stat(key) is { } def
        ? def.Category == StatCategory.Primary && def.Proxy is { } proxy ? db.Stat(proxy)?.Category : def.Category
        : null;

    public EngravingPlan Plan(CharacterSnapshot c, ResolvedProfile p, GearPlan gear, StatProfile sp)
    {
        var rules = db.Progression.Engraving;
        var tolerance = db.Settings.Stats.CategoryTolerance;
        // Pour choisir quoi viser : l'importance de la stat, relevée si sa famille est en manque, baissée si elle est en trop.
        // Avec des joueurs comparés, ce sont eux qui disent ce qui manque, stat par stat (comme l'écran Stats) ; sans joueurs, la
        // part de la famille face à sa part visée.
        var players = sp.Comparison.Peers is not null;
        var gaps = sp.Categories.ToDictionary(x => x.Category, x => x.Gap);
        double Need(string key) => players
            ? sp.Comparison.PlayerVerdict(Proxy(key)) switch { < 0 => 1.4, > 0 => 0.7, _ => 1 }
            : Category(key) is { } cat && gaps.TryGetValue(cat, out var gap) ? Math.Clamp(1 - 2 * gap, 0.6, 1.5) : 1;
        var lacking = sp.Categories.Where(x => x.Gap < -tolerance).Select(x => x.Category).ToHashSet();
        var surplus = sp.Categories.Where(x => x.Gap > tolerance).Select(x => x.Category).ToHashSet();
        List<string> Rank(IEnumerable<string> keys) => keys.Select(k => (Key: k, W: Weight(k, p)))
            .Where(x => x.W >= rules.GoodWeight).OrderByDescending(x => x.W * Need(x.Key)).Take(3).Select(x => x.Key).ToList();
        // Nom de la stat, suivi de ce qui la rend utile ou non ici : sa place face aux joueurs, sinon l'état de sa famille.
        string Note(string k) => players
            ? sp.Comparison.PlayerVerdict(Proxy(k)) switch { < 0 => " (sous la moyenne des joueurs)", > 0 => " (déjà au-dessus de la moyenne, à défaut)", _ => "" }
            : Category(k) is { } cat
                ? lacking.Contains(cat) ? $" ({GameDatabase.CategoryName(cat)} en manque)" : surplus.Contains(cat) ? $" ({GameDatabase.CategoryName(cat)} déjà en trop, à défaut)" : ""
                : "";
        string Aim(IEnumerable<string> keys, string separator) => string.Join(separator, keys.Select(k => db.StatName(k) + Note(k)));
        // Pierres : elles ne passent pas au transfert, donc seulement sur les pièces gardées (pas celles que le palier accessible remplacera).
        var available = GearRoadmap.AvailableTier(db, c.ItemLevel);
        // Options vues sur tes propres pièces : elles complètent les listes relevées ailleurs.
        var observed = c.Equipment.Where(i => i.Category is not null).GroupBy(i => i.Category!)
            .ToDictionary(g => g.Key, g => g.SelectMany(i => i.SubStats).Where(e => e.IsStat).Select(e => e.StatKey!).ToHashSet());
        var maxWeight = Math.Max(0.01, p.Weights.Values.DefaultIfEmpty(1).Max());

        var items = new List<ItemEngraving>();
        foreach (var slot in gear.Slots.Where(s => s.Item is { HasDetail: true }))
        {
            var item = slot.Item!;
            if (item.SoulLineCount == 0 && item.MagicStoneSlots == 0 && item.Stones.Count == 0) continue;

            var lines = item.SubStats.Select(e => Line(e, p)).ToList();
            var stones = item.Stones.Select(s => Line(s.Effect, p, s.Grade)).ToList();
            var isSoulStone = item.Stones.Count > 0 ? item.Stones[0].IsSoulStone : slot.Group == "Accessory";
            var stoneKind = isSoulStone ? "pierres d'âme" : "pierres de mana";

            // Options à viser : celles possibles sur ce type de pièce, utiles à la classe, absentes de la pièce.
            var pool = new HashSet<string>(item.Category is { } cat && rules.SoulPools.TryGetValue(cat, out var known) ? known
                : slot.Slot == "MainHand" ? rules.WeaponPool : []);
            if (item.Category is { } category && observed.TryGetValue(category, out var mine)) pool.UnionWith(mine);
            var present = lines.Select(l => l.Effect.StatKey).ToHashSet();
            var betterKeys = Rank(pool.Where(k => !present.Contains(k)));
            var stoneKeys = Rank(rules.StonePool);
            var better = betterKeys.Select(db.StatName).ToList();
            var betterStones = stoneKeys.Select(db.StatName).ToList();

            var poor = lines.Where(l => l.Verdict == LineVerdict.Poor).ToList();
            var rate = item.SoulBindRate ?? 0;
            var rateText = $"{Fmt.Num(rate)} %";
            var aim = better.Count > 0 ? $" Vise : {Aim(betterKeys, ", ")}." : "";
            var poorNames = string.Join(", ", poor.Select(l => l.StatName));
            var replaced = slot.Status == SlotStatus.Replace;
            SoulAction action;
            string advice;
            if (replaced)
            {
                action = SoulAction.Skip;
                advice = "Pièce à remplacer : ne dépense rien dans ses gravures ni ses pierres, garde tes livres et tes pierres pour la suivante.";
            }
            else if (item.SoulLineCount == 0)
            {
                action = SoulAction.None;
                advice = "Pas de gravure d'âme sur cette pièce.";
            }
            else if (rate >= 100)
            {
                action = SoulAction.Locked;
                advice = poor.Count == 0
                    ? "Lien d'âme à 100 % : options utiles, toutes au maximum."
                    : $"Lien d'âme à 100 % : options verrouillées, « {poorNames} » ne se change plus. Remplace la pièce le jour où elle pèse trop.";
            }
            else if (poor.Count >= 2 && rate < rules.ResetBelowRate)
            {
                action = SoulAction.Reset;
                advice = $"{poor.Count} options sur {lines.Count} servent peu ({poorNames}) et le lien d'âme n'est qu'à {rateText} : réinitialise " +
                         $"(le taux repart de 0, tu perds peu), puis monte le lien d'âme une fois de bonnes options obtenues.{aim}";
            }
            else if (poor.Count >= 1)
            {
                var worst = poor.OrderBy(l => l.Weight).First();
                action = SoulAction.Sync;
                advice = $"Synchronise la ligne « {worst.StatName} » (une pièce du même grade est sacrifiée) avant de monter le lien d'âme ({rateText}).{aim}";
            }
            else if (rate < 100)
            {
                action = SoulAction.RaiseRate;
                advice = $"Options utiles : monte le lien d'âme ({rateText} → 100 %) avec des livres de gravure ; les valeurs grimpent jusqu'au maximum.";
            }
            else
            {
                action = SoulAction.None;
                advice = "Options utiles.";
            }

            // Pièce en retard sur le palier : elle partira peut-être bientôt, inutile d'y mettre beaucoup.
            if (slot.Status == SlotStatus.Weak && action is SoulAction.Reset or SoulAction.Sync or SoulAction.RaiseRate)
                advice += " Pièce en retard sur tes autres pièces : si tu la remplaces bientôt, n'y dépense rien.";

            var empty = Math.Max(0, item.MagicStoneSlots - item.Stones.Count);
            var poorStones = stones.Where(s => s.Verdict == LineVerdict.Poor).ToList();
            var commonStones = stones.Where(s => s.Verdict != LineVerdict.Poor && s.Grade == "Common").ToList();
            var bestStones = betterStones.Count > 0 ? Aim(stoneKeys.Take(2), " ou ") : "une stat utile à ta classe";
            var stoneParts = new List<string>();
            // Pièce provisoire (le palier accessible la remplacera, par transfert ou non) : ses pierres seraient perdues.
            var keepStones = !replaced && GearRoadmap.TierSource(db, slot.Slot, slot.Group, item, available) is null;
            if (keepStones && empty > 0) stoneParts.Add($"{Fmt.Plural(empty, "emplacement libre", "emplacements libres")} : grave des {stoneKind} de {bestStones}.");
            if (keepStones && poorStones.Count > 0) stoneParts.Add($"Remplace {string.Join(", ", poorStones.Select(s => s.Effect.Raw))} par {bestStones}.");
            if (!keepStones)
            {
                poorStones.Clear();
                commonStones.Clear();
                empty = 0;
            }
            // Grade commun : gain faible, à faire quand une meilleure pierre tombe (hors « à revoir »).
            var upgrade = !keepStones || commonStones.Count == 0 ? null
                : $"Grade commun : {string.Join(", ", commonStones.Select(s => s.Effect.Raw))}. {(commonStones.Count > 1 ? "Remplace-les" : "Remplace-la")} par la même stat en grade supérieur quand tu en obtiens (gain faible, pas prioritaire).";

            var quality = lines.Count == 0 ? 1 : lines.Average(l => Math.Min(1, l.Weight / maxWeight));
            var gw = GroupWeight.GetValueOrDefault(slot.Group, 1) * (slot.Status == SlotStatus.Weak ? 0.6 : 1);
            var priority = replaced ? 0 : gw * (10 * poor.Count + (action == SoulAction.Reset ? 10 : 0) + 6 * poorStones.Count + 8 * empty + commonStones.Count
                                                + (action == SoulAction.RaiseRate ? (100 - rate) / 10 : 0));
            items.Add(new ItemEngraving(slot.Slot, slot.SlotName, item, lines, item.SoulBindRate, action, advice, better,
                stones, item.MagicStoneSlots, stoneKind, stoneParts.Count == 0 ? null : string.Join(" ", stoneParts), betterStones, upgrade, quality, priority));
        }
        return new EngravingPlan(items.OrderByDescending(i => i.Priority).ToList());
    }

    static string Icon(LineVerdict v) => v switch { LineVerdict.Good => "✓", LineVerdict.Poor => "✕", _ => "~" };

    public static string Describe(EngravingLine l) => $"{Icon(l.Verdict)} {l.Effect.Raw}";

    public List<Finding> Findings(EngravingPlan plan, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        foreach (var e in plan.Items.Where(e => e.Action is SoulAction.Reset or SoulAction.Sync).Take(5))
            findings.Add(new Finding(Area.Gear, e.Action == SoulAction.Reset || e.Item.Slot == "MainHand" ? Severity.Important : Severity.Advice,
                $"{e.SlotName} : gravures d'âme à revoir",
                $"{e.Item.Name} — lien d'âme {Fmt.Num(e.SoulBindRate ?? 0)} % : {string.Join(" · ", e.SoulLines.Select(Describe))}",
                e.SoulAdvice, 30 + e.Priority));

        var raise = plan.Items.Where(e => e.Action == SoulAction.RaiseRate).OrderBy(e => e.SoulBindRate).ToList();
        if (raise.Count > 0)
            findings.Add(new Finding(Area.Gear, Severity.Advice, $"Lien d'âme à monter ({Fmt.Plural(raise.Count, "pièce")} aux bonnes options)",
                string.Join(" · ", raise.Select(e => $"{e.SlotName} {Fmt.Num(e.SoulBindRate ?? 0)} %")),
                $"Monte d'abord « {raise[0].SlotName} » ({Fmt.Num(raise[0].SoulBindRate ?? 0)} %) : options déjà bonnes, chaque livre de gravure y rapporte. " +
                "Ne monte pas le lien d'âme d'une pièce dont les options sont mauvaises : une réinitialisation le remettrait à 0.", 28));

        var stones = plan.Items.Where(e => e.StoneAdvice is not null).ToList();
        if (stones.Count > 0)
            findings.Add(new Finding(Area.Gear, stones.Any(e => e.Stones.Count < e.StoneSlots) ? Severity.Important : Severity.Advice,
                $"Pierres gravées à revoir ({Fmt.Plural(stones.Count, "pièce")})",
                string.Join("\n", stones.Take(6).Select(e => $"{e.SlotName} ({e.Stones.Count}/{e.StoneSlots} {e.StoneKind}) : {e.StoneAdvice}")),
                $"Les pierres comptent dans tes stats : vise {string.Join(", ", stones[0].BetterStones)} pour un {p.Class.Name} en {GameDatabase.ModeName(p.Mode)}.",
                25 + stones.Max(e => e.Priority) / 2) { Need = Need.Stones });

        var skipped = plan.Items.Where(e => e.Action == SoulAction.Skip).ToList();
        if (skipped.Count > 0)
            findings.Add(new Finding(Area.Gear, Severity.Info, $"Gravures : rien à dépenser sur {Fmt.Plural(skipped.Count, "pièce")} à remplacer",
                string.Join(", ", skipped.Select(e => e.SlotName)),
                "Garde tes livres de gravure et tes pierres pour les pièces qui les remplaceront."));
        return findings;
    }
}
