using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KruzhLatency.Core.Tests;

/// <summary>
/// Los fixtures son datos reales de la PC del autor (A3). El repo es público: estos tests garantizan que siguen
/// íntegros y anonimizados (sin seriales ni MACs) si alguien los vuelve a exportar.
/// </summary>
public class FixtureIntegrityTests
{
    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void EdidGs25f2_256Bytes_ChecksumsValidos_YSerialAnonimizado()
    {
        var edid = Convert.FromBase64String(File.ReadAllText(PathOf("edid-gs25f2-factory.b64")));

        Assert.Equal(256, edid.Length);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }, edid[..8]);
        Assert.Equal(0, edid.Take(128).Sum(b => b) % 256);
        Assert.Equal(0, edid.Skip(128).Sum(b => b) % 256);
        Assert.Equal(0x02, edid[128]); // extensión CTA-861 de fábrica

        // Descriptores de 18 bytes: el 0xFF (serial) tiene que estar en ceros ASCII.
        foreach (int o in new[] { 54, 72, 90, 108 })
        {
            if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 3] == 0xFF)
            {
                Assert.Matches("^0+$", Encoding.ASCII.GetString(edid, o + 5, 13).TrimEnd('\n', ' '));
            }
        }
    }

    [Theory]
    [InlineData("ndi-params-rtl8125.json", "DEV_8125")]
    [InlineData("ndi-params-rtl8111.json", "DEV_8168")]
    public void NdiParams_Realtek_TieneKeywordsYSinMac(string file, string device)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PathOf(file)));
        var root = doc.RootElement;

        Assert.Contains(device, root.GetProperty("componentId").GetString());
        var keywords = root.GetProperty("params").EnumerateArray().ToDictionary(p => p.GetProperty("keyword").GetString()!);
        Assert.True(keywords.ContainsKey("*SpeedDuplex"));
        Assert.True(keywords.ContainsKey("*InterruptModeration"));
        Assert.True(keywords["*SpeedDuplex"].TryGetProperty("enum", out _));
        Assert.False(keywords["NetworkAddress"].TryGetProperty("current", out _));
    }

    [Theory]
    [InlineData("devices-author-5600x-rtx2060s.json")]
    [InlineData("ndi-params-rtl8125.json")]
    [InlineData("ndi-params-rtl8111.json")]
    public void Fixtures_SinMacsNiSerialesUsb(string file)
    {
        var text = File.ReadAllText(PathOf(file));

        // MAC suelta (12 hex) que no sea el final de un GUID ni un hash ya anonimizado.
        Assert.DoesNotMatch(new Regex("(?<![0-9A-Fa-f-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])"), text);
        // Instancias USB con serial: USB\VID_xxxx&PID_xxxx\<serial sin &> → solo se aceptan anonimizadas.
        foreach (Match m in Regex.Matches(text, @"USB(?:STOR)?\\\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\\\([^""\\&]+)""", RegexOptions.IgnoreCase))
        {
            Assert.StartsWith("ANON", m.Groups[1].Value);
        }
    }
}
