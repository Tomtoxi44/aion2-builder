using System.Globalization;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

public enum Severity { Critical, Important, Advice, Info }
public enum Area { Stats, Gear, Enchant, Skills, Daevanion, General }

/// <summary>
/// Un conseil. <see cref="Priority"/> mesure le gain attendu rapporté au coût en ressources :
/// c'est l'ordre du plan d'action.
/// </summary>
public sealed record Finding(Area Area, Severity Severity, string Title, string Detail, string? Action = null, double Priority = 0);

// ----- Stats -----

public sealed record StatSource(string Source, Effect Effect);

public sealed record StatTotal(StatDef Def, double Flat, double Percent, double Budget, double Weight, double Share, double ExpectedShare, List<StatSource> Sources)
{
    /// <summary>Positif = il en manque par rapport à la répartition idéale de la classe.</summary>
    public double Deficit => ExpectedShare - Share;
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
    double Efficiency, double OtherModeBudget, List<string> Unrecognized)
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

public enum SkillStatus { CoreMissing, CoreNotEquipped, LevelUp, Wasted, Ok }

public sealed record SkillAdvice(SkillInfo Skill, SkillStatus Status, bool IsCore, string Advice, int OpenableNodes, double Priority);

public sealed record SkillPlan(List<SkillAdvice> Skills, double AverageActiveLevel, double AveragePassiveLevel);

// ----- Daevanion -----

public sealed record PlannedNode(int Step, string Pool, DaevanionBoard Board, DaevanionNode Node, double Value, double Cost, bool IsPassage)
{
    public string Effects => Node.Effects.Count == 0 ? Node.Name : string.Join(", ", Node.Effects.Select(e => e.Raw));
}

public sealed record BoardSummary(DaevanionBoard Board, string Pool, bool Relevant, double RemainingValue, double RemainingCost, int Reachable)
{
    public double ValuePerCost => RemainingCost <= 0 ? 0 : RemainingValue / RemainingCost;
}

public sealed record DaevanionPlan(List<PlannedNode> Steps, List<BoardSummary> Boards)
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
    StatProfile Stats, GearPlan Gear, SkillPlan Skills, DaevanionPlan Daevanion,
    List<Finding> Findings, AreaScores Scores)
{
    /// <summary>Les conseils actionnables, du plus rentable au moins rentable.</summary>
    public IEnumerable<Finding> ActionPlan => Findings.Where(f => f.Action is not null && f.Severity != Severity.Info)
        .OrderByDescending(f => f.Priority);
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

    public static string Status(SkillStatus s) => s switch
    {
        SkillStatus.CoreMissing => "Clé non apprise",
        SkillStatus.CoreNotEquipped => "Clé non équipée",
        SkillStatus.LevelUp => "À monter",
        SkillStatus.Wasted => "Investie, non équipée",
        _ => "OK",
    };
}
