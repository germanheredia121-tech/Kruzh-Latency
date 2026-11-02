# KRUZH LATENCY

Optimizador de latencia de sistema para CS2 y VALORANT, compatible con FACEIT AC y Riot Vanguard (perfil Profesional).
Windows 10/11 · .NET 8 · WinUI 3.

- Spec: [specs/001-kruzh-latency-mvp.md](specs/001-kruzh-latency-mvp.md)
- Plan técnico: [docs/PLAN.md](docs/PLAN.md)
- Tareas: [tasks.md](tasks.md)
- Auditoría v3: [docs/AUDITORIA.md](docs/AUDITORIA.md)

## Build
```
cd src/KruzhLatency
dotnet build KruzhLatency.sln -c Debug
dotnet test KruzhLatency.sln
```

## Créditos / fuentes
Investigación basada en PC-Tuning (valleyofdoom), BlurBusters, Calypto y otros (ver `research/`).
Los planes `.pow` de `research/powerplans/` son de terceros y no se redistribuyen hasta verificar su licencia.
