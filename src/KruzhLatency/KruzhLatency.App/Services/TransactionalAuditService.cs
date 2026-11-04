using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading.Tasks;
using KruzhLatency.Core.Journal;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed class TransactionalAuditService
{
    private readonly object _sync = new();
    private readonly JournalStore _store;

    /// <summary>Cómo se cargó el journal: si hubo que recuperarlo del .bak o arrancó vacío por corrupción, la UI lo avisa.</summary>
    public JournalLoadOutcome JournalLoadOutcome => _store.LoadOutcome;
    public string JournalPath => _store.Path;
    public int MigratedFromV1 { get; }

    public TransactionalAuditService()
    {
        // T11: journal v2 en C:\ProgramData\KruzhLatency\journal (solo admins/SYSTEM escriben: la app elevada restaura
        // lo que dice este archivo, así que un usuario común no puede poder editarlo).
        string dir = KruzhLatency.Infra.Security.SecureFolders.ProgramData("journal");
        _store = new JournalStore(Path.Combine(dir, "journal.json"));
        MigratedFromV1 = MigrateV1();
    }

    /// <summary>Migra el journal v1 (%LOCALAPPDATA%\KruzhLatency\Journal\backup_journal.json) y lo renombra.</summary>
    private int MigrateV1()
    {
        string v1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KruzhLatency", "Journal", "backup_journal.json");
        if (!File.Exists(v1)) return 0;
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllText(v1), AppJsonContext.Default.BackupJournalDocument);
            int added = doc is null ? 0 : _store.Import(
                doc.TweakBackups.SelectMany(kv => kv.Value.Select(r => ToEntry(r, kv.Key))),
                doc.AuditHistory.Select(r => ToEntry(r, r.TweakId)));
            File.Move(v1, v1 + ".migrado-a-v2", overwrite: true);
            return added;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            App.LogError("Migración journal v1", ex);
            return 0;
        }
    }

    private static JournalEntry ToEntry(AtomicBackupRecord r, string tweakId) => new()
    {
        TweakId = tweakId,
        OperationId = JournalEntry.MakeOperationId(r.TargetKind.ToString(), r.TargetPath, r.ValueName),
        Kind = r.TargetKind.ToString(),
        TargetPath = r.TargetPath,
        ValueName = r.ValueName,
        PreviousValueExisted = r.PreviousValueExisted,
        ValueKind = r.RegistryValueKindName,
        PreviousValue = r.PreviousSerializedValue,
        AppliedValue = r.AppliedSerializedValue,
        TimestampUtc = r.TimestampUtc
    };

    /// <summary>null si el tipo no existe en esta versión (journal de una versión más nueva): no se toca.</summary>
    private static AtomicBackupRecord? FromEntry(JournalEntry e) =>
        Enum.TryParse<BackupTargetKind>(e.Kind, out var kind)
            ? new AtomicBackupRecord
            {
                TweakId = e.TweakId,
                TimestampUtc = e.TimestampUtc,
                TargetKind = kind,
                TargetPath = e.TargetPath,
                ValueName = e.ValueName,
                PreviousValueExisted = e.PreviousValueExisted,
                RegistryValueKindName = e.ValueKind,
                PreviousSerializedValue = e.PreviousValue,
                AppliedSerializedValue = e.AppliedValue
            }
            : null;

    private static bool SameOperation(AtomicBackupRecord r, JournalEntry e) =>
        e.OperationId == JournalEntry.MakeOperationId(r.TargetKind.ToString(), r.TargetPath, r.ValueName);

    public IReadOnlyList<AtomicBackupRecord> GetAuditHistory() =>
        _store.History().Select(FromEntry).OfType<AtomicBackupRecord>().OrderByDescending(x => x.TimestampUtc).ToList();

    /// <summary>
    /// Descarta entradas del journal SIN restaurarlas. Se usa para las entradas "*_Rev" que dejaban los fallbacks de
    /// revert de v3 (bug B2): su "valor original" es el valor tweakeado, así que revertirlas re-aplicaba el tweak.
    /// </summary>
    public int DiscardEntries(Func<string, bool> tweakIdPredicate) => _store.RemoveTweaks(tweakIdPredicate);

    public static bool IsLegacyFallbackEntry(string tweakId) => tweakId.EndsWith("_Rev", StringComparison.Ordinal);

    /// <summary>Cambios ACTIVOS (con backup pendiente de revertir), agrupados por tweak: cantidad de valores y fecha del último.</summary>
    public IReadOnlyList<(string TweakId, int ValueCount, DateTime LastChangeUtc)> GetActiveBackupSummary() =>
        _store.AllBaselines()
            .Select(kv => (kv.Key, kv.Value.Count, kv.Value.Max(r => r.TimestampUtc)))
            .OrderByDescending(x => x.Item3)
            .ToList();

    public bool HasBackupForTweak(string tweakId) => _store.HasBaseline(tweakId);

    /// <summary>Ids del journal con un prefijo (ej. "AutoMsi:" → uno por dispositivo).</summary>
    public IReadOnlyList<string> TweakIdsStartingWith(string prefix) =>
        _store.AllBaselines().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    /// <summary>Guarda el original una sola vez por (tweak, operación) — atómico, antes de tocar el sistema.</summary>
    private void RecordBaselineIfNotExistsLocked(AtomicBackupRecord record) => _store.RecordBaseline(ToEntry(record, record.TweakId));

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

    /// <summary>
    /// Borra un valor del registro guardando antes el original en el journal (revertir lo vuelve a crear).
    /// Si el valor no existe no hace nada ni registra nada.
    /// </summary>
    public void DeleteRegistryValue(string tweakId, RegistryHive hive, string subKeyPath, string valueName)
    {
        lock (_sync)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: true);
            object? rawOld = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key is null || rawOld is null) return;

            var oldKind = key.GetValueKind(valueName);
            RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.RegistryValue,
                TargetPath = $"{GetHivePrefix(hive)}\\{subKeyPath}",
                ValueName = valueName,
                PreviousValueExisted = true,
                RegistryValueKindName = oldKind.ToString(),
                PreviousSerializedValue = SerializeRegistryValue(rawOld, oldKind),
                AppliedSerializedValue = "<DELETED>"
            });
            key.DeleteValue(valueName, throwOnMissingValue: false);
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

        // Por el Administrador de servicios: varias claves de Services solo dan lectura a Administradores (DPS, Wdi*, TrkWks).
        if (oldStart != targetStartMode)
        {
            KruzhLatency.Infra.Services.ServiceConfigStore.SetStartType(serviceName, targetStartMode);
            using var verify = hklm.OpenSubKey(regPath, writable: false);
            if (verify?.GetValue("Start") is not int now || now != targetStartMode)
            {
                throw new InvalidOperationException($"{serviceName}: Windows no tomó el tipo de inicio {targetStartMode}.");
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

    /// <summary>
    /// Escribe o borra un elemento de {current} vía WMI BcdStore (sin parsear bcdedit, que está traducido).
    /// El journal guarda el valor previo como yes/no/número, que bcdedit y WMI entienden en cualquier idioma.
    /// </summary>
    public void SetBcdValue(string tweakId, string bcdKey, string? targetValueOrNullToDelete)
    {
        var element = KruzhLatency.Infra.Wmi.WmiBcdStore.FromBcdEditName(bcdKey)
            ?? throw new ArgumentException($"Elemento BCD no soportado: {bcdKey}", nameof(bcdKey));
        var store = new KruzhLatency.Infra.Wmi.WmiBcdStore();
        var previous = store.Read(element);
        string? existingVal = previous is null ? null : KruzhLatency.Infra.Wmi.WmiBcdStore.Serialize(previous);

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

        if (KruzhLatency.Infra.Wmi.WmiBcdStore.Parse(element, targetValueOrNullToDelete) is { } target)
        {
            store.Write(element, target);
        }
        else
        {
            store.Delete(element);
        }
    }

    /// <summary>E3: ajuste global TCP/IP por WMI con backup del valor anterior (sin netsh).</summary>
    public void SetNetTcpGlobal(string tweakId, string target, string property, uint value)
    {
        uint? previous = KruzhLatency.Infra.Network.NetTcpGlobalStore.Read(target, property)
            ?? throw new InvalidOperationException($"{target}.{property} no existe en este Windows.");
        if (previous == value) return;

        lock (_sync)
        {
            RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.NetTcpGlobal,
                TargetPath = target,
                ValueName = property,
                PreviousValueExisted = true,
                PreviousSerializedValue = previous.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AppliedSerializedValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        KruzhLatency.Infra.Network.NetTcpGlobalStore.Write(target, property, value);
    }

    /// <summary>E6: liga o desliga un protocolo de una placa (WMI MSFT_NetAdapterBindingSettingData) con backup.</summary>
    public void SetAdapterBinding(string tweakId, string adapterName, string componentId, bool enabled)
    {
        bool previous = KruzhLatency.Infra.Network.NetAdapterBindingStore.IsEnabled(adapterName, componentId)
            ?? throw new InvalidOperationException($"{adapterName} no tiene el protocolo {componentId}.");
        if (previous == enabled) return;

        lock (_sync)
        {
            RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.NetAdapterBinding,
                TargetPath = adapterName,
                ValueName = componentId,
                PreviousValueExisted = true,
                PreviousSerializedValue = previous ? "True" : "False",
                AppliedSerializedValue = enabled ? "True" : "False"
            });
        }
        KruzhLatency.Infra.Network.NetAdapterBindingStore.SetEnabled(adapterName, componentId, enabled);
    }

    /// <summary>
    /// E12: crea una tarea programada propia. Si ya existía una con esa ruta, no se registra como creada por KRUZH
    /// (revertir no la borraría): solo se reemplaza su definición.
    /// </summary>
    public void CreateScheduledTask(string tweakId, string taskPath, string xml)
    {
        bool existed = KruzhLatency.Infra.Debloat.ScheduledTaskStore.IsEnabled(taskPath) is not null;
        if (!existed)
        {
            lock (_sync)
            {
                RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
                {
                    TweakId = tweakId,
                    TimestampUtc = DateTime.UtcNow,
                    TargetKind = BackupTargetKind.ScheduledTaskCreated,
                    TargetPath = taskPath,
                    ValueName = "Created",
                    PreviousValueExisted = false,
                    PreviousSerializedValue = null,
                    AppliedSerializedValue = "Created"
                });
            }
        }
        KruzhLatency.Infra.Debloat.ScheduledTaskStore.Register(taskPath, xml);
    }

    private static void RestoreBcdRecord(AtomicBackupRecord rec)
    {
        if (KruzhLatency.Infra.Wmi.WmiBcdStore.FromBcdEditName(rec.ValueName) is not { } element)
        {
            // Elemento que no maneja WmiBcdStore (journal viejo): bcdedit con el valor guardado.
            RunHiddenProcess("bcdedit.exe", rec.PreviousValueExisted && !string.IsNullOrEmpty(rec.PreviousSerializedValue)
                ? $"/set {{current}} {rec.ValueName} {rec.PreviousSerializedValue}"
                : $"/deletevalue {{current}} {rec.ValueName}");
            return;
        }
        var store = new KruzhLatency.Infra.Wmi.WmiBcdStore();
        if (rec.PreviousValueExisted && KruzhLatency.Infra.Wmi.WmiBcdStore.Parse(element, rec.PreviousSerializedValue) is { } previous)
        {
            store.Write(element, previous);
        }
        else
        {
            store.Delete(element);
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

    /// <summary>
    /// Habilita/deshabilita una tarea programada por la API COM del Programador de tareas (T41: sin parsear schtasks,
    /// que está traducido). Si la tarea no existe en esta PC no hace nada y devuelve false.
    /// </summary>
    public bool SetScheduledTaskEnabled(string tweakId, string taskFullPath, bool enable)
    {
        bool? current = KruzhLatency.Infra.Debloat.ScheduledTaskStore.IsEnabled(taskFullPath);
        if (current is null) return false;

        RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
        {
            TweakId = tweakId,
            TimestampUtc = DateTime.UtcNow,
            TargetKind = BackupTargetKind.ScheduledTaskState,
            TargetPath = taskFullPath,
            ValueName = "Enabled",
            PreviousValueExisted = true,
            PreviousSerializedValue = current.Value.ToString(),
            AppliedSerializedValue = enable.ToString()
        });

        if (current.Value != enable) KruzhLatency.Infra.Debloat.ScheduledTaskStore.SetEnabled(taskFullPath, enable);
        return KruzhLatency.Infra.Debloat.ScheduledTaskStore.IsEnabled(taskFullPath) == enable;
    }

    /// <summary>T41: quita una app AppX para el usuario con backup (revertir la vuelve a registrar).</summary>
    public (bool Ok, string Detail) RemoveAppxForUser(string tweakId, string packageFullName, string familyName)
    {
        if (!KruzhLatency.Infra.Debloat.AppxStore.IsInstalled(packageFullName)) return (true, "Ya no estaba instalada.");
        RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
        {
            TweakId = tweakId,
            TimestampUtc = DateTime.UtcNow,
            TargetKind = BackupTargetKind.AppxRemoved,
            TargetPath = packageFullName,
            ValueName = familyName,
            PreviousValueExisted = true,
            PreviousSerializedValue = "Installed",
            AppliedSerializedValue = "Removed"
        });
        return KruzhLatency.Infra.Debloat.AppxStore.RemoveForUser(packageFullName);
    }

    /// <summary>T41, solo Entusiasta: desaprovisiona (irreversible desde KRUZH). Queda en el journal para el historial.</summary>
    public (bool Ok, string Detail) DeprovisionAppx(string tweakId, string familyName)
    {
        RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
        {
            TweakId = tweakId,
            TimestampUtc = DateTime.UtcNow,
            TargetKind = BackupTargetKind.AppxDeprovisioned,
            TargetPath = familyName,
            ValueName = "Provisioned",
            PreviousValueExisted = true,
            PreviousSerializedValue = "Provisioned",
            AppliedSerializedValue = "Deprovisioned"
        });
        return KruzhLatency.Infra.Debloat.AppxStore.Deprovision(familyName);
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

    /// <summary>
    /// C6: escribe EDID_OVERRIDE (valores "0", "1"… de 128 bytes, el formato de CRU) con cada bloque en el journal;
    /// los bloques de un override anterior que sobren se borran (también con backup). Revertir deja el EDID previo.
    /// </summary>
    public void WriteEdidOverride(string tweakId, string monitorInstanceId, byte[] edid)
    {
        if (edid.Length == 0 || edid.Length % 128 != 0) throw new ArgumentException("El EDID debe tener bloques de 128 bytes.", nameof(edid));
        string path = $@"SYSTEM\CurrentControlSet\Enum\{monitorInstanceId}\Device Parameters\EDID_OVERRIDE";
        int blocks = edid.Length / 128;
        for (int i = 0; i < blocks; i++)
        {
            SetRegistryValue(tweakId, RegistryHive.LocalMachine, path, i.ToString(CultureInfo.InvariantCulture),
                edid.AsSpan(i * 128, 128).ToArray(), RegistryValueKind.Binary);
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
        {
            if (int.TryParse(name, out int idx) && idx >= blocks)
            {
                DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, path, name);
            }
        }
    }

    /// <summary>
    /// C4: exporta GraphicsDrivers\Configuration y \Connectivity a .reg (carpeta de backups de KRUZH), los registra en el
    /// journal y borra sus subclaves. Revertir los vuelve a importar. Devuelve cuántas entradas se borraron.
    /// </summary>
    public int ResetDisplayCache(string tweakId)
    {
        // En la carpeta protegida: "Revertir" hace reg import de estos archivos como administrador.
        string dir = KruzhLatency.Infra.Security.SecureFolders.ProgramData("backups", $"pantallas-{DateTime.Now:yyyyMMdd-HHmmss}");
        int removed = 0;
        foreach (var key in KruzhLatency.Infra.Display.DisplayCache.CacheKeys)
        {
            string file = Path.Combine(dir, key + ".reg");
            KruzhLatency.Infra.Display.DisplayCache.Export(key, file); // sin backup no se borra (lanza si falla)
            lock (_sync)
            {
                RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
                {
                    TweakId = tweakId,
                    TimestampUtc = DateTime.UtcNow,
                    TargetKind = BackupTargetKind.RegistryExport,
                    TargetPath = file,
                    ValueName = $@"HKLM\{KruzhLatency.Infra.Display.DisplayCache.GraphicsDrivers}\{key}",
                    PreviousValueExisted = true,
                    PreviousSerializedValue = file,
                    AppliedSerializedValue = "<CLEARED>"
                });
            }
            removed += KruzhLatency.Infra.Display.DisplayCache.ClearSubKeys(key);
        }
        return removed;
    }

    /// <summary>
    /// Rescate (ADR-007): reactiva TODOS los dispositivos que KRUZH deshabilitó (journal: estado previo "Enabled") y
    /// quita esas entradas. No toca lo que el usuario ya tenía deshabilitado antes de KRUZH.
    /// Devuelve cuántos se reactivaron y los que fallaron.
    /// </summary>
    public (int Enabled, IReadOnlyList<string> Failed) RescueDisabledDevices()
    {
        var targets = _store.AllBaselines()
            .SelectMany(kv => kv.Value)
            .Select(FromEntry)
            .OfType<AtomicBackupRecord>()
            .Where(r => r.TargetKind == BackupTargetKind.DeviceState &&
                        string.Equals(r.PreviousSerializedValue, "Enabled", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(r.TargetPath))
            .Select(r => (r.TweakId, Rec: r))
            .ToList();

        int enabled = 0;
        var failed = new List<string>();
        foreach (var instance in targets.Select(t => t.Rec.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (exit, _) = RunPnpUtil("/enable-device", instance);
            if (exit is 0 or 3010) enabled++;
            else failed.Add(instance);
        }

        var done = targets.Where(t => !failed.Contains(t.Rec.TargetPath, StringComparer.OrdinalIgnoreCase)).Select(t => t.Rec).ToList();
        _store.RemoveEntries(e => done.Any(r => r.TweakId == e.TweakId && SameOperation(r, e)));
        return (enabled, failed);
    }

    /// <summary>
    /// Marca/desmarca "Permitir que el equipo apague este dispositivo" (WMI) guardando el valor original en el journal.
    /// null = el dispositivo no expone la casilla; false = WMI rechazó el cambio o la relectura no coincide.
    /// </summary>
    public bool? SetDeviceAllowPowerOff(string tweakId, string instanceId, bool allow)
    {
        var power = new KruzhLatency.Infra.Wmi.WmiDevicePowerManagement();
        bool? current = power.GetAllowPowerOff(instanceId);
        if (current is null) return null;

        lock (_sync)
        {
            RecordBaselineIfNotExistsLocked(new AtomicBackupRecord
            {
                TweakId = tweakId,
                TimestampUtc = DateTime.UtcNow,
                TargetKind = BackupTargetKind.DevicePowerOff,
                TargetPath = instanceId,
                ValueName = "AllowPowerOff",
                PreviousValueExisted = true,
                PreviousSerializedValue = current.Value ? "True" : "False",
                AppliedSerializedValue = allow ? "True" : "False"
            });
        }

        if (current.Value == allow) return true;
        try
        {
            power.SetAllowPowerOff(instanceId, allow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Management.ManagementException or UnauthorizedAccessException)
        {
            return false;
        }
        return power.GetAllowPowerOff(instanceId) == allow;
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
        var records = _store.Baselines(tweakId).Select(FromEntry).OfType<AtomicBackupRecord>().ToList();
        if (records.Count == 0)
        {
            statusMessage = "Sin entradas previas en el journal; aplicando estado seguro predeterminado.";
            return false;
        }
        var restored = new List<AtomicBackupRecord>();

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
                        RestoreBcdRecord(rec);
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
                        if (rec.PreviousValueExisted && KruzhLatency.Infra.Debloat.ScheduledTaskStore.IsEnabled(rec.TargetPath) is not null)
                        {
                            bool wasEnabled = !string.Equals(rec.PreviousSerializedValue, "False", StringComparison.OrdinalIgnoreCase);
                            KruzhLatency.Infra.Debloat.ScheduledTaskStore.SetEnabled(rec.TargetPath, wasEnabled);
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.AppxRemoved:
                        if (!KruzhLatency.Infra.Debloat.AppxStore.IsInstalled(rec.TargetPath))
                        {
                            var (ok, detail) = KruzhLatency.Infra.Debloat.AppxStore.Register(rec.TargetPath);
                            if (!ok) throw new InvalidOperationException(detail); // queda en el journal para reintentar
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.AppxDeprovisioned:
                        // Irreversible desde KRUZH (se avisó al aplicar): la app se reinstala desde la Microsoft Store.
                        restoredCount++;
                        break;

                    case BackupTargetKind.NetshTcpSetting:
                        if (!string.IsNullOrEmpty(rec.PreviousSerializedValue))
                        {
                            RunHiddenProcess("netsh.exe", $"int tcp set global {rec.ValueName}={rec.PreviousSerializedValue}");
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.NetTcpGlobal:
                        if (uint.TryParse(rec.PreviousSerializedValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint oldTcp)
                            && KruzhLatency.Infra.Network.NetTcpGlobalStore.Read(rec.TargetPath, rec.ValueName) is { } nowTcp && nowTcp != oldTcp)
                        {
                            KruzhLatency.Infra.Network.NetTcpGlobalStore.Write(rec.TargetPath, rec.ValueName, oldTcp);
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.ScheduledTaskCreated:
                        KruzhLatency.Infra.Debloat.ScheduledTaskStore.Delete(rec.TargetPath);
                        restoredCount++;
                        break;

                    case BackupTargetKind.NetAdapterBinding:
                        // Si la placa ya no existe no hay nada que volver a ligar.
                        if (KruzhLatency.Infra.Network.NetAdapterBindingStore.IsEnabled(rec.TargetPath, rec.ValueName) is not null)
                        {
                            KruzhLatency.Infra.Network.NetAdapterBindingStore.SetEnabled(rec.TargetPath, rec.ValueName,
                                string.Equals(rec.PreviousSerializedValue, "True", StringComparison.OrdinalIgnoreCase));
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

                    case BackupTargetKind.RegistryExport:
                        if (File.Exists(rec.TargetPath))
                        {
                            KruzhLatency.Infra.Display.DisplayCache.Import(rec.TargetPath);
                        }
                        restoredCount++;
                        break;

                    case BackupTargetKind.DevicePowerOff:
                        if (!string.IsNullOrEmpty(rec.TargetPath))
                        {
                            new KruzhLatency.Infra.Wmi.WmiDevicePowerManagement().SetAllowPowerOff(
                                rec.TargetPath, string.Equals(rec.PreviousSerializedValue, "True", StringComparison.OrdinalIgnoreCase));
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
                restored.Add(rec);
            }
            catch (Exception ex)
            {
                // Sigue con el resto; este valor queda en el journal para reintentar (antes se perdía su original).
                App.LogError($"Revertir {tweakId} · {rec.TargetKind} {rec.TargetPath} {rec.ValueName}", ex);
            }
        }

        // Solo se quita lo que de verdad se restauró.
        _store.RemoveEntries(e => e.TweakId == tweakId && restored.Any(r => SameOperation(r, e)));
        int pending = records.Count - restored.Count;

        statusMessage = pending == 0
            ? $"Revertido desde el journal ({restoredCount} valores originales restaurados)."
            : $"Revertido en parte: {restored.Count} restaurados, {pending} fallaron y quedan guardados para reintentar.";
        return pending == 0;
    }

    public int RevertAllFromJournal(out string summaryMessage)
    {
        var keys = _store.AllBaselines().Keys.ToList();

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
            using var svcKey = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{rec.TargetPath}", writable: false);
            // Si ya está como antes (ej. el cambio nunca se pudo aplicar) no hay nada que restaurar.
            if (svcKey?.GetValue("Start") is int current && current != prevStart)
            {
                KruzhLatency.Infra.Services.ServiceConfigStore.SetStartType(rec.TargetPath, prevStart);
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
