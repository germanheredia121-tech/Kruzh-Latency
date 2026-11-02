using System.Reflection;
using KruzhLatency.Core;

namespace KruzhLatency.Core.Tests;

/// <summary>Reglas de arquitectura de CLAUDE.md / ADR-001: Core no depende de Windows ni de UI.</summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    {
        "Microsoft.WinUI",
        "Microsoft.UI",
        "Microsoft.Windows.SDK",
        "Microsoft.Win32.Registry",
        "System.ServiceProcess",
        "System.Management",
        "KruzhLatency.Infra",
        "KruzhLatency",            // la app (nombre exacto)
    };

    [Fact]
    public void Core_NoReferenciaEnsambladosDeWindowsNiDeUi()
    {
        var referenced = CoreAssembly.Reference.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        var violations = referenced
            .Where(name => ForbiddenAssemblyPrefixes.Any(p =>
                p == "KruzhLatency" ? name == p : name.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Core_NoUsaTiposDeRegistroNiProcesos()
    {
        // Microsoft.Win32.Registry y System.Diagnostics.Process viven en ensamblados compartidos en net8.0,
        // asi que se controla por tipos referenciados en las firmas publicas/miembros del Core.
        var forbiddenTypes = new[] { "Microsoft.Win32.Registry", "Microsoft.Win32.RegistryKey", "System.Diagnostics.Process" };

        var usedTypes = CoreAssembly.Reference.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => m switch
            {
                FieldInfo f => new[] { f.FieldType },
                PropertyInfo p => new[] { p.PropertyType },
                MethodInfo mi => mi.GetParameters().Select(x => x.ParameterType).Append(mi.ReturnType),
                _ => Array.Empty<Type>()
            })
            .Select(t => t.FullName ?? string.Empty);

        Assert.DoesNotContain(usedTypes, n => forbiddenTypes.Contains(n));
    }
}
