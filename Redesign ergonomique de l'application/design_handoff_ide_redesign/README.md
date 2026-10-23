# Handoff : refonte « IDE » d'Aion 2 Builder

## Vue d'ensemble
Refonte de l'interface graphique `src/Aion2Builder.App` (Avalonia, C# sans XAML). Objectifs :
- remplacer la barre d'outils en vrac (8 contrôles) par un **écran d'import dédié** et une **barre latérale** ;
- un **accueil tableau de bord** (scores + top 3 actions) qui dit par où commencer ;
- une **bascule JcE / JcJ toujours visible** en haut ;
- des **tableaux allégés + inspecteur au clic** (panneau de droite) au lieu de tableaux denses ;
- des textes d'explication **repliables** ;
- une **couleur secondaire au choix** (8 thèmes).

Le moteur (`Aion2Builder.Core`, `AdviceReport`) ne change pas : seules la présentation et la navigation changent.

## À propos des fichiers
`Aion2 Builder - IDE.dc.html` est une **maquette de référence en HTML**, pas du code de production. Il faut **recréer ce design dans l'application Avalonia existante**, avec ses conventions : interface écrite en C#, helpers de `Ui.cs` (`Text`, `Card`, `Stack`, `Row`, `Chip`, `Table`…), **sans fichier .axaml** (Smart App Control, voir le README du dépôt).
Pour voir la maquette : ouvrir le fichier `.dc.html` dans un navigateur (garder `support.js` et `aion-data.js` dans le même dossier). Les données affichées (Lyra, Templier) sont fictives et viennent de `aion-data.js` ; dans l'application, elles viennent de `AdviceReport`.

## Fidélité
**Haute fidélité** : couleurs, typographie, espacements et interactions sont définitifs. À reproduire au plus près, avec les contrôles Avalonia.

## Structure générale (fenêtre 1280×860, min 1180×760)
De haut en bas :
1. **Barre de titre / menu** : 36 px, fond `#1D1E22`, bordure basse `#2A2C31`, marge horizontale 12.
   - À gauche : « Aion 2 Builder » (13 px, demi-gras, `#D4D7DD`), puis « Fichier · Personnage · Données · Aide » (13 px, `#858A93`, espacement 16). Le menu Données porte « Recharger les données ».
   - À droite : bouton **thème** (pastille 10 px de la couleur secondaire + « thème » en JetBrains Mono 12, bordure `#2A2C31`, rayon 4), puis « mode » et le **segmenté JcE | JcJ** (JetBrains Mono 12, demi-gras ; actif = fond couleur secondaire et texte `#0E0F11`, inactif = `#858A93`).
2. **Zone centrale** (prend la hauteur restante), en trois colonnes :
   - **Rail d'activité** : 52 px, fond `#1D1E22`, bordure droite `#2A2C31`. En haut, l'avatar initiale (36×36, rayon 6, fond `#2A2C31`, lettre en couleur secondaire), qui mène à l'écran d'import. Dessous, 6 entrées : Accueil, Stats, Équip., Compét., Daeva., Détails. Chaque entrée affiche son **score** (JetBrains Mono 13, demi-gras, coloré selon le score) au-dessus d'un libellé de 10 px. L'entrée active a une bordure gauche de 2 px en couleur secondaire et un texte `#D4D7DD` ; les autres sont en `#858A93`.
   - **Contenu** : en haut, un **fil d'Ariane** de 36 px (fond `#1B1C20`) avec « lyra › Équipement » à gauche et « Templier · Tank · 45 · Azphel (eu) · obj 1482 · pc 48210 » à droite (JetBrains Mono 12, `#858A93`, valeurs en `#D4D7DD`).
   - **Inspecteur** à droite : 340 px, fond `#1B1C20`, bordure gauche `#2A2C31`. Absent sur l'écran Détails.
3. **Barre d'état** : 24 px, fond = couleur sombre du thème (`bar`), JetBrains Mono 11, texte `#FFF6EE`. À gauche « ● Lyra · Azphel », « vue JcE », « N pièces à traiter » ; à droite « importé 22/10 21:14 », « données par défaut ».

Les en-têtes de section, dans le contenu comme dans l'inspecteur, suivent tous le même style : padding 8×14 (ou 8×20), 11 px, majuscules, interlettrage 0,08em, `#858A93`, bordure basse `#2A2C31`.

## Écrans

### Import (remplace la barre d'outils)
Une palette de recherche centrée à 14 % de la hauteur : 620 px de large, fond `#1B1C20`, bordure `#2A2C31`, rayon 6, ombre `0 12px 40px rgba(0,0,0,.5)`.
- **Ligne de saisie** : « > » en couleur secondaire, le nom en JetBrains Mono 14, puis une étiquette « eu · tous serveurs » (région et serveur ; serveur vide = tous, recherche filtrable comme aujourd'hui).
- **« Site officiel »** : les résultats de recherche (`SearchHit`), en grille `1fr 120 100 60` (nom demi-gras, classe, serveur, niveau). Survol `#24262B`.
- **« Récents »** : les personnages enregistrés (`CharacterStore.List()`).
- **Sous la palette** : « Entrée pour importer · Ctrl+K pour rouvrir » (JetBrains Mono 12, `#858A93`).
Un clic sur un résultat lance `ImportAsync`, un clic sur un récent lance `OpenSaved`. Les messages de statut ou d'erreur passent dans la barre d'état.

### Accueil
- **Contenu** : la fiche en silhouette. Grille de 4 colonnes `200px 1fr 1fr 200px`, espacements 5 (vertical) × 12 (horizontal), padding 18×20.
  - Colonne 1, lignes 1 à 7 : Casque, Épaulettes, Torse, Jambières, Gants, Bottes, Cape.
  - Colonne 4, lignes 1 à 8 : Collier, Boucle 1, Boucle 2, Anneau 1, Anneau 2, Bracelet 1, Bracelet 2, Ceinture.
  - Colonnes 2 et 3, lignes 1 à 6 : emplacement du rendu du personnage (bordure pointillée `#2E3036`, hachures `#18191C`/`#151619`).
  - Ligne 7 : Arme | Main secondaire ; ligne 8 : Rune 1 | Amulette.
- **Tuile de pièce** : padding 6×9, rayon 3, fond `#1B1C20`, bordure `#2A2C31`. Elle contient une barre verticale 3×30 de la couleur de rareté, le nom de l'emplacement (11 px, `#858A93`), le niveau d'objet et l'enchantement (JetBrains Mono 13, « +N » en `#858A93`), et un code de statut à droite (JetBrains Mono 11, demi-gras) : E = vide, W = à remplacer ou en retard, I = à enchanter, ✓ = OK, chacun dans la couleur du statut. Survol `#22242A`. Sélectionnée : fond `dim` du thème et bordure en couleur secondaire. Un clic ouvre l'écran Équipement sur cette pièce.
- **Inspecteur, section « Score »** : « Global » et le score en JetBrains Mono 26, puis 4 lignes au format `96px 1fr 28px` (libellé, barre de 4 px sur fond `#2A2C31`, valeur). Un clic sur une ligne mène à l'écran correspondant.
- **Inspecteur, section « Top 3 · vue JcE »** : lien « tout voir (N) » qui mène à Détails. Chaque action est une ligne `18px 1fr` : icône de gravité (✕ critique, ! important, i conseil), titre (13 px, 500) et « Zone — action » (12 px, `#858A93`). Un clic mène à l'écran ou à la pièce concernés.
- **Bloc repliable** « + Comment c'est calculé ? » (12 px, couleur secondaire) : il affiche le texte d'explication des scores.

### Équipement
Même silhouette que l'accueil. L'inspecteur, « Inspecteur · pièce », affiche :
- le nom de l'emplacement et le nom de la pièce (17 px, demi-gras, couleur de rareté) ;
- une liste clé/valeur en JetBrains Mono 12, colonnes `110px 1fr` : rareté, niveau_objet, enchantement « +N / max », statut (en couleur) ;
- « Conseil » (`SlotPlan.Advice`) et « Où chercher » (`NextSource` ou source du palier).

### Stats
- **En-tête** : « Répartition des stats ajustables · vue JcE » à gauche, « efficacité 72 % » à droite.
- **4 familles**, en grille `80px 1fr 220px` : nom, barre de 8 px (remplissage coloré, avec un repère blanc de 2×14 à la cible), puis « part / cible » suivi de l'écart (« équilibré », « +14 pts de trop » ou « −11 pts manquants »). Couleur : vert si l'écart est ≤ tolérance, orange si trop, rouge si manque.
- **Bloc repliable** : « Qu'est-ce qu'une stat ajustable ? ».
- **Tableau** en colonnes `150 100 90 80 80 1fr` : Stat, Famille, Total, Part, Repère, Poids (barre de 3 px en couleur secondaire + valeur). Lignes padding 7×20, 13 px, survol `#1E1F23`. Ligne sélectionnée : fond `dim` et bordure gauche de 2 px en couleur secondaire.
- **Inspecteur · stat** : nom, description, clé/valeur (famille, total, part, repère, poids), puis un verdict dans un encadré à bordure gauche colorée, « Où la trouver » et « Principales sources ».

### Compétences
- **Barre de budget** (JetBrains Mono 12) : « budget = 38 investis + [− 3 +] non dépensés = 41 », et à droite « réinit. gratuite · max base 10 ». Le compteur remplace le `NumericUpDown` + « Recalculer » : chaque clic recalcule tout de suite via `onInputs`.
- **Tableau** en colonnes `80 1fr 140 60 60 60 110` : Type, Compétence, Rôle, Acheté, Bonus, Total, Conseil. Le conseil est une pastille « +4 → 10 » (vert) ou « −4 → 6 » (rouge), fond de la couleur à 15 % d'opacité, rayon 3 ; « = » en gris quand il n'y a rien à changer.
- **Section Stigmas** : lignes `80 1fr 140 90 110` avec l'étiquette Garder (vert), Mettre (bleu) ou Retirer (rouge), le nom, le rôle, le niveau « 8 → 10 » et le coût en fragments.
- **Inspecteur · compétence** : type, rôle, niveau « acheté +bonus = total », conseil, priorité, et le texte « Pourquoi » (`SkillAdvice.Why`).

### Daevanion
- **Onglets de plateaux** (34 px, comme des onglets d'éditeur) : « Nezekan 34/62 ». L'onglet actif a une bordure haute de 2 px en couleur secondaire et le fond `#151619`.
- **Grille 15×15** : cases de 26 px, espacement 2, dans un cadre `#18191C`. Case ouverte = couleur de rareté pleine ; case fermée = fond `#1F2024` avec bordure de rareté à 33 % et opacité 0,55. Les compétences sont rondes (rayon 50 %), les stats carrées (rayon 3). Une case à prendre porte son numéro d'étape avec une bordure de 2 px en couleur secondaire. Une case à retirer a un fond sombre, une bordure de 2 px `#E5484D` et un ✕ rouge.
- **À droite de la grille** : nom du plateau, cases et points, réserve, et la légende.
- **Inspecteur, section « Points »** : dépensés et libres par réserve (Commun, puis Ariel en JcE ou Azphel en JcJ), avec un compteur − / + pour les points libres.
- **Inspecteur, section « Réorganisation »** (si un plan existe, avec le gain « +6 % ») : les cases à retirer en « − », puis les cases à prendre numérotées, avec le libellé « faisable maintenant » en vert.

### Détails
Pas d'inspecteur. En-tête « Problèmes · vue JcE » avec le nombre total, puis les conseils groupés par zone. Chaque groupe a un en-tête « ▾ Équipement 3 » (fond `#18191C`). Chaque conseil est une ligne `22px 1fr 1fr` : icône de gravité, titre et détail, puis « → action ». Un clic mène à l'écran ou à la pièce concernés.

## Interactions et état
- `screen` : import | home | stats | gear | skills | daeva | details. À sauvegarder dans `UiPrefs`, qui garde déjà `Tab`.
- `mode` : PvE | PvP. Basculer recalcule le rapport (comme aujourd'hui) et met à jour scores, actions, repères et réserve Ariel/Azphel.
- Sélections : `slot`, `stat`, `skill` et `board`, plus l'état des blocs repliables.
- Points non dépensés `sp`, `dp`, `ariel`, `azphel` : les compteurs appellent `onInputs` à chaque changement.
- `accent` : couleur secondaire choisie dans le menu « thème ». **À ajouter à `UiPrefs`.**
- Les infobulles existantes (`Tip`) peuvent rester sur les tuiles et les cases, en plus de l'inspecteur.

## Design tokens
**Fond et surfaces**
- fenêtre `#151619` ;
- panneaux et inspecteur `#1B1C20` ;
- chrome, menu et rail `#1D1E22` ;
- lignes et cadres `#18191C` ;
- survol `#1E1F23` à `#24262B` ;
- bordure `#2A2C31` ;
- séparateur de ligne `#1F2024` ou `#24262B`.

**Texte** : principal `#D4D7DD`, secondaire `#A3A8B1`, discret `#858A93`, très discret `#565A62`.

**Sémantique** (reprise de `Ui.cs`) : Good `#46A758`, Warn `#F5A524`, Bad `#E5484D`, Info (à enchanter) `#3FA7D6`. Score : ≥ 75 vert, ≥ 50 orange, sinon rouge.

**Raretés** (reprises de `progression.json`) : Commun `#EDEDED`, Rare `#52B35C`, Épique `#3D94D8`, Unique `#FB9800`, Orange `#EE6C2A`, Rouge `#D33939`, Spécial `#34B798`.

**Couleurs secondaires** (main / bar ; `dim` = main à 13 % d'opacité ; texte sur main = `#0E0F11`) :

| Thème | main | bar |
|---|---|---|
| Orange (défaut) | `#E8833A` | `#8A4718` |
| Vert | `#3DBE7A` | `#1C6440` |
| Bleu | `#4C9AFF` | `#2456A0` |
| Violet | `#A57CF0` | `#5A3B9A` |
| Rose | `#F06BA8` | `#963A66` |
| Cyan | `#33C9D6` | `#1A6E76` |
| Jaune | `#E6C04A` | `#7E6618` |
| Rouge | `#EF5B5B` | `#8C2C2C` |

**Typographie**
- Interface : IBM Plex Sans 400/500/600. Tailles : 10 (libellés du rail), 11 (en-têtes en majuscules), 12, 13 (corps), 15, 17 (titres de l'inspecteur).
- Chiffres, clés/valeurs, fil d'Ariane, barre d'état : JetBrains Mono 400/600, 11 à 13 ; scores en 26.
- Avalonia : embarquer les deux polices (`FontFamily` avec ressource `avares://`) ou se rabattre sur Inter / Cascadia Mono.

**Espacements** : 2, 5, 6, 8, 10, 12, 14, 16, 20.
**Rayons** : 3 (tuiles, pastilles), 4 (boutons), 6 (palette, avatar).
**Ombres** : uniquement sur la palette d'import et le menu thème, `0 12px 32–40px rgba(0,0,0,.5)`.

## Correspondance avec le code actuel
- `MainWindow` : remplacer la `WrapPanel` de la barre d'outils par une `DockPanel`. Ordre : menu en haut, barre d'état en bas, rail à gauche, puis le contenu.
- `MainWindow.Welcome()` → écran Import.
- `MainWindow.Header()` → fil d'Ariane et métadonnées.
- `ReportView.Build()` (`TabControl`) → un `ContentControl` piloté par le rail. Chaque méthode actuelle (`ActionPlan`, `Stats`, `Gear`, `Skills`, `Daevanion`, `Details`) se découpe en deux parties : contenu central et inspecteur.
- `Ui.cs` : remplacer `Muted`, `CardBackground` et `Accent` par les jetons ci-dessus, et `Accent` par la couleur secondaire choisie. Ajouter les helpers `SectionHeader`, `KeyValue`, `SlotTile` et `Stepper`.
- `ReportView.BoardGrid` : passer à des cases de 26 px et appliquer les styles ci-dessus.

## Fichiers
- `Aion2 Builder - IDE.dc.html` : la maquette interactive (s'ouvre dans un navigateur).
- `aion-data.js` : les données fictives et la logique de vue ; c'est la référence pour les règles de couleur, les seuils et les libellés.
- `support.js` : le moteur d'affichage de la maquette. Ne pas porter dans l'application.
