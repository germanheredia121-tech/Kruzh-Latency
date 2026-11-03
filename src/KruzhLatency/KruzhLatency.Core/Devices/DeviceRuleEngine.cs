using System.Text.RegularExpressions;
using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Devices;

/// <summary>Hechos del sistema que necesitan las reglas además del árbol (los detecta Infra).</summary>
public sealed record DeviceRuleContext(
    DeviceSafetyContext Safety,
    /// <summary>Placas Wi-Fi (NetworkInterfaceType.Wireless80211), con o sin enlace.</summary>
    IReadOnlyCollection<string>? WirelessNicInstanceIds = null,
    bool HasRealPrinters = false);

/// <summary>Un grupo de dispositivos que una regla propone deshabilitar.</summary>
public sealed record DeviceRecommendation(
    string RuleId,
    string Title,
    string Section,
    string Why,
    /// <summary>Perfil mínimo que la incluye (los más agresivos también).</summary>
    Profile From,
    IReadOnlyList<DeviceNode> Devices,
    /// <summary>Aviso visible antes de aplicar (null = ninguno).</summary>
    string? Warning = null);

/// <summary>
/// Spec 002 / F11: reglas del Device Manager. Cada regla elige devnodes por topología, clase, IDs o servicio
/// (nunca por nombre visible) y TODO pasa por <see cref="NeverTouchPolicy"/> antes de proponerse.
/// Solo dispositivos presentes; los ya deshabilitados se incluyen para que el estado se vea "aplicado".
/// </summary>
public static class DeviceRuleEngine
{
    public const string VirtualSection = "Dispositivos virtuales que no se usan";
    public const string GpuSection = "Placa de video";
    public const string InputSection = "Teclado y controles";
    public const string NetworkSection = "Red";
    public const string SystemSection = "Sistema y audio";

    public static IReadOnlyList<DeviceRecommendation> Evaluate(DeviceTree tree, DeviceRuleContext context)
    {
        var protectedSet = NeverTouchPolicy.Evaluate(tree, context.Safety);
        var present = tree.All.Where(n => n.IsPresent).ToList();
        var result = new List<DeviceRecommendation>();

        void Add(string id, string title, string section, string why, Profile from, IEnumerable<DeviceNode> devices, string? warning = null)
        {
            var allowed = devices
                .Where(d => d.IsPresent && !protectedSet.ContainsKey(d.InstanceId))
                .DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (allowed.Count > 0) result.Add(new DeviceRecommendation(id, title, section, why, from, allowed, warning));
        }

        IEnumerable<DeviceNode> ByHwId(params string[] prefixes) =>
            present.Where(n => prefixes.Any(n.HasHardwareIdPrefix));

        // ------------------------------------------------ virtuales
        Add("KernelDebugNic", "Adaptador de red de depuración del kernel", VirtualSection,
            "Placa de red virtual para depurar el kernel. Nunca se usa en una PC de juego.", Profile.Normal,
            ByHwId(@"ROOT\KDNIC"));
        Add("CompositeBus", "Enumerador de bus compuesto", VirtualSection,
            "Bus para dispositivos compuestos virtuales (ej. proyectar a otra pantalla por Wi-Fi).", Profile.Competitivo,
            ByHwId(@"ROOT\CompositeBus"));
        Add("NdisVirtualBus", "Enumerador de adaptadores de red virtuales NDIS", VirtualSection,
            "Crea adaptadores de red virtuales (punto de acceso móvil, Wi-Fi Direct).", Profile.Competitivo,
            ByHwId(@"ROOT\NdisVirtualBus"));
        Add("RdpBus", "Bus de redirección de Escritorio remoto", VirtualSection,
            "Redirige dispositivos en sesiones de Escritorio remoto.", Profile.Competitivo,
            ByHwId(@"ROOT\RDPBUS"));
        Add("UmBus", "Enumerador de bus UMBus", VirtualSection,
            "Bus para drivers de modo usuario de algunos dispositivos móviles/sensores.", Profile.Profesional,
            ByHwId(@"ROOT\umbus"));
        Add("VirtualDrive", "Enumerador de unidades virtuales", VirtualSection,
            "Montaje de discos virtuales (VHD/ISO montados como disco).", Profile.Profesional,
            ByHwId(@"ROOT\vdrvroot"), "Si montás discos VHD/VHDX no lo deshabilites.");
        Add("RrasRoot", "Enumerador raíz de RRAS (WAN Miniports)", VirtualSection,
            "Crea los 8 WAN Miniport de VPN/PPPoE que cargan ndis.sys aunque no los uses.", Profile.Profesional,
            present.Where(n => n.InstanceId.StartsWith(@"SWD\MSRRAS\", StringComparison.OrdinalIgnoreCase) && n.ClassName == "SoftwareDevice"),
            "Desactiva las VPN integradas de Windows y las conexiones PPPoE. Si tu internet usa PPPoE en la PC, no lo apliques.");

        // ------------------------------------------------ placa de video: funciones hermanas (chat: bus 43 fn1-3)
        var gpus = GpusToConsider(tree, context.Safety);
        Add("GpuHdmiAudio", "Controladora de audio HDMI/DisplayPort de la placa de video", GpuSection,
            "Función 1 de la GPU: el audio por el cable del monitor. Si no la usás, deshabilitar la controladora PCI (no solo el parlante) saca su driver e interrupciones.",
            Profile.Competitivo,
            gpus.SelectMany(tree.PciSiblings).Where(s => PciClassStarts(s, "0403")),
            "Si escuchás por los parlantes del monitor no lo apliques (si es tu salida predeterminada, KRUZH no lo toca).");
        Add("GpuUsbC", "USB-C de la placa de video (xHCI + UCSI)", GpuSection,
            "Controladora USB y de puerto USB-C (VirtualLink) de la GPU sin nada conectado: driver e interrupciones de más.",
            Profile.Competitivo,
            gpus.SelectMany(g =>
            {
                var usb = tree.PciSiblings(g).Where(s => PciClassStarts(s, "0C03") || PciClassStarts(s, "0C80")).ToList();
                // Si hay algo enchufado al USB-C de la GPU, no se toca ni la xHCI ni el UCSI que maneja ese puerto.
                return usb.Any(s => HasConnectedUsbDevices(tree, s)) ? Enumerable.Empty<DeviceNode>() : usb;
            }));
        Add("NvidiaPlatformFramework", "NVIDIA Platform Controllers and Framework", GpuSection,
            "Control de potencia dinámico pensado para notebooks; en una PC de escritorio no aporta y suma un driver.",
            Profile.Competitivo,
            present.Where(n => ServiceIs(n, "nvpcf") || n.HasHardwareIdPrefix(@"ACPI\NVDA0820") || n.HasHardwareIdPrefix(@"ACPI\VEN_NVDA&DEV_0820")));
        Add("NvidiaVirtualAudio", "NVIDIA Virtual Audio Device", GpuSection,
            "Audio virtual de NVIDIA para streaming (Shield/Broadcast). Si no lo usás, es un driver de audio más.",
            Profile.Competitivo,
            present.Where(n => ServiceIs(n, "nvvad_WaveExtensible") || ServiceIs(n, "nvvhci")));

        // ------------------------------------------------ teclado: nodos de joystick virtuales
        var keyboardIds = present.Where(n => n.ClassName.Equals("Keyboard", StringComparison.OrdinalIgnoreCase))
            .Select(VidPid).Where(v => v is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Add("KeyboardGameControllers", "Joystick virtual del teclado (XInput / HID game controller)", InputSection,
            "Teclados analógicos (Wooting, Razer Huntsman V3…) exponen un joystick virtual; el juego sondea esos nodos aunque no uses gamepad. Las teclas y el Rapid Trigger siguen funcionando.",
            Profile.Competitivo,
            present.Where(n => IsGameControllerNode(n) && VidPid(n) is { } v && keyboardIds.Contains(v)),
            "Si usás el modo gamepad analógico del teclado, no lo apliques.");

        // ------------------------------------------------ red
        var linked = (context.Safety.LinkedNicInstanceIds ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wireless = (context.WirelessNicInstanceIds ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ethernetLinked = linked.Any(id => !wireless.Contains(id));
        if (ethernetLinked)
        {
            Add("WifiWithEthernet", "Wi-Fi (jugás por cable)", NetworkSection,
                "Con el cable conectado, la placa Wi-Fi sigue escaneando redes y generando DPC en ndis.sys.", Profile.Competitivo,
                present.Where(n => wireless.Contains(n.InstanceId) && !linked.Contains(n.InstanceId)));
            Add("SecondNic", "Segunda placa de red sin cable", NetworkSection,
                "Placa de red física sin enlace: su driver sigue cargado y atendiendo interrupciones.", Profile.Competitivo,
                present.Where(n => IsPhysicalEthernet(n) && !linked.Contains(n.InstanceId) && !wireless.Contains(n.InstanceId)));
        }

        // ------------------------------------------------ sistema y audio
        Add("SystemSpeaker", "Altavoz del sistema (beep)", SystemSection,
            "El parlante interno de la placa madre (pitidos). No se usa para el audio del juego.", Profile.Normal,
            ByHwId(@"ACPI\PNP0800", @"ACPI\VEN_PNP&DEV_0800"));
        Add("Hpet", "High Precision Event Timer (HPET)", SystemSection,
            "Windows 10/11 no usa el HPET como reloj principal; deshabilitarlo en el Administrador evita que algún driver lo consulte. Medí DPC antes y después.",
            Profile.Entusiasta,
            ByHwId(@"ACPI\PNP0103", @"ACPI\VEN_PNP&DEV_0103"),
            "Efecto discutido: si notás peor frametime, revertilo.");
        Add("GsWavetable", "Microsoft GS Wavetable Synth", SystemSection,
            "Sintetizador MIDI por software. Ningún juego lo usa.", Profile.Competitivo,
            present.Where(n => n.InstanceId.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase) && n.ClassName == "SoftwareDevice"));
        Add("Midi2", "Servicio MIDI 2.0 (dispositivos virtuales y de prueba)", SystemSection,
            "Dispositivos virtuales de MIDI 2.0 de Windows 11. Sin instrumentos MIDI no hacen nada.", Profile.Competitivo,
            present.Where(n => n.InstanceId.StartsWith(@"SWD\MIDISRV\", StringComparison.OrdinalIgnoreCase)));
        Add("VoiceClarity", "Voice Clarity (efecto de audio por IA)", SystemSection,
            "Filtro APO de Windows 11 que procesa el audio con IA: CPU y latencia de audio de más.", Profile.Competitivo,
            ByHwId(@"SWC\VEN_MSFT&AUDIO_EFFECTPACK_VOICECLARITY"));
        if (!context.HasRealPrinters)
        {
            Add("PrintQueues", "Colas de impresión", SystemSection,
                "No tenés impresoras reales: las colas virtuales (Root Print Queue, Print to PDF) no hacen falta.", Profile.Competitivo,
                present.Where(n => n.ClassName.Equals("PrintQueue", StringComparison.OrdinalIgnoreCase)),
                "Deja de funcionar \"Imprimir a PDF\".");
        }

        return result;
    }

    // ------------------------------------------------------------ helpers

    private static IEnumerable<DeviceNode> GpusToConsider(DeviceTree tree, DeviceSafetyContext safety)
    {
        if (safety.ActiveGpuInstanceId is { } id && tree.Get(id) is { } active) return new[] { active };
        return tree.All.Where(n => n.IsPresent && n.IsPci && PciClassStarts(n, "03"));
    }

    /// <summary>¿Hay algún dispositivo USB real (no hubs raíz) colgando de esta controladora?</summary>
    private static bool HasConnectedUsbDevices(DeviceTree tree, DeviceNode controller) =>
        tree.Descendants(controller).Any(d => d.IsPresent &&
            d.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
            !d.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase));

    private static bool IsGameControllerNode(DeviceNode n) =>
        n.ClassName.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase) ||
        n.ClassName.Equals("XboxComposite", StringComparison.OrdinalIgnoreCase) ||
        n.HardwareIds.Concat(n.CompatibleIds).Any(id =>
            id.Contains("MS_COMP_XUSB", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("HID_DEVICE_UP:0001_U:0004", StringComparison.OrdinalIgnoreCase) ||   // joystick
            id.Equals("HID_DEVICE_UP:0001_U:0005", StringComparison.OrdinalIgnoreCase)) || // gamepad
        n.InstanceId.Contains("&IG_", StringComparison.OrdinalIgnoreCase);                   // XInput sobre HID

    private static readonly Regex VidPidRegex = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? VidPid(DeviceNode n)
    {
        foreach (var s in n.HardwareIds.Prepend(n.InstanceId))
        {
            var m = VidPidRegex.Match(s);
            if (m.Success) return $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToUpperInvariant();
        }
        return null;
    }

    private static bool IsPhysicalEthernet(DeviceNode n) =>
        n.ClassName.Equals("Net", StringComparison.OrdinalIgnoreCase) &&
        (PciClassStarts(n, "0200") || n.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase));

    private static bool ServiceIs(DeviceNode n, string service) =>
        string.Equals(n.Service, service, StringComparison.OrdinalIgnoreCase);

    internal static bool PciClassStarts(DeviceNode n, string classPrefix) =>
        n.IsPci && n.CompatibleIds.Any(c => c.StartsWith(@"PCI\CC_" + classPrefix, StringComparison.OrdinalIgnoreCase));
}
