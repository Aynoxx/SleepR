# Sleepr

Petit utilitaire Windows pour **garder le PC allumé quand l'écran s'éteint ou que le capot est fermé**,
afin de laisser tourner un calcul long (IA locale, rendu, transfert, compilation) sans avoir à
surveiller la machine. Un seul bouton pour basculer, un seul bouton pour revenir exactement à la
configuration d'origine.

Aucune installation, aucun droit administrateur : `Sleepr.exe` est autonome (compilé avec le
compilateur C# fourni par Windows).

## Utilisation

1. Double-clique sur **`Sleepr.exe`**.
2. Bascule l'interrupteur **vers la droite** : le libellé au-dessus passe à « Sleepr actif » et
   le PC reste éveillé même écran éteint et capot fermé.
3. Rebascule-le **vers la gauche** pour revenir au comportement normal.

La fenêtre est une petite **carte flottante** aux coins arrondis et légèrement translucide (on
devine le fond du bureau au travers). Elle contient l'état courant, un interrupteur rond
gauche/droite, et une ligne d'indice discrète : rien d'autre, pas de barre de titre.

- **Déplacer** : maintiens un clic n'importe où sur la carte et fais-la glisser (le curseur
  devient une croix de déplacement) ; la position est mémorisée pour les fois suivantes.
- **Masquer** : le **×** en haut à droite (ou **Échap**) — la carte disparaît, l'application
  continue de tourner et le PC reste éveillé.
- **Basculer** : l'interrupteur, un double-clic sur la carte, ou Espace/Entrée quand la carte a le
  focus.
- **Menu** : clic droit sur la carte (ou sur l'icône près de l'horloge).

Le fond est composé **pixel par pixel** (fenêtre superposée), pas par une couleur « trou » :
texte et interrupteur restent nets sur n'importe quel fond, et les zones réellement transparentes
laissent passer les clics vers la fenêtre du dessous.

Fermer la fenêtre (le **×**, **Échap** ou Alt+F4) **ne quitte pas** et **ne coupe pas la
protection** : la carte disparaît, sans bouton dans la barre des tâches, mais l'application reste
vivante près de l'horloge et continue de porter la demande d'éveil. Rouvre-la par un clic sur son
icône (ou un double-clic) ; en la survolant, le résumé des applications affiche l'état
(`Sleepr — actif` ou `Sleepr — normal`) et l'icône change de couleur (verte en mode actif, grise
sinon). Pour arrêter complètement : clic droit sur la carte ou sur l'icône → **Quitter** (les
réglages d'origine sont alors restaurés ; si le mode est actif, une confirmation est demandée,
car quitter rend la mise en veille possible).

## Ce que fait le mode actif

L'application enregistre d'abord les valeurs actuelles du mode d'alimentation actif, puis les met à
zéro (`powercfg`) :

| Réglage                | Valeur en mode Sleepr          |
| ---------------------- | ------------------------------ |
| Action du capot        | Ne rien faire (secteur + batterie) |
| Action du bouton d'alimentation | Ne rien faire (secteur + batterie) |
| Mise en veille         | Jamais (secteur + batterie)    |
| Mise en veille prolongée | Jamais (secteur + batterie)  |
| Veille automatique     | Jamais (quand le réglage existe) |
| Demandes d'éveil système (`SYSTEMREQUIRED`) | Autorisées (secteur + batterie) |
| Demandes d'éveil de l'écran (`ALLOWDISPLAY`) | Autorisées (secteur + batterie) |
| Mot de passe au réveil | Jamais (`DelayLockInterval`, restauré à la sortie) |

Pendant que l'application tourne, elle demande en plus à Windows de rester éveillé, **écran
compris** (`SetThreadExecutionState` avec `ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED`) : sur les PC
récents, c'est l'extinction de l'écran qui déclenche la mise en veille. Cette demande n'empêche pas
le capot de couper le rétroéclairage : l'écran reste noir, le PC continue de calculer.

Les valeurs d'origine sont stockées dans `%LOCALAPPDATA%\Sleepr\etat.txt` et servent à la
restauration. Une sauvegarde de l'ancien nom (`%LOCALAPPDATA%\ModeIA\etat.txt`) est reprise
automatiquement au premier lancement. Supprimer ce fichier revient à considérer que le mode est
inactif.

## Points importants sur ce PC (HP ProBook 440 G10)

- La machine est en **veille moderne (S0)**, pas en veille S3. Conséquence : le réglage « action du
  capot » est **masqué** dans les Paramètres de Windows (`ATTRIB_HIDE`) et n'apparaît pas dans
  `powercfg /query`. L'application l'écrit quand même dans le mode d'alimentation ; vérifié sur cette
  machine : la valeur passe de `1` (veille) à `0` (ne rien faire), puis revient à `1` à la
  désactivation.
- **Le capot fermé n'affiche plus l'écran de connexion.** Fermer le capot éteint brièvement
  l'écran, et Windows verrouille alors la session : au retour, on retombe sur l'écran de
  connexion même si la machine ne s'est jamais endormie. Sur une machine en veille moderne
  (S0), ce délai n'est pas le réglage `CONSOLELOCK` (absent, `powercfg` refuse même de le
  lire) mais la valeur utilisateur `HKCU\Control Panel\Desktop\DelayLockInterval`, en
  secondes, où `0xFFFFFFFF` signifie « ne jamais demander ». Sleepr l'écrit et restaure
  l'ancienne valeur à la sortie, sans droits administrateur. À savoir : ouvrir la page
  « Options de connexion » des Paramètres réécrit cette valeur.
- **Deux réglages cachés peuvent annuler la demande d'éveil.** Windows n'obéit à
  `SetThreadExecutionState` que si les autorisations « Allow system required requests »
  (`SYSTEMREQUIRED`) et « Allow display required policy » (`ALLOWDISPLAY`) valent « oui ». Elles
  sont masquées dans les Paramètres de Windows et peuvent être à « non » sur certaines
  configurations : Sleepr les force à `1` (secteur et batterie) puis les restaure.
- **La seule limite qui reste est l'action à batterie critique** (`BATACTIONCRIT`, par défaut
  « mettre en veille » à 5 %). Elle endort la machine quel que soit le mode, et il n'existe pas
  d'autre levier propre que de la neutraliser — au prix d'un arrêt brutal à 0 % :
  `powercfg /setdcvalueindex SCHEME_CURRENT SUB_BATTERY BATACTIONCRIT 0` (annulable avec la valeur
  d'origine, `1`). L'application ne le fait pas d'elle-même. Sur batterie, elle affiche un
  avertissement orange.
- **Garde l'application lancée** (fenêtre ou icône près de l'horloge) : c'est elle qui porte la
  demande d'éveil. Les réglages `powercfg` restent en place si tu la fermes brutalement, mais sans
  la demande d'éveil le PC peut s'endormir à l'expiration du délai de veille.
- **Ventilation** : capot fermé, le PC évacue moins bien la chaleur. Pose-le sur une surface dure,
  ne le mets pas dans une housse, et surveille la température sur un calcul de plusieurs heures.
- **Vérifier après une session** : dans une invite de commandes, lance `powercfg /sleepstudy` et
  ouvre le rapport. Si une session de veille recouvre la période, le PC s'est endormi malgré le mode.
  `powercfg /requests` (en administrateur) montre les demandes d'éveil actives.
- **Si quelque chose se passe mal**, la trace est écrite dans `%LOCALAPPDATA%\Sleepr\crash.txt` :
  l'application journalise les erreurs inattendues et continue de tourner au lieu de disparaître
  en silence.

Si le PC s'endort malgré tout, la solution la plus fiable est de le laisser branché à un écran
externe (mode « clamshell ») ; l'alternative — forcer la veille S3 via le registre
(`PlatformAoAcOverride`) — n'est pas supportée par Microsoft et n'est pas nécessaire ici.

## Ligne de commande (pratique pour tester)

```
Sleepr.exe --on       Active le mode, et lance l'interface si elle ne tourne pas
Sleepr.exe --off      Restaure les valeurs enregistrées
Sleepr.exe --status   État, valeurs en cours, et présence de l'interface
Sleepr.exe --startup on|off   Démarrage automatique à l'ouverture de session
```

La demande d'éveil n'existe que tant que l'interface tourne : `--on` la lance donc si besoin
(démarrage en arrière-plan, sans afficher la carte). `--status` indique lequel des deux cas on est.

Le démarrage automatique s'active aussi depuis le menu de l'icône (case « Démarrer avec Windows ») :
il écrit la clé `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (valeur `Sleepr`), donc sans
droits administrateur, et lance l'application avec `--tray`.

## Recompiler

Lance `build.cmd`. Il utilise le compilateur C# livré avec Windows
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), donc aucun SDK ni paquet à installer.

## Désinstaller

Désactive le mode, décoche « Démarrer avec Windows » dans le menu (ou lance
`Sleepr.exe --startup off`), puis supprime le dossier de l'application et le dossier
`%LOCALAPPDATA%\Sleepr`. Rien n'est écrit ailleurs (pas de tâche planifiée).
