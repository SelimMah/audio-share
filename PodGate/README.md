# PodGate (POC)

Petite app de la zone de notification Windows 11 qui **empêche Windows de se connecter tout seul à tes AirPods** et te **propose de les connecter** quand elle les détecte.

## Ce qu'elle fait

| Situation | Comportement |
|---|---|
| PC allumé, AirPods dans le boîtier | Windows ne s'y connecte pas. |
| Tu sors les AirPods (ou ouvres le boîtier) | Une fenêtre apparaît en bas à droite : **Connecter** / **Plus tard**, avec la batterie. |
| Le PC démarre alors que tu utilises les AirPods avec ton iPhone | Même fenêtre. Ton iPhone garde la connexion tant que tu ne cliques pas sur Connecter. |
| Tu cliques sur **Connecter** dans les Paramètres Windows (ou Win + A) | PodGate le détecte et active l'audio : les AirPods fonctionnent normalement. |
| AirPods rangés ou partis sur l'iPhone | Après 8 s, PodGate reverrouille : pas de reconnexion surprise. |
| Arrêt du PC ou mise en veille | Reverrouillage immédiat. |

**Comment ça marche :** c'est l'automatisation du test que tu as fait à la main. Les cases *Récepteur audio*, *Contrôle à distance* et *Téléphonie mains libres* restent décochées. PodGate les coche quand tu veux te connecter, puis les décoche à la déconnexion. La détection se fait en lisant les annonces Bluetooth Low Energy des AirPods, sans aucun appairage supplémentaire.

## Compiler

1. Installe le SDK .NET 10 (une seule fois) :
   ```
   winget install Microsoft.DotNet.SDK.10
   ```
2. Double-clique sur `build.cmd` (ou lance-le depuis un terminal).
3. Le fichier `dist\PodGate.exe` est autonome : copie-le où tu veux (par ex. `%LOCALAPPDATA%\Programs\PodGate\`), puis lance-le.

> ⚠️ Je n'ai pas pu compiler le projet de mon côté : mon environnement n'a pas accès au SDK .NET. La syntaxe a été vérifiée, mais si `build.cmd` affiche une erreur, envoie-la-moi et je corrige.

## Utilisation

- **Icône** (deux écouteurs) près de l'horloge. Une pastille indique l'état : aucune = verrouillé, bleue = détectés à proximité, orange = opération en cours, verte = connectés.
- **Clic gauche** : propose la connexion (ou affiche l'état si déjà connectés).
- **Clic droit** : menu.
  - Connecter / Déconnecter
  - *Appareil géré* : choisis l'appareil si tu as plusieurs écouteurs appairés.
  - *Proposer la connexion quand ils sont détectés* : active ou coupe la fenêtre automatique.
  - *Lancer au démarrage de Windows* : activé automatiquement au premier lancement.
  - *Ouvrir le journal*
  - **Désactiver PodGate et restaurer Windows…** : recoche les services audio, retire le lancement au démarrage, efface les réglages et ferme l'app. Windows retrouve exactement son comportement d'origine.
  - *Quitter* : ferme l'app en laissant l'audio verrouillé. Les AirPods restent connectables depuis les Paramètres en cochant les services, ou en relançant PodGate.

Au **tout premier lancement**, si tes AirPods sont connectés et en cours d'utilisation, PodGate ne les coupe pas. Il verrouillera à la prochaine déconnexion.

## Droits administrateur

Normalement, aucun droit administrateur n'est nécessaire, puisque tu as pu cocher et décocher les cases sans en avoir. Si Windows en exige malgré tout, PodGate se relance automatiquement avec une demande UAC pour cette seule opération. Si cette demande revient à chaque connexion, dis-le-moi : on passera par une tâche planifiée qui s'exécute avec les droits élevés.

## Réglages avancés

Ils se trouvent dans `%LOCALAPPDATA%\PodGate\settings.json`. Ferme PodGate avant de modifier ce fichier.

| Clé | Défaut | Rôle |
|---|---|---|
| `RssiThreshold` | `-72` | Signal minimum (dBm) pour considérer les AirPods « à proximité ». Monte vers `-60` si des AirPods voisins déclenchent la fenêtre, descends vers `-80` si les tiens ne sont pas détectés. |
| `PresenceTimeoutSeconds` | `45` | Sans signal pendant ce délai, les AirPods sont considérés rangés. |
| `AutoLockDelaySeconds` | `8` | Délai avant de reverrouiller après une déconnexion. |
| `WindowsUiGraceSeconds` | `20` | Fenêtre pendant laquelle une connexion est attribuée aux Paramètres Windows. |
| `ConnectTimeoutSeconds` | `25` | Temps laissé à Windows pour se connecter. |
| `LearnedModel` | appris | Modèle de tes AirPods, appris la première fois qu'ils sont connectés. Il filtre les AirPods des autres. Mets `null` pour le réapprendre. |

Le journal est dans `%LOCALAPPDATA%\PodGate\podgate.log`. C'est le premier endroit à regarder si quelque chose ne se passe pas comme prévu.

## Limites connues du POC

- **Connexion depuis les Paramètres Windows** : PodGate repère que tu as cliqué sur Connecter parce que la fenêtre Paramètres (ou les Paramètres rapides) était au premier plan dans les 20 dernières secondes. Le panneau de configuration classique n'est pas pris en compte. Si tu y coches les cases à la main, PodGate le voit quand même et laisse faire.
- **Détection BLE** : avant que le modèle soit appris, n'importe quels AirPods proches avec un signal fort peuvent déclencher la fenêtre.
- **Batterie** : précision de 10 %. Elle n'est affichée que lorsque les AirPods sont détectés.
- **Démarrage rapide de Windows** : si PodGate n'a pas pu reverrouiller à l'arrêt, Windows peut se reconnecter au démarrage suivant. PodGate le détecte alors, coupe la connexion et te propose de la rétablir.

## Structure du code (`src/`)

| Fichier | Contenu |
|---|---|
| `TrayApp.cs` | Logique principale : états, sondage, menu, connexion et verrouillage |
| `BluetoothClassic.cs` | API Win32 Bluetooth (`BluetoothSetServiceState`…), relance en administrateur |
| `AirPodsScanner.cs` | Écoute BLE et décodage des annonces Apple (modèle, batterie) |
| `ForegroundTracker.cs` | Détection de la fenêtre Paramètres au premier plan |
| `PromptPopup.cs` | Fenêtre en bas à droite (thème clair/sombre, couleur d'accentuation) |
| `IconFactory.cs` | Icônes dessinées à la volée |
| `Infrastructure.cs` | Réglages, journal, démarrage auto, thème, boîtes de dialogue |
