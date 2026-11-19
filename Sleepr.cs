// Sleepr — petit utilitaire Windows pour garder le PC allumé (écran éteint, capot fermé)
//
// Ce fichier se compile avec le csc.exe fourni par Windows (pas de SDK à installer) :
//     build.cmd
//
// Modes ligne de commande (pratiques pour tester) :
//     Sleepr.exe --on | --off | --status | --startup [on|off]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Sleepr
{
    // ---------------------------------------------------------------------
    // Description d'un réglage d'alimentation Windows (sous-groupe + réglage)
    // ---------------------------------------------------------------------
    internal sealed class PowerSetting
    {
        public readonly string SubgroupAlias;
        public readonly string SettingAlias;
        public readonly string SubgroupGuid;
        public readonly string SettingGuid;
        public readonly string Label;
        // Valeur écrite en mode IA (0 pour les délais, 1 pour les autorisations d'éveil).
        public readonly int ActiveValue;
        // Valeur supposée quand la clé de registre n'existe pas encore : c'est le cas des
        // réglages masqués jamais touchés, dont le défaut Windows est connu.
        public readonly int? FallbackValue;

        public PowerSetting(string subgroupAlias, string settingAlias, string subgroupGuid, string settingGuid,
            string label, int activeValue, int? fallbackValue = null)
        {
            this.SubgroupAlias = subgroupAlias;
            this.SettingAlias = settingAlias;
            this.SubgroupGuid = subgroupGuid;
            this.SettingGuid = settingGuid;
            this.Label = label;
            this.ActiveValue = activeValue;
            this.FallbackValue = fallbackValue;
        }
    }

    // Valeur d'origine d'un réglage, sauvegardée avant modification.
    internal sealed class SavedValue
    {
        public PowerSetting Setting;
        public int Ac;
        public int Dc;
    }

    // ---------------------------------------------------------------------
    // Accès à powercfg (lecture / écriture des réglages du mode actif)
    // ---------------------------------------------------------------------
    internal static class PowerCfg
    {
        public static readonly PowerSetting LidAction = new PowerSetting(
            "SUB_BUTTONS", "LIDACTION",
            "4f971e89-eebd-4455-a8de-9e59040e7347",
            "5ca83367-6e45-459f-a27b-476b1d01c936",
            "Action du capot", 0);

        // Un appui sur le bouton d'alimentation endort la machine même quand l'application
        // tient une demande d'éveil : c'est une action de l'utilisateur, que Windows ne
        // laisse volontairement pas bloquer par une application.
        public static readonly PowerSetting PowerButtonAction = new PowerSetting(
            "SUB_BUTTONS", "PBUTTONACTION",
            "4f971e89-eebd-4455-a8de-9e59040e7347",
            "7648efa3-dd9c-4e3e-b566-50f929386280",
            "Bouton d'alimentation", 0);

        public static readonly PowerSetting SleepTimeout = new PowerSetting(
            "SUB_SLEEP", "STANDBYIDLE",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20",
            "29f6c1db-86da-48c5-9fdb-f2b67b1f44da",
            "Mise en veille", 0);

        public static readonly PowerSetting HibernateTimeout = new PowerSetting(
            "SUB_SLEEP", "HIBERNATEIDLE",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20",
            "9d7815a6-7ee4-497e-8888-515a05f02364",
            "Mise en veille prolongée", 0);

        public static readonly PowerSetting UnattendedSleep = new PowerSetting(
            "SUB_SLEEP", "UNATTENDSLEEP",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20",
            "7bc4a2f9-d8fc-4469-b07b-33eb785aaca0",
            "Veille automatique", 0);

        // Par défaut Windows accepte les demandes d'éveil des applications. Ces deux
        // autorisations sont masquées et peuvent être à « non » (0) sur certaines
        // configurations : Windows ignore alors purement et simplement
        // SetThreadExecutionState, et c'est ce qui fait « annuler » la demande d'éveil.
        public static readonly PowerSetting SystemRequiredRequests = new PowerSetting(
            "SUB_SLEEP", "SYSTEMREQUIRED",
            "238c9fa8-0aad-41ed-83f4-97be242c8f20",
            "a4b195f5-8225-47d8-8012-9d41369786e2",
            "Demandes d'éveil système", 1, 1);

        public static readonly PowerSetting DisplayRequiredRequests = new PowerSetting(
            "SUB_VIDEO", "ALLOWDISPLAY",
            "7516b95f-f776-4464-8c53-06167f40cc99",
            "a9ceb8da-cd46-44fb-a98b-02af69de4623",
            "Demandes d'éveil de l'écran", 1, 1);

        // « Exiger un mot de passe au réveil » : présent sur les PC en veille S3, absent
        // sur ceux en veille moderne S0 (où powercfg refuse jusqu'à la lecture). Aucune
        // valeur de repli n'est fournie : si le réglage n'existe pas, il est simplement
        // ignoré au lieu de faire échouer l'activation.
        public static readonly PowerSetting ConsoleLock = new PowerSetting(
            "SUB_NONE", "CONSOLELOCK",
            "fea3413e-7e05-4911-9a71-700331f1c294",
            "0e796bdb-100d-47d6-a2d5-f7d2daa51f51",
            "Mot de passe au réveil", 0);

        public static readonly PowerSetting[] Managed = new PowerSetting[]{
            LidAction, PowerButtonAction, SleepTimeout, HibernateTimeout, UnattendedSleep,
            SystemRequiredRequests, DisplayRequiredRequests, ConsoleLock
        };

        private sealed class CommandResult
        {
            public bool Ok;
            public string Output;
        }

        private static CommandResult Run(string arguments)
        {
            CommandResult result = new CommandResult();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powercfg.exe", arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    result.Ok = p.HasExited && p.ExitCode == 0;
                    result.Output = stdout + "\n" + stderr;
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Output = ex.Message;
            }
            return result;
        }

        public static string GetActiveSchemeGuid()
        {
            CommandResult r = Run("/getactivescheme");
            if (!r.Ok) return null;

            Match m = Regex.Match(r.Output,
                "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        // Renvoie { secteur, batterie } ou null si la valeur est illisible.
        // Astuce : on ne dépend pas de la langue de Windows, on ne lit que les valeurs hexadécimales.
        public static int[] ReadCurrent(string schemeGuid, PowerSetting setting)
        {
            CommandResult r = Run("/query " + schemeGuid + " " + setting.SubgroupAlias + " " + setting.SettingAlias);
            if (r.Ok)
            {
                MatchCollection matches = Regex.Matches(r.Output, "0x([0-9a-fA-F]{1,8})");
                if (matches.Count >= 2)
                {
                    int ac, dc;
                    if (TryParseHex(matches[matches.Count - 2].Groups[1].Value, out ac) &&
                        TryParseHex(matches[matches.Count - 1].Groups[1].Value, out dc))
                    {
                        return new int[] { ac, dc };
                    }
                }
            }

            // Réglage masqué (fréquent pour l'action du capot sur les PC à veille moderne) :
            // powercfg n'affiche rien, on lit alors la valeur enregistrée dans le registre.
            int? regAc = ReadRegistryValue(schemeGuid, setting, "ACSettingIndex");
            int? regDc = ReadRegistryValue(schemeGuid, setting, "DCSettingIndex");

            // Réglage jamais touché : la clé n'existe pas, mais on connaît le défaut Windows.
            // On le prend comme valeur d'origine pour pouvoir le restaurer correctement.
            if (setting.FallbackValue.HasValue)
            {
                if (!regAc.HasValue) regAc = setting.FallbackValue;
                if (!regDc.HasValue) regDc = setting.FallbackValue;
            }

            if (regAc.HasValue && regDc.HasValue) return new int[] { regAc.Value, regDc.Value };

            return null;
        }

        public static bool SetValue(string schemeGuid, PowerSetting setting, int ac, int dc, out string error)
        {
            error = null;

            CommandResult a = Run("/setacvalueindex " + schemeGuid + " " + setting.SubgroupAlias + " " + setting.SettingAlias + " " + ac.ToString(CultureInfo.InvariantCulture));
            CommandResult d = Run("/setdcvalueindex " + schemeGuid + " " + setting.SubgroupAlias + " " + setting.SettingAlias + " " + dc.ToString(CultureInfo.InvariantCulture));
            if (a.Ok && d.Ok)
            {
                // Remettre aussi la valeur en registre pour les configurations S0 (veille moderne)
                // où powercfg n'est pas le seul mécanisme pris en compte.
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath(schemeGuid, setting)))
                    {
                        if (key != null)
                        {
                            key.SetValue("ACSettingIndex", ac, RegistryValueKind.DWord);
                            key.SetValue("DCSettingIndex", dc, RegistryValueKind.DWord);
                        }
                    }
                    Run("/setactive " + schemeGuid);
                }
                catch { }
                return true;
            }

            // Repli : écriture directe (nécessaire pour certains réglages masqués).
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath(schemeGuid, setting)))
                {
                    if (key == null)
                    {
                        error = "accès au registre refusé (lance l'application en administrateur)";
                        return false;
                    }
                    key.SetValue("ACSettingIndex", ac, RegistryValueKind.DWord);
                    key.SetValue("DCSettingIndex", dc, RegistryValueKind.DWord);
                }
                Run("/setactive " + schemeGuid); // prise en compte immédiate
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static PowerSetting FindByAlias(string alias)
        {
            foreach (PowerSetting s in Managed)
                if (string.Equals(s.SettingAlias, alias, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public static string DescribeCurrent()
        {
            string scheme = GetActiveSchemeGuid();
            if (scheme == null) return "Réglages d'alimentation indisponibles.";

            int[] sleep = ReadCurrent(scheme, SleepTimeout);
            int[] hiber = ReadCurrent(scheme, HibernateTimeout);
            int[] lid = ReadCurrent(scheme, LidAction);
            int[] button = ReadCurrent(scheme, PowerButtonAction);

            StringBuilder sb = new StringBuilder();
            sb.Append("Veille : ").Append(sleep == null ? "?" : FormatSeconds(sleep[0]));
            sb.Append("  ·  Hibernation : ").Append(hiber == null ? "?" : FormatSeconds(hiber[0]));
            sb.Append("  ·  Capot : ").Append(lid == null ? "?" : FormatLid(lid[0]));
            sb.Append("  ·  Bouton : ").Append(button == null ? "?" : FormatLid(button[0]));
            return sb.ToString();
        }

        private static string FormatSeconds(int seconds)
        {
            if (seconds <= 0) return "jamais";
            if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + " s";
            if (seconds < 3600) return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " min";
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            return m == 0
                ? h.ToString(CultureInfo.InvariantCulture) + " h"
                : h.ToString(CultureInfo.InvariantCulture) + " h " + m.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string FormatLid(int value)
        {
            switch (value)
            {
                case 0: return "ne rien faire";
                case 1: return "veille";
                case 2: return "veille prolongée";
                case 3: return "arrêt";
                default: return "valeur " + value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static bool TryParseHex(string hex, out int value)
        {
            return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static string RegistryPath(string schemeGuid, PowerSetting setting)
        {
            return "SYSTEM\\\\CurrentControlSet\\\\Control\\\\Power\\\\User\\\\PowerSchemes\\\\" + schemeGuid +
                   "\\\\" + setting.SubgroupGuid + "\\\\" + setting.SettingGuid;
        }

        private static int? ReadRegistryValue(string schemeGuid, PowerSetting setting, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath(schemeGuid, setting)))
                {
                    if (key == null) return null;
                    object value = key.GetValue(valueName);
                    if (value == null) return null;
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    // ---------------------------------------------------------------------
    // État « mode IA » : sauvegarde des réglages puis restauration
    // ---------------------------------------------------------------------
    internal static class ModeState
    {
        private const string FolderName = "Sleepr";
        private const string LegacyFolderName = "ModeIA";

        // L'application s'appelait avant « ModeIA » : si une sauvegarde existe dans
        // l'ancien dossier, elle est reprise, sinon un mode déjà activé ne pourrait
        // plus être restauré.
        static ModeState()
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string legacy = Path.Combine(root, LegacyFolderName, "etat.txt");
                string current = Path.Combine(root, FolderName, "etat.txt");
                if (File.Exists(legacy) && !File.Exists(current))
                {
                    Directory.CreateDirectory(Path.Combine(root, FolderName));
                    File.Move(legacy, current);
                }
            }
            catch { }
        }

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    FolderName);
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Folder, "etat.txt"); }
        }

        public static bool IsActive
        {
            get { return File.Exists(FilePath); }
        }

        public static bool Activate(bool keepDisplayAwake, out string error)
        {
            error = null;

            // Déjà actif : on ne réécrit surtout pas la sauvegarde (elle serait perdue).
            if (IsActive) return true;

            string scheme = PowerCfg.GetActiveSchemeGuid();
            if (scheme == null)
            {
                error = "Impossible d'identifier le mode de gestion de l'alimentation actif.";
                return false;
            }

            List<SavedValue> saved = new List<SavedValue>();
            foreach (PowerSetting setting in PowerCfg.Managed)
            {
                int[] current = PowerCfg.ReadCurrent(scheme, setting);
                if (current == null) continue; // réglage inconnu : on n'y touche pas
                SavedValue v = new SavedValue();
                v.Setting = setting;
                v.Ac = current[0];
                v.Dc = current[1];
                saved.Add(v);
            }

            if (saved.Count == 0)
            {
                error = "Aucun réglage d'alimentation exploitable sur ce PC.";
                return false;
            }

            List<SavedValue> applied = new List<SavedValue>();
            foreach (SavedValue v in saved)
            {
                string setError;
                if (!PowerCfg.SetValue(scheme, v.Setting, v.Setting.ActiveValue, v.Setting.ActiveValue, out setError))
                {
                    // Annulation : on remet ce qui a déjà été changé.
                    foreach (SavedValue done in applied)
                    {
                        string ignored;
                        PowerCfg.SetValue(scheme, done.Setting, done.Ac, done.Dc, out ignored);
                    }
                    error = "Réglage « " + v.Setting.Label + " » impossible : " + setError;
                    return false;
                }
                applied.Add(v);
            }

            // Verrouillage de session : non bloquant. Si l'écriture échoue, le mode
            // s'active quand même (on retombera simplement sur l'écran de connexion).
            int previousLockDelay;
            bool lockDelayWasSet = LockDelay.TryRead(out previousLockDelay);
            string lockError;
            bool lockDelayApplied = LockDelay.Set(LockDelay.Never, out lockError);

            SaveState(scheme, saved, keepDisplayAwake, lockDelayApplied, lockDelayWasSet, previousLockDelay);
            return true;
        }

        public static bool Deactivate(out string error)
        {
            error = null;
            if (!IsActive) return true;

            string scheme = null;
            bool display = true;
            bool lockDelayHandled = false;
            bool lockDelayPresent = false;
            int lockDelayValue = 0;
            List<SavedValue> saved = new List<SavedValue>();

            string[] lines;
            try
            {
                lines = File.ReadAllLines(FilePath);
            }
            catch (Exception ex)
            {
                error = "Sauvegarde illisible : " + ex.Message;
                return false;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (string.Equals(key, "scheme", StringComparison.OrdinalIgnoreCase))
                {
                    scheme = value;
                    continue;
                }
                if (string.Equals(key, "display", StringComparison.OrdinalIgnoreCase))
                {
                    display = value == "1";
                    continue;
                }
                if (string.Equals(key, "LOCKDELAY", StringComparison.OrdinalIgnoreCase))
                {
                    lockDelayHandled = true;
                    lockDelayPresent = !string.Equals(value, "absent", StringComparison.OrdinalIgnoreCase);
                    if (lockDelayPresent)
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out lockDelayValue);
                    continue;
                }

                PowerSetting setting = PowerCfg.FindByAlias(key);
                if (setting == null) continue;

                string[] parts = value.Split(',');
                if (parts.Length != 2) continue;

                int ac, dc;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ac)) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out dc)) continue;

                SavedValue v = new SavedValue();
                v.Setting = setting;
                v.Ac = ac;
                v.Dc = dc;
                saved.Add(v);
            }

            if (scheme == null) scheme = PowerCfg.GetActiveSchemeGuid();
            if (scheme == null)
            {
                error = "Sauvegarde incomplète : impossible de retrouver le mode d'alimentation.";
                return false;
            }

            List<string> failures = new List<string>();
            foreach (SavedValue v in saved)
            {
                string setError;
                if (!PowerCfg.SetValue(scheme, v.Setting, v.Ac, v.Dc, out setError))
                    failures.Add(v.Setting.Label + " (" + setError + ")");
            }

            if (lockDelayHandled)
            {
                string lockError;
                bool ok = lockDelayPresent
                    ? LockDelay.Set(lockDelayValue, out lockError)
                    : LockDelay.Clear(out lockError);
                if (!ok) failures.Add("Mot de passe au réveil (" + lockError + ")");
            }

            if (failures.Count > 0)
            {
                // On garde la sauvegarde pour pouvoir réessayer.
                error = "Restauration partielle : " + string.Join(", ", failures.ToArray());
                return false;
            }

            try { File.Delete(FilePath); }
            catch { /* sans conséquence : le fichier sera écrasé au prochain démarrage */ }

            return true;
        }

        private static void SaveState(string scheme, List<SavedValue> saved, bool keepDisplayAwake,
            bool lockDelaySaved, bool lockDelayPresent, int lockDelayValue)
        {
            Directory.CreateDirectory(Folder);

            List<string> lines = new List<string>();
            lines.Add("# Sauvegarde créée par Sleepr — supprimer ce fichier remet le mode normal");
            lines.Add("scheme=" + scheme);
            lines.Add("display=" + (keepDisplayAwake ? "1" : "0"));
            if (lockDelaySaved)
            {
                lines.Add("LOCKDELAY=" + (lockDelayPresent
                    ? lockDelayValue.ToString(CultureInfo.InvariantCulture)
                    : "absent"));
            }
            foreach (SavedValue v in saved)
            {
                lines.Add(v.Setting.SettingAlias + "=" +
                          v.Ac.ToString(CultureInfo.InvariantCulture) + "," +
                          v.Dc.ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllLines(FilePath, lines.ToArray());
        }
    }

    // ---------------------------------------------------------------------
    // Verrouillage de session à l'extinction de l'écran (veille moderne S0)
    //\
    // Windows n'utilise pas CONSOLELOCK sur ces machines (powercfg refuse même de le
    // lire) : le délai avant de réclamer le mot de passe après extinction de l'écran
    // est une valeur utilisateur, HKCU\\Control Panel\\Desktop\\DelayLockInterval, en
    // secondes, 0xFFFFFFFF signifiant « ne jamais demander ». Aucun droit admin requis.
    //\
    // Réouverture du capot sans mot de passe = même résultat que « Require sign-in :
    // Never » dans les Paramètres. À noter : ouvrir la page « Options de connexion »
    // des Paramètres réécrit cette valeur.
    // ---------------------------------------------------------------------
    internal static class LockDelay
    {
        private const string KeyPath = "Control Panel\\Desktop";
        private const string ValueName = "DelayLockInterval";

        // 0xFFFFFFFF : ne jamais réclamer le mot de passe.
        public const int Never = -1;

        public static bool TryRead(out int value)
        {
            value = 0;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return false;
                    object raw = key.GetValue(ValueName);
                    if (raw == null) return false;
                    value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Set(int value, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null)
                    {
                        error = "clé de registre inaccessible";
                        return false;
                    }
                    key.SetValue(ValueName, value, RegistryValueKind.DWord);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Clear(out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key != null) key.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Démarrage automatique à l'ouverture de session
    //\
    // Clé HKCU\\…\\Run : aucun droit administrateur, et rien d'autre n'est écrit.
    // L'application est lancée avec --tray pour rester en arrière-plan : c'est la
    // seule façon d'être sûr que la demande d'éveil est portée dès la connexion.
    // ---------------------------------------------------------------------
    internal static class AutoStart
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "Sleepr";
        // Nom utilisé avant le renommage de l'application : la valeur est supprimée
        // pour ne pas lancer deux fois l'application (et pas un exécutable disparu).
        private const string LegacyValueName = "ModeIA";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (key == null) return false;
                        string value = key.GetValue(ValueName) as string;
                        return !string.IsNullOrEmpty(value);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool Set(bool enabled, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        error = "Impossible d'écrire la clé de démarrage automatique.";
                        return false;
                    }

                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray", RegistryValueKind.String);
                    else
                        key.DeleteValue(ValueName, false);
                    key.DeleteValue(LegacyValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Démarrage automatique : " + ex.Message;
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Fenêtre principale
    //\
    // C'est une fenêtre superposée (WS_EX_LAYERED) composée par pixel : le
    // contenu est entièrement dessiné dans un bitmap ARGB puis présenté via
    // UpdateLayeredWindow. On obtient une carte translucide aux coins
    // arrondis, du texte net sur n'importe quel fond (pas de couleur « trou »
    // ni de halo), et les zones vraiment transparentes laissent passer les
    // clics. Comme il n'y a plus de barre de titre, la carte se déplace à la
    // souris et le « × » dessiné l'envoie en arrière-plan.
    // ---------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private const string AppTitle = "Sleepr";

        // Palette de la carte
        private static readonly Color CardColor = Color.FromArgb(24, 25, 28);
        private static readonly Color TextColor = Color.FromArgb(244, 245, 247);
        private static readonly Color HintColor = Color.FromArgb(154, 159, 167);
        private static readonly Color ActiveTextColor = Color.FromArgb(118, 228, 154);
        private static readonly Color WarnColor = Color.FromArgb(246, 190, 92);
        private static readonly Color TrackOff = Color.FromArgb(96, 100, 108);
        private static readonly Color TrackOn = Color.FromArgb(34, 160, 74);

        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ToolStripMenuItem _trayToggle = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _trayStartup = new ToolStripMenuItem();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _animation = new System.Windows.Forms.Timer();

        private bool _active;
        private bool _lastKnownActive;
        private readonly bool _startHidden;
        private bool _exitRequested;
        private bool _balloonShown;

        private float _scale = 1f;
        private Size _surface = new Size(220, 132);   // taille logique de la carte (avant mise à l'échelle DPI)
        private float _position;      // 0 = à gauche (mode normal), 1 = à droite (mode IA)
        private bool _hoverSwitch;
        private bool _pressedSwitch;
        private bool _hoverClose;
        private bool _pressedClose;
        private bool _dragging;
        private Point _dragOffset;

        private Rectangle _switchRect;
        private Rectangle _closeRect;

        private string _modeText = "Mode normal";
        private string _hintText = "Comportement normal";
        private Color _modeColor = TextColor;
        private Color _hintColor = HintColor;

        private Icon _iconOn;
        private Icon _iconOff;

        public MainForm() : this(false)
        {
        }

        // startHidden : lancée au démarrage de Windows, la carte reste en arrière-plan.
        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            Text = AppTitle;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            // Mise en page calculée en pixels : le facteur d'échelle est appliqué
            // à la création de la fenêtre (voir OnHandleCreated).
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;   // jamais affiché : le rendu passe par UpdateLayeredWindow
            KeyPreview = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            ClientSize = new Size(220, 132);

            _iconOn = MakeIcon(Color.FromArgb(30, 142, 62));
            _iconOff = MakeIcon(Color.FromArgb(110, 110, 110));
            Icon = _iconOn;

            _animation.Interval = 15;
            _animation.Tick += delegate { StepAnimation(); };

            SetupTray();

            _timer.Interval = 3000;
            _timer.Tick += delegate
            {
                // Le mode peut être basculé ailleurs (ligne de commande, autre session) :
                // la demande d'éveil doit suivre, sinon l'application afficherait « actif »
                // sans rien empêcher.
                bool active = ModeState.IsActive;
                if (active != _active)
                {
                    _active = active;
                    if (_active) ApplyExecutionState();
                    else ReleaseExecutionState();
                }

                if (_active != _lastKnownActive) RefreshUi();
                else UpdateHint();
            };
            _timer.Start();

            _active = ModeState.IsActive;
            if (_active)
            {
                ApplyExecutionState();
                _position = 1f;
            }
            RefreshUi();
        }

        // ----- Apparence ---------------------------------------------------

        private static Icon MakeIcon(Color color)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush brush = new SolidBrush(color))
                    {
                        g.FillEllipse(brush, 1, 1, 30, 30);
                    }
                    using (Font font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush white = new SolidBrush(Color.White))
                    using (StringFormat format = new StringFormat())
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        g.DrawString("IA", font, white, new RectangleF(0, 0, 32, 32), format);
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        // La fenêtre doit être superposée dès sa création pour que
        // UpdateLayeredWindow puisse composer le bitmap avec son alpha.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            float dpi;
            using (Graphics g = CreateGraphics()) dpi = g.DpiX;
            _scale = dpi / 96f;
            if (_scale < 0.5f) _scale = 0.5f;
            if (_scale > 4f) _scale = 4f;

            // La taille de la surface est fixée une fois pour toutes, et la fenêtre est
            // bloquée sur cette taille (minimum = maximum) : sinon UpdateLayeredWindow et
            // WinForms se renvoient la balle et la carte se fait rogner à chaque rendu.
            _surface = new Size(S(220), S(132));
            MinimumSize = _surface;
            MaximumSize = _surface;
            ClientSize = _surface;
            UpdateLayout();
            Render();
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private float SF(float value)
        {
            return value * _scale;
        }

        private Font MakeFont(float points, FontStyle style)
        {
            return new Font("Segoe UI", points * _scale, style, GraphicsUnit.Point);
        }

        private void UpdateLayout()
        {
            int width = _surface.Width;
            int switchWidth = S(90);
            int switchHeight = S(46);
            _switchRect = new Rectangle((width - switchWidth) / 2, S(42), switchWidth, switchHeight);

            int close = S(20);
            _closeRect = new Rectangle(width - S(8) - close, S(10), close, close);
        }

        // ----- Rendu -------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            // Le contenu est présenté par UpdateLayeredWindow : rien à peindre ici.
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        private void Render()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;

            int width = _surface.Width;
            int height = _surface.Height;
            if (width <= 0 || height <= 0) return;

            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);

                    DrawCard(g, width, height);
                    DrawSwitch(g);
                    DrawClose(g);
                }

                Present(bitmap);
            }
        }

        private void DrawCard(Graphics g, int width, int height)
        {
            Rectangle card = new Rectangle(S(2), S(2), width - S(4) - 1, height - S(4) - 1);

            using (GraphicsPath path = RoundedRect(card, S(18)))
            {
                int alpha = (_hoverSwitch || _hoverClose || _dragging) ? 246 : 236;
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, CardColor)))
                    g.FillPath(brush, path);

                using (Pen pen = new Pen(Color.FromArgb(34, 255, 255, 255), Math.Max(1f, SF(1f))))
                    g.DrawPath(pen, path);
            }

            using (StringFormat format = new StringFormat())
            using (Font font = MakeFont(12f, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(_modeColor))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(_modeText, font, brush, new RectangleF(0, S(12), width, S(28)), format);
            }

            using (StringFormat format = new StringFormat())
            using (Font font = MakeFont(8f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(_hintColor))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(_hintText, font, brush, new RectangleF(S(10), S(94), width - S(20), S(22)), format);
            }
        }

        private void DrawSwitch(Graphics g)
        {
            Rectangle track = _switchRect;
            track.Inflate(-S(2), -S(2));

            Color trackColor = Blend(TrackOff, TrackOn, _position);
            if (_pressedSwitch) trackColor = Blend(trackColor, Color.Black, 0.18f);
            else if (_hoverSwitch) trackColor = Blend(trackColor, Color.White, 0.12f);

            using (GraphicsPath path = RoundedRect(track, track.Height / 2))
            using (SolidBrush brush = new SolidBrush(trackColor))
                g.FillPath(brush, path);

            int knobSize = track.Height - S(8);
            float travel = track.Width - knobSize - S(8);
            float knobX = track.Left + S(4) + travel * _position;
            float knobY = track.Top + S(4);

            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                g.FillEllipse(shadow, knobX, knobY + Math.Max(1f, SF(1f)), knobSize, knobSize);

            using (SolidBrush knob = new SolidBrush(Color.FromArgb(252, 253, 255)))
                g.FillEllipse(knob, knobX, knobY, knobSize, knobSize);
        }

        private void DrawClose(Graphics g)
        {
            if (_hoverClose || _pressedClose)
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(_pressedClose ? 52 : 36, 255, 255, 255)))
                    g.FillEllipse(brush, _closeRect);
            }

            int alpha = _pressedClose ? 240 : (_hoverClose ? 200 : 120);
            int inset = S(6);

            using (Pen pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), Math.Max(1f, SF(1.6f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen,
                    _closeRect.Left + inset, _closeRect.Top + inset,
                    _closeRect.Right - inset, _closeRect.Bottom - inset);
                g.DrawLine(pen,
                    _closeRect.Right - inset, _closeRect.Top + inset,
                    _closeRect.Left + inset, _closeRect.Bottom - inset);
            }
        }

        private void Present(Bitmap bitmap)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);

                Native.SIZE size = new Native.SIZE(bitmap.Width, bitmap.Height);
                Native.POINT source = new Native.POINT(0, 0);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = 0;              // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;          // AC_SRC_ALPHA

                Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref source, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, oldBitmap);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ----- Souris : glissement de la carte, interrupteur, « × » --------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                if (_tray.ContextMenuStrip != null) _tray.ContextMenuStrip.Show(Cursor.Position);
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (_closeRect.Contains(e.Location))
            {
                _pressedClose = true;
                Render();
                return;
            }

            if (_switchRect.Contains(e.Location))
            {
                _pressedSwitch = true;
                Activate();
                Render();
                return;
            }

            // Reste de la carte : on déplace la fenêtre à la souris.
            _dragging = true;
            _dragOffset = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging)
            {
                Location = new Point(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y);
                return;
            }

            bool overSwitch = _switchRect.Contains(e.Location);
            bool overClose = _closeRect.Contains(e.Location);
            if (overSwitch == _hoverSwitch && overClose == _hoverClose) return;

            _hoverSwitch = overSwitch;
            _hoverClose = overClose;
            Cursor = (overSwitch || overClose) ? Cursors.Hand : Cursors.Default;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button != MouseButtons.Left) return;

            if (_dragging)
            {
                _dragging = false;
                Render();
                return;
            }

            bool wasSwitch = _pressedSwitch;
            bool wasClose = _pressedClose;
            _pressedSwitch = false;
            _pressedClose = false;

            if (wasSwitch && _switchRect.Contains(e.Location)) ToggleMode();
            else if (wasClose && _closeRect.Contains(e.Location)) Quit();

            Render();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (!_hoverSwitch && !_hoverClose) return;
            _hoverSwitch = false;
            _hoverClose = false;
            Cursor = Cursors.Default;
            Render();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                ToggleMode();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        // ----- Zone de notification ----------------------------------------

        private void SetupTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Ouvrir Sleepr", null, delegate { RestoreWindow(); });
            _trayToggle.Text = "Activer Sleepr";
            _trayToggle.Click += delegate { ToggleMode(); };
            menu.Items.Add(_trayToggle);
            menu.Items.Add(new ToolStripSeparator());
            _trayStartup.Text = "Démarrer avec Windows";
            _trayStartup.CheckOnClick = true;
            _trayStartup.Checked = AutoStart.IsEnabled;
            _trayStartup.Click += delegate { SetStartup(_trayStartup.Checked); };
            menu.Items.Add(_trayStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Comment ça marche ?", null, delegate { ShowHelp(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quitter", null, delegate { Quit(); });

            _tray.Text = AppTitle;
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { RestoreWindow(); }
        }

        private void SetStartup(bool enabled)
        {
            string error;
            if (!AutoStart.Set(enabled, out error))
            {
                _trayStartup.Checked = !enabled;
                MessageBox.Show(this, error, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            Render();
        }

        private void Quit()
        {
            if (_active)
            {
                string error;
                if (!ModeState.Deactivate(out error) && error != null)
                {
                    MessageBox.Show(this, error, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                ReleaseExecutionState();
                _active = false;
            }
            _exitRequested = true;
            Close();
        }

        // ----- Actions -----------------------------------------------------

        private void ToggleMode()
        {
            string error;

            if (!_active)
            {
                // L'écran reste éveillé : sur les PC en veille moderne (S0), c'est
                // l'extinction de l'écran qui déclenche la mise en veille.
                if (!ModeState.Activate(true, out error))
                {
                    MessageBox.Show(this, error, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _active = true;
                ApplyExecutionState();
            }
            else
            {
                ReleaseExecutionState();
                if (!ModeState.Deactivate(out error) && error != null)
                {

[read_files: showing lines 1-1280 of 1710. The window was shortened to stay under 50 000 characters. Use code_search to locate the part you need and read a window around it, or call read_files again with offset=1281 to continue.]