namespace KruzhLatency.Core;

/// <summary>Marcador para referenciar el ensamblado Core desde tests y reflexion.</summary>
public static class CoreAssembly
{
    public static System.Reflection.Assembly Reference => typeof(CoreAssembly).Assembly;
}
