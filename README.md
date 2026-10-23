# Aion 2 Builder

Application locale (.NET 10) qui importe ton personnage AION 2 depuis le site officiel et te dit, **pour ta classe et
en vue JcE (PvE) ou JcJ (PvP)** :

- ce qui est optimisé ou non dans ta répartition de stats (ex. « trop de Défense, pas assez de PV »),
- quelle pièce aller chercher **au palier suivant** (on ne saute pas directement au dernier stuff), pièce la plus faible d'abord,
- quoi enchanter et quoi ne plus enchanter (pièces de transition),
- quelles compétences monter, équiper, ou arrêter de monter,
- quelles cases **Daevanion** ouvrir et dans quel ordre (chemins compris), par réserve de points (plateaux communs, Ariel en JcE, Azphel en JcJ),
- un plan d'action classé par **rentabilité** (gain attendu / ressources dépensées).

Deux interfaces partagent le même moteur :

| Projet | Rôle |
|---|---|
| `src/Aion2Builder.Core` | Import du site officiel, données de jeu, moteur de conseils |
| `src/Aion2Builder.Cli` | Ligne de commande |
| `src/Aion2Builder.App` | Interface graphique Avalonia (écrite en C#, sans XAML) |

## Utilisation

```bash
dotnet run --project src/Aion2Builder.Cli -- advise Lyra --server Azphel --region eu --mode both
```

Commandes du CLI :

| Commande | Effet |
|---|---|
| `search <nom> [--server S] [--region eu]` | Cherche un personnage |
| `import <nom> --server S [--raw <dossier>]` | Importe et enregistre (et garde les réponses brutes si `--raw`) |
| `import-raw <dossier>` | Réimporte des réponses brutes sauvegardées (diagnostic) |
| `advise <nom ou fichier.json> [--mode pve\|pvp\|both] [--refresh] [--json]` | Analyse et conseils |
| `list` | Personnages enregistrés |
| `data [export] [--force]` | Montre / copie les fichiers de données modifiables |

Interface graphique :

```bash
dotnet run --project src/Aion2Builder.App
```

Région, serveur, nom → « Importer depuis le site officiel ». Les personnages importés sont gardés dans
`%APPDATA%\Aion2Builder\characters` et rouverts sans connexion ; « Actualiser » relit le site.

## Comment l'algorithme raisonne

1. **Budget de stats.** Toutes les stats que tu contrôles (équipement, Daevanion, titres équipés, stats principales et
   divines) sont ramenées à une unité commune calée sur les cases Daevanion communes du jeu
   (ATQ +3 = DÉF +30 = PV +100 = PM +50 = CRIT +5). Un « % d'ATQ » s'applique à ton ATQ plate.
   Les stats « JcJ » ne comptent qu'en vue JcJ, et inversement.
2. **Profil de classe.** Chaque classe hérite d'un rôle (DPS, tank, soigneur, soutien) avec, par mode, un poids par stat et
   une répartition visée Attaque / Défense / Survie / Utilité. On compare ta répartition à la cible, on repère les stats
   qui prennent de la place sans servir la classe, et celles qui manquent.
3. **Paliers d'équipement.** Ton palier dépend de ton niveau et de ton niveau d'objet. À chaque palier correspond une
   rareté visée : en dessous, la pièce est de transition (à remplacer, ne pas trop l'enchanter) ; au-dessus, elle est à garder
   (à enchanter jusqu'au niveau sûr). Les pièces en retard sur la médiane passent en premier, l'arme pèse plus lourd.
4. **Daevanion.** Une case ne s'ouvre qu'à côté d'une case ouverte (depuis le centre). Pour chaque case fermée, on calcule
   le chemin le moins cher depuis la zone ouverte et on prend le meilleur rapport *valeur du chemin / coût*. Les cases de
   niveau de compétence valent plus pour les compétences équipées et les compétences clés.
5. **Compétences.** Les actives équipées forment ta rotation ; on signale les compétences clés absentes, celles en retard
   sur la moyenne, et les niveaux investis dans des compétences non équipées. En JcJ, les compétences communes
   indispensables (ex. *Dissipation de choc*) sont exigées.

## Ajuster les données

Poids, cibles, paliers, sources d'équipement, coûts Daevanion et libellés de stats sont dans des fichiers JSON
embarqués (`src/Aion2Builder.Core/GameData/Defaults`). Pour les modifier sans recompiler :

```bash
dotnet run --project src/Aion2Builder.Cli -- data export
```

puis édite les fichiers copiés dans `%APPDATA%\Aion2Builder\data` (un fichier présent remplace la version intégrée) et
clique « Recharger les données » dans l'application.

Les valeurs fournies sont **indicatives** (guides communautaires, observations) : seuils de paliers, coûts Daevanion
(le site ne les donne pas) et compétences clés par classe sont à affiner selon la saison. Seul le Spiritualiste a déjà
sa liste de compétences clés ; pour les autres classes, ajoute les noms français dans `classes.json` (`coreSkills`).

## Limites

- Les adresses du site officiel ne sont pas documentées et peuvent changer sans préavis. En cas de souci,
  `import --raw <dossier>` garde les réponses pour diagnostic.
- Le site n'expose ni les stats de base de la classe, ni les pierres de mana serties : l'analyse porte sur ce qui est
  visible et contrôlable.

## Compiler sous Windows avec Smart App Control

Smart App Control bloque les DLL non signées, dont les tâches de compilation d'Avalonia (compilateur XAML,
télémétrie). D'où une interface écrite en C# sans `.axaml` et la cible vide `AvaloniaStats` dans
`Directory.Build.targets`. Smart App Control bloque aussi l'exécution des binaires compilés localement : pour lancer
l'application sur ce poste, il faut le désactiver (Sécurité Windows → Contrôle des applications et du navigateur) ou
utiliser une autre machine.
