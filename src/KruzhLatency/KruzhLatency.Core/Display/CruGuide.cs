using System.Globalization;

namespace KruzhLatency.Core.Display;

public sealed record GuideStep(string Title, IReadOnlyList<string> Lines, string? Link = null);

public enum GpuVendor { Nvidia, Amd, Intel, Unknown }

public sealed record CruGuideRequest(
    MonitorAnalysis Monitor,
    int StretchedWidth,
    int StretchedHeight,
    GpuVendor Gpu);

public sealed record CruGuide(
    string MonitorName,
    DetailedTiming Native,
    DetailedTiming Stretched,
    DisplayIdRecommendation NativeChoice,
    DisplayIdRecommendation StretchedChoice,
    /// <summary>Versión de DisplayID a crear en CRU: si para algún modo 1.3 no da exacto, va 2.0 (ADR-009).</summary>
    string DisplayIdVersion,
    IReadOnlyList<GuideStep> Steps);

public sealed record VerificationCheck(string Name, bool Ok, string Detail);

/// <summary>
/// Spec 002 / C5 (ADR-009, modo manual): guía de CRU paso a paso armada con los datos reales del monitor.
/// Reproduce el flujo del chat: backup, copiar el timing nativo, reemplazar CTA-861 por DisplayID, agregar la 4:3 al
/// refresco nativo exacto, reiniciar (nunca restart64), escalado en Pantalla y OSD. Más la recuperación y la verificación.
/// </summary>
public static class CruGuideBuilder
{
    public const string CruUrl = "https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU";

    public static CruGuide Build(CruGuideRequest req)
    {
        var native = req.Monitor.NativeMax ?? throw new InvalidOperationException("El monitor no tiene timings detallados en el EDID.");
        double hz = Math.Round(native.RefreshHz);
        var stretched = RefreshCalculator.CvtReducedBlanking(req.StretchedWidth, req.StretchedHeight, hz);

        var nativeChoice = RefreshCalculator.RecommendDisplayId(native.HTotal, native.VTotal, hz);
        var stretchedChoice = RefreshCalculator.RecommendDisplayId(stretched.HTotal, stretched.VTotal, hz);
        bool needs20 = nativeChoice.Chosen.Source == TimingSource.DisplayIdType7 || stretchedChoice.Chosen.Source == TimingSource.DisplayIdType7;
        string version = needs20 ? "2.0" : "1.3";
        string name = req.Monitor.Monitor.Name;
        string res = $"{req.StretchedWidth}x{req.StretchedHeight}";
        var cta = req.Monitor.Parsed?.Cta.FirstOrDefault();

        var steps = new List<GuideStep>
        {
            new("1. Descargar CRU (Custom Resolution Utility)", new[]
            {
                "Bajalo solo del foro oficial de ToastyX (monitortests.com). Descomprimilo en una carpeta fija, por ejemplo C:\\CRU.",
                "Trae CRU.exe (el editor), reset-all.exe (vuelve todo a fábrica) y restart64.exe (NO lo uses: reinicia el driver en caliente y en tu PC dejó la placa en Code 45)."
            }, CruUrl),
            new("2. Abrir CRU y hacer backup", new[]
            {
                $"Ejecutá CRU.exe como administrador y elegí arriba \"{name} (active)\".",
                "Botón Export… → guardá el EDID actual como backup (por ejemplo C:\\CRU\\backup.bin). Con Import… lo volvés a cargar si algo sale mal."
            }),
            new("3. Copiar el timing nativo de fábrica", new[]
            {
                cta is null
                    ? $"En Detailed resolutions, doble clic en {native.HActive}x{native.VActive} @ {Hz(native.RefreshHz)} → botón Copy → Cancel."
                    : $"Extension blocks → CTA-861 → Edit… → en Detailed resolutions, doble clic en {native.HActive}x{native.VActive} @ {Hz(native.RefreshHz)} → botón Copy → Cancel.",
                $"Si preferís cargarlo a mano: Manual · activo {native.HActive}x{native.VActive} · front porch {native.HFrontPorch}/{native.VFrontPorch} · sync {native.HSyncWidth}/{native.VSyncWidth} · back porch {native.HBackPorch}/{native.VBackPorch} · total {native.HTotal}x{native.VTotal} · pixel clock {Mhz(native.PixelClockHz)} · polaridad H {(native.HSyncPositive ? "+" : "−")} V {(native.VSyncPositive ? "+" : "−")}."
            }),
            new($"4. Reemplazar CTA-861 por DisplayID {version}", CtaLines(cta, version, nativeChoice, stretchedChoice)),
            new($"5. Agregar el nativo y la {res} al bloque DisplayID", new[]
            {
                $"Dentro del bloque DisplayID {version}: Add… → Detailed resolutions → Add… → botón Paste (pega el nativo copiado en el paso 3) → OK.",
                $"Add… otra vez → Timing: Automatic PC · activo {req.StretchedWidth}x{req.StretchedHeight} · refresco {hz:0.000} Hz → OK.",
                $"CRU tiene que mostrar {Hz(stretchedChoice.Chosen.RefreshHz)} (total {stretched.HTotal}x{stretched.VTotal}, pixel clock {Mhz(stretchedChoice.Chosen.PixelClockHz)}). " +
                (stretchedChoice.Type1.IsExact ? "" : $"Con DisplayID 1.3 verías {Hz(stretchedChoice.Type1.RefreshHz)}: por eso va 2.0."),
                "En el bloque base, Detailed resolutions: dejá el nativo; Standard y Established pueden quedar vacíos. OK en todas las ventanas."
            }),
            new("6. Reiniciar la PC", new[]
            {
                "Reiniciá Windows normal (no uses restart64.exe). El driver lee el EDID nuevo al arrancar.",
                "Después, en KRUZH → Pantalla, tocá Verificar."
            })
        };

        steps.Add(req.Gpu switch
        {
            GpuVendor.Nvidia => new GuideStep("7. Panel de control de NVIDIA", new[]
            {
                "Pantalla → Ajustar el tamaño y la posición del escritorio: modo Pantalla completa, Realizar escalado en: Pantalla.",
                "NO marques \"Anular el modo de escalado establecido por juegos y programas\": así el juego hace el cambio de modo directo.",
                $"Cambiar resolución: elegí {native.HActive}x{native.VActive} @ {hz:0} Hz para el escritorio. En el juego, {res} en Pantalla completa."
            }),
            GpuVendor.Amd => new GuideStep("7. AMD Software (Adrenalin)", new[]
            {
                "Pantalla → Escalado de GPU: Desactivado (el monitor escala la 4:3).",
                $"En el juego, {res} en Pantalla completa."
            }),
            _ => new GuideStep("7. Escalado", new[] { "En el panel de tu GPU dejá el escalado en el monitor (no en la GPU).", $"En el juego, {res} en Pantalla completa." })
        });

        steps.Add(new GuideStep("8. Menú del monitor (OSD)", new[]
        {
            $"Con el juego abierto en {res}, el OSD del monitor tiene que decir {res} @ {hz:0} Hz: eso confirma que escala el monitor y no la GPU.",
            "Overdrive: el nivel intermedio del fabricante (el máximo suele generar halos).",
            cta?.FreeSyncRange is { } fs
                ? $"FreeSync / Adaptive-Sync ({fs.MinHz}–{fs.MaxHz} Hz): si jugás con FPS muy por encima de {hz:0}, apagalo en el OSD."
                : "Si tu monitor tiene FreeSync / Adaptive-Sync y jugás con FPS muy por encima del refresco, apagalo en el OSD."
        }));

        steps.Add(new GuideStep("Si algo sale mal", new[]
        {
            "Pantalla negra al iniciar: esperá 15 segundos (Windows vuelve solo a un modo seguro) o conectá por otra entrada/cable.",
            "Desde Windows: ejecutá reset-all.exe de la carpeta de CRU y reiniciá. Vuelve el EDID de fábrica.",
            "Si la placa aparece con Code 45 o en \"Microsoft Basic Display Adapter\": reiniciá (no restart64) y, si sigue, KRUZH → Device Manager → Rescate.",
            "También podés reimportar el backup del paso 2 con Import…"
        }));

        return new CruGuide(name, native, stretched, nativeChoice, stretchedChoice, version, steps);
    }

    private static string[] CtaLines(CtaExtension? cta, string version, DisplayIdRecommendation n, DisplayIdRecommendation s)
    {
        var lines = new List<string>();
        if (cta is not null)
        {
            var loses = new List<string>();
            if (cta.Audio.Count > 0) loses.Add("el audio por el cable (parlantes del monitor)");
            if (cta.HasHdrStaticMetadata) loses.Add("HDR");
            if (cta.FreeSyncRange is not null) loses.Add("FreeSync");
            lines.Add("En Extension blocks seleccioná CTA-861 → Delete. Se van los modos de TV, los InfoFrames de audio y el VSDB: el monitor queda en modo PC puro.");
            if (loses.Count > 0) lines.Add($"Perdés {string.Join(", ", loses)}. Si usás alguno de eso, no borres CTA-861 y agregá la resolución dentro de CTA-861.");
        }
        lines.Add($"Extension blocks → Add… → Type: DisplayID {version}.");
        lines.Add(version == "2.0"
            ? $"Por qué {version}: {s.Reason}"
            : $"Por qué {version}: {n.Reason}");
        return lines.ToArray();
    }

    /// <summary>Botón Verificar: relee el EDID efectivo y el modo actual y chequea que la guía haya quedado aplicada.</summary>
    public static IReadOnlyList<VerificationCheck> Verify(CruGuide guide, MonitorAnalysis now)
    {
        var checks = new List<VerificationCheck>
        {
            new("EDID modificado activo", now.Monitor.HasOverride,
                now.Monitor.HasOverride ? "Windows está usando el EDID de CRU." : "No hay EDID_OVERRIDE: ¿guardaste con OK en CRU y reiniciaste?")
        };

        var edid = now.Parsed;
        var did = edid?.DisplayId.FirstOrDefault();
        checks.Add(new("Bloque DisplayID " + guide.DisplayIdVersion, did is not null && did.Version.StartsWith(guide.DisplayIdVersion[..1], StringComparison.Ordinal),
            did is null ? "No hay bloque DisplayID en el EDID." : $"Encontrado DisplayID {did.Version}."));

        DetailedTiming? Find(int w, int h) => edid?.AllDetailedTimings.FirstOrDefault(t => t.HActive == w && t.VActive == h && Math.Abs(t.RefreshHz - Math.Round(guide.Native.RefreshHz)) < 0.5);
        foreach (var (w, h) in new[] { (guide.Native.HActive, guide.Native.VActive), (guide.Stretched.HActive, guide.Stretched.VActive) })
        {
            var t = Find(w, h);
            double target = Math.Round(guide.Native.RefreshHz);
            checks.Add(new($"{w}x{h} @ {target:0} Hz", t is not null && Math.Abs(t.RefreshHz - target) < 0.0005,
                t is null ? "No está en el EDID." : $"{Hz(t.RefreshHz)} ({(Math.Abs(t.RefreshHz - target) < 0.0005 ? "exacto" : "no exacto")})."));
        }

        if (now.Monitor.Current is { } cur)
        {
            checks.Add(new("Escritorio al máximo", Math.Abs(cur.RefreshHz - Math.Round(guide.Native.RefreshHz)) < 0.5,
                $"Hoy: {cur.SignalWidth}x{cur.SignalHeight} @ {Hz(cur.RefreshHz)}."));
        }
        return checks;
    }

    private static string Hz(double hz) => hz.ToString("0.000", CultureInfo.InvariantCulture) + " Hz";
    private static string Mhz(long hz) => (hz / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " MHz";
}
