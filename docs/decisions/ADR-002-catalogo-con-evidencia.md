# ADR-002 — Solo entran al catálogo tweaks con mecanismo documentado y efecto medible

Fecha: 2026-11-02 · Estado: PROPUESTA · Enmendada por ADR-011 (sección "Laboratorio" para ítems sin evidencia, nunca en perfiles)

## Contexto
El catálogo v3 mezcla tweaks reales con placebo (Nagle para un juego UDP, MarkC con EPP apagado, claves NVIDIA no documentadas). Para un producto pago eso es un riesgo reputacional y, a veces, de estabilidad.

## Decisión
Un tweak entra al catálogo solo si:
1. el mecanismo está documentado (Microsoft Docs, documentación del vendor, o fuente técnica reproducible), **y**
2. tiene un efecto plausible en CS2 (UDP, DX11/Vulkan, Reflex), **y**
3. todas sus operaciones son reversibles con backup exacto.

Cada `TweakDefinition` lleva `Source` y `Why`. Los marcados ❌ en `docs/AUDITORIA.md §3` se eliminan. Los 🟡 quedan fuera de los presets Seguro/Competitivo hasta que una medición A/B los justifique.

## Consecuencias
- + Menos tweaks, pero cada uno defendible.
- + La medición A/B (spec 001 §7) se convierte en la herramienta para promover 🟡 → ✅.
- − Menos "cantidad" para marketing.
