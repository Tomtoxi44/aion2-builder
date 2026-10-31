using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Un joueur à comparer, mesuré comme toi : stats totales en JcE (le % pour une stat en %, la valeur plate sinon), en JcJ les
/// seules stats qui changent, puissance de combat, et la note globale du Builder dans chaque mode (la qualité de son build).
/// Sert au relevé hebdomadaire (commande « bench ») et à ta liste de joueurs.
/// </summary>
public static class PeerMeasure
{
    public static (BenchPlayer Player, int DaevanionNodes) Measure(GameDatabase db, CharacterSnapshot s)
    {
        var advisor = new Advisor(db);
        var pve = advisor.Analyze(s, GameMode.PvE);
        var pvp = advisor.Analyze(s, GameMode.PvP);
        static Dictionary<string, double> Totals(StatProfile sp) => sp.Stats.Where(x => Math.Abs(x.BenchValue) >= 0.05)
            .OrderBy(x => x.Def.Key, StringComparer.Ordinal).ToDictionary(x => x.Def.Key, x => Math.Round(x.BenchValue, 1));
        var a = Totals(pve.Stats);
        var b = Totals(pvp.Stats);
        var changed = a.Keys.Union(b.Keys).Where(k => Math.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)) > 1e-9)
            .OrderBy(k => k, StringComparer.Ordinal).ToDictionary(k => k, k => b.GetValueOrDefault(k));
        var nodes = pve.Daevanion.Boards.Where(x => x.Pool == DaevanionPlanner.NormalPool).Sum(x => x.Board.OpenNodes);
        return (new BenchPlayer(s.ItemLevel, a, changed) { CombatPower = s.CombatPower, Score = pve.Scores.Overall, PvpScore = pvp.Scores.Overall }, nodes);
    }
}
