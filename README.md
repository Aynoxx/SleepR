# Mode IA

Petit utilitaire Windows pour **garder le PC allumé quand l'écran s'éteint ou que le capot est fermé**,
afin de laisser tourner un calcul long (IA locale, rendu, transfert, compilation) sans avoir à
surveiller la machine. Un seul bouton pour basculer, un seul bouton pour revenir exactement à la
configuration d'origine.

Aucune installation, aucun droit administrateur : `ModeIA.exe` est autonome (compilé avec le
compilateur C# fourni par Windows).

## Utilisation

1. Double-clique sur **`ModeIA.exe`**.
2. Bascule l'interrupteur **vers la droite** : le libellé au-dessus passe à « Mode IA actif » et
   le PC reste éveillé même écran éteint et capot fermé.
3. Rebascule-le **vers la gauche** pour revenir au comportement normal.

La fenêtre est une petite **carte flottante** aux coins arrondis et légèrement translucide (on
devine le fond du bureau au travers). Elle contient l'état courant, un interrupteur rond
gauche/droite, et une ligne d'indice discrète : rien d'autre, pas de barre de titre.

- **Déplacer** : maintiens un clic n'importe où sur la carte et fais-la glisser.
- **Mettre en arrière-plan** : le **×** en haut à droite.
- **Menu** : clic droit sur la carte (ou sur l'icône près de l'horloge).

Le fond est composé **pixel par pixel** (fenêtre superposée), pas par une couleur « trou » :
texte et interrupteur restent nets sur n'importe quel fond, et les zones réellement transparentes
laissent passer les clics vers la fenêtre du dessous.

Fermer la fenêtre (le **×** ou Alt+F4) **ne quitte pas** : l'application continue en arrière-plan et reste
présente dans la **barre des tâches**. En survolant son bouton, le résumé des applications affiche
l'état (`Mode IA — actif` ou `Mode IA — normal`) ; l'icône change aussi de couleur (verte en mode IA,
grise sinon). Pour arrêter complètement : clic droit sur l'icône près de l'horloge → **Quitter**
(les réglages d'origine sont alors restaurés).

## Ce que fait le mode IA

L'application enregistre d'abord les valeurs actuelles du mode d'alimentation actif, puis les met à
zéro (`powercfg`) :

| Réglage                | Valeur en mode IA              |
| ---------------------- | ------------------------------ |
| Action du capot        | Ne rien faire (secteur + batterie) |
| Action du bouton d'alimentation | Ne rien faire (secteur + batterie) |
| Mise en veille         | Jamais (secteur + batterie)    |
| Mise en veille prolongée | Jamais (secteur + batterie)  |
| Veille automatique     | Jamais (quand le réglage existe) |
| Demandes d'éveil système (`SYSTEMREQUIRED`) | Autorisées (secteur + batterie) |
| Demandes d'éveil de l'écran (`ALLOWDISPLAY`) | Autorisées (secteur + batterie) |

Pendant que l'application tourne, elle demande en plus à Windows de rester éveillé, **écran
compris** (`SetThreadExecutionState` avec `ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED`) : sur les PC
récents, c'est l'extinction de l'écran qui déclenche la mise en veille. Cette demande n'empêche pas
le capot de couper le rétroéclairage : l'écran reste noir, le PC continue de calculer.

Les valeurs d'origine sont stockées dans `%LOCALAPPDATA%\ModeIA\etat.txt` et servent à la
restauration. Supprimer ce fichier revient à considérer que le mode est inactif.

## Points importants sur ce PC (HP ProBook 440 G10)

- La machine est en **veille moderne (S0)**, pas en veille S3. Conséquence : le réglage « action du
  capot » est **masqué** dans les Paramètres de Windows (`ATTRIB_HIDE`) et n'apparaît pas dans
  `powercfg /query`. L'application l'écrit quand même dans le mode d'alimentation ; vérifié sur cette
  machine : la valeur passe de `1` (veille) à `0` (ne rien faire), puis revient à `1` à la
  désactivation.
- **Deux réglages cachés peuvent annuler la demande d'éveil.** Windows n'obéit à
  `SetThreadExecutionState` que si les autorisations « Allow system required requests »
  (`SYSTEMREQUIRED`) et « Allow display required policy » (`ALLOWDISPLAY`) valent « oui ». Elles
  sont masquées dans les Paramètres de Windows et peuvent être à « non » sur certaines
  configurations : le mode IA les force à `1` (secteur et batterie) puis les restaure.
- **La seule limite qui reste est l'action à batterie critique** (`BATACTIONCRIT`, par défaut
  « mettre en veille » à 5 %). Elle endort la machine quel que soit le mode, et il n'existe pas
  d'autre levier propre que de la neutraliser — au prix d'un arrêt brutal à 0 % :
  `powercfg /setdcvalueindex SCHEME_CURRENT SUB_BATTERY BATACTIONCRIT 0` (annulable avec la valeur
  d'origine, `1`). L'application ne le fait pas d'elle-même. Sur batterie, elle affiche un
  avertissement orange.
- **Garde l'application lancée** (fenêtre, bouton de la barre des tâches ou icône près de
  l'horloge) : c'est elle qui porte la demande d'éveil. Les réglages `powercfg` restent en place si tu la fermes brutalement, mais sans la
  demande d'éveil le PC peut s'endormir à l'expiration du délai de veille.
- **Ventilation** : capot fermé, le PC évacue moins bien la chaleur. Pose-le sur une surface dure,
  ne le mets pas dans une housse, et surveille la température sur un calcul de plusieurs heures.
- **Vérifier après une session** : dans une invite de commandes, lance `powercfg /sleepstudy` et
  ouvre le rapport. Si une session de veille recouvre la période, le PC s'est endormi malgré le mode.
  `powercfg /requests` (en administrateur) montre les demandes d'éveil actives.

Si le PC s'endort malgré tout, la solution la plus fiable est de le laisser branché à un écran
externe (mode « clamshell ») ; l'alternative — forcer la veille S3 via le registre
(`PlatformAoAcOverride`) — n'est pas supportée par Microsoft et n'est pas nécessaire ici.

## Ligne de commande (pratique pour tester)

```
ModeIA.exe --on       Active le mode, et lance l'interface si elle ne tourne pas
ModeIA.exe --off      Restaure les valeurs enregistrées
ModeIA.exe --status   État, valeurs en cours, et présence de l'interface
ModeIA.exe --startup on|off   Démarrage automatique à l'ouverture de session
```

La demande d'éveil n'existe que tant que l'interface tourne : `--on` la lance donc si besoin
(démarrage en arrière-plan, sans afficher la carte). `--status` indique lequel des deux cas on est.

Le démarrage automatique s'active aussi depuis le menu de l'icône (case « Démarrer avec Windows ») :
il écrit la clé `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, donc sans droits administrateur,
et lance l'application avec `--tray`.

## Recompiler

Lance `build.cmd`. Il utilise le compilateur C# livré avec Windows
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), donc aucun SDK ni paquet à installer.

## Désinstaller

Désactive le mode, décoche « Démarrer avec Windows » dans le menu (ou lance
`ModeIA.exe --startup off`), puis supprime le dossier `ModeIA` et le dossier `%LOCALAPPDATA%\ModeIA`.
Rien n'est écrit ailleurs (pas de tâche planifiée).
