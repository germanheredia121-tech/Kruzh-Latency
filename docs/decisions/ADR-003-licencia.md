# ADR-003 — Sacar licencia y anti-debug del MVP

Fecha: 2026-11-02 · Estado: PROPUESTA (decisión del dueño del producto)

## Contexto
La licencia actual no protege nada: cualquier clave `KRUZH-…` (≥14 chars) activa, al primer arranque se emite un token maestro automático, y la clave HMAC está embebida en el binario. El anti-debug (`IsDebuggerPresent`, `NtQueryInformationProcess`) no frena a nadie y aumenta falsos positivos de antivirus en una app que ya corre como admin y toca el registro.

## Decisión
- MVP sin licencia ni anti-debug. Se elimina `AntiTamperAndLicenseService` y el endpoint `api.kruzhlatency.com`.
- Si se vende: licencias firmadas con **Ed25519** emitidas por un backend (o un proveedor tipo Keygen / Lemon Squeezy / Paddle), la app solo tiene la **clave pública**. Spec propia (`specs/00X-licencias.md`) cuando se decida.
- Code signing (Authenticode) sí, desde el primer release público: reduce SmartScreen/antivirus.

## Consecuencias
- + Menos código, menos falsos positivos, foco en lo que hace el producto.
- − Sin monetización hasta implementar la spec de licencias.
