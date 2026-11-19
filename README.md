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
restauration. Une ancienne sauvegarde issue de l'ancien nom (`%LOCALAPPDATA%\ModeIA\etat.txt`) peut être reprise automatiquement au premier lancement si le fichier existe dans l'ancien emplacement. Supprimer ce fichier revient à considérer que le mode est inactif.

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

L'icône `Sleepr.ico` est compilée dans l'exécutable (`-win32icon`, une seule copie) : c'est elle
qui s'affiche dans l'Explorateur, la barre des tâches et la zone de notification (avec une
pastille verte quand Sleepr est actif).

## Désinstaller

Désactive le mode, décoche « Démarrer avec Windows » dans le menu (ou lance
`Sleepr.exe --startup off`), puis supprime le dossier de l'application et le dossier
`%LOCALAPPDATA%\Sleepr`. Rien n'est écrit ailleurs (pas de tâche planifiée).
