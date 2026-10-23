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
    public string Display => Def.Kind == StatKind.Percent || Flat == 0
        ? $"{Fmt.Num(Percent)} %" + (Flat > 0 ? $" + {Fmt.Num(Flat)}" : "")
        : Fmt.Num(Flat) + (Percent > 0 ? $" (+{Fmt.Num(Percent)} %)" : "");
}

public sealed record CategoryShare(StatCategory Category, double Budget, double Share, double Target)
{
    public double Gap => Share - Target;
}

/// <param name="OtherModeBudget">Budget des stats réservées à l'autre mode (ex. "ATQ JcJ" vu en JcE), non compté ici.</param>
public sealed record StatProfile(GameMode Mode, List<StatTotal> Stats, List<CategoryShare> Categories, double TotalBudget,
    double ChoiceBudget, double Efficiency, double OtherModeBudget, List<string> Unrecognized)
{
    public StatTotal? Get(string key) => Stats.FirstOrDefault(s => s.Def.Key == key);
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
}

/// <summary>Stigma : niveau monté avec des fragments, 4 emplacements, échange gratuit.</summary>
public sealed record StigmaAdvice(SkillInfo Skill, string Role, string Why, bool Known, bool Unverified, double Priority,
    bool Recommended, int Target, int ShardsToTarget);

public sealed record SkillPlan(List<SkillAdvice> Skills, List<StigmaAdvice> Stigmas, int SpentPoints, int UnspentPoints,
    bool HasKnowledge, string? Source)
{
    public int Budget => SpentPoints + UnspentPoints;
    public IEnumerable<SkillAdvice> ToRemove => Skills.Where(s => s.Delta < 0).OrderBy(s => s.Priority);
    public IEnumerable<SkillAdvice> ToAdd => Skills.Where(s => s.Delta > 0).OrderByDescending(s => s.Priority);
}

// ----- Daevanion -----

/// <param name="Affordable">Faisable avec les points non dépensés déclarés (sinon : à prendre plus tard).</param>
public sealed record PlannedNode(int Step, string Pool, DaevanionBoard Board, DaevanionNode Node, double Value, double Cost, bool IsPassage, bool Affordable = false)
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

public sealed record DaevanionPlan(List<PlannedNode> Steps, List<BoardSummary> Boards, List<PoolBudget> Budgets, List<RespecPlan> Respecs)
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
