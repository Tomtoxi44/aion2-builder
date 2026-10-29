using System.Globalization;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

public enum Severity { Critical, Important, Advice, Info }
public enum Area { Stats, Gear, Enchant, Skills, Daevanion, Pets, General }

/// <summary>
/// Un conseil. <see cref="Priority"/> mesure le gain attendu rapporté au coût en ressources :
/// c'est l'ordre du plan d'action.
/// </summary>
public sealed record Finding(Area Area, Severity Severity, string Title, string Detail, string? Action = null, double Priority = 0);

// ----- Stats -----

/// <param name="Fixed">Stat principale d'une pièce : imposée par le type de pièce, pas un choix du joueur.</param>
public sealed record StatSource(string Source, Effect Effect, bool Fixed = false);

/// <summary>
/// <see cref="Budget"/> compte tout ; <see cref="ChoiceBudget"/>, <see cref="Share"/> et <see cref="ExpectedShare"/> ne portent que sur
/// ce que le joueur peut changer (stats secondaires, Daevanion, titres, stats divines), pas les stats fixes des pièces.
/// </summary>
public sealed record StatTotal(StatDef Def, double Flat, double Percent, double Budget, double ChoiceBudget, double Weight, double Share, double ExpectedShare, List<StatSource> Sources)
{
    /// <summary>Positif = il en manque par rapport à la répartition idéale de la classe.</summary>
    public double Deficit => ExpectedShare - Share;
    /// <summary>Repère affiché : part minimale visée si la stat manque, ✓ si une stat prioritaire est bien servie.</summary>
    public string TargetDisplay => Deficit > 0.01 ? $"≥ {Fmt.Pct(ExpectedShare)}" : Weight >= 0.6 && Share > 0 ? "✓" : "";
    /// <summary>Valeur comparée aux repères des joueurs : le % pour une stat en %, la valeur plate sinon.</summary>
    public double BenchValue => Def.Kind == StatKind.Percent ? Percent : Flat;
    public string Display => Def.Kind == StatKind.Percent || Flat == 0
        ? $"{Fmt.Num(Percent)} %" + (Flat > 0 ? $" + {Fmt.Num(Flat)}" : "")
        : Fmt.Num(Flat) + (Percent > 0 ? $" (+{Fmt.Num(Percent)} %)" : "");
}

public sealed record CategoryShare(StatCategory Category, double Budget, double Share, double Target)
{
    public double Gap => Share - Target;
}

/// <summary>
/// Une stat face aux joueurs de comparaison : leurs valeurs (le % pour une stat en %, la valeur plate sinon, comme
/// <see cref="StatTotal.BenchValue"/>), leur moyenne, et combien en ont moins que toi.
/// </summary>
public sealed record StatVsPlayers(StatTotal Stat, List<double> Values)
{
    public double Mine => Stat.BenchValue;
    public double Average => Values.Count == 0 ? 0 : Values.Average();
    public int Below => Values.Count(v => v < Mine - 1e-9);
    /// <summary>1 au-dessus de la moyenne des joueurs, -1 en dessous, 0 dans la moyenne (à 3 % près).</summary>
    public int Verdict => Math.Abs(Mine - Average) <= Math.Max(1e-6, 0.03 * Math.Abs(Average)) ? 0 : Mine > Average ? 1 : -1;
}

/// <summary>
/// Une stat à monter : pourquoi (« il manque 694 pour 900 (Conquête) », « en dessous de la moyenne ») et où la trouver encore
/// (cases Daevanion fermées, stat secondaire possible, titres).
/// </summary>
public sealed record StatToRaise(StatTotal Stat, string Why, string Where);

/// <summary>Une stat où reprendre de la place, et pourquoi (au-delà de son plafond, au-dessus de la moyenne, peu utile).</summary>
public sealed record StatToFree(StatTotal Stat, string Why);

/// <summary>
/// Une famille de stats : ta part des stats ajustables face à la part conseillée pour la classe, ses stats (face aux joueurs
/// quand l'échantillon existe), celles à monter et celles où reprendre de la place (plafond dépassé, ou famille au-delà
/// de sa part conseillée).
/// </summary>
public sealed record FamilyReview(CategoryShare Share, List<StatTotal> Stats, List<StatVsPlayers> Players, List<StatToRaise> Raise, List<StatToFree> Free)
{
    public StatVsPlayers? Vs(StatTotal x) => Players.FirstOrDefault(v => v.Stat.Def.Key == x.Def.Key);

    /// <summary>Stats utiles de la famille (poids ≥ 0,6) face aux joueurs comparés.</summary>
    public List<StatVsPlayers> Useful => Players.Where(v => v.Stat.Weight >= 0.6).ToList();

    /// <summary>
    /// True si aucune stat utile n'est sous la moyenne des joueurs, false si l'une l'est, null sans joueurs. Avec des joueurs,
    /// c'est ce qui décide du verdict de la famille : la part ne compte que les stats ajustables, alors que des PV, par
    /// exemple, viennent surtout des stats fixes des pièces.
    /// </summary>
    public bool? AtPlayers => Useful is { Count: > 0 } useful ? useful.All(v => v.Verdict >= 0) : null;
}

/// <summary>
/// Les stats famille par famille. <see cref="Peers"/> : joueurs de comparaison (ta classe, niveau 45, niveau d'objet le plus
/// proche du tien), null sans échantillon ou en JcJ (l'échantillon est relevé en JcE). <see cref="Targets"/> : valeurs
/// conseillées par les guides (de la classe, sinon communes), par stat, de la plus basse à la plus haute.
/// </summary>
public sealed record StatComparison(List<BenchPlayer>? Peers, string? PeersText, List<FamilyReview> Families)
{
    public Dictionary<string, List<StatTarget>> Targets { get; init; } = [];

    public List<StatTarget> TargetsFor(StatTotal x) => Targets.GetValueOrDefault(x.Def.Key) ?? [];

    /// <summary>Prochaine valeur conseillée pas encore atteinte ; null si toutes le sont (ou s'il n'y en a pas).</summary>
    public StatTarget? NextTarget(StatTotal x) => TargetsFor(x).FirstOrDefault(t => t.Value > x.BenchValue + 1e-9);

    /// <summary>Plafond dépassé : la stat ne sert plus au-delà (2 % de marge).</summary>
    public StatTarget? CapExceeded(StatTotal x) => TargetsFor(x).FirstOrDefault(t => t.Cap && x.BenchValue > t.Value * 1.02);
}

/// <param name="OtherModeBudget">Budget des stats réservées à l'autre mode (ex. "ATQ JcJ" vu en JcE), non compté ici.</param>
public sealed record StatProfile(GameMode Mode, List<StatTotal> Stats, List<CategoryShare> Categories, double TotalBudget,
    double ChoiceBudget, double Efficiency, double OtherModeBudget, List<string> Unrecognized)
{
    public StatTotal? Get(string key) => Stats.FirstOrDefault(s => s.Def.Key == key);

    /// <summary>Les mêmes stats rangées par famille, face aux joueurs de ton niveau d'objet.</summary>
    public StatComparison Comparison { get; init; } = new(null, null, []);
}

// ----- Équipement -----

public enum SlotStatus { Empty, Replace, Weak, Enchant, Ok }

public sealed record SlotPlan(string Slot, string SlotName, string Group, EquippedItem? Item, int Power, SlotStatus Status,
    string Advice, string? NextSource, double Fit, List<string> PoorStats, double Priority);

public sealed record GearPlan(ProgressionStage Stage, ProgressionStage? NextStage, int MedianPower, List<SlotPlan> Slots)
{
    public double ProgressToNext(int itemLevel) =>
        NextStage is null ? 1 : Math.Clamp((double)(itemLevel - Stage.MinItemLevel) / Math.Max(1, NextStage.MinItemLevel - Stage.MinItemLevel), 0, 1);
}

// ----- Compétences -----

public enum SkillKind { Active, Passive, Stigma }

/// <summary>
/// Conseil pour une compétence active ou passive. <see cref="Base"/> = niveau acheté avec des points (1 à 10),
/// <see cref="Bonus"/> = niveaux gratuits (Daevanion, équipement), <see cref="RecommendedBase"/> = répartition conseillée.
/// </summary>
public sealed record SkillAdvice(
    SkillInfo Skill, SkillKind Kind, string Role, string Why, bool Known, bool Unverified, double Priority,
    int Base, int Bonus, int Target, int RecommendedBase, int OpenableNodes, bool InBar)
{
    public int Delta => RecommendedBase - Base;
    public int Total => Base + Bonus;
    public int TotalAfter => RecommendedBase + Bonus;

    /// <summary>Niveaux gratuits par source : cases Daevanion ouvertes, arcanes, autres pièces d'équipement.</summary>
    public int BonusDaevanion { get; init; }
    public int BonusArcana { get; init; }
    public int BonusGear => Bonus - BonusDaevanion - BonusArcana;

    /// <summary>Part des dégâts de la rotation simulée (0 à 1) et rang (1 = celle qui en fait le plus) ; 0 et null sans dégâts directs.</summary>
    public double DamageShare { get; init; }
    public int? DamageRank { get; init; }

    /// <summary>Pourquoi la répartition conseillée monte, baisse ou garde cette compétence, en clair.</summary>
    public string Decision { get; init; } = "";
    /// <summary>La même raison sans dire qui donne les points (la ligne de déplacement le montre déjà).</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Compétence bonus portée par une pièce ou une arcane (« Défense d'égide +1 » sur les gants) et ce qu'elle vaut pour toi :
/// <see cref="Useful"/> faux = bonus perdu (compétence hors barre, peu utile ou absente), <see cref="Strong"/> = bon bonus.
/// </summary>
public sealed record GearSkillBonus(string Slot, string Item, bool Arcana, string SkillName, int Levels, SkillAdvice? Skill, bool Useful, bool Strong, string Verdict);

/// <summary>Stigma : niveau monté avec des fragments, 4 emplacements, échange gratuit.</summary>
public sealed record StigmaAdvice(SkillInfo Skill, string Role, string Why, bool Known, bool Unverified, double Priority,
    bool Recommended, int Target, int ShardsToTarget);

/// <summary>
/// Points déplacés par la répartition conseillée : d'une compétence nettement moins utile vers une plus utile
/// (<see cref="From"/> null : points non dépensés).
/// </summary>
public sealed record SkillMove(SkillAdvice? From, SkillAdvice To, int Points);

/// <summary>
/// Prochains points à placer quand tu en gagnes, dans l'ordre : niveau acheté avant et après, et spécialisation
/// débloquée au passage (niveau total 8, 12, 16 ou 20), s'il y en a une.
/// </summary>
public sealed record SkillStep(SkillAdvice Skill, int From, int To, int? Specialty)
{
    public int Points => To - From;
    public bool BeyondTarget => To > Skill.Target;
}

/// <summary>Prochain palier d'un stigma (niveaux 5, 10, 15, 20 : ses effets s'y débloquent) et son coût en fragments.</summary>
public sealed record StigmaStep(StigmaAdvice Stigma, int From, int To, int Shards);

public sealed record SkillPlan(List<SkillAdvice> Skills, List<StigmaAdvice> Stigmas, int SpentPoints, int UnspentPoints,
    bool HasKnowledge, string? Source, List<SkillMove> Moves, List<SkillStep> NextPoints, List<StigmaStep> NextShards)
{
    public int Budget => SpentPoints + UnspentPoints;

    /// <summary>Ce qui fait les dégâts : compétences triées par part des dégâts simulés.</summary>
    public List<SkillAdvice> Focus { get; init; } = [];
    /// <summary>
    /// D'où viennent les parts de dégâts : « mesuré : 6 combats (boss, mannequin)… » (combats du Pet Meter ou de NotMeter)
    /// ou la rotation simulée (« rotation conçue simulée, JcE, 1 cible, 2 min ») ; null sans l'un ni l'autre.
    /// </summary>
    public string? DamageSource { get; init; }
    /// <summary>Combats mesurés dont viennent les parts (0 : simulation).</summary>
    public int MeasuredFights { get; init; }
    /// <summary>Suites d'enchaînement comptées avec le sort lancé (parts mesurées seulement).</summary>
    public List<Combat.ChainLink> Chains { get; init; } = [];
    /// <summary>Compétences bonus de l'équipement et des arcanes, et celles à chercher en priorité sur les prochaines.</summary>
    public List<GearSkillBonus> GearBonuses { get; init; } = [];
    public List<SkillAdvice> BonusWanted { get; init; } = [];
    public IEnumerable<SkillAdvice> ToRemove => Skills.Where(s => s.Delta < 0).OrderBy(s => s.Priority);
    public IEnumerable<SkillAdvice> ToAdd => Skills.Where(s => s.Delta > 0).OrderByDescending(s => s.Priority);
}

// ----- Daevanion -----

/// <param name="Affordable">Faisable avec les points non dépensés déclarés (sinon : à prendre plus tard).</param>
/// <param name="Cumulative">Points dépensés depuis le début de la liste, cette case comprise.</param>
public sealed record PlannedNode(int Step, string Pool, DaevanionBoard Board, DaevanionNode Node, double Value, double Cost, bool IsPassage,
    bool Affordable = false, double Cumulative = 0)
{
    public string Effects => Node.Effects.Count == 0 ? Node.Name : string.Join(", ", Node.Effects.Select(e => e.Raw));
}

public sealed record NodeRef(DaevanionBoard Board, DaevanionNode Node, double Value, double Cost)
{
    public string Effects => Node.Effects.Count == 0 ? Node.Name : string.Join(", ", Node.Effects.Select(e => e.Raw));
}

public sealed record BoardSummary(DaevanionBoard Board, string Pool, bool Relevant, double RemainingValue, double RemainingCost, int Reachable,
    int Spent, int Total)
{
    public double ValuePerCost => RemainingCost <= 0 ? 0 : RemainingValue / RemainingCost;
}

/// <summary>Points d'une réserve : dépensés (cases ouvertes), non dépensés (déclarés), et coût pour tout remplir.</summary>
public sealed record PoolBudget(string Pool, int Spent, int Unspent, int FillCost)
{
    public int Budget => Spent + Unspent;
}

/// <summary>
/// Réorganisation : meilleure répartition des mêmes points en repartant du centre, comparée au placement actuel.
/// <see cref="Remove"/> = cases ouvertes qui ne sont pas dans la répartition optimale, <see cref="Add"/> = cases à prendre à la place.
/// </summary>
public sealed record RespecPlan(string Pool, int Budget, double CurrentValue, double OptimalValue, List<NodeRef> Remove, List<PlannedNode> Add,
    HashSet<long> OptimalNodes)
{
    public double Gain => CurrentValue <= 0 ? (OptimalValue > 0 ? 1 : 0) : OptimalValue / CurrentValue - 1;
}

/// <summary>
/// Route conçue d'une réserve : ordre d'ouverture depuis le centre, sans tenir compte du placement actuel.
/// Les cases <see cref="PlannedNode.Affordable"/> tiennent dans les points de la réserve (dépensés + non dépensés), les suivantes sont la suite.
/// </summary>
public sealed record DesignedRoute(string Pool, int Budget, double CurrentValue, double Value, List<PlannedNode> Nodes)
{
    public double Gain => CurrentValue <= 0 ? (Value > 0 ? 1 : 0) : Value / CurrentValue - 1;
    public IEnumerable<PlannedNode> InBudget => Nodes.Where(n => n.Affordable);
}

public sealed record DaevanionPlan(List<PlannedNode> Steps, List<BoardSummary> Boards, List<PoolBudget> Budgets, List<RespecPlan> Respecs,
    List<DesignedRoute> Routes)
{
    public IEnumerable<IGrouping<string, PlannedNode>> ByPool => Steps.GroupBy(s => s.Pool);
}

// ----- Rapport -----

public sealed record AreaScores(int Stats, int Gear, int Skills, int Daevanion)
{
    public int Overall => (int)Math.Round(Stats * 0.35 + Gear * 0.3 + Skills * 0.15 + Daevanion * 0.2);
}

public sealed record AdviceReport(
    CharacterSnapshot Character, ResolvedProfile Profile, GameMode Mode,
    StatProfile Stats, GearPlan Gear, SkillPlan Skills, DaevanionPlan Daevanion, EngravingPlan Engravings, PetPlan Pets,
    List<Finding> Findings, AreaScores Scores)
{
    /// <summary>Les conseils actionnables, du plus rentable au moins rentable.</summary>
    public IEnumerable<Finding> ActionPlan => Findings.Where(f => f.Action is not null && f.Severity != Severity.Info)
        .OrderByDescending(f => f.Priority * f.Severity switch { Severity.Critical => 1.5, Severity.Important => 1.2, _ => 1.0 });
}

public static class Fmt
{
    static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    public static string Num(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 0.05 ? "0" : "0.#", Fr);
    public static string Pct(double ratio) => $"{Math.Round(ratio * 100)} %";
    /// <summary>Grand nombre avec séparateur de milliers : 537 198.</summary>
    public static string Big(double v) => Math.Round(v).ToString("#,0", Fr);

    /// <summary>
    /// Nombre et mot accordés, à la française (0 et 1 au singulier) : « 1 pièce », « 11 pièces » ; pluriel irrégulier en
    /// troisième argument (« 2 emplacements de pierre », « 3 cases fermées »).
    /// </summary>
    public static string Plural(int n, string one, string? many = null) => $"{n} {(n <= 1 ? one : many ?? one + "s")}";
    public static string Plural(double n, string one, string? many = null) => $"{Num(n)} {(n < 2 ? one : many ?? one + "s")}";

    /// <summary>
    /// Les conseils comptés d'une seule façon dans toute l'interface : « conseils » = critiques, importants et à suivre ;
    /// « importants » = critiques et importants ; les infos à part.
    /// </summary>
    public static (int Advice, int Important, int Info) Counts(IEnumerable<Finding> findings)
    {
        var all = findings.ToList();
        return (all.Count(f => f.Severity <= Analysis.Severity.Advice), all.Count(f => f.Severity <= Analysis.Severity.Important),
                all.Count(f => f.Severity == Analysis.Severity.Info));
    }

    /// <summary>« 26 conseils · 10 importants » (ou « aucun conseil »), le même texte partout.</summary>
    public static string CountsText(IEnumerable<Finding> findings)
    {
        var (advice, important, _) = Counts(findings);
        return advice == 0 ? "aucun conseil" : $"{Plural(advice, "conseil")} · {Plural(important, "important")}";
    }

    public static string Severity(Severity s) => s switch
    {
        Analysis.Severity.Critical => "Critique",
        Analysis.Severity.Important => "Important",
        Analysis.Severity.Advice => "Conseil",
        _ => "Info",
    };

    public static string Area(Area a) => a switch
    {
        Analysis.Area.Stats => "Stats",
        Analysis.Area.Gear => "Équipement",
        Analysis.Area.Enchant => "Enchantement",
        Analysis.Area.Skills => "Compétences",
        Analysis.Area.Daevanion => "Daevanion",
        Analysis.Area.Pets => "Familiers",
        _ => "Général",
    };

    public static string Status(SlotStatus s) => s switch
    {
        SlotStatus.Empty => "Vide",
        SlotStatus.Replace => "À remplacer",
        SlotStatus.Weak => "En retard",
        SlotStatus.Enchant => "À enchanter",
        _ => "OK",
    };
}
