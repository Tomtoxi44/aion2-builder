namespace Aion2Builder.Combat;

/// <summary>
/// Un combat relevé par un meter (NotMeter : fenêtre « Détails du combat »), lu sur des captures d'écran.
/// Les captures ne montrent que ce que la fenêtre affiche : une liste de sorts défilée sur plusieurs captures est fusionnée.
/// </summary>
public sealed record CombatLog
{
    public string Source { get; init; } = "NotMeter";
    public DateTime ImportedAtUtc { get; init; }
    public string Character { get; init; } = "";
    public string Target { get; init; } = "";
    /// <summary>Durée du combat, en secondes.</summary>
    public double Duration { get; init; }
    public double TotalDamage { get; init; }
    public double Dps { get; init; }
    public int Hits { get; init; }
    /// <summary>Nombre de sorts annoncé par le meter (« Compétences 24 ») ; 0 s'il n'a pas été lu.</summary>
    public int SkillCount { get; init; }
    public List<CombatSkill> Skills { get; init; } = [];
    public List<CombatBuff> Buffs { get; init; } = [];
    /// <summary>Captures dont le combat a été lu.</summary>
    public int Pages { get; init; }

    // Combats du Pet Meter seulement (lus dans le trafic du jeu, pas sur la fenêtre NotMeter) :
    /// <summary>Fichier du Pet Meter d'où vient le combat (pour ne pas l'importer deux fois).</summary>
    public string? SourceFile { get; init; }
    /// <summary>Coups reçus pendant le combat : total, nombre, le plus gros ; soins sur toi.</summary>
    public double? DamageTaken { get; init; }
    public int? HitsTaken { get; init; }
    public double? MaxTaken { get; init; }
    public double? Heals { get; init; }
    /// <summary>Tes lancers, dans l'ordre : secondes depuis le début du combat et sort.</summary>
    public List<CombatCast>? Casts { get; init; }

    /// <summary>Tous les sorts du combat ont été lus (sinon : faire défiler la liste et ajouter une capture).</summary>
    public bool Complete => SkillCount > 0 && Skills.Count >= SkillCount;
}

/// <summary>Un sort du combat : dégâts, part des dégâts (0 à 1), utilisations, coups, critique et coups de dos (0 à 1).</summary>
public sealed record CombatSkill(string Name, int? Level, double Damage, double Share, int Uses, int Hits, double? Crit, double? Back, double? IntervalMs);

/// <summary>Un buff du combat : temps actif (0 à 1 ; null si l'OCR ne l'a pas lu), nombre d'applications, niveau.</summary>
public sealed record CombatBuff(string Name, double? Uptime, int Count, int? Level);

/// <summary>Un lancer du combat : secondes depuis le début, code et nom du sort.</summary>
public sealed record CombatCast(double T, long Code, string Name);

/// <summary>Mot reconnu sur une capture, avec sa position en pixels.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);
