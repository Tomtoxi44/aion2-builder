using Aion2Builder.GameData;

namespace Aion2Builder.Analysis;

/// <summary>Résultat d'une rotation simulée : dégâts totaux en % d'ATQ, lancers par sort, sorts écartés.</summary>
public sealed record RotationResult(double Duration, int Targets, double Damage, List<RotationCast> Casts, List<string> Skipped)
{
    public double PerSecond => Duration > 0 ? Damage / Duration : 0;
}

public sealed record RotationCast(string Name, int Casts, double Damage, bool Triggered);

/// <summary>
/// Simulateur de rotation volontairement simple, pour comparer des rotations entre elles : les sorts à recharge partent
/// dans l'ordre de la rotation dès qu'ils sont prêts (et que leur condition est remplie) ; entre deux, le meilleur sort
/// sans recharge. 1 s par lancer, 2 s pour un sort chargé (au maximum). Ni buffs, ni PM, ni critiques ; les sorts
/// impossibles sur un boss sont écartés. Les données viennent de spells.json (niveau 1).
/// </summary>
public static class RotationSimulator
{
    public const double CastTime = 1.0, ChargedCastTime = 2.0;
    static readonly HashSet<string> ChargedSpells = ["Visée de flèche", "Feu de l'enfer", "Fulguration", "Châtiment"];

    static string Norm(string s) => GameDatabase.NormalizeLabel(s);

    /// <summary>Ordre de priorité d'un guide de jeu : les sorts de toutes ses phases, dans l'ordre de première apparition.</summary>
    public static List<string> Priority(PlayGuide? play) =>
        play is null ? [] : play.Rotation.SelectMany(p => p.Steps).Distinct().ToList();

    public static RotationResult Run(GameDatabase db, string classCode, IReadOnlyList<string> priority, double duration = 120, int targets = 1, bool boss = true)
    {
        var spells = db.Spells.Values.Where(s => s.Class == classCode && s.Kind != "Passive").ToList();
        SpellData? Find(string step)
        {
            var stigma = step.EndsWith("(stigma)", StringComparison.Ordinal);
            var name = Norm(stigma ? step[..^"(stigma)".Length] : step);
            return spells.Where(s => Norm(s.Name) == name).OrderBy(s => (s.Kind == "Stigma") == stigma ? 0 : 1).FirstOrDefault();
        }

        var skipped = new List<string>();
        var order = new List<SpellData>();
        foreach (var step in priority)
        {
            if (Find(step) is not { } s) continue;
            if (order.Contains(s) || s.TriggeredBy is { Count: > 0 }) continue;
            if (s.Coef <= 0) continue; // buffs et utilitaires : pas de dégâts directs à simuler
            if (boss && !s.Boss) { skipped.Add($"{s.Name} (condition impossible sur un boss)"); continue; }
            order.Add(s);
        }
        double Hit(SpellData s) => s.Coef * Math.Min(targets, s.Targets);
        var withCooldown = order.Where(s => s.Cooldown > 0).ToList();
        var fillers = order.Where(s => s.Cooldown <= 0).ToList();
        if (fillers.Count == 0 && spells.Where(s => s.Kind == "Active" && s.Cooldown <= 0 && s.Coef > 0 && s.Boss && s.Requires is null && s.TriggeredBy is null)
                .OrderByDescending(Hit).FirstOrDefault() is { } basic)
            fillers.Add(basic);
        var procs = spells.Where(s => s.TriggeredBy is { Count: > 0 }).ToList();

        var ready = new Dictionary<SpellData, double>();
        var last = new Dictionary<string, double>();
        var casts = new Dictionary<SpellData, (int N, double Damage)>();
        bool Ok(SpellData s, double t) => s.Requires is not { Count: > 0 } req
            || req.Any(r => last.TryGetValue(Norm(r), out var at) && t - at <= s.Within);
        void Add(SpellData s, double damage) => casts[s] = (casts.GetValueOrDefault(s).N + 1, casts.GetValueOrDefault(s).Damage + damage);

        double time = 0, total = 0;
        while (time < duration)
        {
            var pick = withCooldown.FirstOrDefault(s => ready.GetValueOrDefault(s) <= time + 1e-9 && Ok(s, time))
                       ?? fillers.Where(s => Ok(s, time)).OrderByDescending(Hit).FirstOrDefault();
            if (pick is null) { time += CastTime; continue; }
            var damage = Hit(pick);
            total += damage;
            Add(pick, damage);
            foreach (var proc in procs.Where(p => p.TriggeredBy!.Any(n => Norm(n) == Norm(pick.Name))))
            {
                total += Hit(proc);
                Add(proc, Hit(proc));
            }
            ready[pick] = time + pick.Cooldown;
            last[Norm(pick.Name)] = time;
            time += ChargedSpells.Contains(pick.Name) ? ChargedCastTime : CastTime;
        }
        var list = casts.OrderByDescending(c => c.Value.Damage)
            .Select(c => new RotationCast(c.Key.Name, c.Value.N, c.Value.Damage, c.Key.TriggeredBy is { Count: > 0 })).ToList();
        return new RotationResult(duration, targets, total, list, skipped);
    }
}
