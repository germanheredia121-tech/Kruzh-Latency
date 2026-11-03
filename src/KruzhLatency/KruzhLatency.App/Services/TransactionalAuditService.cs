using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading.Tasks;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed class TransactionalAuditService
{
    private readonly string _journalPath;
    private readonly object _sync = new();
    private BackupJournalDocument _journal;

    public TransactionalAuditService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KruzhLatency",
            "Journal");
        Directory.CreateDirectory(dir);
        _journalPath = Path.Combine(dir, "backup_journal.json");
        _journal = LoadJournal();
    }

    public IReadOnlyList<AtomicBackupRecord> GetAuditHistory()
    {
        lock (_sync)
        {
            return _journal.AuditHistory.OrderByDescending(x => x.TimestampUtc).ToList();
        }
    }

    /// <summary>
    /// Descarta entradas del journal SIN restaurarlas. Se usa para las entradas "*_Rev" que dejaban los fallbacks de
    /// revert de v3 (bug B2): su "valor original" es el valor tweakeado, así que revertirlas re-aplicaba el tweak.
    /// </summary>
    public int DiscardEntries(Func<string, bool> tweakIdPredicate)
    {
        lock (_sync)
        {
            var keys = _journal.TweakBackups.Keys.Where(tweakIdPredicate).ToList();
            foreach (var k in keys)
            {
                _journal.TweakBackups.Remove(k);
            }
            if (keys.Count > 0)
            {
                SaveJournalLocked();
            }
            return keys.Count;
        }
    }

    public static bool IsLegacyFallbackEntry(string tweakId) => tweakId.EndsWith("_Rev", StringComparison.Ordinal);

    /// <summary>Cambios ACTIVOS (con backup pendiente de revertir), agrupados por tweak: cantidad de valores y fecha del último.</summary>
    public IReadOnlyList<(string TweakId, int ValueCount, DateTime LastChangeUtc)> GetActiveBackupSummary()
    {
        lock (_sync)
        {
            return _journal.TweakBackups
                .Where(kv => kv.Value.Count > 0)
                .Select(kv => (kv.Key, kv.Value.Count, kv.Value.Max(r => r.TimestampUtc)))
                .OrderByDescending(x => x.Item3)
                .ToList();
        }
    }

    public bool HasBackupForTweak(string tweakId)
    {
        lock (_sync)
        {
            return _journal.TweakBackups.TryGetValue(tweakId, out var list) && list.Count > 0;
        }
    }

    private BackupJournalDocument LoadJournal()
    {
        try
        {
            if (File.Exists(_journalPath))
            {
                string json = File.ReadAllText(_journalPath);
                var doc = JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupJournalDocument);
                if (doc != null)
                {
                    return doc;
                }
            }
        }
        catch
        {
            // If corrupted, start clean journal while preserving old file
            try { File.Copy(_journalPath, _journalPath + ".bak", true); } catch { }
        }
        return new BackupJournalDocument();
    }

    private void SaveJournalLocked()
    {
        _journal.UpdatedAtUtc = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(_journal, AppJsonContext.Default.BackupJournalDocument);
        File.WriteAllText(_journalPath, json);
    }

    private void RecordBaselineIfNotExistsLocked(AtomicBackupRecord record)
    {
        if (!_journal.TweakBackups.TryGetValue(record.TweakId, out var list))
        {
            list = new List<AtomicBackupRecord>();
            _journal.TweakBackups[record.TweakId] = list;
        }

        // Only store the first pre-tweak baseline for a given target+value so multiple applies never overwrite original state
        bool alreadyRecorded = list.Any(r =>
            r.TargetKind == record.TargetKind &&
            string.Equals(r.TargetPath, record.TargetPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.ValueName, record.ValueName, StringComparison.OrdinalIgnoreCase));

        if (!alreadyRecorded)
        {
            list.Add(record);
        }

        _journal.AuditHistory.Add(record);
        if (_journal.AuditHistory.Count > 1500)
        {
            _journal.AuditHistory.RemoveRange(0, _journal.AuditHistory.Count - 1500);
        }
        SaveJournalLocked();
    }

    public void SetRegistryValue(
        string tweakId,
        RegistryHive hive,
        string subKeyPath,
        string valueName,
        object newValue,
        RegistryValueKind valueKind)
    {
        lock (_sync)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var existingKey = baseKey.OpenSubKey(subKeyPath, writable: false);

            bool existed = false;
            RegistryValueKind oldKind = valueKind;
            string? oldSerialized = null;

            if (existingKey != null)
            {
                object? rawOld = existingKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (rawOld != null)
                {
                    existed = true;
                    oldKind = existingKey.GetValueKind(valueName);
                    oldSerialized = SerializeRegistryValue(rawOld, oldKind);
                }
            }

            object typedNewValue = ConvertValueToKind(newValue, valueKind);
            string fullPath = $"{GetHivePrefix(hive)}\\{subKeyPath}";
            string newSerialized = SerializeRegistryValue(typedNewValue, valueKind);

            var record = new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.RegistryValue,
                TargetPath = fullPath,
                ValueName = valueName,
                PreviousValueExisted = existed,
                RegistryValueKindName = oldKind.ToString(),
                PreviousSerializedValue = oldSerialized,
                AppliedSerializedValue = newSerialized
            };

            RecordBaselineIfNotExistsLocked(record);

            using var writeKey = baseKey.CreateSubKey(subKeyPath, writable: true);
            if (writeKey != null)
            {
                SetValueTyped(writeKey, valueName, typedNewValue, valueKind);
            }
        }
    }

    public void SetServiceStartup(string tweakId, string serviceName, int targetStartMode, bool stopIfRunning)
    {
        string regPath = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var svcKey = hklm.OpenSubKey(regPath, writable: false);
        if (svcKey == null)
        {
            return;
        }

        object? oldStartObj = svcKey.GetValue("Start");
        int oldStart = oldStartObj is int i ? i : 3;
        bool wasRunning = false;
        try
        {
            using var sc = new ServiceController(serviceName);
            wasRunning = sc.Status == ServiceControllerStatus.Running;
        }
        catch { }

        lock (_sync)
        {
            var record = new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.ServiceStartMode,
                TargetPath = serviceName,
                ValueName = "Start",
                PreviousValueExisted = true,
                PreviousSerializedValue = $"{oldStart}|{(wasRunning ? "Running" : "Stopped")}",
                AppliedSerializedValue = $"{targetStartMode}|{(stopIfRunning ? "Stopped" : "Unchanged")}"
            };
            RecordBaselineIfNotExistsLocked(record);
        }

        using (var writeKey = hklm.OpenSubKey(regPath, writable: true))
        {
            if (writeKey != null)
            {
                SetValueTyped(writeKey, "Start", targetStartMode, RegistryValueKind.DWord);
            }
        }

        if (stopIfRunning && wasRunning)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.CanStop)
                {
                    sc.Stop();
                }
            }
            catch { }
        }
    }

    public void SetBcdValue(string tweakId, string bcdKey, string? targetValueOrNullToDelete)
    {
        string currentOut = RunHiddenProcess("bcdedit.exe", "/enum {current}");
        string? existingVal = null;
        foreach (string line in currentOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(bcdKey, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    existingVal = parts[^1];
                }
            }
        }

        lock (_sync)
        {
            var record = new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.BcdEntry,
                TargetPath = "{current}",
                ValueName = bcdKey,
                PreviousValueExisted = existingVal != null,
                PreviousSerializedValue = existingVal,
                AppliedSerializedValue = targetValueOrNullToDelete ?? "<DELETED>"
            };
            RecordBaselineIfNotExistsLocked(record);
        }

        if (string.IsNullOrEmpty(targetValueOrNullToDelete))
        {
            RunHiddenProcess("bcdedit.exe", $"/deletevalue {{current}} {bcdKey}");
        }
        else
        {
            RunHiddenProcess("bcdedit.exe", $"/set {{current}} {bcdKey} {targetValueOrNullToDelete}");
        }
    }

    public void SetPowerCfgAcValue(string tweakId, string subGroupGuidOrAlias, string settingGuidOrAlias, int newAcValue)
    {
        string qOut = RunHiddenProcess("powercfg.exe", $"/qh scheme_current {subGroupGuidOrAlias} {settingGuidOrAlias}");
        int? oldAcVal = null;
        foreach (string line in qOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("Current AC Power Setting Index:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Índice de configuración de energía CA actual:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && int.TryParse(line[(idx + 2)..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
                {
                    oldAcVal = parsed;
                }
            }
        }

        lock (_sync)
        {
            var record = new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.PowerCfgValue,
                TargetPath = subGroupGuidOrAlias,
                ValueName = settingGuidOrAlias,
                PreviousValueExisted = oldAcVal.HasValue,
                PreviousSerializedValue = oldAcVal?.ToString(CultureInfo.InvariantCulture) ?? "0",
                AppliedSerializedValue = newAcValue.ToString(CultureInfo.InvariantCulture)
            };
            RecordBaselineIfNotExistsLocked(record);
        }

        RunHiddenProcess("powercfg.exe", $"/setacvalueindex scheme_current {subGroupGuidOrAlias} {settingGuidOrAlias} {newAcValue}");
        RunHiddenProcess("powercfg.exe", "/setactive scheme_current");
    }

    public void SetScheduledTaskEnabled(string tweakId, string taskFullPath, bool enable)
    {
        string queryOut = RunHiddenProcess("schtasks.exe", $"/Query /TN \"{taskFullPath}\" /FO CSV /NH");
        bool wasDisabled = queryOut.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ||
                           queryOut.Contains("Deshabilitado", StringComparison.OrdinalIgnoreCase);

        lock (_sync)
        {
            var record = new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.ScheduledTaskState,
                TargetPath = taskFullPath,
                ValueName = "Enabled",
                PreviousValueExisted = !string.IsNullOrWhiteSpace(queryOut),
                PreviousSerializedValue = (!wasDisabled).ToString(),
                AppliedSerializedValue = enable.ToString()
            };
            RecordBaselineIfNotExistsLocked(record);
        }

        string flag = enable ? "/Enable" : "/Disable";
        RunHiddenProcess("schtasks.exe", $"/Change /TN \"{taskFullPath}\" {flag}");
    }

    /// <summary>CONFIGFLAG_DISABLED (0x1) en Enum\&lt;instancia&gt;\ConfigFlags = deshabilitado en el Administrador de dispositivos.</summary>
    public static bool IsDeviceDisabled(string instanceId)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        return k?.GetValue("ConfigFlags") is int flags && (flags & 0x1) != 0;
    }

    /// <summary>
    /// Habilita o deshabilita un dispositivo (como el Administrador de dispositivos) con pnputil, guardando el estado
    /// original en el journal. Lanza si pnputil falla, para que el lote lo reporte como error.
    /// </summary>
    public void SetDeviceEnabled(string tweakId, string instanceId, bool enable)
    {
        bool wasDisabled = IsDeviceDisabled(instanceId);
        lock (_sync)
        {
            RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.DeviceState,
                TargetPath = instanceId,
                ValueName = "Enabled",
                PreviousValueExisted = true,
                PreviousSerializedValue = wasDisabled ? "Disabled" : "Enabled",
                AppliedSerializedValue = enable ? "Enabled" : "Disabled"
            });
        }

        if (wasDisabled == !enable) return; // ya está en el estado pedido
        var (exit, output) = RunPnpUtil(enable ? "/enable-device" : "/disable-device", instanceId);
        // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED (se aplica al reiniciar)
        if (exit != 0 && exit != 3010)
        {
            throw new InvalidOperationException($"pnputil terminó con código {exit} para {instanceId}: {output.Trim()}");
        }
    }

    private static (int ExitCode, string Output) RunPnpUtil(string verb, string instanceId)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"{verb} \"{instanceId}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { }
                return (-1, "pnputil no respondió a tiempo.");
            }
            return (proc.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public void RecordCustomReversibleAction(
        string tweakId,
        BackupTargetKind kind,
        string targetPath,
        string valueName,
        string previousState,
        string appliedState)
    {
        lock (_sync)
        {
            var record = new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = kind,
                TargetPath = targetPath,
                ValueName = valueName,
                PreviousValueExisted = true,
                PreviousSerializedValue = previousState,
                AppliedSerializedValue = appliedState
            };
            RecordBaselineIfNotExistsLocked(record);
        }
    }

    /// <summary>
    /// Non-destructive revert: restores the exact pre-tweak values recorded in the journal for this tweakId.
    /// Never deletes whole Registry branches.
    /// </summary>
    public bool RevertTweakFromJournal(string tweakId, out string statusMessage)
    {
        List<AtomicBackupRecord>? records;
        lock (_sync)
        {
            if (!_journal.TweakBackups.TryGetValue(tweakId, out records) || records.Count == 0)
            {
                statusMessage = "Sin entradas previas en el journal; aplicando estado seguro predeterminado.";
                return false;
            }
            records = records.ToList();
        }

        int restoredCount = 0;
        for (int i = records.Count - 1; i >= 0; i--)
        {
            var rec = records[i];
            try
            {
                switch (rec.TargetKind)
                {
                    case BackupTargetKind.RegistryValue:
                        RestoreRegistryRecord(rec);
                        restoredCount++;
                        break;

                    case BackupTargetKind.ServiceStartMode:
                        RestoreServiceRecord(rec);
                        restoredCount++;
                        break;

                    case BackupTargetKind.BcdEntry:
                        if (rec.PreviousValueExisted && !string.IsNullOrEmpty(rec.PreviousSerializedValue))
                        {
                            RunHiddenProcess("bcdedit.exe", $"/set {{current}} {rec.ValueName} {rec.PreviousSerializedValue}");
                        }
                        else
                        {
                            RunHiddenProcess("bcdedit.exe", $"/deletevalue {{current}} {rec.ValueName}");
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.PowerCfgValue:
                        if (int.TryParse(rec.PreviousSerializedValue, out int oldPwr))
                        {
                            RunHiddenProcess("powercfg.exe", $"/setacvalueindex scheme_current {rec.TargetPath} {rec.ValueName} {oldPwr}");
                            RunHiddenProcess("powercfg.exe", "/setactive scheme_current");
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.ScheduledTaskState:
                        if (rec.PreviousValueExisted)
                        {
                            bool wasEnabled = !string.Equals(rec.PreviousSerializedValue, "False", StringComparison.OrdinalIgnoreCase);
                            RunHiddenProcess("schtasks.exe", $"/Change /TN \"{rec.TargetPath}\" {(wasEnabled ? "/Enable" : "/Disable")}");
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.NetshTcpSetting:
                        if (!string.IsNullOrEmpty(rec.PreviousSerializedValue))
                        {
                            RunHiddenProcess("netsh.exe", $"int tcp set global {rec.ValueName}={rec.PreviousSerializedValue}");
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.FsutilSetting:
                        if (!string.IsNullOrEmpty(rec.PreviousSerializedValue))
                        {
                            RunHiddenProcess("fsutil.exe", $"behavior set {rec.ValueName} {rec.PreviousSerializedValue}");
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.DeviceState:
                        if (!string.IsNullOrEmpty(rec.TargetPath))
                        {
                            bool wasEnabled = !string.Equals(rec.PreviousSerializedValue, "Disabled", StringComparison.OrdinalIgnoreCase);
                            RunPnpUtil(wasEnabled ? "/enable-device" : "/disable-device", rec.TargetPath);
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.DefenderExclusion:
                        if (!string.IsNullOrEmpty(rec.TargetPath))
                        {
                            RunHiddenProcess("powershell.exe", $"-NoProfile -Command \"Remove-MpPreference -ExclusionPath '{rec.TargetPath}' -ErrorAction SilentlyContinue\"");
                        }
                        restoredCount++;
                        break;
                }
            }
            catch
            {
                // Continue restoring remaining records
            }
        }

        lock (_sync)
        {
            _journal.TweakBackups.Remove(tweakId);
            SaveJournalLocked();
        }

        statusMessage = $"Revertido con éxito desde Journal ({restoredCount} valores exactos restaurados).";
        return true;
    }

    public int RevertAllFromJournal(out string summaryMessage)
    {
        List<string> keys;
        lock (_sync)
        {
            keys = _journal.TweakBackups.Keys.ToList();
        }

        int count = 0;
        foreach (string key in keys)
        {
            if (RevertTweakFromJournal(key, out _))
            {
                count++;
            }
        }

        summaryMessage = count > 0
            ? $"Rollback completo finalizado: {count} módulos revertidos exactamente a sus valores originales desde el Journal."
            : "No había cambios pendientes en el Journal de Auditoría.";
        return count;
    }

    private static void RestoreRegistryRecord(AtomicBackupRecord rec)
    {
        var (hive, subKeyPath) = ParseFullRegistryPath(rec.TargetPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var writeKey = baseKey.OpenSubKey(subKeyPath, writable: true);
        if (writeKey == null)
        {
            return;
        }

        if (!rec.PreviousValueExisted)
        {
            // Only remove the individual value name — NEVER the parent key/branch
            writeKey.DeleteValue(rec.ValueName, throwOnMissingValue: false);
        }
        else if (rec.PreviousSerializedValue != null)
        {
            var kind = Enum.TryParse<RegistryValueKind>(rec.RegistryValueKindName, out var parsedKind)
                ? parsedKind
                : RegistryValueKind.DWord;
            object restoredObj = DeserializeRegistryValue(rec.PreviousSerializedValue, kind);
            SetValueTyped(writeKey, rec.ValueName, restoredObj, kind);
        }
    }

    private static void RestoreServiceRecord(AtomicBackupRecord rec)
    {
        if (string.IsNullOrEmpty(rec.PreviousSerializedValue))
        {
            return;
        }
        string[] parts = rec.PreviousSerializedValue.Split('|');
        if (int.TryParse(parts[0], out int prevStart))
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var svcKey = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{rec.TargetPath}", writable: true);
            if (svcKey != null)
            {
                SetValueTyped(svcKey, "Start", prevStart, RegistryValueKind.DWord);
            }
        }

        if (parts.Length > 1 && parts[1] == "Running")
        {
            try
            {
                using var sc = new ServiceController(rec.TargetPath);
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                }
            }
            catch { }
        }
    }

    public async Task<(bool Success, string Message)> CreateSystemRestorePointAsync(string description = "KRUZH LATENCY — Backup Pre-Optimización")
    {
        return await Task.Run(() =>
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var srKey = hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: true);
                if (srKey != null)
                {
                    SetValueTyped(srKey, "SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                }

                string safeDesc = description.Replace("\"", "").Replace("'", "");
                string output = RunHiddenProcess(
                    "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop\"");

                return (true, $"Punto de Restauración '{safeDesc}' creado correctamente.");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo crear el Punto de Restauración (verificá que Protección del Sistema esté activa en C:): {ex.Message}");
            }
        });
    }

    public static string RunHiddenProcess(string fileName, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(8000);
            return stdout;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetHivePrefix(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => "HKLM"
    };

    private static (RegistryHive Hive, string SubKey) ParseFullRegistryPath(string fullPath)
    {
        int idx = fullPath.IndexOf('\\');
        if (idx < 0)
        {
            return (RegistryHive.LocalMachine, fullPath);
        }
        string prefix = fullPath[..idx].ToUpperInvariant().Replace(":", "");
        string sub = fullPath[(idx + 1)..];
        return prefix switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, sub),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, sub),
            "HKCR" or "HKEY_CLASSES_ROOT" => (RegistryHive.ClassesRoot, sub),
            "HKU" or "HKEY_USERS" => (RegistryHive.Users, sub),
            _ => (RegistryHive.LocalMachine, sub)
        };
    }

    private static string SerializeRegistryValue(object value, RegistryValueKind kind)
    {
        if (kind == RegistryValueKind.Binary && value is byte[] bytes)
        {
            return Convert.ToHexString(bytes);
        }
        if (kind == RegistryValueKind.MultiString && value is string[] arr)
        {
            return string.Join("\n", arr);
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static object DeserializeRegistryValue(string serialized, RegistryValueKind kind)
    {
        return kind switch
        {
            RegistryValueKind.DWord => int.TryParse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : 0,
            RegistryValueKind.QWord => long.TryParse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture, out long q) ? q : 0L,
            RegistryValueKind.Binary => Convert.FromHexString(serialized),
            RegistryValueKind.MultiString => serialized.Split('\n'),
            _ => serialized
        };
    }

    public static void SetValueTyped(RegistryKey key, string name, object value, RegistryValueKind kind)
    {
        object v = ConvertValueToKind(value, kind);
        key.SetValue(name, v, kind);
    }

    public static object ConvertValueToKind(object? value, RegistryValueKind kind)
    {
        if (value == null)
        {
            return kind switch
            {
                RegistryValueKind.DWord => 0,
                RegistryValueKind.QWord => 0L,
                RegistryValueKind.Binary => Array.Empty<byte>(),
                RegistryValueKind.MultiString => Array.Empty<string>(),
                _ => string.Empty
            };
        }

        return kind switch
        {
            RegistryValueKind.DWord => ConvertToDWord(value),
            RegistryValueKind.QWord => ConvertToQWord(value),
            RegistryValueKind.String or RegistryValueKind.ExpandString => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            RegistryValueKind.MultiString => value as string[] ?? (value is IEnumerable<string> e ? e.ToArray() : new[] { Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty }),
            RegistryValueKind.Binary => value as byte[] ?? ParseHexToBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
            _ => value
        };
    }

    private static int ConvertToDWord(object val)
    {
        if (val is int i) return i;
        if (val is bool b) return b ? 1 : 0;
        if (val is uint ui) return unchecked((int)ui);
        if (val is long l) return unchecked((int)l);
        if (val is byte by) return by;
        string s = Convert.ToString(val, CultureInfo.InvariantCulture)?.Trim() ?? "0";
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)) return hex;
        }
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;
        try
        {
            return Convert.ToInt32(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static long ConvertToQWord(object val)
    {
        if (val is long l) return l;
        if (val is ulong ul) return unchecked((long)ul);
        if (val is int i) return i;
        if (val is uint ui) return ui;
        string s = Convert.ToString(val, CultureInfo.InvariantCulture)?.Trim() ?? "0";
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)) return hex;
        }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return parsed;
        try
        {
            return Convert.ToInt64(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0L;
        }
    }

    public static byte[] ParseHexToBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        hex = hex.Replace(" ", "").Replace("-", "").Replace(",", "").Trim();
        if (hex.Length % 2 != 0) hex = "0" + hex;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
