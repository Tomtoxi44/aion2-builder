using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Aion2Builder.Model;

namespace Aion2Builder.App;

sealed partial class ScreenViews
{
    /// <summary>
    /// Calculs des écrans faits d'avance, hors du fil de l'interface (<c>MainWindow.PrepareScreens</c>) : ils vont dans les mémos
    /// partagés (par analyse, par combat, simulations « et si »), où le premier affichage de chaque écran les trouve prêts.
    /// Aucun contrôle n'est créé ici ; une nouvelle analyse arrête la préparation entre deux calculs.
    /// </summary>
    public void Prepare(CancellationToken ct)
    {
        Action[] steps =
        [
            () => _ = Route,                                               // Équipement : barre secondaire, route, pièces, enchantement
            () => _ = Optimization,                                        // Équipement → Optimisation des stats
            () => _ = Specs,                                               // Compétences → Spécialisations
            () => MacroAdvisor.Plan(db, r),                                // Compétences → Macro (code chargé d'avance)
            () => WhatIf.Gains(r),                                         // Accueil → « Et si… »
            () => { foreach (var (_, o) in ArcanaWhatIfs()) simulate(o); }, // Équipement → Arcanes : deux analyses complètes
            () =>
            {
                // Combat : points à corriger de chaque combat de la liste (gardés par combat).
                var profile = db.Resolve(r.Character.ClassCode, GameMode.PvE, inputs.UsesDesigned);
                var analyzer = new CombatAnalyzer(db);
                foreach (var (_, log) in combats)
                {
                    if (ct.IsCancellationRequested) return;
                    TodoCount(analyzer, log, profile);
                }
            },
        ];
        foreach (var step in steps)
        {
            if (ct.IsCancellationRequested) return;
            try { step(); }
            catch (Exception) { /* l'écran refera ce calcul lui-même et montrera l'erreur, s'il y en a une */ }
        }
    }
}
