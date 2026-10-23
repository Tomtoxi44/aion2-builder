using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Plan d'équipement par emplacement : on ne saute pas de palier (le dernier stuff n'est pas accessible
/// directement), donc on vise la rareté du palier actuel, pièce la plus faible d'abord, et on n'enchante
/// que ce qu'on garde.
/// </summary>
public sealed class GearAdvisor(GameDatabase db, StatAnalyzer stats)
{
    /// <summary>L'arme pèse le plus dans les stats, puis les accessoires (gardés plus longtemps).</summary>
    static readonly Dictionary<string, double> GroupWeight = new() { ["Weapon"] = 1.3, ["Accessory"] = 1.1, ["Armor"] = 1.0, ["Special"] = 0.9 };

    public ProgressionStage CurrentStage(CharacterSnapshot c)
    {
        var stages = db.Progression.Stages;
        return stages.LastOrDefault(s => c.Level >= s.MinLevel && c.Level <= s.MaxLevel && c.ItemLevel >= s.MinItemLevel)
               ?? stages.First();
    }

    public GearPlan Plan(CharacterSnapshot c, ResolvedProfile p, StatProfile sp)
    {
        var prog = db.Progression;
        var stage = CurrentStage(c);
        var index = prog.Stages.IndexOf(stage);
        var next = index + 1 < prog.Stages.Count ? prog.Stages[index + 1] : null;
        var targetRank = db.Grade(stage.TargetGrade).Rank;
        // Une pièce qui ne passera pas le palier suivant est de transition : enchantement plafonné.
        var keepRank = db.Grade((next ?? stage).TargetGrade).Rank;

        var bySlot = c.Equipment.GroupBy(i => i.Slot).ToDictionary(g => g.Key, g => g.First());
        var powers = prog.RequiredSlots.Select(s => bySlot.GetValueOrDefault(s)).Where(i => i is { HasDetail: true })
            .Select(i => i!.Power).OrderBy(x => x).ToList();
        var median = powers.Count == 0 ? 0 : powers[powers.Count / 2];

        var slots = new List<SlotPlan>();
        foreach (var slot in prog.RequiredSlots.Concat(prog.OptionalSlots))
        {
            var optional = !prog.RequiredSlots.Contains(slot);
            var group = db.SlotGroup(slot) ?? "Armor";
            var gw = GroupWeight.GetValueOrDefault(group, 1);
            var sourceNow = stage.Gear.FirstOrDefault(g => g.Group == group)?.For(p.Mode);
            var sourceNext = next?.Gear.FirstOrDefault(g => g.Group == group)?.For(p.Mode);
            var name = db.SlotName(slot);
            var rule = prog.SlotRules.GetValueOrDefault(slot);
            if (rule?.Source is { } ruleSource) sourceNow = sourceNext = ruleSource;

            if (!bySlot.TryGetValue(slot, out var item))
            {
                slots.Add(new SlotPlan(slot, name, group, null, 0, SlotStatus.Empty,
                    optional ? "Emplacement facultatif / pas encore débloqué." : "Emplacement vide : n'importe quelle pièce est un gros gain.",
                    sourceNow, 0, [], optional ? 1 : 100 * gw));
                continue;
            }

            var (fit, poor) = Fit(item, sp, p);
            var rank = db.Grade(item.Grade).Rank;
            var gap = item.HasDetail && median > 0 ? median - item.Power : 0;
            SlotStatus status;
            string advice;
            string? source;
            double priority;

            if (rule is { Keep: true })
            {
                // Pièce unique qui évolue au lieu d'être remplacée (ex. ceinture noble) : seul l'enchantement compte.
                var target = Math.Min(rule.EnchantTarget ?? prog.Enchant.SafeLevel, Math.Max(item.MaxEnchantLevel, rule.EnchantTarget ?? 0));
                if (item.EnchantLevel < target)
                {
                    var cost = EnchantCost(item.EnchantLevel, target);
                    status = SlotStatus.Enchant;
                    advice = $"À garder : {rule.Why} Enchante +{item.EnchantLevel} → +{target}.";
                    priority = (25 + 30.0 * (target - item.EnchantLevel) / (cost + 1)) * gw;
                }
                else
                {
                    status = SlotStatus.Ok;
                    advice = $"À garder : {rule.Why} Déjà +{item.EnchantLevel} : passe au grade supérieur.";
                    priority = 20;
                }
                source = rule.Source;
            }
            else if (rank > 0 && rank < targetRank)
            {
                status = SlotStatus.Replace;
                advice = $"Pièce de grade {db.Grade(item.Grade).Name.ToLowerInvariant()} : à ce palier, vise le {db.Grade(stage.TargetGrade).Name.ToLowerInvariant()}.";
                source = sourceNow;
                priority = (40 + 3 * Math.Max(gap, 0) + 10 * (targetRank - rank)) * gw;
            }
            else if (gap >= db.Settings.Gear.WeakSlotGap)
            {
                status = SlotStatus.Weak;
                advice = $"Niveau d'objet {item.Power} contre {median} en médiane : c'est ta pièce en retard.";
                source = sourceNow;
                priority = (35 + 3 * gap) * gw;
            }
            else if (EnchantCap(item, keepRank) is var cap && item.MaxEnchantLevel > item.EnchantLevel && item.EnchantLevel < cap)
            {
                var target = Math.Min(cap, item.MaxEnchantLevel);
                var cost = EnchantCost(item.EnchantLevel, target);
                status = SlotStatus.Enchant;
                advice = (cap < prog.Enchant.SafeLevel ? "Pièce de transition (remplacée au palier suivant) : " : "À garder : ")
                         + $"enchante +{item.EnchantLevel} → +{target} (coût relatif {Fmt.Num(cost)}).";
                source = sourceNext;
                priority = (15 + 30.0 * (target - item.EnchantLevel) / (cost + 1)) * gw;
            }
            else
            {
                status = SlotStatus.Ok;
                advice = next is null ? "Pièce au niveau du palier." : $"Au niveau du palier. Étape suivante : {next.Name}.";
                source = sourceNext;
                priority = 5;
            }

            if (poor.Count > 0 && item.RandomSubStats)
                advice += $" Stats secondaires peu utiles : {string.Join(", ", poor)}.";
            slots.Add(new SlotPlan(slot, name, group, item, item.Power, status, advice, source, fit, poor, priority));
        }
        return new GearPlan(stage, next, median, slots);
    }

    /// <summary>Niveau d'enchantement rentable : sûr pour une pièce gardée, plafonné pour une pièce de transition.</summary>
    public int EnchantCap(EquippedItem item, int keepRank) =>
        db.Grade(item.Grade).Rank >= keepRank ? db.Progression.Enchant.SafeLevel : db.Progression.Enchant.TransitionalMax;

    int EnchantGoal(string slot, EquippedItem item, int keepRank) =>
        db.Progression.SlotRules.GetValueOrDefault(slot)?.EnchantTarget is { } t ? t : Math.Min(EnchantCap(item, keepRank), item.MaxEnchantLevel);

    public double EnchantCost(int from, int to)
    {
        var cost = 0.0;
        for (var l = from; l < to; l++) cost += db.Progression.Enchant.Cost(l);
        return cost;
    }

    /// <summary>Adéquation des stats de la pièce à la classe (0..1) et stats secondaires peu utiles.</summary>
    (double Fit, List<string> Poor) Fit(EquippedItem item, StatProfile sp, ResolvedProfile p)
    {
        var effects = item.MainStats.Concat(item.SubStats).Where(e => e.IsStat && StatAnalyzer.Applies(e, p.Mode)).ToList();
        var total = effects.Sum(e => stats.EffectBudget(e, sp));
        var weighted = effects.Sum(e => stats.EffectValue(e, sp, p));
        var maxWeight = Math.Max(0.01, p.Weights.Values.DefaultIfEmpty(1).Max());
        var poor = item.SubStats.Where(e => e.IsStat && p.Weight(e.StatKey!) <= db.Settings.Stats.LowWeight)
            .Select(e => db.StatName(e.StatKey!)).Distinct().ToList();
        return (total > 0 ? weighted / total / maxWeight : 1, poor);
    }

    public List<Finding> Findings(GearPlan plan, CharacterSnapshot c, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        var stage = plan.Stage;
        var nextText = plan.NextStage is null
            ? "Dernier palier défini dans progression.json."
            : $"Niveau d'objet {c.ItemLevel} / {plan.NextStage.MinItemLevel} pour passer au palier « {plan.NextStage.Name} » ({Fmt.Pct(plan.ProgressToNext(c.ItemLevel))}).";
        findings.Add(new Finding(Area.Gear, Severity.Info, $"Palier actuel : {stage.Name}", $"{stage.Goal} {nextText}"));

        foreach (var s in plan.Slots.Where(s => s.Status == SlotStatus.Empty && s.Priority > 1))
            findings.Add(new Finding(Area.Gear, Severity.Critical, $"{s.SlotName} : emplacement vide", s.Advice,
                s.NextSource is null ? null : $"Va chercher : {s.NextSource}", s.Priority));

        foreach (var s in plan.Slots.Where(s => s.Status is SlotStatus.Replace or SlotStatus.Weak).OrderByDescending(s => s.Priority).Take(4))
            findings.Add(new Finding(Area.Gear, s.Priority >= 60 ? Severity.Important : Severity.Advice,
                $"{s.SlotName} : {Fmt.Status(s.Status).ToLowerInvariant()}",
                $"{s.Item!.Name} — {db.Grade(s.Item.Grade).Name}, niveau d'objet {s.Power}. {s.Advice}",
                s.NextSource is null ? null : $"Va chercher : {s.NextSource}", s.Priority));

        var keepRank = db.Grade((plan.NextStage ?? stage).TargetGrade).Rank;
        var enchants = plan.Slots.Where(s => s.Status == SlotStatus.Enchant).OrderByDescending(s => s.Priority).ToList();
        if (enchants.Count > 0)
            findings.Add(new Finding(Area.Enchant, Severity.Advice, $"Enchantements rentables ({enchants.Count} pièce(s))",
                "Pièces sous leur niveau d'enchantement rentable (sûr pour celles à garder, plafonné pour celles de transition) : " +
                string.Join(" · ", enchants.Take(6).Select(s => $"{s.SlotName} +{s.Item!.EnchantLevel} → +{EnchantGoal(s.Slot, s.Item, keepRank)}")),
                $"Enchante dans cet ordre (meilleur gain par ressource d'abord) : {string.Join(", ", enchants.Take(4).Select(s => s.SlotName))}.",
                enchants[0].Priority));

        var overInvested = plan.Slots.Where(s => s.Item is { } i && !db.Progression.SlotRules.ContainsKey(s.Slot) && db.Grade(i.Grade).Rank < keepRank
                                                 && i.EnchantLevel > db.Progression.Enchant.TransitionalMax).ToList();
        if (overInvested.Count > 0)
            findings.Add(new Finding(Area.Enchant, Severity.Info, "Pièces de transition déjà bien enchantées",
                $"{string.Join(", ", overInvested.Select(s => $"{s.SlotName} +{s.Item!.EnchantLevel}"))} : ce sont des pièces de transition (remplacées d'ici le palier suivant), n'y remets plus de ressources."));

        foreach (var s in plan.Slots.Where(s => s.Item is { RandomSubStats: true } && s.PoorStats.Count > 0).OrderBy(s => s.Fit).Take(2))
            findings.Add(new Finding(Area.Gear, Severity.Advice, $"{s.SlotName} : stats secondaires peu adaptées",
                $"{s.Item!.Name} porte {string.Join(", ", s.PoorStats)}, peu utile(s) pour un {p.Class.Name} en {GameDatabase.ModeName(p.Mode)} (adéquation {Fmt.Pct(s.Fit)}).",
                "Relance ces stats secondaires si possible, sinon priorise cette pièce au prochain remplacement.", 20 + 30 * (1 - s.Fit)));

        if (stage.Tips.Count > 0)
            findings.Add(new Finding(Area.Gear, Severity.Info, "Conseils du palier", string.Join(" ", stage.Tips)));
        return findings;
    }
}
