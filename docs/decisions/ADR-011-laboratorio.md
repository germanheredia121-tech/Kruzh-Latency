# ADR-011 — Sección "Laboratorio" para tweaks sin evidencia (enmienda ADR-002)

Fecha: 2026-11-04 · Estado: ACEPTADA (decisión del autor)

## Contexto
ADR-002 solo admite en el catálogo tweaks con mecanismo documentado y efecto plausible. El autor quiere poder probar también
los tweaks populares sin evidencia (los de los chats 1–5 y guías de la comunidad), aunque resulten placebo, midiéndolos en
su propia PC. La medición A/B de `docs/AUDITORIA.md` §6 mostró que meter ítems sin evidencia en los perfiles baja el
rendimiento sin que el usuario se entere.

## Decisión
- Los tweaks sin evidencia entran en una sección **"Laboratorio (sin evidencia)"** dentro de su categoría.
- Un ítem de Laboratorio:
  - **nunca** está en ningún perfil (Normal, Competitivo, Profesional ni Entusiasta): se aplica solo a mano;
  - dice en la descripción que no tiene evidencia y que hay que medir antes y después (benchmark + PresentMon);
  - cumple igual el resto de ADR-002: `Source` (de dónde salió), backup en el journal y revert exacto;
  - si baja la seguridad, se marca `ReducesSecurity` (modal rojo al aplicar) y el texto dice qué protección se pierde.
- Un ítem sale de Laboratorio hacia el catálogo normal solo con una medición A/B registrada en `docs/AUDITORIA.md`.
- No entra a Laboratorio lo que contradice ADR-004 (tocar archivos o memoria de los juegos) ni lo que deja la PC sin arrancar
  sin forma de revertir.

## Consecuencias
- + El autor puede probar todo lo que circula sin ensuciar los perfiles.
- + Lo que se mide queda documentado y puede promoverse o descartarse.
- − Más ítems en la UI: Laboratorio va al final de cada categoría.
