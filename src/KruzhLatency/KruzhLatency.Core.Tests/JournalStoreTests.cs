using KruzhLatency.Core.Journal;

namespace KruzhLatency.Core.Tests;

/// <summary>T11: journal v2 atómico con baseline único y recuperación de corrupción.</summary>
public sealed class JournalStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kruzh-journal-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, "journal.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static JournalEntry Entry(string tweak, string value, string? previous, string? applied = "1") => new()
    {
        TweakId = tweak,
        OperationId = JournalEntry.MakeOperationId("RegistryValue", @"HKLM\SOFTWARE\Test", value),
        Kind = "RegistryValue",
        TargetPath = @"HKLM\SOFTWARE\Test",
        ValueName = value,
        PreviousValueExisted = previous is not null,
        PreviousValue = previous,
        AppliedValue = applied
    };

    [Fact]
    public void Baseline_SoloLaPrimeraVez_NoSePisaElOriginal()
    {
        var store = new JournalStore(JournalPath);

        Assert.True(store.RecordBaseline(Entry("T1", "A", previous: "0")));
        Assert.False(store.RecordBaseline(Entry("T1", "A", previous: "1"))); // segundo apply: el "previo" ya es el tweakeado
        Assert.True(store.RecordBaseline(Entry("T1", "B", previous: null)));

        var b = store.Baselines("T1");
        Assert.Equal(2, b.Count);
        Assert.Equal("0", b.Single(e => e.ValueName == "A").PreviousValue);
        Assert.Equal(3, store.History().Count); // el historial sí registra los tres
    }

    [Fact]
    public void MismaOperacionEnOtroTweak_EsOtroBaseline()
    {
        var store = new JournalStore(JournalPath);

        Assert.True(store.RecordBaseline(Entry("T1", "A", "0")));
        Assert.True(store.RecordBaseline(Entry("T2", "A", "0")));
    }

    [Fact]
    public void Persiste_YSeRecargaConSchema2()
    {
        new JournalStore(JournalPath).RecordBaseline(Entry("T1", "A", "0"));

        var reloaded = new JournalStore(JournalPath);

        Assert.Equal(JournalLoadOutcome.Loaded, reloaded.LoadOutcome);
        Assert.Equal("0", Assert.Single(reloaded.Baselines("T1")).PreviousValue);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(JournalPath));
    }

    [Fact]
    public void Atomico_TmpABMedioEscribir_NoRompeElJournal()
    {
        new JournalStore(JournalPath).RecordBaseline(Entry("T1", "A", "0"));
        File.WriteAllText(JournalPath + ".tmp", "{ \"schemaVer"); // corte de luz a mitad de la escritura siguiente

        var reloaded = new JournalStore(JournalPath);

        Assert.Equal(JournalLoadOutcome.Loaded, reloaded.LoadOutcome);
        Assert.Single(reloaded.Baselines("T1"));
        reloaded.RecordBaseline(Entry("T1", "B", "5")); // y se puede seguir escribiendo encima del .tmp viejo
        Assert.Equal(2, new JournalStore(JournalPath).Baselines("T1").Count);
    }

    [Fact]
    public void Corrupto_UsaElBak()
    {
        var store = new JournalStore(JournalPath);
        store.RecordBaseline(Entry("T1", "A", "0"));
        store.RecordBaseline(Entry("T1", "B", "7")); // la segunda escritura deja la primera como .bak
        File.WriteAllText(JournalPath, "basura{{{");

        var reloaded = new JournalStore(JournalPath);

        Assert.Equal(JournalLoadOutcome.RecoveredFromBackup, reloaded.LoadOutcome);
        Assert.Equal("0", Assert.Single(reloaded.Baselines("T1")).PreviousValue);
    }

    [Fact]
    public void AmbosCorruptos_ArrancaVacio_YGuardaEvidencia()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(JournalPath, "basura");
        File.WriteAllText(JournalPath + ".bak", "más basura");

        var store = new JournalStore(JournalPath);

        Assert.Equal(JournalLoadOutcome.CorruptStartedEmpty, store.LoadOutcome);
        Assert.Empty(store.AllBaselines());
        Assert.Single(Directory.GetFiles(_dir, "journal.json.corrupt-*"));
    }

    [Fact]
    public void RemoveEntries_YRemoveTweak()
    {
        var store = new JournalStore(JournalPath);
        store.RecordBaseline(Entry("T1", "A", "0"));
        store.RecordBaseline(Entry("T1", "B", "0"));
        store.RecordBaseline(Entry("T2", "A", "0"));

        Assert.Equal(1, store.RemoveEntries(e => e.TweakId == "T1" && e.ValueName == "A"));
        store.RemoveTweak("T2");

        var reloaded = new JournalStore(JournalPath);
        Assert.Equal("B", Assert.Single(reloaded.Baselines("T1")).ValueName);
        Assert.False(reloaded.HasBaseline("T2"));
    }

    [Fact]
    public void Import_RespetaUnicidad()
    {
        var store = new JournalStore(JournalPath);
        store.RecordBaseline(Entry("T1", "A", "0"));

        int added = store.Import(new[] { Entry("T1", "A", "9"), Entry("T3", "Z", "1") }, Array.Empty<JournalEntry>());

        Assert.Equal(1, added);
        Assert.Equal("0", store.Baselines("T1").Single().PreviousValue);
    }
}
