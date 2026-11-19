// Sleepr — petit utilitaire Windows pour garder le PC allumé (écran éteint, capot fermé)
// tout en pouvant revenir d'un clic au comportement normal.
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
        // Valeur écrite en mode Sleepr (0 pour les délais, 1 pour les autorisations d'éveil).
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

        public static readonly PowerSetting[] Managed = new PowerSetting[]
        {
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
            return "SYSTEM\\CurrentControlSet\\Control\\Power\\User\\PowerSchemes\\" + schemeGuid +
                   "\\" + setting.SubgroupGuid + "\\" + setting.SettingGuid;
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
    // État de protection : sauvegarde des réglages puis restauration
    // ---------------------------------------------------------------------
    internal static class ModeState
    {
        private const string FolderName = "Sleepr";
        private const string LegacyFolderName = "ModeIA";

        // Anciennement appelé « ModeIA » : si une sauvegarde existe dans l'ancien dossier,
        // elle est reprise automatiquement au premier lancement de Sleepr.
        // Sinon, un mode déjà activé ne pourrait plus être restauré.
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
    //
    // Windows n'utilise pas CONSOLELOCK sur ces machines (powercfg refuse même de le
    // lire) : le délai avant de réclamer le mot de passe après extinction de l'écran
    // est une valeur utilisateur, HKCU\Control Panel\Desktop\DelayLockInterval, en
    // secondes, 0xFFFFFFFF signifiant « ne jamais demander ». Aucun droit admin requis.
    //
    // Réouverture du capot sans mot de passe = même résultat que « Require sign-in :
    // Never » dans les Paramètres. À noter : ouvrir la page « Options de connexion »
    // des Paramètres réécrit cette valeur.
    // ---------------------------------------------------------------------
    internal static class LockDelay
    {
        private const string KeyPath = @"Control Panel\Desktop";
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
    //
    // Clé HKCU\…\Run : aucun droit administrateur, et rien d'autre n'est écrit.
    // L'application est lancée avec --tray pour rester en arrière-plan : c'est la
    // seule façon d'être sûr que la demande d'éveil est portée dès la connexion.
    // ---------------------------------------------------------------------
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Sleepr";
        // Ancienne clé de registre (ancien nom de l'application) : supprimée
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
    // Journal des erreurs : une exception non gérée ne doit pas faire
    // disparaître l'application en silence (et avec elle la demande d'éveil).
    // ---------------------------------------------------------------------
    internal static class CrashLog
    {
        private static string FilePath
        {
            get { return Path.Combine(ModeState.Folder, "crash.txt"); }
        }

        public static void Write(string context, Exception error)
        {
            try
            {
                Directory.CreateDirectory(ModeState.Folder);
                StringBuilder text = new StringBuilder();
                text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + context);
                if (error != null) text.AppendLine(error.ToString());
                text.AppendLine();
                File.AppendAllText(FilePath, text.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------
    // Position de la carte, mémorisée d'une session à l'autre
    // ---------------------------------------------------------------------
    internal static class PositionStore
    {
        private static string FilePath
        {
            get { return Path.Combine(ModeState.Folder, "position.txt"); }
        }

        public static bool TryRead(out Point location)
        {
            location = Point.Empty;
            try
            {
                if (!File.Exists(FilePath)) return false;
                string[] parts = File.ReadAllText(FilePath).Trim().Split(',');
                if (parts.Length != 2) return false;

                int x;
                int y;
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) return false;
                if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) return false;

                location = new Point(x, y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Save(Point location)
        {
            try
            {
                Directory.CreateDirectory(ModeState.Folder);
                File.WriteAllText(FilePath,
                    location.X.ToString(CultureInfo.InvariantCulture) + "," +
                    location.Y.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        // La position enregistrée n'est réutilisée que si la carte tombe encore sur un
        // écran : après un débranchement de moniteur, elle pourrait être hors champ.
        public static bool IsOnScreen(Point location, Size size)
        {
            Rectangle bounds = new Rectangle(location, size);
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            }
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Fenêtre principale
    //
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
        private static readonly Color CardTop = Color.FromArgb(35, 37, 43);
        private static readonly Color CardBottom = Color.FromArgb(19, 20, 23);
        private static readonly Color AccentColor = Color.FromArgb(72, 214, 126);

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
        private float _position;      // 0 = à gauche (mode normal), 1 = mode actif
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

        private Icon _iconApp;
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
            // Pas de bouton dans la barre des tâches : la carte vit dans la zone de
            // notification et se rouvre par son icône.
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

            _iconApp = LoadAppIcon();
            _iconOn = MakeStatusIcon(_iconApp, Color.FromArgb(46, 204, 113), true);
            _iconOff = MakeStatusIcon(_iconApp, Color.FromArgb(140, 146, 156), false);
            Icon = _iconApp != null ? _iconApp : _iconOn;

            _animation.Interval = 15;
            _animation.Tick += delegate { StepAnimation(); };

            SetupTray();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer.Interval = 3000;
            _timer.Tick += delegate
            {
                try
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

                    // Ré-affirmation régulière : Windows peut oublier la demande d'éveil
                    // après un réveil ou un changement de session, et une demande perdue
                    // suffit à laisser le PC s'endormir au mauvais moment.
                    if (_active) ApplyExecutionState();

                    if (_active != _lastKnownActive) RefreshUi();
                    else UpdateHint();
                }
                catch
                {
                    // Une exception ici ne doit jamais arrêter le minuteur : ce serait
                    // perdre la demande d'éveil sans prévenir.
                }
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

        // Icône de l'application : celle compilée dans l'exécutable (-win32icon dans
        // build.cmd, donc embarquée une seule fois), sinon lue à côté de l'exécutable.
        private static Icon LoadAppIcon()
        {
            try
            {
                Icon fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (fromExe != null)
                    return fromExe;
            }
            catch { }

            try
            {
                string file = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Sleepr.ico");
                if (File.Exists(file))
                {
                    using (Icon loaded = new Icon(file, new Size(32, 32)))
                        return (Icon)loaded.Clone();
                }
            }
            catch { }

            return null;
        }

        // Icône de la zone de notification : l'icône de l'application, plus une pastille
        // verte quand Sleepr protège la machine. L'ancienne icône ronde sert de repli si
        // aucune icône n'est disponible.
        private static Icon MakeStatusIcon(Icon source, Color badge, bool showBadge)
        {
            using (Bitmap bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);

                    if (source != null)
                    {
                        g.DrawIcon(source, new Rectangle(0, 0, 32, 32));
                    }
                    else
                    {
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(58, 92, 214)))
                            g.FillEllipse(brush, 1, 1, 30, 30);
                        using (Font font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (SolidBrush white = new SolidBrush(Color.White))
                        using (StringFormat format = new StringFormat())
                        {
                            format.Alignment = StringAlignment.Center;
                            format.LineAlignment = StringAlignment.Center;
                            g.DrawString("S", font, white, new RectangleF(0, 0, 32, 32), format);
                        }
                    }

                    if (showBadge)
                    {
                        using (SolidBrush halo = new SolidBrush(Color.FromArgb(240, 24, 25, 28)))
                            g.FillEllipse(halo, 15, 15, 17, 17);
                        using (SolidBrush dot = new SolidBrush(badge))
                            g.FillEllipse(dot, 18, 18, 11, 11);
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

            // Cible de clic un peu plus large que le dessin : 24 px, comme un vrai bouton.
            int close = S(24);
            _closeRect = new Rectangle(width - S(8) - close, S(8), close, close);
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
                // Dégradé vertical discret : la carte se détache mieux d'un fond clair
                // qu'un aplat unique, sans jamais devenir opaque.
                int alpha = (_hoverSwitch || _hoverClose || _dragging) ? 250 : 240;
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(card.X, card.Y, card.Width, card.Height + 1),
                    Color.FromArgb(alpha, CardTop),
                    Color.FromArgb(alpha, CardBottom),
                    LinearGradientMode.Vertical))
                    g.FillPath(brush, path);

                // Liseré : vert quand Sleepr protège la machine, blanc très discret sinon.
                Color border = _active
                    ? Color.FromArgb(78, AccentColor)
                    : Color.FromArgb(26, 255, 255, 255);
                using (Pen pen = new Pen(border, Math.Max(1f, SF(1f))))
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

            // Au survol du « × », l'indice explique ce que fait le bouton : masquer sans
            // arrêter la protection. C'est le doute qui pousse à quitter pour de bon.
            string hint = _hoverClose
                ? (_active ? "Masquer · reste actif" : "Masquer · tourne en fond")
                : _hintText;
            Color hintColor = _hoverClose ? Color.FromArgb(216, 220, 226) : _hintColor;

            using (StringFormat format = new StringFormat())
            using (Font font = MakeFont(8f, FontStyle.Regular))
            using (SolidBrush brush = new SolidBrush(hintColor))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(hint, font, brush, new RectangleF(S(10), S(94), width - S(20), S(22)), format);
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

            // Reflet supérieur : donne du relief au rail sans ajouter de bordure.
            Rectangle sheen = new Rectangle(track.X + S(6), track.Y + S(3), track.Width - S(12), S(9));
            if (sheen.Width > 0 && sheen.Height > 0)
            {
                using (GraphicsPath sheenPath = RoundedRect(sheen, sheen.Height / 2))
                using (SolidBrush sheenBrush = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                    g.FillPath(sheenBrush, sheenPath);
            }

            int knobSize = track.Height - S(8);
            float travel = track.Width - knobSize - S(8);
            float knobX = track.Left + S(4) + travel * _position;
            float knobY = track.Top + S(4);

            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                g.FillEllipse(shadow, knobX, knobY + Math.Max(1f, SF(1f)), knobSize, knobSize);

            using (SolidBrush knob = new SolidBrush(Color.FromArgb(252, 253, 255)))
                g.FillEllipse(knob, knobX, knobY, knobSize, knobSize);

            using (Pen ring = new Pen(Color.FromArgb(16, 0, 0, 0), Math.Max(1f, SF(1f))))
                g.DrawEllipse(ring, knobX, knobY, knobSize, knobSize);
        }

        private void DrawClose(Graphics g)
        {
            if (_hoverClose || _pressedClose)
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(_pressedClose ? 52 : 36, 255, 255, 255)))
                    g.FillEllipse(brush, _closeRect);
            }

            int alpha = _pressedClose ? 240 : (_hoverClose ? 200 : 120);
            int inset = S(7);

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

            // Sur le corps de la carte, le curseur dit qu'elle se déplace (il n'y a ni
            // barre de titre ni bordure pour l'indiquer autrement).
            Cursor = (overSwitch || overClose) ? Cursors.Hand : Cursors.SizeAll;

            if (overSwitch == _hoverSwitch && overClose == _hoverClose) return;

            _hoverSwitch = overSwitch;
            _hoverClose = overClose;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button != MouseButtons.Left) return;

            if (_dragging)
            {
                _dragging = false;
                PositionStore.Save(Location);
                Render();
                return;
            }

            bool wasSwitch = _pressedSwitch;
            bool wasClose = _pressedClose;
            _pressedSwitch = false;
            _pressedClose = false;

            if (wasSwitch && _switchRect.Contains(e.Location)) ToggleMode();
            else if (wasClose && _closeRect.Contains(e.Location)) MinimizeToBackground();
            // Fermer la carte ne coupe jamais la protection : l'application continue de
            // tourner en arrière-plan (icône près de l'horloge) et garde le PC éveillé.

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

            if (e.KeyCode == Keys.Escape)
            {
                MinimizeToBackground();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        // Double-clic sur la carte : bascule immédiate, sans viser l'interrupteur.
        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);

            Point point = PointToClient(Cursor.Position);
            if (_switchRect.Contains(point) || _closeRect.Contains(point)) return;
            ToggleMode();
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
            _tray.DoubleClick += delegate { RestoreWindow(); };
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

            // Si l'écran a changé depuis la dernière fois, la carte revient au centre.
            if (!PositionStore.IsOnScreen(Location, _surface)) CenterOnScreen();

            Activate();
            Render();
        }

        private void CenterOnScreen()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                area.Left + (area.Width - _surface.Width) / 2,
                area.Top + (area.Height - _surface.Height) / 2);
        }

        private void Quit()
        {
            if (_active)
            {
                // Quitter pour de bon rend la veille possible : une confirmation évite de
                // perdre un calcul long sur un clic de trop.
                DialogResult answer = MessageBox.Show(this,
                    "Sleepr est actif : quitter arrête la protection et rend de nouveau possible "
                    + "la mise en veille du PC.\r\n\r\nQuitter quand même ?",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;

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
                    MessageBox.Show(this, error, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _active = ModeState.IsActive;
            }

            RefreshUi();
        }

        private void RefreshUi()
        {
            _lastKnownActive = _active;

            _modeText = _active ? "Sleepr actif" : "Mode normal";
            _modeColor = _active ? ActiveTextColor : TextColor;

            // Le curseur glisse vers sa nouvelle position.
            float target = _active ? 1f : 0f;
            if (Math.Abs(_position - target) < 0.02f)
            {
                _position = target;
                _animation.Stop();
            }
            else
            {
                _animation.Start();
            }

            // Le titre de la fenêtre est ce qu'affiche la barre des tâches (survol du bouton,
            // vignette du « résumé » des applications) : il porte donc l'état courant.
            string title = _active ? "Sleepr — actif" : "Sleepr — normal";
            if (Text != title) Text = title;
            _tray.Icon = _active ? _iconOn : _iconOff;
            _tray.Text = title;
            _trayToggle.Text = _active ? "Désactiver Sleepr" : "Activer Sleepr";

            UpdateHint();
        }

        private void StepAnimation()
        {
            float target = _active ? 1f : 0f;
            float delta = target - _position;

            if (Math.Abs(delta) <= 0.04f)
            {
                _position = target;
                _animation.Stop();
            }
            else
            {
                _position += delta * 0.35f;   // glissement qui ralentit en fin de course
            }

            Render();
        }

        private void UpdateHint()
        {
            string text;
            Color color;

            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online;

            if (_active && onBattery)
            {
                float level = SystemInformation.PowerStatus.BatteryLifePercent;
                string percent = (level >= 0f && level <= 1f)
                    ? " (" + (int)Math.Round(level * 100f) + " %)"
                    : "";
                text = "Batterie" + percent + " · branche le chargeur";
                color = WarnColor;
            }
            else if (_active)
            {
                text = "Écran éteint · capot ignoré";
                color = Color.FromArgb(198, 204, 212);
            }
            else
            {
                text = "Comportement normal";
                color = HintColor;
            }

            // Le minuteur passe toutes les 3 s : on ne redessine que si l'indice change
            // vraiment (et seulement si la carte est visible).
            if (text == _hintText && color == _hintColor) return;
            _hintText = text;
            _hintColor = color;
            if (Visible) Render();
        }

        private void ApplyExecutionState()
        {
            Native.SetThreadExecutionState(
                ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
        }

        private void ReleaseExecutionState()
        {
            Native.SetThreadExecutionState(ExecutionState.Continuous);
        }

        // Après une veille ou un changement de session, Windows peut avoir perdu la
        // demande d'éveil : on la repose tout de suite plutôt que d'attendre.
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            if (_active) ApplyExecutionState();
            UpdateHint();
        }

        private void ShowHelp()
        {
            string message =
                "Sleepr modifie le mode d'alimentation actif de Windows :\r\n" +
                "  • fermeture du capot : ne rien faire\r\n" +
                "  • bouton d'alimentation : ne rien faire\r\n" +
                "  • mise en veille : jamais\r\n" +
                "  • mise en veille prolongée : jamais\r\n" +
                "  • mot de passe au réveil : jamais (le capot fermé ne renvoie plus à l'écran de connexion)\r\n\r\n" +
                "L'application demande aussi à Windows de rester éveillé, écran compris : sur un PC récent (veille moderne S0), c'est l'extinction " +
                "de l'écran qui déclenche la mise en veille, donc c'est indispensable pour que le mode tienne capot fermé. Elle autorise aussi " +
                "explicitement ces demandes dans les réglages cachés : sans elles, Windows les ignore purement et simplement.\r\n\r\n" +
                "Cette demande d'éveil n'existe que tant que l'application tourne : « Sleepr.exe --on » lance donc l'interface si besoin, et " +
                "le menu propose « Démarrer avec Windows » pour qu'elle soit là dès la connexion.\r\n\r\n" +
                "La fenêtre est une petite carte flottante : fais-la glisser pour la déplacer, double-clique dessus pour basculer le mode, " +
                "et le × la masque simplement. Masquée, elle continue de tourner dans la zone de notification (icône près de l'horloge) et " +
                "garde le PC éveillé ; Échap fait la même chose. Clic droit sur la carte ou sur l'icône pour le menu, dont « Quitter », qui " +
                "arrête vraiment Sleepr et remet les réglages d'origine (une confirmation est demandée si le mode est actif).\r\n\r\n" +
                "Vérification après une session : dans une invite de commandes, lance\r\n" +
                "      powercfg /sleepstudy\r\n" +
                "et regarde si une session de veille apparaît sur la période.";

            MessageBox.Show(this, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ----- Arrière-plan et fermeture -----------------------------------

        // Masquer la carte ne quitte jamais : l'application reste en vie (icône près de
        // l'horloge) et continue de porter la demande d'éveil. Seul le menu « Quitter »
        // arrête vraiment Sleepr — c'est précisément ce qui protège un calcul long.
        private void MinimizeToBackground()
        {
            MinimizeToBackground(true);
        }

        private void MinimizeToBackground(bool notify)
        {
            Hide();

            if (!notify || _balloonShown) return;
            _balloonShown = true;
            _tray.BalloonTipTitle = _active ? "Sleepr reste actif" : "Sleepr tourne en arrière-plan";
            _tray.BalloonTipText = _active
                ? "Le PC reste éveillé. Rouvre la carte par l'icône près de l'horloge ; « Quitter » arrête vraiment Sleepr."
                : "Rouvre la carte par l'icône près de l'horloge ; « Quitter » arrête vraiment Sleepr.";
            _tray.ShowBalloonTip(5000);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Position mémorisée d'une session à l'autre, si elle est encore visible.
            Point saved;
            if (PositionStore.TryRead(out saved) && PositionStore.IsOnScreen(saved, _surface))
                Location = saved;

            if (_startHidden) MinimizeToBackground(false);
            else Render();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Fermer (×, Alt+F4) ne quitte jamais : la carte disparaît du bureau et de la
            // barre des tâches, mais l'application continue de tourner et reste accessible
            // par l'icône de la zone de notification. La demande d'éveil continue aussi.
            //
            // TaskManagerClosing couvre les fermetures venues de l'extérieur (gestionnaire
            // de tâches, un programme qui poste WM_CLOSE) : elles non plus ne doivent pas
            // couper la protection par surprise. Un vrai arrêt de Windows, lui, passe.
            if (!_exitRequested &&
                (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.TaskManagerClosing))
            {
                e.Cancel = true;
                // Masquer la fenêtre depuis l'événement de fermeture arrête la boucle de
                // messages (donc l'application, donc la demande d'éveil) : on diffère le
                // masquage d'un tour, une fois la fermeture annulée.
                BeginInvoke(new MethodInvoker(delegate { MinimizeToBackground(true); }));
                return;
            }

            if (!_active) ReleaseExecutionState();
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!IsHandleCreated) return;
            UpdateLayout();
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _timer.Stop();
                _timer.Dispose();
                _animation.Stop();
                _animation.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                if (_iconApp != null) _iconApp.Dispose();
                if (_iconOn != null) _iconOn.Dispose();
                if (_iconOff != null) _iconOff.Dispose();
            }
            base.Dispose(disposing);
        }

        // ----- Petits utilitaires de dessin --------------------------------

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = Math.Max(1, radius) * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d - 1, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d - 1, bounds.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }
    }

    [Flags]
    internal enum ExecutionState : uint
    {
        None = 0,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    // Fonctions Win32 utilisées par l'application.
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int processId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;

            public SIZE(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--gui";

            if (mode == "--on" || mode == "--off" || mode == "--status" || mode == "--startup")
                return RunCommandLine(mode, args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Une erreur inattendue est écrite dans %LOCALAPPDATA%\Sleepr\crash.txt et
            // n'arrête pas l'application : sans elle, plus aucune demande d'éveil.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                CrashLog.Write("Exception d'interface", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                CrashLog.Write("Exception non gérée", e.ExceptionObject as Exception);
            };

            bool created;
            using (Mutex mutex = new Mutex(true, "Sleepr_single_instance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Sleepr est déjà lancé : cherche son icône près de l'horloge.",
                        "Sleepr", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.Run(new MainForm(mode == "--tray"));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static int RunCommandLine(string mode, string[] args)
        {
            Native.AttachConsole(-1); // pour afficher la sortie dans la console appelante

            string error;

            if (mode == "--startup")
            {
                if (args.Length > 1)
                {
                    string value = args[1].ToLowerInvariant();
                    bool enabled;
                    if (value == "on" || value == "1" || value == "oui") enabled = true;
                    else if (value == "off" || value == "0" || value == "non") enabled = false;
                    else
                    {
                        Console.WriteLine("Usage : Sleepr.exe --startup on|off");
                        return 1;
                    }

                    if (!AutoStart.Set(enabled, out error))
                    {
                        Console.WriteLine("ERREUR : " + error);
                        return 1;
                    }
                }

                Console.WriteLine(AutoStart.IsEnabled ? "demarrage=actif" : "demarrage=inactif");
                return 0;
            }

            if (mode == "--on")
            {
                bool ok = ModeState.Activate(true, out error);
                if (!ok)
                {
                    Console.WriteLine("ERREUR : " + error);
                    return 1;
                }
                Console.WriteLine("Sleepr activé (veille/hibernation désactivées, capot et bouton ignorés).");
                Console.WriteLine(PowerCfg.DescribeCurrent());

                // Les réglages powercfg ne suffisent pas : la demande d'éveil n'existe que
                // tant que l'interface tourne. On la lance donc ici, sinon la ligne de
                // commande laisserait le PC s'endormir capot fermé.
                if (EnsureGuiRunning())
                    Console.WriteLine("Interface lancée : elle maintient la demande d'éveil.");
                else
                    Console.WriteLine("ATTENTION : impossible de lancer l'interface ; le PC peut s'endormir.");
                return 0;
            }

            if (mode == "--off")
            {
                bool ok = ModeState.Deactivate(out error);
                if (!ok)
                {
                    Console.WriteLine("ERREUR : " + error);
                    return 1;
                }
                Console.WriteLine("Mode normal restauré.");
                Console.WriteLine(PowerCfg.DescribeCurrent());
                return 0;
            }

            Console.WriteLine(ModeState.IsActive ? "mode=actif" : "mode=normal");
            Console.WriteLine(PowerCfg.DescribeCurrent());
            Console.WriteLine(IsGuiRunning()
                ? (ModeState.IsActive
                    ? "interface=lancee (demande d'éveil tenue)"
                    : "interface=lancee (mode normal)")
                : "interface=arretee (aucune demande d'éveil)");
            return 0;
        }

        // L'interface est le seul porteur de la demande d'éveil : sans elle, le mode
        // n'empêche rien. On la détecte par son processus (le processus courant, qui
        // est aussi Sleepr.exe en ligne de commande, ne compte pas).
        private static bool IsGuiRunning()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("Sleepr"))
            {
                try
                {
                    if (p.Id != self) return true;
                }
                finally
                {
                    p.Dispose();
                }
            }
            return false;
        }

        private static bool EnsureGuiRunning()
        {
            if (IsGuiRunning()) return true;

            try
            {
                // ShellExecute (et non CreateProcess) : le processus lancé n'hérite pas des
                // tubes de la console appelante, sinon un « --on » dans un script resterait
                // bloqué tant que l'interface tourne.
                Process.Start(Application.ExecutablePath);
                Thread.Sleep(1500);   // le temps que la fenêtre pose la demande d'éveil
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
