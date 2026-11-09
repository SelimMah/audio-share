# Refonte de l'interface Duckast — spécification

Maquettes retenues avec Selim (nov. 2026). **Implémentées le 8/11/2026**
(pas encore publiées) : composant `Tile` + illustrations + `BatteryBars` +
`VolumeBar` dans `Tile.cs`, pages dans `MainWindow.xaml`, fenêtre AirPods dans
`AirPodsPopup.xaml.cs`. Voir « Choix d'implémentation » en fin de document.

Canvas Claude Design (toutes les explorations, page « Explorations », et les
designs retenus, page « Sélection ») :
https://claude.ai/artifact/QmJdWhb4vRnLpycxgwTSia

## Les maquettes

Dans [maquettes/](maquettes/), des HTML statiques (HTML + SVG en ligne). Ils
s'ouvrent dans un navigateur ; les valeurs exactes (couleurs, tailles,
coordonnées des tracés SVG) sont dans le balisage : **c'est la référence**,
ce document n'en est que le résumé.

| Fichier | Contenu |
|---|---|
| `01-page-principale.html` | Page principale (360 × 600) |
| `02-reglages.html` | Page Réglages (360 × 600) |
| `03-tuiles-autres-etats.html` | Les 4 tuiles dans leur autre état |
| `04-fenetre-airpods.html` | Fenêtre AirPods, 4 états (336 × 92 chacun) |

Les `<svg>`, `<symbol>` et `<use>` se transposent en `Path`/`Geometry` WPF ;
les `radial-gradient` en `RadialGradientBrush` fondu en transparence.

## Contraintes du projet (voir CLAUDE.md)

- **Ne jamais mettre de `BlurEffect`/`Effect` WPF** dans le flyout : ça casse
  l'acrylique DWM. Les halos se font en dégradés radiaux.
- Les maquettes utilisent `backdrop-filter` pour la fenêtre AirPods : dans
  l'appli, c'est l'acrylique DWM de `WindowEffects.ApplyPopup`.
- Le mode sombre (`WindowEffects.Veil`) reste un voile noir par-dessus.
- Textes via `Loc.T(en, fr)`. Les maquettes sont en français.
- Le flyout garde sa hauteur (`PageScroll` figé / `ReserveSettingsHeight`) :
  Réglages et page principale font la même hauteur.

## Jetons

Existants (`Styles.xaml`) : `Accent` #0A84FF, `Green` #30D158, `Label` #FFF,
`LabelSecondary` #99EBEBF5, `LabelTertiary` #4DEBEBF5, `CardFill` #14FFFFFF,
`CardStroke` #1FFFFFFF, `SegmentBed` #3D787880.

À ajouter : orange #FF9F0A (texte #FFB340), rouge #FF6961 (texte #FF8A84),
bleu clair #4DA3FF (texte #7DBBFF), indigo #7D7AFF (non utilisé finalement).

Rayons : tuile 18, fenêtre AirPods 20, pastille ronde = moitié de la taille.

## Tuile (composant commun)

Remplace la carte « SOURCES » (liste + lignes) de la page principale. Grille
2 × 2, écart 10, largeur (332 − 10) / 2 = 161, **hauteur 112**, padding 14.

Anatomie, identique pour les 4 tuiles :

- **Haut gauche : le nom** (14, semi-bold). Il est TOUJOURS au-dessus du statut.
- **Haut droite : bouton ✕ rond 24 px** (fond #40000000, glyphe 10), seulement
  s'il y a quelque chose à arrêter / déconnecter.
- **Sous le nom : le statut** = point 6 px (avec lueur `0 0 6px` de sa couleur
  quand actif) + texte 11,5.
- **Bas : éventuellement une ligne de détail** 12 en `LabelSecondary`, puis le
  pied (batteries, sélecteur…).
- **Fond : illustration au trait** (voir plus bas), qui déborde à droite.

États de fond (bordure 1 px, reflet intérieur `0 1px 0 #1AFFFFFF`) :

| État | Fond | Bordure | Point/texte du statut |
|---|---|---|---|
| Inactif | #14FFFFFF (verre) | #1FFFFFFF | gris #66EBEBF5 / `LabelSecondary` |
| Actif vert | dégradé 160° rgba(48,209,88,.30) → .10 | rgba(48,209,88,.45) | #30D158 |
| Actif bleu (Envoyer) | dégradé 160° rgba(10,132,255,.32) → .10 | rgba(10,132,255,.55) | #4DA3FF / texte #7DBBFF |

**Comportement** (celui de la tuile iPhone actuelle, généralisé) : la tuile
change de couleur quand elle est active ; le ✕ arrête / déconnecte ; **plus
aucun interrupteur dans les tuiles** (Selim n'en veut pas).

### Les 4 tuiles

| Tuile | Inactif | Actif | ✕ |
|---|---|---|---|
| iPhone | « Disponible » · « Toucher pour recevoir » | vert · « Connecté » · « Son sur ce PC » | arrête la réception (`Disconnect_Click`) |
| Réseau local | verre · « En écoute » · « Aucun PC » | vert · « Reçoit » · « depuis SURFACE-11 » | quitte le flux |
| Envoyer | verre · « Éteint » · « vers SURFACE-11 » | **bleu** · « Émission » · « vers SURFACE-11 » | arrête l'envoi |
| AirPods | verre · « À proximité » + niveaux | vert · « Connectés » + niveaux | déconnecte |

- **Envoyer** : toute la tuile est le bouton (remplace `SendToggle` visible).
  Le statut `SendStatus` (recherche d'un récepteur…) peut passer dans la ligne
  de détail.
- **AirPods** : sous le statut, trois mini-barres G / D / B (boîtier).
  Libellé 10,5 + valeur à droite ; barre de 3 px. Sur tuile verte : piste
  #38000000, remplissage blanc ; sur verre : piste rgba(120,120,128,.35),
  remplissage rgba(255,255,255,.85).
  La tuile n'apparaît que si la gestion AirPods est activée (comme `AirPodsCard`).

### Illustrations (SVG, repère 161 × 112 = la tuile)

Trait blanc 30 % d'opacité (0,6–1 px), remplissage blanc 5 %, plus un halo
de la couleur d'état (cercles concentriques de 10–30 % d'opacité) et un
petit point lumineux plein. Elles débordent à droite, derrière le texte.

- **iPhone** : téléphone penché de 9°, îlot, écran qui luit.
- **Réseau local** : globe (cercle, 2 méridiens, équateur, 2 parallèles) et un
  point lumineux en haut à droite.
- **Envoyer** : écran + pied, ondes qui sortent à droite.
- **AirPods** : boîtier au trait (rect 70×46 rx 20 dans un repère 76 × 52,
  trait de charnière, voyant), agrandi (≈ 170 × 116) et décalé en haut à
  droite. **Pas d'écouteurs dessinés**, seulement le boîtier.

Tracés exacts dans `01-page-principale.html` (symboles `sphone`, `smonitor`,
`scase`, globe en ligne).

## Page principale

De haut en bas : en-tête (logo + « Duckast », engrenage) ; grille 2 × 2 des
tuiles ; tuile **Son reçu** pleine largeur (hauteur 150, rayon 18) :
« SON REÇU » (11, interlettrage .06 em) + pourcentage à droite (15 semi-bold),
barre de volume épaisse (44 de haut, rayon 14, piste rgba(120,120,128,.30),
remplissage rgba(255,255,255,.92), glyphe ♪ dedans — **le glyphe est de la
maquette**, garder le bouton muet), puis le sélecteur Gauche/Stéréo/Droite
(34, rayon 9). Le vumètre « aurore » actuel est à conserver derrière la tuile.

## Fenêtre AirPods (popup à l'ouverture du boîtier)

Taille **336 × 92**, rayon 20, acrylique sombre (maquette : fond
rgba(38,41,48,.74) + flou), bordure 1 px, ombre `0 14 36 #80000000`.
Positions absolues (voir `04-fenetre-airpods.html`) :

- nom : gauche 16, haut 11, 15 semi-bold ;
- statut : gauche 16, haut 32, 12 (+ éventuellement une icône d'action ronde
  de 22 px à la fin de la ligne) ;
- niveaux G/D/B : gauche 16, bas 11, largeur 196 ;
- ✕ (ferme seulement) : droite 14, haut 11 ;
- boîtier au trait : 150 × 103, droite −30, haut −28 (déborde).

**Toute la fenêtre est cliquable** pour l'action principale. États :

| État | Teinte | Statut | Action |
|---|---|---|---|
| Proposition | verre | « Toucher pour connecter » (#7DBBFF, point #4DA3FF) | clic fenêtre = connecter |
| Connexion en cours | **orange** (#FF9F0A 30 % → 6 %, bordure 55 %) | « Connexion en cours… » (#FFB340) + anneau de chargement ; niveaux à 55 % | icône ■ = annuler |
| Connectés | vert | « Connectés » | icône maillon barré = dissocier |
| Échec | rouge (#FF6961 24 % → 4 %, bordure 50 %) | « Échec » (#FF8A84) + message « Sors-les du boîtier, puis réessaie. » à la place des niveaux | icône ↻ = réessayer |

Remplace l'affichage actuel de `AirPodsPopup` (bouton « Connecter » + états
`AirPodsMessage`). La logique (`AirPodsGate`, événements `PromptRequested`,
`MessageRequested`, `StatusPeekRequested`) reste ; il faut ajouter les actions
Annuler / Dissocier / Réessayer et l'état « connexion en cours » coloré.
Le « coup d'œil batterie » à l'ouverture du boîtier quand ils sont déjà
connectés (v1.9.1) = l'état « Connectés ».

## Page Réglages

En-tête : logo + « Réglages », engrenage allumé (fond #38FFFFFF). Même hauteur
que la page principale.

- **GÉNÉRAL** : tuiles 161 × **88** (padding 12/14), mêmes règles que ci-dessus
  (nom en haut, statut juste dessous, vert si activé, pas d'interrupteur) :
  *Lancer avec Windows* (« Activé »), *Mode sombre* (« Désactivé »),
  *Langue* (sélecteur English | Français de 26 px dedans),
  *Mises à jour* (« À jour · 1.9.1 », un toucher lance la vérification ;
  `UpdateStatus` s'affiche dans le statut).
  Illustrations : symbole d'alimentation, croissant de lune, globe, flèche
  circulaire — **les tracés les plus simples** (voir le fichier, c'est ceux
  que Selim a validés ; ne pas ajouter de détails).
- **AIRPODS** : une carte verte (comme une tuile active, boîtier en filigrane
  qui déborde en haut à droite) : titre « Gestion des AirPods » + « ● Activée »,
  puis lignes séparées par un filet : *Appareil* (valeur + chevron, liste),
  *Proposer à l'ouverture du boîtier* (« ● Activé » en vert, un toucher sur la
  ligne bascule — **pas d'interrupteur**), *Aperçu de la fenêtre* (bouton rond
  ▶ de 26 px). `AirPodsHint` est à replacer sous la carte.
- **AVANCÉ** : une ligne repliée avec un résumé (« Sortie, destination, Opus,
  diagnostics ») et un chevron ; se déplie sur place comme aujourd'hui
  (`AdvancedHost`).

## Pas encore dessiné

- Section **Avancé dépliée** (sortie, « Envoyer vers », Opus, lien Diagnostic)
  et la page **Diagnostic** : à décliner dans le même style.
- États **Envoyer / Réseau** quand aucun récepteur n'est trouvé
  (« Recherche d'un récepteur… »).
- Mode sombre appliqué à ces maquettes.

## Décisions de Selim à respecter

- Pas d'interrupteur à bascule dans les tuiles, ni « radio button » dans la
  carte AirPods.
- Le nom d'une tuile ne doit jamais être sous son statut.
- Les illustrations d'AirPods montrent le boîtier seul.
- Pas de gros bouton « Connecter » : fenêtre cliquable + petite icône d'action.
- Icônes de la section Général : la version la plus simple.

## Choix d'implémentation (8/11/2026, à valider par Selim)

États non dessinés, déclinés avec les mêmes couleurs :

- **Téléphone choisi mais pas encore relié** : orange « En attente… » ·
  « Choisis ce PC dessus », avec ✕. Plusieurs téléphones appairés : clic
  droit sur la tuile pour en choisir un autre (le dernier utilisé est
  mémorisé, `Prefs.LastPhoneId`). Aucun téléphone : un toucher ouvre les
  réglages Bluetooth de Windows.
- **Envoyer sans récepteur** : bleu « Recherche… » · « d'un récepteur » (ou
  « vers X » si une destination est fixée), puis « Connexion… », « Émission ».
  Un toucher sur la tuile allume / éteint, le ✕ arrête.
- **Réseau local, ✕** : met l'émetteur en pause (`NetworkReceiver.PauseCurrentSender`) ;
  tuile « En pause » · « Toucher pour reprendre ». La pause est oubliée si
  l'émetteur se tait 15 s (il a arrêté d'émettre).
- **AirPods** : orange « Connexion… » pendant la connexion (✕ = annuler,
  `AirPodsGate.CancelConnectAsync`), « Déconnexion… » en verre pendant le
  verrouillage, « Au repos » quand ils ne sont pas détectés.
- **Mises à jour** : « À jour · 1.9.1 » (point vert) d'après
  `Updater.LastOutcome` ; la flèche tourne pendant la vérification, le
  message complet est en infobulle.
- **Carte AIRPODS des Réglages** : toucher son en-tête active / désactive la
  gestion (orange « Activation… » pendant l'opération) ; les lignes se
  déplient dessous.
- **Avancé déplié** et **Diagnostic** : mêmes cartes (rayon 18), lignes de 40,
  « ● Activée » au lieu de l'interrupteur Opus.
- Les titres des tuiles des Réglages passent sur deux lignes si besoin
  (« Lancer avec Windows »).

Écarts techniques assumés :

- Fenêtre AirPods : coins arrondis par DWM (≈ 8 px), pas 20 — une fenêtre
  acrylique ne peut pas avoir d'autre rayon sans région découpée (crénelée).
- Pas d'interlettrage (WPF ne sait pas faire `letter-spacing`).

Animations : tuiles qui s'allument en fondu croisé avec un léger gonflement
et une onde, pression (enfoncement + onde depuis le doigt), survol, ✕ qui
surgit avec un rebond, textes d'état qui montent en fondu, point d'état et
halo d'illustration qui respirent quand c'est actif (coupés panneau caché),
ondes de l'écran « Envoyer » en émission, flèche des mises à jour qui tourne,
entrée des tuiles en cascade, engrenage qui tourne et s'allume, barre de
volume lissée, pastilles des sélecteurs qui glissent, sections qui se
déplient. Fenêtre AirPods : entrée qui se pose, boîtier qui glisse, contenu
en cascade, onde verte à la connexion, tremblement à l'échec, anneau de
chargement.

