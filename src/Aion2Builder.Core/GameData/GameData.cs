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
    /// <summary>Rotation et façon de jouer du build des joueurs (guides et profils observés), par mode.</summary>
    public Dictionary<GameMode, PlayGuide> Play { get; init; } = [];
    /// <summary>Build « conçu » à partir des seules données des sorts ; null tant qu'il n'est pas rédigé.</summary>
    public DesignedBuild? Designed { get; init; }
    /// <summary>Repères mesurés sur des joueurs niveau 45 de la classe (niveau d'objet, puissance, cases Daevanion).</summary>
    public ClassBenchmark? Benchmark { get; init; }
}

/// <summary>Une phase de rotation (ouverture, burst, boucle…) : compétences dans l'ordre et consigne.</summary>
public sealed record RotationPhase
{
    public string Name { get; init; } = "";
    public List<string> Steps { get; init; } = [];
    public string Note { get; init; } = "";
}

/// <summary>Façon de jouer un build dans un mode : rotation en phases et conseils de jeu.</summary>
public sealed record PlayGuide
{
    public List<RotationPhase> Rotation { get; init; } = [];
    public List<string> Tips { get; init; } = [];
}

/// <summary>
/// Build « conçu » : construit à partir des seules données des sorts (coefficients d'ATQ, recharges, conditions,
/// spécialisations), sans guide ni profil de joueur. Ses fiches remplacent celles des joueurs compétence par compétence ;
/// les compétences absentes gardent la fiche des joueurs.
/// </summary>
/// <summary>
/// Échantillon de joueurs niveau 45 de la classe (valeurs triées) : sert à situer un personnage (« devant 30 % d'entre eux »).
/// </summary>
public sealed record ClassBenchmark
{
    public string Source { get; init; } = "";
    public List<int> ItemLevel { get; init; } = [];
    public List<int> CombatPower { get; init; } = [];
    public List<int> DaevanionNodes { get; init; } = [];

    /// <summary>Part de l'échantillon sous la valeur (les égalités comptent pour moitié), de 0 à 1.</summary>
    public static double Percentile(List<int> sample, int value) =>
        sample.Count == 0 ? 0 : (sample.Count(x => x < value) + 0.5 * sample.Count(x => x == value)) / sample.Count;

    public static int Median(List<int> sample) => sample.Count == 0 ? 0 : sample.OrderBy(x => x).ElementAt(sample.Count / 2);
}

public sealed record DesignedBuild
{
    public string Summary { get; init; } = "";
    public string Method { get; init; } = "";
    public List<SkillKnowledge> Skills { get; init; } = [];
    public Dictionary<GameMode, PlayGuide> Play { get; init; } = [];
    /// <summary>Écarts avec le build des joueurs, et pourquoi.</summary>
    public List<string> Differences { get; init; } = [];
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
    /// <summary>Plafond de la liste des prochaines cases quand les points non dépensés indiqués demandent plus que <see cref="PlanLength"/>.</summary>
    public int MaxPlanLength { get; init; } = 400;
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
/// <param name="UsesDesigned">Les conseils suivent le build conçu (sinon celui des joueurs).</param>
public sealed record ResolvedProfile(ClassProfile Class, RoleProfile Role, GameMode Mode, ModeProfile Base, IReadOnlyDictionary<string, double> Weights,
    bool UsesDesigned = false)
{
    public double Weight(string statKey) => Weights.TryGetValue(statKey, out var w) ? w : Base.DefaultWeight;
    public double Target(StatCategory c) => Base.Targets.GetValueOrDefault(c);

    /// <summary>Fiches en vigueur : celles du build conçu remplacent celles des joueurs, compétence par compétence.</summary>
    public IReadOnlyList<SkillKnowledge> Skills => UsesDesigned && Class.Designed is { Skills.Count: > 0 } d
        ? Class.Skills.Select(k => d.Skills.FirstOrDefault(o => Same(o, k)) ?? k).Concat(d.Skills.Where(o => !Class.Skills.Any(k => Same(o, k)))).ToList()
        : Class.Skills;

    static bool Same(SkillKnowledge a, SkillKnowledge b) =>
        GameDatabase.NormalizeLabel(a.Name) == GameDatabase.NormalizeLabel(b.Name) && a.Kind.Equals(b.Kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rotation et façon de jouer du build en vigueur, pour le mode.</summary>
    public PlayGuide? Play => (UsesDesigned ? Class.Designed?.Play.GetValueOrDefault(Mode) : null) ?? Class.Play.GetValueOrDefault(Mode);

    public IReadOnlyList<string> CoreSkills =>
        Skills.Count > 0
            ? Skills.Where(s => s.Priority.GetValueOrDefault(Mode) >= 0.85).Select(s => s.Name).ToList()
            : Class.CoreSkills.GetValueOrDefault(Mode) ?? [];

    /// <summary>
    /// Fiche d'une compétence. Deux compétences peuvent porter le même nom (Aède : « Coup fracassant », attaque de base
    /// et stigma) : le type (Active, Passive, Stigma) départage.
    /// </summary>
    public SkillKnowledge? Skill(string name, string? kind = null)
    {
        var matches = Skills.Where(s => GameDatabase.NormalizeLabel(s.Name) == GameDatabase.NormalizeLabel(name)).ToList();
        return kind is null || matches.Count <= 1
            ? matches.FirstOrDefault()
            : matches.FirstOrDefault(s => s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) ?? matches[0];
    }
}

/// <summary>Toutes les données de jeu chargées, avec index de recherche.</summary>
public sealed class GameDatabase
{
    readonly Dictionary<string, StatDef> byKey;
    readonly Dictionary<string, StatDef> byApiId;
    readonly Dictionary<string, StatDef> byLabel;

    public GameDatabase(StatCatalogFile stats, ClassesFile classes, ProgressionFile progression, SettingsFile settings, SpellsFile? spells = null)
    {
        Spells = spells?.Spells ?? [];
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
    /// <summary>Données des sorts par identifiant officiel (recharge, % d'ATQ, cibles).</summary>
    public Dictionary<long, SpellData> Spells { get; }

    /// <summary>Sort d'un code de compétence, y compris un code de combat (variante dans les 4 derniers chiffres).</summary>
    public SpellData? Spell(long code) => Spells.GetValueOrDefault(code) ?? Spells.GetValueOrDefault(code / 10000 * 10000);

    public StatDef? Stat(string key) => byKey.GetValueOrDefault(key);
    public string StatName(string key) => byKey.TryGetValue(key, out var s) ? s.Name : key;
    public StatDef? StatByApiId(string id) => byApiId.GetValueOrDefault(id);
    public StatDef? StatByLabel(string label) => byLabel.GetValueOrDefault(NormalizeLabel(label));

    public ClassProfile? FindClass(string codeOrName) =>
        Classes.FirstOrDefault(c => c.Code.Equals(codeOrName, StringComparison.OrdinalIgnoreCase)
                                    || c.Name.Equals(codeOrName, StringComparison.OrdinalIgnoreCase)
                                    || c.Aliases.Any(a => a.Equals(codeOrName, StringComparison.OrdinalIgnoreCase)));

    /// <param name="designed">Suivre le build conçu (s'il existe pour la classe) plutôt que celui des joueurs.</param>
    public ResolvedProfile Resolve(string classCode, GameMode mode, bool designed = false)
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
        return new ResolvedProfile(cls, role, mode, baseProfile, weights, designed && cls.Designed is not null);
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

public sealed record SpellsFile
{
    public Dictionary<long, SpellData> Spells { get; init; } = [];
}

/// <summary>Un sort au niveau 1 (metabot.gg) : recharge en secondes (0 = sans), % d'ATQ maximal, cibles touchées au plus.</summary>
public sealed record SpellData
{
    public string Class { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public double Cooldown { get; init; }
    public double Coef { get; init; }
    public int Targets { get; init; } = 1;
    /// <summary>Simulation : le sort ne part que si l'un de ces sorts a été lancé dans les <see cref="Within"/> secondes.</summary>
    public List<string>? Requires { get; init; }
    public double Within { get; init; }
    /// <summary>false : condition impossible sur un boss (déséquilibre, étourdissement, gel, après une esquive).</summary>
    public bool Boss { get; init; } = true;
    /// <summary>Pas lancé : se déclenche à chaque lancer de ces sorts.</summary>
    public List<string>? TriggeredBy { get; init; }
}
