using System.Globalization;
using System.Text.RegularExpressions;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Import;

/// <summary>
/// Traduit les libellés du site officiel en effets chiffrés :
/// "PV +600", "Augmentation de l'ATQ +1.7%", "Temps de recharge -0.5%", "ATQ JcJ +4", "Cri de l'âme +1".
/// </summary>
public sealed partial class EffectParser(GameDatabase data)
{
    [GeneratedRegex(@"^(?<label>.*?)\s*(?<sign>[+\-])\s*(?<num>\d+(?:[.,]\d+)?)\s*(?<pct>%)?\s*$")]
    private static partial Regex EffectRegex();

    /// <param name="skillLookup">Renvoie le nom de compétence correspondant à un libellé, ou null.</param>
    /// <param name="skillNode">Case Daevanion de type "SkillLevel" : le libellé est un nom de compétence.</param>
    public Effect Parse(string raw, Func<string, string?>? skillLookup = null, bool skillNode = false)
    {
        var m = EffectRegex().Match(raw.Trim());
        if (!m.Success) return new Effect(raw, null, StatScope.All, 0, false);

        var label = m.Groups["label"].Value.Trim();
        // Le signe "-" décrit une réduction ("Temps de recharge -0.5%", "Coût en PM -0.7%") : c'est un bonus.
        var value = Math.Abs(ParseNumber(m.Groups["num"].Value));
        var percent = m.Groups["pct"].Success;

        if (!skillNode)
        {
            var (key, scope) = ResolveLabel(label);
            if (key is not null) return new Effect(raw, key, scope, value, percent);
        }
        var skill = skillLookup?.Invoke(label) ?? (skillNode ? label : null);
        return new Effect(raw, null, StatScope.All, value, percent, skill);
    }

    /// <summary>
    /// Un effet resté sans stat à l'import (libellé inconnu de la version qui l'a importé), relu avec le catalogue actuel :
    /// « ATQ +3 » comme une stat d'objet sans signe (« ATQ frontale 82 »). Rendu tel quel s'il reste inconnu.
    /// </summary>
    public Effect Recognize(Effect e)
    {
        if (e.IsStat || e.IsSkill || e.Raw.Length == 0) return e;
        if (Parse(e.Raw) is { IsStat: true } signed) return signed;
        var m = UnsignedRegex().Match(e.Raw.Trim());
        if (!m.Success || ResolveLabel(m.Groups["label"].Value) is not { Key: { } key } found) return e;
        return new Effect(e.Raw, key, found.Scope, ParseNumber(m.Groups["num"].Value), m.Groups["pct"].Success);
    }

    [GeneratedRegex(@"^(?<label>.+?)\s+(?<num>\d+(?:[.,]\d+)?)\s*(?<pct>%)?$")]
    private static partial Regex UnsignedRegex();

    public (string? Key, StatScope Scope) ResolveLabel(string label)
    {
        var (baseLabel, scope) = SplitScope(label);
        return (data.StatByLabel(baseLabel)?.Key, scope);
    }

    /// <summary>Identifiants des fiches objet : "HPMax", "PvEAmplifyDamage" (préfixe JcE/JcJ géré).</summary>
    public (string? Key, StatScope Scope) ResolveApiId(string id)
    {
        if (data.StatByApiId(id) is { } stat) return (stat.Key, StatScope.All);
        foreach (var (prefix, scope) in new[] { ("PvE", StatScope.PvE), ("PvP", StatScope.PvP) })
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && data.StatByApiId(id[prefix.Length..]) is { } scoped)
                return (scoped.Key, scope);
        return (null, StatScope.All);
    }

    /// <summary>Stat d'une fiche objet : valeur + bonus d'enchantement ("extra"). Les plages min-max d'arme sont moyennées.</summary>
    public Effect FromItemStat(string id, string name, string? value, string? extra, string? minValue)
    {
        var (key, scope) = ResolveApiId(id);
        if (key is null) (key, scope) = ResolveLabel(name);

        var percent = value?.Contains('%') == true;
        var v = ParseNumber(value);
        var bonus = ParseNumber(extra);
        if (id == "WeaponFixingDamage" && minValue is not null && ParseNumber(minValue) is var min && min > 0 && min < v)
            v = (min + v) / 2;

        var raw = bonus > 0 ? $"{name} {value} (+{extra})" : $"{name} {value}";
        return new Effect(raw, key, scope, v + bonus, percent);
    }

    static (string Label, StatScope Scope) SplitScope(string label)
    {
        var t = label.Trim();
        if (t.EndsWith(" JcJ", StringComparison.OrdinalIgnoreCase)) return (t[..^4].TrimEnd(), StatScope.PvP);
        if (t.EndsWith(" JcE", StringComparison.OrdinalIgnoreCase)) return (t[..^4].TrimEnd(), StatScope.PvE);
        return (t, StatScope.All);
    }

    public static double ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim().TrimEnd('%').Replace(',', '.').Replace("+", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
