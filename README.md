# 🔊 Audio Share

Transforme un PC Windows en **enceinte Bluetooth et réseau** : le son d'un
téléphone (ou d'un autre PC) est joué sur ce PC, avec contrôle du volume et de
la **balance gauche/droite appliquée uniquement au son reçu** — jamais aux sons
locaux du PC.

## Fonctionnalités

- **Récepteur Bluetooth (A2DP Sink)** via l'API `AudioPlaybackConnection` de
  Windows : appaire ton téléphone, clique dessus dans la liste, connecte-toi
  depuis le téléphone — sa musique sort sur le PC.
- **Réseau local, dans les deux sens** : chaque instance d'Audio Share écoute
  en permanence, et un interrupteur « Envoyer le son de ce PC » capture tout le
  son local et le diffuse en PCM 48 kHz vers l'autre PC, avec découverte
  automatique (UDP) — aucun réglage d'IP. Installe simplement l'app sur les
  deux machines.
- **Balance gauche/droite et volume du flux reçu uniquement**, via le volume
  par canal de la session audio (`IChannelAudioVolume`) pour le Bluetooth, et
  par traitement direct des échantillons pour le réseau.
- **Interface iOS-like** : panneau « flyout » en bas à droite (comme le volume
  Windows), fond acrylique Windows 11, animation d'ouverture, icône de zone de
  notification dessinée à l'exécution (colorée quand la réception est active).
- Absente de la barre des tâches et d'Alt+Tab ; tout se pilote depuis l'icône
  de notification (clic gauche : panneau ; clic droit : balance, quitter).

## Prérequis

- Windows 10 2004+ (Windows 11 recommandé pour l'acrylique)
- .NET 10 SDK pour compiler
- Un adaptateur Bluetooth pour la réception Bluetooth

## Compiler et lancer

```powershell
dotnet build AudioShare.csproj
dotnet run --project AudioShare.csproj
```

## Installateur

Le script [installer.iss](installer.iss) (Inno Setup 6) produit
`AudioShare-Setup.exe` : installation par utilisateur (sans droits admin),
raccourcis Menu Démarrer/Bureau, lancement au démarrage optionnel,
désinstallateur. Relancer l'installateur sur une machine où l'app existe déjà
**met à jour en place** : l'instance en cours est fermée automatiquement et
remplacée, sans doublon.

```powershell
dotnet publish AudioShare.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish
iscc installer.iss
```

## Notes techniques

Ce projet contourne plusieurs angles morts de Windows, documentés dans le code :
le flux Bluetooth entrant est rendu par le moteur audio système (aucune session
applicative interceptable), les périphériques n'exposent souvent qu'un volume
matériel mono, et le routage par application ne s'applique pas aux flux du
moteur. D'où le choix final du volume par canal de session, seul mécanisme qui
isole proprement le son reçu.
