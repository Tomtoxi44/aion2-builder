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
    /// <summary>
    /// Valeurs conseillées par les guides de la classe (Précision, CRIT, VIT de combat…), par mode : elles remplacent, stat
    /// par stat, les valeurs communes de <see cref="StatSettings.Targets"/>.
    /// </summary>
    public Dictionary<GameMode, List<StatTarget>> StatTargets { get; init; } = [];
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

    /// <summary>
    /// Stats totales des mêmes joueurs, calculées par le Builder (même calcul que pour toi, en JcE) : valeurs triées par
    /// clé de stat (le % pour une stat en %, la valeur plate sinon).
    /// </summary>
    public Dictionary<string, List<double>> Stats { get; init; } = [];

    public static double Percentile(List<double> sample, double value) =>
        sample.Count == 0 ? 0 : (sample.Count(x => x < value - 1e-9) + 0.5 * sample.Count(x => Math.Abs(x - value) <= 1e-9)) / sample.Count;

    /// <summary>Valeur au rang q (0 à 1) de l'échantillon trié.</summary>
    public static double Quantile(List<double> sample, double q) =>
        sample.Count == 0 ? 0 : sample.OrderBy(x => x).ElementAt(Math.Clamp((int)Math.Round(q * (sample.Count - 1)), 0, sample.Count - 1));

    /// <summary>
    /// Les mêmes joueurs un par un, sans nom : niveau d'objet et stats totales (même calcul que <see cref="Stats"/>, une stat
    /// absente vaut 0). Les stats montent avec le niveau d'objet : on se compare à ceux qui ont le niveau d'objet du sien.
    /// </summary>
    public List<BenchPlayer> Players { get; init; } = [];

    /// <summary>Joueurs de comparaison : les <paramref name="count"/> au niveau d'objet le plus proche.</summary>
    public List<BenchPlayer> Peers(int itemLevel, int count = 8) =>
        Players.OrderBy(p => Math.Abs(p.ItemLevel - itemLevel)).Take(count).OrderBy(p => p.ItemLevel).ToList();

    /// <summary>Valeurs d'une stat chez ces joueurs.</summary>
    public static List<double> Sample(IEnumerable<BenchPlayer> players, string key) => players.Select(p => p.Stats.GetValueOrDefault(key)).ToList();
}

/// <summary>
/// Un joueur de l'échantillon : niveau d'objet et stats totales par clé (le % pour une stat en %, la valeur plate sinon), en
/// JcE ; <paramref name="PvpStats"/> : celles qui changent en JcJ (stats réservées à un mode), null s'il n'a pas été relevé en JcJ.
/// </summary>
public sealed record BenchPlayer(int ItemLevel, Dictionary<string, double> Stats, Dictionary<string, double>? PvpStats = null)
{
    /// <summary>Le même joueur vu dans un mode : en JcJ, ses stats JcE corrigées de celles qui changent ; null sans relevé JcJ.</summary>
    public BenchPlayer? In(GameMode mode)
    {
        if (mode == GameMode.PvE) return this;
        if (PvpStats is null) return null;
        var stats = new Dictionary<string, double>(Stats);
        foreach (var (key, value) in PvpStats) stats[key] = value;
        return this with { Stats = stats, PvpStats = null };
    }
}

/// <summary>
/// benchmark.json : les joueurs de comparaison de chaque classe (par code de classe), relevés chaque semaine sur le site
/// officiel par la commande « bench » ; ils remplacent ceux de classes.json.
/// </summary>
public sealed record BenchmarkFile(string Updated, Dictionary<string, ClassBenchmark> Classes);

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
    public RoadmapRules Roadmap { get; init; } = new();
    public ArcanaRules Arcana { get; init; } = new();
    public List<ProgressionStage> Stages { get; init; } = [];
}

/// <summary>Arcanes (cartes de Transcendance) : ce que donne une carte de chaque grade à +0, relevé sur des joueurs.</summary>
public sealed record ArcanaRules
{
    public string Source { get; init; } = "";
    /// <summary>Compétences bonus au plus sur une carte : chaque niveau d'enchantement en ajoute une jusque-là.</summary>
    public int MaxSkills { get; init; } = 4;
    public Dictionary<string, ArcanaGrade> Grades { get; init; } = [];
}

/// <summary>Une carte d'un grade : niveau d'objet, points de stat divine (1 point = 0,1 % de chaque effet de la divinité), compétences bonus.</summary>
public sealed record ArcanaGrade(int ItemLevel, int Divine, int Skills);

/// <summary>Feuille de route au niveau maximum : seuils de score d'équipement, paliers de pièces et où les trouver.</summary>
public sealed record RoadmapRules
{
    public int MaxLevel { get; init; } = 45;
    public string LevelUp { get; init; } = "";
    public List<GearScoreGate> Gates { get; init; } = [];
    public List<GearTier> Tiers { get; init; } = [];
    public string CraftNote { get; init; } = "";
    public RoadmapEnchant Enchant { get; init; } = new();
    public RoadmapStones Stones { get; init; } = new();
}

/// <summary>Score d'équipement (niveau d'objet du personnage) qui ouvre du contenu, et comment l'atteindre.</summary>
public sealed record GearScoreGate
{
    public int Score { get; init; }
    public string Unlocks { get; init; } = "";
    public bool Optional { get; init; }
    public List<string> How { get; init; } = [];
}

/// <summary>Niveau d'objet d'une pièce et où la trouver par groupe (Weapon, Guard, Armor, Accessory) ; Gate null = artisanat.</summary>
public sealed record GearTier
{
    public int ItemLevel { get; init; }
    public int? Gate { get; init; }
    public Dictionary<string, string> Where { get; init; } = [];

    public string WhereFor(string group) => Where.GetValueOrDefault(group) ?? Where.GetValueOrDefault("Armor") ?? "";
}

public sealed record RoadmapEnchant
{
    public int Guaranteed { get; init; } = 10;
    public List<int> Rates { get; init; } = [];
    public string Transfer { get; init; } = "";
    public string KeepTen { get; init; } = "";
    public string Transmute { get; init; } = "";
}

public sealed record RoadmapStones
{
    public string Mana { get; init; } = "";
    public string Sacred { get; init; } = "";
    public string SacredPvP { get; init; } = "";
    /// <summary>Conseil JcE par code de classe ; « default » pour les autres.</summary>
    public Dictionary<string, string> SacredPvE { get; init; } = [];

    public string SacredFor(string classCode, GameMode mode) =>
        mode == GameMode.PvP ? SacredPvP : SacredPvE.GetValueOrDefault(classCode) ?? SacredPvE.GetValueOrDefault("default") ?? "";
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
    /// <summary>Case de compétence : × (1 + part des dégâts simulés × ce poids) ; 2 = un sort à 30 % des dégâts vaut × 1,6.</summary>
    public double SkillDamageWeight { get; init; } = 2;
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
    /// <summary>Niveaux de compétence qui ouvrent les emplacements de spécialisation (3 au plus, d'après les guides).</summary>
    public List<int> SpecialtySlots { get; init; } = [8, 12, 20];
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

/// <summary>
/// Une valeur conseillée pour une stat (clé de stats.json), en valeur totale comme <c>StatTotal.BenchValue</c> : le % pour
/// une stat en %, la valeur plate sinon. <see cref="Label"/> dit pour quoi (« Sanctuaire », « 50 % de critiques »),
/// <see cref="Source"/> d'où vient le chiffre ; <see cref="Cap"/> : au-delà, la stat ne sert plus.
/// </summary>
public sealed record StatTarget(string Stat, double Value, string Label, string Source, bool Cap = false);

public sealed record StatSettings
{
    public double CategoryTolerance { get; init; } = 0.06;
    public double WastedShare { get; init; } = 0.05;
    public double LowWeight { get; init; } = 0.15;
    /// <summary>Valeurs conseillées communes à toutes les classes (elles dépendent du contenu visé), par mode.</summary>
    public Dictionary<GameMode, List<StatTarget>> Targets { get; init; } = [];
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

    public GameDatabase(StatCatalogFile stats, ClassesFile classes, ProgressionFile progression, SettingsFile settings, SpellsFile? spells = null,
        BenchmarkFile? benchmark = null)
    {
        Spells = spells?.Spells ?? [];
        Stats = stats.Stats;
        Roles = classes.Roles;
        Classes = classes.Classes
            .Select(c => benchmark?.Classes.GetValueOrDefault(c.Code) is { } b ? c with { Benchmark = b } : c)
            .ToList();
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
    /// <summary>Buff passif : ce qui le déclenche, pour l'analyse de combat (temps actif trop bas).</summary>
    public string? Trigger { get; init; }
    /// <summary>Spécialisations du sort (relevées sur metabot.gg) : niveau de compétence requis et effet.</summary>
    public List<SpellSpec> Specs { get; init; } = [];
}

/// <summary>Une spécialisation : niveau de compétence qui la débloque et son effet, tel qu'affiché en jeu.</summary>
public sealed record SpellSpec(int Level, string Text);
