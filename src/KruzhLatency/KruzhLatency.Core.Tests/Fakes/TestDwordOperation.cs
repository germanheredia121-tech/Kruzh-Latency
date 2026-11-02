using System.Globalization;
using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Tests.Fakes;

/// <summary>
/// Operación mínima de prueba (DWORD en HKLM) para ejercitar el contrato de <see cref="Operation"/>.
/// La implementación real de producción llega en T21 (RegistryValueOp).
/// </summary>
public sealed class TestDwordOperation : Operation
{
    private readonly string _subKey;
    private readonly string _name;
    private readonly uint _target;
    private readonly bool _onlyIfKeyExists;

    public TestDwordOperation(string subKey, string name, uint target, bool onlyIfKeyExists = false)
    {
        _subKey = subKey;
        _name = name;
        _target = target;
        _onlyIfKeyExists = onlyIfKeyExists;
    }

    public override string Id => $@"reg:HKLM\{_subKey}\{_name}";
    public override string Kind => "test-dword";
    public override string Describe() => $@"HKLM\{_subKey} → {_name} = {_target}";

    public override OpState Read(ISystemContext sys)
    {
        if (_onlyIfKeyExists && !sys.Registry.KeyExists(RegHive.LocalMachine, _subKey)) return OpState.NotPresent;
        var v = sys.Registry.GetValue(RegHive.LocalMachine, _subKey, _name);
        return v is { Kind: RegKind.DWord } && v.AsDWord() == _target ? OpState.Target : OpState.NotTarget;
    }

    public override OperationSnapshot Capture(ISystemContext sys)
    {
        var v = sys.Registry.GetValue(RegHive.LocalMachine, _subKey, _name);
        return new OperationSnapshot(Id, Kind, v is not null, v?.AsDWord().ToString(CultureInfo.InvariantCulture));
    }

    public override void Apply(ISystemContext sys)
    {
        if (Read(sys) == OpState.NotPresent) return;
        sys.Registry.SetValue(RegHive.LocalMachine, _subKey, _name, RegValue.DWord(_target));
    }

    public override void Restore(ISystemContext sys, OperationSnapshot snapshot)
    {
        EnsureSnapshotIsMine(snapshot);
        if (!snapshot.Existed)
        {
            sys.Registry.DeleteValue(RegHive.LocalMachine, _subKey, _name);
            return;
        }
        sys.Registry.SetValue(RegHive.LocalMachine, _subKey, _name,
            RegValue.DWord(uint.Parse(snapshot.Payload!, CultureInfo.InvariantCulture)));
    }
}
