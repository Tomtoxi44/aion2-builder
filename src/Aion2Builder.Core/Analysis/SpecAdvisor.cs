using System.Globalization;
using System.Text.RegularExpressions;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Ce que fait une spécialisation, reconnu sur son texte.</summary>
public enum SpecKind { Combo, Damage, Cooldown, Crit, Speed, Control, Heal, Resource, Defense, Utility }

/// <summary>Une spécialisation jugée : type, note pour le rôle et le mode, débloquée ou non, conseillée (rang 1, 2, 3) ou pour plus tard.</summary>
public sealed record SpecChoice(SpellSpec Spec, SpecKind Kind, double Score, bool Unlocked, int? Rank, string Why)
{
    /// <summary>À prendre au prochain emplacement (le suivant à s'ouvrir).</summary>
    public bool Later { get; init; }
}

/// <summary>
/// Spécialisations d'une compétence : niveau total (et après les déplacements conseillés), emplacements ouverts
/// (1 au niveau 8, 2 au 12, 3 au 20), le prochain emplacement, et les 5 options, les meilleures conseillées.
/// </summary>
public sealed record SkillSpecPlan(SkillAdvice Skill, int Level, int LevelAfter, int Slots, int? NextSlot, List<SpecChoice> Options)
{
    public IEnumerable<SpecChoice> Recommended => Options.Where(o => o.Rank is not null).OrderBy(o => o.Rank);
}

/// <summary>
/// Conseil de spécialisations : le site officiel ne dit pas lesquelles sont équipées, on dit donc lesquelles prendre.
/// Chaque option est classée d'après son texte (dégâts, enchaînement ajouté, recharge, critique, vitesse, contrôle, soin,
/// PM, défense) puis notée selon le rôle de la classe et le mode : en JcE, ce qui fait des dégâts sur un boss d'abord
/// (le contrôle y sert peu) ; en JcJ, le contrôle et les dégâts. Règle simple, à confronter au jeu.
/// </summary>
public static class SpecAdvisor
{
    static readonly (SpecKind Kind, Regex Pattern)[] Rules =
    [
        (SpecKind.Combo, new Regex(@"enchaînement|\bactiver\b|déclench|activation supplémentaire|utilisations? consécutives?", RegexOptions.IgnoreCase)),
        (SpecKind.Control, new Regex(@"recul|étourdi|renvers|immobilis|ralenti|lenteur|silence|scell|aveugl|cécité|sommeil|peur|terreur|repouss|attir|attraction|entrav|gel|paralys|enracin|projet|VIT de déplacement -|VIT de combat -|RÉS aux altérations d'état -", RegexOptions.IgnoreCase)),
        (SpecKind.Cooldown, new Regex(@"recharge", RegexOptions.IgnoreCase)),
        (SpecKind.Heal, new Regex(@"soin|restaur|drain|récupér|PV \+|régénèr|régénération|dissip", RegexOptions.IgnoreCase)),
        (SpecKind.Resource, new Regex(@"\bPM\b|endurance", RegexOptions.IgnoreCase)),
        (SpecKind.Speed, new Regex(@"VIT de compétence|VIT d'incantation|vitesse d'incantation|VIT d'attaque|VIT de combat \+", RegexOptions.IgnoreCase)),
        (SpecKind.Crit, new Regex(@"\bCRIT\b|critique|coup parfait", RegexOptions.IgnoreCase)),
        (SpecKind.Defense, new Regex(@"DÉF|bouclier|DGT subis|réduction des DGT|tolérance|ténacité|blocage|esquive|invincib|résistance|immunité", RegexOptions.IgnoreCase)),
        (SpecKind.Damage, new Regex(@"DGT|dégâts|multi-coups|attaque|\bATQ\b|boost|fragilité|hémorragie|malédiction|précision de compétence|stats de l'Esprit|cibles?|zone", RegexOptions.IgnoreCase)),
    ];

    /// <summary>Poids de chaque type selon le rôle et le mode (1 = ce qu'on cherche en premier).</summary>
    static double Weight(SpecKind kind, string role, GameMode mode) => mode == GameMode.PvP
        ? kind switch
        {
            SpecKind.Control => 1.0, SpecKind.Combo or SpecKind.Damage => 0.9, SpecKind.Cooldown => 0.85, SpecKind.Crit => 0.8, SpecKind.Speed => 0.75,
            SpecKind.Heal => role == "healer" ? 1.0 : 0.55, SpecKind.Defense => role == "tank" ? 0.8 : 0.55, SpecKind.Resource => 0.35, _ => 0.4,
        }
        : role switch
        {
            "healer" => kind switch
            {
                SpecKind.Heal => 1.0, SpecKind.Cooldown => 0.9, SpecKind.Speed => 0.85, SpecKind.Resource => 0.7, SpecKind.Combo or SpecKind.Damage => 0.6,
                SpecKind.Crit => 0.55, SpecKind.Defense => 0.45, SpecKind.Control => 0.2, _ => 0.25,
            },
            "tank" => kind switch
            {
                SpecKind.Defense => 0.9, SpecKind.Combo or SpecKind.Damage => 0.85, SpecKind.Cooldown => 0.85, SpecKind.Crit => 0.7, SpecKind.Speed => 0.65,
                SpecKind.Heal => 0.6, SpecKind.Control => 0.35, SpecKind.Resource => 0.35, _ => 0.25,
            },
            _ => kind switch
            {
                SpecKind.Combo or SpecKind.Damage => 1.0, SpecKind.Cooldown => 0.9, SpecKind.Crit => 0.8, SpecKind.Speed => 0.75,
                SpecKind.Heal => role == "support" ? 0.6 : 0.35, SpecKind.Defense => 0.3, SpecKind.Resource => 0.3, SpecKind.Control => 0.25, _ => 0.2,
            },
        };

    /// <summary>Type d'une spécialisation ; les noms de sorts entre crochets sont retirés (« [Coup puissant au bouclier] » n'est pas une défense).</summary>
    public static SpecKind Classify(string text)
    {
        var plain = Regex.Replace(text, @"\[[^\]]*\]", "");
        return Rules.FirstOrDefault(r => r.Pattern.IsMatch(plain)) is { Pattern: not null } rule ? rule.Kind : SpecKind.Utility;
    }

    static string KindName(SpecKind kind) => kind switch
    {
        SpecKind.Combo => "enchaînement en plus", SpecKind.Damage => "dégâts", SpecKind.Cooldown => "recharge plus courte", SpecKind.Crit => "critique",
        SpecKind.Speed => "vitesse", SpecKind.Control => "contrôle", SpecKind.Heal => "soin ou vol de vie", SpecKind.Resource => "PM",
        SpecKind.Defense => "défense", _ => "utilité",
    };

    /// <summary>Note d'une option : poids du type, un peu moins si l'effet est conditionnel, un peu plus s'il est fort.</summary>
    static double Score(string text, SpecKind kind, string role, GameMode mode)
    {
        var score = Weight(kind, role, mode);
        // « si moins de cibles », « lors d'un coup CRIT », « 5 % de chances » : effet qui ne joue pas à chaque lancer.
        if (kind != SpecKind.Control && Regex.IsMatch(text, @"\bsi\b|lors d|chances?", RegexOptions.IgnoreCase)) score *= 0.85;
        var pct = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*%");
        if (pct.Success && double.TryParse(pct.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            score += Math.Min(v, 30) / 300;
        return score;
    }

    public static List<SkillSpecPlan> Plan(GameDatabase db, AdviceReport r)
    {
        var slots = db.Settings.Skills.SpecialtySlots;
        var role = r.Profile.Role.Key;
        var plans = new List<SkillSpecPlan>();
        foreach (var a in r.Skills.Skills.Where(a => a.Kind == SkillKind.Active && a.Skill.Acquired))
        {
            if (db.Spell(a.Skill.Id) is not { Specs.Count: > 0 } spell) continue;
            var level = a.Total;
            var open = slots.Count(l => l <= level);
            var choices = spell.Specs.Select(sp =>
            {
                var kind = Classify(sp.Text);
                return new SpecChoice(sp, kind, Score(sp.Text, kind, role, r.Mode), sp.Level <= level, null, KindName(kind));
            }).ToList();
            // Les meilleures options débloquées, autant que d'emplacements ouverts (avant le niveau 8 : celle à prendre au 8) ;
            // puis la meilleure des autres pour le prochain emplacement, si elle sera débloquée d'ici là.
            var next = slots.FirstOrDefault(l => l > level) is var n and > 0 ? n : (int?)null;
            var picked = open > 0
                ? choices.Where(c => c.Unlocked).OrderByDescending(c => c.Score).Take(open).ToList()
                : choices.Where(c => c.Spec.Level <= (next ?? 0)).OrderByDescending(c => c.Score).Take(1).ToList();
            var later = open > 0 && next is { } at
                ? choices.Except(picked).Where(c => c.Spec.Level <= at).OrderByDescending(c => c.Score).FirstOrDefault()
                : null;
            var options = choices.Select(c => picked.IndexOf(c) is >= 0 and var i ? c with { Rank = i + 1 } : c == later ? c with { Later = true } : c).ToList();
            plans.Add(new SkillSpecPlan(a, level, a.TotalAfter, open, next, options));
        }
        // Ce qui fait les dégâts d'abord (part mesurée ou simulée), puis l'importance pour la classe.
        return plans.OrderByDescending(p => p.Skill.DamageShare).ThenByDescending(p => p.Skill.Priority).ToList();
    }
}
