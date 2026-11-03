using System.Text;

namespace KruzhLatency.Core.Display;

/// <summary>
/// Decodifica un EDID completo (VESA E-EDID 1.3/1.4, CTA-861 y DisplayID 1.x/2.0). Función pura: recibe los bytes
/// que Windows guarda en Enum\DISPLAY\…\Device Parameters\EDID (o EDID_OVERRIDE) y devuelve el modelo.
/// </summary>
public static class EdidParser
{
    private static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    public static Edid Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 128 || !raw[..8].SequenceEqual(Header))
        {
            throw new InvalidDataException("No es un EDID (falta el encabezado 00 FF FF FF FF FF FF 00).");
        }

        var baseBlock = ParseBase(raw[..128]);
        var cta = new List<CtaExtension>();
        var displayId = new List<DisplayIdExtension>();
        var other = new List<UnknownExtension>();

        for (int i = 1; (i + 1) * 128 <= raw.Length; i++)
        {
            var block = raw.Slice(i * 128, 128);
            switch (block[0])
            {
                case 0x02: cta.Add(ParseCta(block)); break;
                case 0x70: displayId.Add(ParseDisplayId(block)); break;
                default: other.Add(new UnknownExtension(block[0], Checksum(block))); break;
            }
        }

        return new Edid(raw.ToArray(), baseBlock, cta, displayId, other);
    }

    public static bool Checksum(ReadOnlySpan<byte> block)
    {
        int sum = 0;
        foreach (byte b in block) sum += b;
        return sum % 256 == 0;
    }

    // ================================================================ bloque base

    private static EdidBaseBlock ParseBase(ReadOnlySpan<byte> b)
    {
        int m = (b[8] << 8) | b[9];
        string manufacturer = new(new[] { (char)('A' - 1 + ((m >> 10) & 0x1F)), (char)('A' - 1 + ((m >> 5) & 0x1F)), (char)('A' - 1 + (m & 0x1F)) });

        bool digital = (b[20] & 0x80) != 0;
        bool v14 = b[18] == 1 && b[19] >= 4;
        int? bits = null;
        string? iface = null;
        if (digital && v14)
        {
            bits = ((b[20] >> 4) & 0x07) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => null };
            iface = (b[20] & 0x0F) switch { 1 => "DVI", 2 => "HDMI-a", 3 => "HDMI-b", 4 => "MDDI", 5 => "DisplayPort", _ => null };
        }

        var detailed = new List<DetailedTiming>();
        string? name = null, serialText = null;
        var texts = new List<string>();
        RangeLimits? range = null;
        for (int d = 54; d <= 108; d += 18)
        {
            var desc = b.Slice(d, 18);
            if (desc[0] != 0 || desc[1] != 0)
            {
                detailed.Add(ParseDtd(desc, preferred: d == 54));
                continue;
            }
            switch (desc[3])
            {
                case 0xFC: name = DescriptorText(desc); break;
                case 0xFF: serialText = DescriptorText(desc); break;
                case 0xFE: texts.Add(DescriptorText(desc)); break;
                case 0xFD: range = ParseRange(desc); break;
            }
        }

        return new EdidBaseBlock(
            manufacturer,
            (ushort)(b[10] | (b[11] << 8)),
            (uint)(b[12] | (b[13] << 8) | (b[14] << 16) | (b[15] << 24)),
            b[16],
            b[17] + 1990,
            $"{b[18]}.{b[19]}",
            digital,
            bits,
            iface,
            b[21],
            b[22],
            b[23] == 0xFF ? null : (b[23] + 100) / 100.0,
            EstablishedTimings(b[35], b[36], b[37]),
            StandardTimings(b.Slice(38, 16), v14 || b[19] >= 3),
            detailed,
            name,
            serialText,
            texts,
            range,
            b[126],
            Checksum(b));
    }

    private static string DescriptorText(ReadOnlySpan<byte> desc)
    {
        var s = Encoding.ASCII.GetString(desc.Slice(5, 13));
        int nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    private static RangeLimits ParseRange(ReadOnlySpan<byte> d)
    {
        byte offsets = d[4]; // EDID 1.4: +255 a los mínimos/máximos
        int minV = d[5] + ((offsets & 0x01) != 0 && (offsets & 0x02) != 0 ? 255 : 0);
        int maxV = d[6] + ((offsets & 0x02) != 0 ? 255 : 0);
        int minH = d[7] + ((offsets & 0x04) != 0 && (offsets & 0x08) != 0 ? 255 : 0);
        int maxH = d[8] + ((offsets & 0x08) != 0 ? 255 : 0);
        return new RangeLimits(minV, maxV, minH, maxH, d[9] * 10);
    }

    private static readonly string[] Established =
    {
        "720x400 @ 70 Hz", "720x400 @ 88 Hz", "640x480 @ 60 Hz", "640x480 @ 67 Hz", "640x480 @ 72 Hz", "640x480 @ 75 Hz", "800x600 @ 56 Hz", "800x600 @ 60 Hz",
        "800x600 @ 72 Hz", "800x600 @ 75 Hz", "832x624 @ 75 Hz", "1024x768i @ 87 Hz", "1024x768 @ 60 Hz", "1024x768 @ 70 Hz", "1024x768 @ 75 Hz", "1280x1024 @ 75 Hz",
        "1152x870 @ 75 Hz"
    };

    private static List<string> EstablishedTimings(byte a, byte b, byte c)
    {
        var list = new List<string>();
        int bits = (a << 16) | (b << 8) | c;
        for (int i = 0; i < Established.Length; i++)
        {
            if ((bits & (1 << (23 - i))) != 0) list.Add(Established[i]);
        }
        return list;
    }

    private static List<StandardTiming> StandardTimings(ReadOnlySpan<byte> s, bool aspect1610)
    {
        var list = new List<StandardTiming>();
        for (int i = 0; i < 16; i += 2)
        {
            if ((s[i] == 0x01 && s[i + 1] == 0x01) || s[i] == 0x00) continue;
            int width = (s[i] + 31) * 8;
            int height = (s[i + 1] >> 6) switch
            {
                0 => aspect1610 ? width * 10 / 16 : width,
                1 => width * 3 / 4,
                2 => width * 4 / 5,
                _ => width * 9 / 16
            };
            list.Add(new StandardTiming(width, height, (s[i + 1] & 0x3F) + 60));
        }
        return list;
    }

    /// <summary>Detailed Timing Descriptor de 18 bytes (pixel clock en pasos de 10 kHz).</summary>
    public static DetailedTiming ParseDtd(ReadOnlySpan<byte> d, bool preferred = false)
    {
        long pclk = (d[0] | (d[1] << 8)) * 10_000L;
        int hActive = d[2] | ((d[4] & 0xF0) << 4);
        int hBlank = d[3] | ((d[4] & 0x0F) << 8);
        int vActive = d[5] | ((d[7] & 0xF0) << 4);
        int vBlank = d[6] | ((d[7] & 0x0F) << 8);
        int hFront = d[8] | ((d[11] & 0xC0) << 2);
        int hSync = d[9] | ((d[11] & 0x30) << 4);
        int vFront = (d[10] >> 4) | ((d[11] & 0x0C) << 2);
        int vSync = (d[10] & 0x0F) | ((d[11] & 0x03) << 4);
        byte flags = d[17];
        bool digitalSeparate = ((flags >> 3) & 0x03) == 0x03;
        return new DetailedTiming(TimingSource.DetailedDescriptor, pclk,
            hActive, hBlank, hFront, hSync, vActive, vBlank, vFront, vSync,
            Interlaced: (flags & 0x80) != 0,
            HSyncPositive: digitalSeparate && (flags & 0x02) != 0,
            VSyncPositive: digitalSeparate && (flags & 0x04) != 0,
            IsPreferred: preferred);
    }

    // ================================================================ CTA-861

    private static CtaExtension ParseCta(ReadOnlySpan<byte> e)
    {
        int dtdOffset = e[2];
        byte flags = e[3];
        var blocks = new List<CtaDataBlock>();
        var video = new List<SupportedVideoMode>();
        var audio = new List<AudioFormat>();
        (int, int)? freeSync = null;
        bool hdmi = false, hdr = false;

        int end = dtdOffset is >= 4 and <= 127 ? dtdOffset : 4;
        for (int p = 4; p < end;)
        {
            int tag = e[p] >> 5;
            int len = e[p] & 0x1F;
            if (p + 1 + len > end) break;
            var payload = e.Slice(p + 1, len).ToArray();
            p += 1 + len;

            switch (tag)
            {
                case 1:
                    for (int i = 0; i + 2 < len; i += 3) audio.Add(ParseSad(payload.AsSpan(i, 3)));
                    blocks.Add(new CtaDataBlock(tag, "Audio", string.Join(", ", audio.Select(a => $"{a.Format} {a.MaxChannels}ch")), payload));
                    break;
                case 2:
                    foreach (byte svd in payload)
                    {
                        bool native = svd is >= 129 and <= 192;
                        int vic = native ? svd & 0x7F : svd;
                        video.Add(new SupportedVideoMode(vic, native, VicName(vic)));
                    }
                    blocks.Add(new CtaDataBlock(tag, "Video", string.Join(", ", video.Select(v => v.Description + (v.Native ? " (nativo)" : ""))), payload));
                    break;
                case 3:
                    int oui = len >= 3 ? payload[0] | (payload[1] << 8) | (payload[2] << 16) : 0;
                    string vsdb = oui switch { 0x000C03 => "HDMI 1.x", 0xC45DD8 => "HDMI Forum", 0x00001A => "AMD FreeSync", 0x00044B => "NVIDIA", _ => $"OUI {oui:X6}" };
                    if (oui == 0x000C03) hdmi = true;
                    string summary = vsdb;
                    if (oui == 0x00001A && len >= 7)
                    {
                        freeSync = (payload[5], payload[6]);
                        summary = $"AMD FreeSync {payload[5]}–{payload[6]} Hz";
                    }
                    blocks.Add(new CtaDataBlock(tag, "Vendor-Specific", summary, payload));
                    break;
                case 4:
                    blocks.Add(new CtaDataBlock(tag, "Speaker Allocation", len > 0 ? SpeakerSummary(payload[0]) : "", payload));
                    break;
                case 5:
                    blocks.Add(new CtaDataBlock(tag, "VESA Display Transfer", "", payload));
                    break;
                case 7:
                    int ext = len > 0 ? payload[0] : -1;
                    if (ext == 6) hdr = true;
                    blocks.Add(new CtaDataBlock(tag, "Extended: " + ExtendedName(ext), ext == 6 ? HdrSummary(payload) : "", payload));
                    break;
                default:
                    blocks.Add(new CtaDataBlock(tag, $"Tag {tag}", "", payload));
                    break;
            }
        }

        var dtds = new List<DetailedTiming>();
        for (int d = dtdOffset; dtdOffset >= 4 && d + 18 <= 127; d += 18)
        {
            if (e[d] == 0 && e[d + 1] == 0) break;
            dtds.Add(ParseDtd(e.Slice(d, 18)));
        }

        return new CtaExtension(e[1], (flags & 0x80) != 0, (flags & 0x40) != 0, (flags & 0x20) != 0, (flags & 0x10) != 0,
            blocks, video, audio, dtds, freeSync, hdmi, hdr, Checksum(e));
    }

    private static AudioFormat ParseSad(ReadOnlySpan<byte> s)
    {
        int code = (s[0] >> 3) & 0x0F;
        string format = code switch
        {
            1 => "LPCM", 2 => "AC-3", 3 => "MPEG-1", 4 => "MP3", 5 => "MPEG-2", 6 => "AAC", 7 => "DTS", 8 => "ATRAC",
            9 => "DSD", 10 => "E-AC-3", 11 => "DTS-HD", 12 => "Dolby TrueHD", 13 => "DST", 14 => "WMA Pro", _ => $"Formato {code}"
        };
        int[] rates = { 32, 44, 48, 88, 96, 176, 192 };
        byte mask = s[1];
        var supported = rates.Where((_, i) => (mask & (1 << i)) != 0).ToList();
        return new AudioFormat(format, (s[0] & 0x07) + 1, supported);
    }

    private static string SpeakerSummary(byte b)
    {
        var parts = new List<string>();
        if ((b & 0x01) != 0) parts.Add("FL/FR");
        if ((b & 0x02) != 0) parts.Add("LFE");
        if ((b & 0x04) != 0) parts.Add("FC");
        if ((b & 0x08) != 0) parts.Add("RL/RR");
        if ((b & 0x10) != 0) parts.Add("RC");
        return parts.Count == 0 ? "sin parlantes declarados" : string.Join(", ", parts);
    }

    private static string ExtendedName(int ext) => ext switch
    {
        0 => "Video Capability", 1 => "Vendor-Specific Video", 2 => "VESA Display Device", 5 => "Colorimetry",
        6 => "HDR Static Metadata", 7 => "HDR Dynamic Metadata", 13 => "Video Format Preference", 14 => "YCbCr 4:2:0 Video",
        15 => "YCbCr 4:2:0 Capability Map", 18 => "HDMI Audio", 19 => "Room Configuration", 20 => "Speaker Location",
        32 => "InfoFrame", 120 => "HDMI Forum EEODB", 121 => "HDMI Forum SCDB", _ => $"tag extendido {ext}"
    };

    private static string HdrSummary(byte[] p)
    {
        if (p.Length < 2) return "";
        var eotf = new List<string>();
        if ((p[1] & 0x01) != 0) eotf.Add("SDR");
        if ((p[1] & 0x02) != 0) eotf.Add("HDR tradicional");
        if ((p[1] & 0x04) != 0) eotf.Add("HDR10 (PQ)");
        if ((p[1] & 0x08) != 0) eotf.Add("HLG");
        return string.Join(", ", eotf);
    }

    /// <summary>Nombres de los VIC más comunes (CTA-861); el resto se muestra como "VIC n".</summary>
    private static string VicName(int vic) => vic switch
    {
        1 => "640x480p 60", 2 or 3 => "720x480p 60", 4 => "1280x720p 60", 5 => "1920x1080i 60", 6 or 7 => "720x480i 60",
        16 => "1920x1080p 60", 17 or 18 => "720x576p 50", 19 => "1280x720p 50", 20 => "1920x1080i 50", 21 or 22 => "720x576i 50",
        31 => "1920x1080p 50", 32 => "1920x1080p 24", 33 => "1920x1080p 25", 34 => "1920x1080p 30",
        47 => "1280x720p 120", 63 => "1920x1080p 120", 64 => "1920x1080p 100",
        93 => "3840x2160p 24", 94 => "3840x2160p 25", 95 => "3840x2160p 30", 96 => "3840x2160p 50", 97 => "3840x2160p 60",
        117 => "3840x2160p 100", 118 => "3840x2160p 120",
        _ => $"VIC {vic}"
    };

    // ================================================================ DisplayID 1.x / 2.0

    private static DisplayIdExtension ParseDisplayId(ReadOnlySpan<byte> e)
    {
        // e[0] = 0x70 (tag de extensión EDID); la sección DisplayID empieza en e[1].
        byte ver = e[1];
        string version = ver >= 0x20 ? $"{ver >> 4}.{ver & 0x0F}" : $"1.{ver & 0x0F}";
        bool v2 = ver >= 0x20;
        int sectionLen = e[2];
        var blocks = new List<DisplayIdDataBlock>();
        var timings = new List<DetailedTiming>();

        int p = 5;
        int end = Math.Min(5 + sectionLen, 127);
        while (p + 3 <= end)
        {
            int tag = e[p], rev = e[p + 1], len = e[p + 2];
            if (tag == 0 && len == 0) break; // relleno
            if (p + 3 + len > end) break;
            var payload = e.Slice(p + 3, len).ToArray();
            blocks.Add(new DisplayIdDataBlock(tag, DisplayIdBlockName(tag), rev, payload));

            if (tag is 0x03 or 0x22)
            {
                var source = tag == 0x22 ? TimingSource.DisplayIdType7 : TimingSource.DisplayIdType1;
                for (int i = 0; i + 20 <= len; i += 20) timings.Add(ParseDisplayIdTiming(payload.AsSpan(i, 20), source));
            }
            p += 3 + len;
        }

        return new DisplayIdExtension(version, blocks, timings, Checksum(e));
    }

    /// <summary>Descriptor de 20 bytes de DisplayID Type I (10 kHz) o Type VII (1 kHz). Todos los campos van "+1".</summary>
    public static DetailedTiming ParseDisplayIdTiming(ReadOnlySpan<byte> t, TimingSource source)
    {
        long raw = t[0] | (t[1] << 8) | (t[2] << 16);
        long pclk = (raw + 1) * (source == TimingSource.DisplayIdType7 ? 1_000L : 10_000L);
        int hFrontRaw = U16(t, 8), vFrontRaw = U16(t, 16);
        return new DetailedTiming(source, pclk,
            HActive: U16(t, 4) + 1, HBlank: U16(t, 6) + 1, HFrontPorch: (hFrontRaw & 0x7FFF) + 1, HSyncWidth: U16(t, 10) + 1,
            VActive: U16(t, 12) + 1, VBlank: U16(t, 14) + 1, VFrontPorch: (vFrontRaw & 0x7FFF) + 1, VSyncWidth: U16(t, 18) + 1,
            Interlaced: (t[3] & 0x10) != 0,
            HSyncPositive: (hFrontRaw & 0x8000) != 0,
            VSyncPositive: (vFrontRaw & 0x8000) != 0,
            IsPreferred: (t[3] & 0x80) != 0);
    }

    private static int U16(ReadOnlySpan<byte> t, int o) => t[o] | (t[o + 1] << 8);

    private static string DisplayIdBlockName(int tag) => tag switch
    {
        0x00 => "Product Identification", 0x01 => "Display Parameters", 0x02 => "Color Characteristics",
        0x03 => "Type I Detailed Timing", 0x04 => "Type II Detailed Timing", 0x05 => "Type III Short Timing",
        0x06 => "Type IV Short Timing", 0x07 => "VESA Timing", 0x08 => "CEA Timing", 0x09 => "Video Timing Range",
        0x0A => "Product Serial", 0x0B => "ASCII String", 0x0C => "Display Device Data", 0x0D => "Interface Power Sequencing",
        0x0E => "Transfer Characteristics", 0x0F => "Display Interface", 0x10 => "Stereo Display Interface",
        0x12 => "Tiled Display Topology", 0x20 => "Product Identification (2.0)", 0x21 => "Display Parameters (2.0)",
        0x22 => "Type VII Detailed Timing", 0x23 => "Type VIII Enumerated Timing", 0x24 => "Type IX Formula Timing",
        0x25 => "Dynamic Video Timing Range", 0x26 => "Display Interface Features", 0x27 => "Stereo Display Interface (2.0)",
        0x28 => "Tiled Display Topology (2.0)", 0x29 => "Container ID", 0x7E => "Vendor-Specific (2.0)", 0x7F => "Vendor-Specific",
        0x81 => "CTA-861 DisplayID", _ => $"Bloque 0x{tag:X2}"
    };
}
