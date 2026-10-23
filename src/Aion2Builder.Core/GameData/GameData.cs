using System.Globalization;
using System.Text;
using Aion2Builder.Model;

namespace Aion2Builder.GameData;

public enum StatCategory { Offense, Defense, Sustain, Utility, Primary }
public enum StatKind { Flat, Percent }

public sealed record StatDef
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public StatCategory Category { get; init; }
    public StatKind Kind { get; init; }
    public double Unit { get; init; } = 1;
    public double PercentUnit { get; init; } = 1;
    public List<string> ApiIds { get; init; } = [];
    public List<string> Labels { get; init; } = [];
    /// <summary>À quoi sert la stat en jeu (infobulles).</summary>
    public string Desc { get; init; } = "";
    public List<string> Where { get; init; } = [];
    /// <summary>Stat principale : stat dont elle augmente le pourcentage (Force → ATQ), pour juger une ligne de gravure.</summary>
    public string? Proxy { get; init; }
    /// <summary>Poids par rôle et par mode quand le rôle ne le précise pas (stats ajoutées après coup).</summary>
    public Dictionary<string, Dictionary<GameMode, double>> RoleWeights { get; init; } = [];
}

public sealed record StatCatalogFile(List<StatDef> Stats);

public sealed record ModeProfile
{
    public double DefaultWeight { get; init; } = 0.1;
    public Dictionary<StatCategory, double> Targets { get; init; } = [];
    public Dictionary<string, double> Weights { get; init; } = [];
}

public sealed record RoleProfile(string Key, string Name, Dictionary<GameMode, ModeProfile> Modes);

public sealed record ClassProfile
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public List<string> Aliases { get; init; } = [];
    public required string Role { get; init; }
    public Dictionary<GameMode, Dictionary<string, double>> WeightOverrides { get; init; } = [];
    /// <summary>Répartition visée propre à la classe (remplace celle du rôle, famille par famille).</summary>
    public Dictionary<GameMode, Dictionary<StatCategory, double>> TargetOverrides { get; init; } = [];
    public Dictionary<GameMode, List<string>> CoreSkills { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public string? SkillsSource { get; init; }
    /// <summary>Rôle de chaque compétence de la classe (actives, passives, stigmas) ; vide = analyse générique.</summary>
    public List<SkillKnowledge> Skills { get; init; } = [];
}

/// <summary>Ce que l'on sait d'une compétence : son rôle, sa priorité (0 = inutile, 1 = indispensable) et le niveau visé par mode.</summary>
public sealed record SkillKnowledge
{
    public required string Name { get; init; }
    public string Kind { get; init; } = "Active";
    public string Role { get; init; } = "";
    public Dictionary<GameMode, double> Priority { get; init; } = [];
    public Dictionary<GameMode, int> Target { get; init; } = [];
    public string Why { get; init; } = "";
    public bool Unverified => Why.StartsWith("À valider", StringComparison.OrdinalIgnoreCase);
}

public sealed record ClassesFile(List<RoleProfile> Roles, List<ClassProfile> Classes);

public sealed record GradeDef(string Code, string Name, int Rank, string Color);

public sealed record StageGear(string Group, string PvE, string PvP)
{
    public string For(GameMode mode) => mode == GameMode.PvP ? PvP : PvE;
}

public sealed record ProgressionStage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int MinLevel { get; init; } = 1;
    public int MaxLevel { get; init; } = int.MaxValue;
    public int MinItemLevel { get; init; }
    public string TargetGrade { get; init; } = "Legend";
    /// <summary>Enchantement visé à ce palier pour les pièces à garder (au moins le niveau sûr).</summary>
    public int? EnchantTarget { get; init; }
    public string Goal { get; init; } = "";
    public List<StageGear> Gear { get; init; } = [];
    public List<string> Tips { get; init; } = [];
}

public sealed record EnchantRules(List<double> CostByLevel, int SafeLevel, int TransitionalMax)
{
    public double Cost(int fromLevel) =>
        CostByLevel.Count == 0 ? 1 : CostByLevel[Math.Clamp(fromLevel, 0, CostByLevel.Count - 1)];
}

public sealed record ProgressionFile
{
    public PetRules Pets { get; init; } = new();
    public EngravingRules Engraving { get; init; } = new();
    public List<GradeDef> Grades { get; init; } = [];
    public Dictionary<string, string> SlotGroups { get; init; } = [];
    public Dictionary<string, string> GroupNames { get; init; } = [];
    public Dictionary<string, string> SlotNames { get; init; } = [];
    public List<string> RequiredSlots { get; init; } = [];
    public List<string> OptionalSlots { get; init; } = [];
    public Dictionary<string, SlotRule> SlotRules { get; init; } = [];
    public required EnchantRules Enchant { get; init; }
    public List<ProgressionStage> Stages { get; init; } = [];
}

/// <summary>Effets de possession des familiers : chaque familier obtenu donne des stats selon son espèce et son niveau (1 à 3).</summary>
public sealed record PetRules
{
    public List<int> Tiers { get; init; } = [5, 30, 105];
    public Dictionary<string, PetGenus> Genera { get; init; } = [];
}

public sealed record PetGenus
{
    public string Name { get; init; } = "";
    public List<PetEffect> Effects { get; init; } = [];
    /// <summary>Effets hors combat ou mal connus : affichés, non comptés.</summary>
    public string? Other { get; init; }
}

/// <summary>Stat donnée par un familier de l'espèce, aux niveaux 1, 2 et 3.</summary>
public sealed record PetEffect(string Stat, List<double> Levels);

/// <summary>Gravures d'âme et pierres gravées : seuils de verdict et options possibles par type de pièce.</summary>
public sealed record EngravingRules
{
    public double GoodWeight { get; init; } = 0.5;
    public double PoorWeight { get; init; } = 0.25;
    public double ResetBelowRate { get; init; } = 50;
    public Dictionary<string, List<string>> SoulPools { get; init; } = [];
    public List<string> WeaponPool { get; init; } = [];
    public List<string> StonePool { get; init; } = [];
    public List<string> StoneGrades { get; init; } = [];
}

/// <summary>Règle propre à un emplacement (ex. la ceinture noble, qui évolue au lieu d'être remplacée).</summary>
public sealed record SlotRule(bool Keep, int? EnchantTarget, string? Source, string? Why);

public sealed record DaevanionSettings
{
    public Dictionary<string, string> BoardPools { get; init; } = [];
    public Dictionary<string, string> PoolNames { get; init; } = [];
    public Dictionary<string, double> NodeCost { get; init; } = [];
    public double SkillNodeValue { get; init; } = 2;
    public double CoreSkillMultiplier { get; init; } = 1.6;
    public double UnequippedSkillNodeValue { get; init; } = 0.2;
    public int MaxPathLength { get; init; } = 5;
    public int PlanLength { get; init; } = 15;
    public string PointSources { get; init; } = "";
    /// <summary>Gain minimal (en part de la valeur actuelle) pour conseiller de réinitialiser un plateau.</summary>
    public double RespecMinGain { get; init; } = 0.04;
    public double RespecMaxImportance { get; init; } = 0.4;
}

public sealed record SkillSettings
{
    public int BaseMax { get; init; } = 10;
    public int CostPerLevel { get; init; } = 1;
    public List<int> SpecialtyLevels { get; init; } = [8, 12, 16, 20];
    public string PointSources { get; init; } = "";
    public List<string> CommonPvpSkills { get; init; } = [];
    public List<string> FixedSlotSkills { get; init; } = [];
    public double UnknownActivePriority { get; init; } = 0.7;
    public double UnknownPassivePriority { get; init; } = 0.6;
    public double UnknownStigmaPriority { get; init; } = 0.6;
    /// <summary>Écart de priorité minimal pour conseiller de déplacer des points d'une compétence à une autre.</summary>
    public double ChangeThreshold { get; init; } = 0.15;
}

public sealed record StigmaSettings
{
    public int Slots { get; init; } = 4;
    public int MaxLevel { get; init; } = 20;
    public List<int> ShardCostByLevel { get; init; } = [];
    public string ShardSources { get; init; } = "";

    /// <summary>Fragments nécessaires pour passer du niveau <paramref name="from"/> au niveau <paramref name="to"/>.</summary>
    public int ShardCost(int from, int to)
    {
        var cost = 0;
        for (var l = Math.Max(from, 0); l < to && l < ShardCostByLevel.Count; l++) cost += ShardCostByLevel[l];
        return cost;
    }
}

public sealed record StatSettings
{
    public double CategoryTolerance { get; init; } = 0.06;
    public double WastedShare { get; init; } = 0.05;
    public double LowWeight { get; init; } = 0.15;
}

public sealed record GearSettings
{
    public int WeakSlotGap { get; init; } = 4;
}

public sealed record SettingsFile
{
    public DaevanionSettings Daevanion { get; init; } = new();
    public SkillSettings Skills { get; init; } = new();
    public StigmaSettings Stigmas { get; init; } = new();
    public StatSettings Stats { get; init; } = new();
    public GearSettings Gear { get; init; } = new();
}

/// <summary>Poids et cibles d'une classe pour un mode, rôle et surcharges fusionnés.</summary>
public sealed record ResolvedProfile(ClassProfile Class, RoleProfile Role, GameMode Mode, ModeProfile Base, IReadOnlyDictionary<string, double> Weights)
{
    public double Weight(string statKey) => Weights.TryGetValue(statKey, out var w) ? w : Base.DefaultWeight;
    public double Target(StatCategory c) => Base.Targets.GetValueOrDefault(c);
    public IReadOnlyList<string> CoreSkills =>
        Class.Skills.Count > 0
            ? Class.Skills.Where(s => s.Priority.GetValueOrDefault(Mode) >= 0.85).Select(s => s.Name).ToList()
            : Class.CoreSkills.GetValueOrDefault(Mode) ?? [];

    public SkillKnowledge? Skill(string name) =>
        Class.Skills.FirstOrDefault(s => GameDatabase.NormalizeLabel(s.Name) == GameDatabase.NormalizeLabel(name));
}

/// <summary>Toutes les données de jeu chargées, avec index de recherche.</summary>
public sealed class GameDatabase
{
    readonly Dictionary<string, StatDef> byKey;
    readonly Dictionary<string, StatDef> byApiId;
    readonly Dictionary<string, StatDef> byLabel;

    public GameDatabase(StatCatalogFile stats, ClassesFile classes, ProgressionFile progression, SettingsFile settings)
    {
        Stats = stats.Stats;
        Roles = classes.Roles;
        Classes = classes.Classes;
        Progression = progression;
        Settings = settings;
        byKey = Stats.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        byApiId = new(StringComparer.OrdinalIgnoreCase);
        byLabel = new(StringComparer.Ordinal);
        foreach (var s in Stats)
        {
            foreach (var id in s.ApiIds) byApiId.TryAdd(id, s);
            byApiId.TryAdd(s.Key, s);
            foreach (var l in s.Labels) byLabel.TryAdd(NormalizeLabel(l), s);
        }
    }

    public List<StatDef> Stats { get; }
    public List<RoleProfile> Roles { get; }
    public List<ClassProfile> Classes { get; }
    public ProgressionFile Progression { get; }
    public SettingsFile Settings { get; }

    public StatDef? Stat(string key) => byKey.GetValueOrDefault(key);
    public string StatName(string key) => byKey.TryGetValue(key, out var s) ? s.Name : key;
    public StatDef? StatByApiId(string id) => byApiId.GetValueOrDefault(id);
    public StatDef? StatByLabel(string label) => byLabel.GetValueOrDefault(NormalizeLabel(label));

    public ClassProfile? FindClass(string codeOrName) =>
        Classes.FirstOrDefault(c => c.Code.Equals(codeOrName, StringComparison.OrdinalIgnoreCase)
                                    || c.Name.Equals(codeOrName, StringComparison.OrdinalIgnoreCase)
                                    || c.Aliases.Any(a => a.Equals(codeOrName, StringComparison.OrdinalIgnoreCase)));

    public ResolvedProfile Resolve(string classCode, GameMode mode)
    {
        var cls = FindClass(classCode) ?? throw new InvalidOperationException($"Classe inconnue : {classCode}");
        var role = Roles.FirstOrDefault(r => r.Key == cls.Role) ?? throw new InvalidOperationException($"Rôle inconnu : {cls.Role}");
        var baseProfile = role.Modes.GetValueOrDefault(mode) ?? new ModeProfile();
        var weights = new Dictionary<string, double>(baseProfile.Weights, StringComparer.OrdinalIgnoreCase);
        if (cls.WeightOverrides.TryGetValue(mode, out var over))
            foreach (var (k, v) in over) weights[k] = v;
        // Stats ajoutées au catalogue avec un poids par rôle (ATQ CRIT, DÉF frontale…).
        foreach (var stat in Stats)
            if (!weights.ContainsKey(stat.Key) && stat.RoleWeights.TryGetValue(role.Key, out var byMode) && byMode.TryGetValue(mode, out var w))
                weights[stat.Key] = w;
        if (cls.TargetOverrides.TryGetValue(mode, out var targetOver))
        {
            var targets = new Dictionary<StatCategory, double>(baseProfile.Targets);
            foreach (var (k, v) in targetOver) targets[k] = v;
            baseProfile = baseProfile with { Targets = targets };
        }
        return new ResolvedProfile(cls, role, mode, baseProfile, weights);
    }

    public GradeDef Grade(string code) =>
        Progression.Grades.FirstOrDefault(g => g.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
        ?? new GradeDef(code, code, 0, "#9AA0A8");

    public string SlotName(string slot) => Progression.SlotNames.GetValueOrDefault(slot, slot);
    public string? SlotGroup(string slot) => Progression.SlotGroups.GetValueOrDefault(slot);
    public string GroupName(string group) => Progression.GroupNames.GetValueOrDefault(group, group);

    public static string CategoryName(StatCategory c) => c switch
    {
        StatCategory.Offense => "Attaque",
        StatCategory.Defense => "Défense",
        StatCategory.Sustain => "Survie",
        StatCategory.Utility => "Utilité",
        _ => "Stats principales",
    };

    /// <summary>"d'Attaque", "de Défense"… pour les phrases.</summary>
    public static string Of(StatCategory c) => c is StatCategory.Offense or StatCategory.Utility ? $"d'{CategoryName(c)}" : $"de {CategoryName(c)}";

    /// <summary>Ce que regroupe chaque famille de stats (infobulles et textes).</summary>
    public static string CategoryDescription(StatCategory c) => c switch
    {
        StatCategory.Offense => "Stats qui augmentent tes dégâts : ATQ, CRIT, Précision, VIT de combat, Boost de DGT, Perforation…",
        StatCategory.Defense => "Stats qui réduisent les dégâts reçus : DÉF, Esquive, Blocage, RÉS aux CRIT, Tolérance aux DGT…",
        StatCategory.Sustain => "Stats qui te gardent en vie plus longtemps : PV, Restauration (soins reçus), régénération de PV, potions.",
        StatCategory.Utility => "Confort et ressources : PM et régénération de PM, coût en PM, vitesse de déplacement, Envol.",
        _ => "Force, Dextérité, Santé… : elles agissent via leurs bonus en %.",
    };

    /// <summary>Exemples courts, pour les phrases : "Survie (PV, soins reçus, régénération)".</summary>
    public static string CategoryWithExamples(StatCategory c) => c switch
    {
        StatCategory.Offense => "Attaque (ATQ, CRIT, Précision, VIT de combat…)",
        StatCategory.Defense => "Défense (DÉF, Esquive, Blocage, résistances…)",
        StatCategory.Sustain => "Survie (PV, soins reçus, régénération)",
        StatCategory.Utility => "Utilité (PM, déplacement, vol)",
        _ => CategoryName(c),
    };

    public static string ModeName(GameMode m) => m == GameMode.PvP ? "JcJ (PvP)" : "JcE (PvE)";

    /// <summary>Minuscules, apostrophes et espaces normalisés : "Augmentation de l’ATQ" == "augmentation de l'atq".</summary>
    public static string NormalizeLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        var space = false;
        foreach (var ch in label.Trim())
        {
            var c = ch is '’' or '`' or '´' ? '\'' : char.ToLower(ch, CultureInfo.InvariantCulture);
            if (char.IsWhiteSpace(c)) { if (!space) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
