using System.Xml.Linq;
using KruzhLatency.Core.Latency;

namespace KruzhLatency.Core.Tests;

public class TimerResidentPolicyTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Theory]
    [InlineData("cs2", true)]
    [InlineData("CS2", true)]
    [InlineData("VALORANT-Win64-Shipping", true)]
    [InlineData("VALORANT", false)] // el launcher no es el juego
    [InlineData("csgo", false)]
    public void SoloLosJuegos(string name, bool expected) => Assert.Equal(expected, TimerResidentPolicy.IsGame(name));

    [Fact]
    public void Resolucion_MedioMilisegundo() => Assert.Equal(5000u, TimerResidentPolicy.ResolutionHundredNs);

    [Fact]
    public void TareaXml_ValidaSinElevarYConRutaEscapada()
    {
        string path = @"C:\Program Files\KRUZH & Co\KruzhLatency.TimerAgent.exe";
        var doc = XDocument.Parse(TimerResidentPolicy.TaskXml(path, @"PC\usuario"));

        Assert.Equal("LeastPrivilege", doc.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal(@"PC\usuario", doc.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("PT0S", doc.Descendants(Ns + "ExecutionTimeLimit").Single().Value); // sin límite de tiempo
        Assert.Equal("false", doc.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal($"\"{path}\"", doc.Descendants(Ns + "Command").Single().Value);
    }
}
