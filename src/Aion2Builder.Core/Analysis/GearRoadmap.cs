using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

public enum RouteKind { Level, Enchant, Replace, Fill }

/// <summary>Une action vers le palier suivant, avec son gain de score estimé (1 niveau d'objet ou d'enchantement = 1 point).</summary>
public sealed record RouteStep(RouteKind Kind, string? Slot, string Title, string Detail, int Gain);

/// <summary>
/// Une pièce face au palier accessible : niveau d'objet visé et où le trouver, puissance après remplacement (enchantement
/// transféré si la pièce est unique) et gain. Provisional = la pièce sera remplacée par le palier accessible.
/// </summary>
public sealed record SlotRoute(string Slot, string SlotName, string Group, EquippedItem? Item, int Power, GearTier? Target, string Where,
    int NewPower, int Gain, bool Provisional, bool Special);

public enum EnchantVerdict { Raise, Replace, Stop, Push, Transmute, Done, Optional }

/// <summary>Conseil d'enchantement d'une pièce : niveau visé, verdict et gain de score jusqu'au niveau visé.</summary>
public sealed record EnchantPlan(string Slot, string SlotName, EquippedItem Item, int Max, int Target, EnchantVerdict Verdict, string Text, int Gain);

public sealed record GearRoadmapPlan(int Score, int Level, int MaxLevel, GearScoreGate? Reached, GearScoreGate? Next, GearTier? Available, GearTier? NextTier,
    List<RouteStep> Steps, List<SlotRoute> Slots, List<EnchantPlan> Enchants, List<(string SlotName, int Count)> SacredSlots);

/// <summary>
/// Feuille de route d'équipement : où en est le score d'équipement face aux seuils qui ouvrent du contenu, ce qu'il faut
/// remplacer (et où), enchanter ou arrêter d'enchanter, chiffré en points de score pour atteindre le palier suivant.
/// Les seuils et les sources viennent des guides (données « roadmap » de progression.json).
/// </summary>
public static class GearRoadmap
{
    public static GearRoadmapPlan Build(GameDatabase db, AdviceReport r)
    {
        var rules = db.Progression.Roadmap;
        var c = r.Character;
        var score = c.ItemLevel;
        var reached = rules.Gates.LastOrDefault(g => g.Score <= score);
        var next = rules.Gates.FirstOrDefault(g => g.Score > score);
        var available = rules.Tiers.Where(t => t.Gate is { } gate && gate <= score).MaxBy(t => t.ItemLevel);
        var nextTier = rules.Tiers.Where(t => t.Gate is not null && t.ItemLevel > (available?.ItemLevel ?? 0)).MinBy(t => t.ItemLevel);

        var slots = r.Gear.Slots.Select(p => Route(db, r, p, available, nextTier)).ToList();
        var enchants = slots.Where(x => x.Item is { HasDetail: true }).Select(x => Enchant(rules, x)).ToList();

        var steps = new List<RouteStep>();
        if (c.Level < rules.MaxLevel) steps.Add(new RouteStep(RouteKind.Level, null, $"Monter au niveau {rules.MaxLevel}", rules.LevelUp, 0));
        // Enchanter jusqu'à +10 est garanti et peu cher : d'abord ; puis les remplacements, le plus gros gain d'abord.
        steps.AddRange(enchants.Where(e => e.Verdict is EnchantVerdict.Raise && e.Gain > 0).OrderByDescending(e => e.Gain)
            .Select(e => new RouteStep(RouteKind.Enchant, e.Slot, $"{e.SlotName} : +{e.Item.EnchantLevel} → +{e.Target}", e.Text, e.Gain)));
        steps.AddRange(slots.Where(x => x.Gain > 0 && x.Target is not null).OrderByDescending(x => x.Gain).Select(x => new RouteStep(
            x.Item is null ? RouteKind.Fill : RouteKind.Replace, x.Slot, $"{x.SlotName} : pièce {x.Target!.ItemLevel} · {x.Where}",
            x.Item is null ? "Emplacement vide."
                : $"{x.Item.Name} ({x.Item.ItemLevel} +{x.Item.EnchantLevel} = {x.Power}) → {x.NewPower}" +
                  (x.Item.Grade == "Unique" && x.Item.EnchantLevel > 0 ? $" en transférant le +{x.Item.EnchantLevel}" : ""),
            x.Gain)));

        var sacred = slots.Where(x => x.Item is { GodStoneSlots: > 0 }).Select(x => (x.SlotName, x.Item!.GodStoneSlots)).ToList();
        return new GearRoadmapPlan(score, c.Level, rules.MaxLevel, reached, next, available, nextTier, steps, slots, enchants, sacred);
    }

    static SlotRoute Route(GameDatabase db, AdviceReport r, SlotPlan p, GearTier? available, GearTier? nextTier)
    {
        var group = p.Slot == "SubHand" ? "Guard" : p.Group;
        var rule = db.Progression.SlotRules.GetValueOrDefault(p.Slot);
        // Runes, amulette et ceinture ne suivent pas les paliers des donjons : transmutation, ou règle propre (ceinture noble).
        if (p.Group == "Special" || rule is { Keep: true })
        {
            var where = rule?.Source ?? r.Gear.Stage.Gear.FirstOrDefault(g => g.Group == p.Group)?.For(r.Mode) ?? "";
            return new SlotRoute(p.Slot, p.SlotName, group, p.Item, p.Power, null, where, p.Power, 0, false, true);
        }
        if (available is null)
        {
            var where = r.Gear.Stage.Gear.FirstOrDefault(g => g.Group == p.Group)?.For(r.Mode) ?? "";
            return new SlotRoute(p.Slot, p.SlotName, group, p.Item, p.Power, null, where, p.Power, 0, false, false);
        }
        if (p.Item is null)
            return new SlotRoute(p.Slot, p.SlotName, group, null, 0, available, available.WhereFor(group), available.ItemLevel, available.ItemLevel, false, false);
        if (p.Item.HasDetail && p.Item.ItemLevel < available.ItemLevel)
        {
            // Le transfert (entre pièces uniques) garde l'enchantement : la nouvelle pièce arrive au même +.
            var kept = p.Item.Grade == "Unique" ? p.Item.EnchantLevel : 0;
            var power = available.ItemLevel + kept;
            return new SlotRoute(p.Slot, p.SlotName, group, p.Item, p.Power, available, available.WhereFor(group), power, Math.Max(0, power - p.Power), true, false);
        }
        return new SlotRoute(p.Slot, p.SlotName, group, p.Item, p.Power, nextTier, nextTier?.WhereFor(group) ?? "", p.Power, 0, false, false);
    }

    static EnchantPlan Enchant(RoadmapRules rules, SlotRoute x)
    {
        var item = x.Item!;
        var max = item.MaxEnchantLevel > 0 ? item.MaxEnchantLevel : rules.Enchant.Guaranteed;
        var safe = Math.Min(rules.Enchant.Guaranteed, max);
        var e = item.EnchantLevel;
        EnchantPlan Plan(int target, EnchantVerdict verdict, string text) =>
            new(x.Slot, x.SlotName, item, max, target, verdict, text, Math.Max(0, target - e));

        if (x.Slot is "Belt" or "Amulet")
            return e < safe
                ? Plan(safe, EnchantVerdict.Raise, $"Monte à +{safe} (garanti), puis transmutation vers le grade supérieur (repart de +0).")
                : Plan(e, EnchantVerdict.Transmute, "À +10 : transmute-la vers le grade supérieur (Transmutation de matière), elle repart de +0.");
        if (x.Special)
            return Plan(e, EnchantVerdict.Optional, "Rune : enchantement secondaire, les guides la laissent basse (à valider).");
        if (x.Provisional && item.Grade != "Unique")
            return Plan(e, EnchantVerdict.Replace,
                $"Pièce provisoire non unique : ne l'enchante plus, remplace-la (pièce {x.Target!.ItemLevel} · {x.Where}) ; son enchantement ne se transfère pas.");
        if (e < safe)
            return Plan(safe, EnchantVerdict.Raise, x.Provisional
                ? $"Monte à +{safe} (garanti) : le transfert le garde sur la pièce {x.Target!.ItemLevel}."
                : $"Monte à +{safe} : réussite garantie jusqu'à +{rules.Enchant.Guaranteed}.");
        if (e >= max) return Plan(e, EnchantVerdict.Done, $"Au maximum (+{max}).");
        return x.Provisional
            ? Plan(e, EnchantVerdict.Stop, $"Arrête à +{e} : pièce provisoire, l'enchantement passera par le transfert sur la pièce {x.Target!.ItemLevel}.")
            : Plan(max, EnchantVerdict.Push, $"+{e + 1} à +{max} : une chance par essai ({string.Join(", ", rules.Enchant.Rates.Select(v => $"{v} %"))}), 1 point de score par niveau ; à faire sur une pièce que tu gardes.");
    }
}
