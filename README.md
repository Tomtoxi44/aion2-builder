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

## Lancer l'application (Windows)

1. Double-clique sur **`publier.cmd`** : il crée `publish\Aion2Builder.exe` (un seul fichier, environ 30 Mo).
2. Double-clique sur **`publish\Aion2Builder.exe`** (ou sur un raccourci vers lui sur le bureau).

Après une mise à jour du code, ferme l'application et relance `publier.cmd`. L'exe utilise le runtime .NET 10
(installé avec le SDK). Si Windows refuse de le lancer (« Une stratégie de contrôle d'application a bloqué ce
fichier »), c'est Smart App Control : voir la dernière section.

## Utilisation en ligne de commande

```bash
dotnet run --project src/Aion2Builder.Cli -- advise Lyra --server Azphel --region eu --mode both
```

Commandes du CLI :

| Commande | Effet |
|---|---|
| `servers [texte] [--region eu] [--refresh]` | Liste des serveurs de la région par race, filtrable (« servers az ») |
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

Région, serveur, nom → « Importer depuis le site officiel ». Le champ serveur est une liste filtrable : tape quelques
lettres (« az ») pour retrouver ton serveur ; vide, la recherche porte sur tous les serveurs de la région. La liste est
gardée en cache une semaine dans `%APPDATA%\Aion2Builder\servers` (« Recharger les données » la relit). Les personnages importés sont gardés dans
`%APPDATA%\Aion2Builder\characters` et rouverts sans connexion ; « Actualiser » relit le site.

## Comment l'algorithme raisonne

1. **Budget de stats.** Les stats sont ramenées à une unité commune calée sur les cases Daevanion communes du jeu
   (ATQ +3 = DÉF +30 = PV +100 = PM +50 = CRIT +5). Un « % d'ATQ » s'applique à ton ATQ plate. La répartition porte
   sur ce que tu peux changer (stats secondaires, Daevanion, titres équipés, stats divines) : les stats fixes d'une
   pièce (la DÉF d'une armure) viennent avec elle et restent hors répartition. Les stats « JcJ » ne comptent qu'en
   vue JcJ, et inversement.
2. **Profil de classe.** Chaque classe hérite d'un rôle (DPS, tank, soigneur, soutien) avec, par mode, un poids par stat et
   une répartition visée Attaque / Défense / Survie / Utilité. On compare ta répartition à la cible, on repère les stats
   qui prennent de la place sans servir la classe, et celles qui manquent.
3. **Paliers d'équipement.** Ton palier dépend de ton niveau et de ton niveau d'objet. À chaque palier correspond une
   rareté visée : en dessous, la pièce est de transition (à remplacer, enchantement plafonné à +3) ; au-dessus, elle est à
   garder (à enchanter jusqu'au niveau sûr). Les pièces en retard sur la médiane passent en premier, l'arme pèse plus lourd.
   Certains emplacements ont leur propre règle (`slotRules`) : la **ceinture noble** ne se remplace pas (pas de ceinture de
   donjon sur les serveurs occidentaux), on la monte à +10 puis on la transmute au grade supérieur.
4. **Compétences.** 1 point = +1 niveau jusqu'au niveau 10, réinitialisation gratuite ; au-delà, seuls le Daevanion et
   l'équipement donnent des niveaux (spécialisations à 8, 12, 16, 20). L'application recalcule les points investis
   (niveau − bonus Daevanion − bonus d'équipement), y ajoute tes points non dépensés, puis redistribue ce budget selon le
   **rôle** de chaque compétence pour ta classe et le mode (dégâts principaux, burst, débuff, contrôle, défensif…) : elle dit
   quelles compétences monter, lesquelles **réduire**, et pourquoi. Les stigmas (4 emplacements, fragments) sont classés de
   la même façon, avec le coût en fragments pour atteindre le niveau visé.
5. **Daevanion.** Une case coûte 1, 2, 3 ou 4 points selon sa couleur (vérifié : 134 points pour Nezekan, Zikel et Vaizel,
   168 pour Triniel, 232 pour Azphel) et ne s'ouvre qu'à côté d'une case ouverte. Deux plans par réserve de points :
   - **réorganisation** : en partant de ton placement actuel, les cases peu utiles en bout de chemin (importance < 0,4)
     sont retirées si leurs points rapportent plus ailleurs → cases rouges à retirer, cases vertes à prendre ;
   - **prochaines cases** : où mettre tes prochains points sans rien retirer (les points non dépensés que tu indiques
     sont marqués « faisable maintenant »).
   Les niveaux de compétence valent selon l'importance de la compétence, les stats selon leur poids et l'équilibre des
   familles (une famille déjà au-dessus de sa cible vaut moins).

Le site officiel ne montre pas les points non dépensés (compétences, Daevanion, cristaux Ariel/Azphel) : indique-les
dans les onglets Compétences et Daevanion, ou avec `--sp`, `--dp`, `--ariel`, `--azphel` dans le CLI. Ils sont gardés par
personnage. Survole une stat, une famille de stats, une note ou une case Daevanion pour son explication.

## Ajuster les données

Poids, cibles, paliers, sources d'équipement, coûts Daevanion et libellés de stats sont dans des fichiers JSON
embarqués (`src/Aion2Builder.Core/GameData/Defaults`). Pour les modifier sans recompiler :

```bash
dotnet run --project src/Aion2Builder.Cli -- data export
```

puis édite les fichiers copiés dans `%APPDATA%\Aion2Builder\data` (un fichier présent remplace la version intégrée) et
clique « Recharger les données » dans l'application.

Les paliers d'équipement sont calés sur des personnages réels des serveurs EU (octobre 2026 : bleu, puis jaune, puis
jaune enchanté +8/+10 ; aucun orange ni rouge observé). Les autres valeurs sont **indicatives** (guides communautaires,
observations) et à affiner selon la saison. Le **Spiritualiste** et le **Templier** ont une fiche de compétences
détaillée ; celle du Templier croise un guide et les compétences réellement montées par 8 Templiers niveau 45 (`classes.json` → `skills` : rôle, priorité et niveau visé par mode, et la raison affichée ; les entrées « à valider »
ne sont pas documentées par les guides). Pour les autres classes, l'analyse se base sur ta barre tant que leur fiche n'est
pas remplie.

## Limites

- Les adresses du site officiel ne sont pas documentées et peuvent changer sans préavis. En cas de souci,
  `import --raw <dossier>` garde les réponses pour diagnostic.
- Le site n'expose ni les stats de base de la classe, ni les pierres de mana serties : l'analyse porte sur ce qui est
  visible et contrôlable.

## Compiler sous Windows avec Smart App Control

Smart App Control bloque les DLL non signées, dont les tâches de compilation d'Avalonia (compilateur XAML,
télémétrie). D'où une interface écrite en C# sans `.axaml` et la cible vide `AvaloniaStats` dans
`Directory.Build.targets`. Smart App Control bloque aussi l'exécution des binaires compilés localement : quand il est actif, l'application ne
peut pas être lancée sur ce poste (désactive-le dans Sécurité Windows → Contrôle des applications et du navigateur, ou
utilise une autre machine).
