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
    public List<string> Where { get; init; } = [];
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
    public Dictionary<GameMode, List<string>> CoreSkills { get; init; } = [];
    public List<string> Notes { get; init; } = [];
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
    public List<GradeDef> Grades { get; init; } = [];
    public Dictionary<string, string> SlotGroups { get; init; } = [];
    public Dictionary<string, string> GroupNames { get; init; } = [];
    public Dictionary<string, string> SlotNames { get; init; } = [];
    public List<string> RequiredSlots { get; init; } = [];
    public List<string> OptionalSlots { get; init; } = [];
    public required EnchantRules Enchant { get; init; }
    public List<ProgressionStage> Stages { get; init; } = [];
}

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
}

public sealed record SkillSettings
{
    public List<string> CommonPvpSkills { get; init; } = [];
    public int LowLevelGap { get; init; } = 4;
    public int WastedLevelThreshold { get; init; } = 5;
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
    public StatSettings Stats { get; init; } = new();
    public GearSettings Gear { get; init; } = new();
}

/// <summary>Poids et cibles d'une classe pour un mode, rôle et surcharges fusionnés.</summary>
public sealed record ResolvedProfile(ClassProfile Class, RoleProfile Role, GameMode Mode, ModeProfile Base, IReadOnlyDictionary<string, double> Weights)
{
    public double Weight(string statKey) => Weights.TryGetValue(statKey, out var w) ? w : Base.DefaultWeight;
    public double Target(StatCategory c) => Base.Targets.GetValueOrDefault(c);
    public IReadOnlyList<string> CoreSkills => Class.CoreSkills.GetValueOrDefault(Mode) ?? [];
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
