using Aion2Builder.GameData;

namespace Aion2Builder.Combat;

/// <summary>Une perte d'aggro : de quand à quand, qui le boss a frappé à ta place, combien de ses attaques, et ton sort de menace.</summary>
public sealed record AggroLoss(double Start, double End, string Who, int Attacks, double Damage, string? Taunt, double? TauntWait, bool TauntedBack);

/// <summary>
/// Qui le boss a frappé (Pet Meter v0.21.2) : ses attaques sur une seule cible qui visent d'habitude le tank (Tank), les
/// pertes d'aggro, et à part ses sorts de zone (plusieurs joueurs à la fois) et ceux qui choisissent une cible au hasard.
/// </summary>
public sealed record AggroReport(int TankAttacks, int OnYou, int Zone, int Random, List<AggroLoss> Losses, bool YouAreTank)
{
    /// <summary>Part de ses attaques de tank reçues par toi, de 0 à 1.</summary>
    public double Share => TankAttacks > 0 ? (double)OnYou / TankAttacks : 0;
}

public static class AggroAnalyzer
{
    /// <summary>Coups d'un même sort du boss à moins de 0,6 s : un seul lancer (de zone s'il touche plusieurs joueurs).</summary>
    const double Window = 0.6;
    /// <summary>Attaques sur un autre joueur à moins de 10 s d'écart, sans retour sur toi entre-temps : une seule perte d'aggro.</summary>
    const double Merge = 10;

    sealed class Attack(double t, long skill, string target, double damage)
    {
        public double T { get; } = t;
        public long Skill { get; } = skill;
        public HashSet<string> Targets { get; } = [target];
        public double Damage { get; set; } = damage;
    }

    /// <summary>L'aggro du boss sur ce combat ; null sans coups du boss (Pet Meter d'avant la v0.21.2) ou sans attaque de tank.</summary>
    public static AggroReport? Analyze(CombatLog log, ResolvedProfile p, GameDatabase db)
    {
        if (log.BossHits is not { Count: > 0 } hits) return null;
        // Lancers : coups d'un même sort à moins de 0,6 s regroupés ; seulement sur des joueurs (pas ses adds ni les invocations).
        var attacks = new List<Attack>();
        var last = new Dictionary<long, Attack>();
        foreach (var h in hits.Where(h => h.Player && h.To is not null).OrderBy(h => h.T))
        {
            if (last.TryGetValue(h.Skill, out var a) && h.T - a.T <= Window)
            {
                a.Targets.Add(h.To!);
                a.Damage += h.Damage;
                continue;
            }
            var created = new Attack(h.T, h.Skill, h.To!, h.Damage);
            attacks.Add(created);
            last[h.Skill] = created;
        }
        // Sorts de tank : ses lancers sur une seule cible te visent au moins une fois sur deux.
        var tankSkills = attacks.Where(a => a.Targets.Count == 1).GroupBy(a => a.Skill)
            .Where(g => g.Count() >= 2 && g.Count(a => a.Targets.Contains("me")) >= 0.5 * g.Count()).Select(g => g.Key).ToHashSet();
        if (tankSkills.Count == 0) return null;
        var tank = attacks.Where(a => a.Targets.Count == 1 && tankSkills.Contains(a.Skill)).ToList();
        var zone = attacks.Count(a => a.Targets.Count > 1);
        var random = attacks.Count(a => a.Targets.Count == 1 && !tankSkills.Contains(a.Skill) && !a.Targets.Contains("me"));

        // Ton sort de menace (Provocation…) : les compétences de rôle « Menace » de ta classe qui se lancent.
        var taunts = p.Class.Skills.Where(k => k.Role.StartsWith("Menace", StringComparison.OrdinalIgnoreCase) && k.Kind != "Passive").Select(k => k.Name).ToList();
        var cooldowns = taunts.ToDictionary(n => n, n => db.Spells.Values.FirstOrDefault(s => s.Class == p.Class.Code && s.Name == n)?.Cooldown ?? 0);
        var tauntCasts = (log.Casts ?? []).Where(c => taunts.Contains(c.Name)).ToList();

        var losses = new List<AggroLoss>();
        AggroLoss? open = null;
        foreach (var a in tank)
        {
            if (a.Targets.Contains("me"))
            {
                if (open is not null) { losses.Add(open); open = null; }
                continue;
            }
            var who = a.Targets.First();
            if (open is not null && open.Who == who && a.T - open.End <= Merge)
            {
                open = open with { End = a.T, Attacks = open.Attacks + 1, Damage = open.Damage + a.Damage };
                continue;
            }
            if (open is not null) losses.Add(open);
            // Au début de la perte : ton sort de menace était-il prêt (et l'as-tu lancé dans les 5 s) ?
            string? taunt = null;
            double? wait = null;
            foreach (var name in taunts)
            {
                var before = tauntCasts.LastOrDefault(c => c.Name == name && c.T <= a.T);
                var left = before is null ? 0 : Math.Max(0, before.T + cooldowns[name] - a.T);
                if (wait is null || left < wait) { taunt = name; wait = left; }
            }
            var back = tauntCasts.Any(c => c.T > a.T && c.T <= a.T + 5);
            open = new AggroLoss(a.T, a.T, who, 1, a.Damage, taunt, wait, back);
        }
        if (open is not null) losses.Add(open);
        return new AggroReport(tank.Count, tank.Count(a => a.Targets.Contains("me")), zone, random, losses, p.Role.Key == "tank");
    }
}
