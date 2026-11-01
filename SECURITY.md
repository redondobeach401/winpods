# Política de seguridad

## Versiones soportadas

Solo la última versión de `main` recibe correcciones de seguridad.

## Informar de una vulnerabilidad

**No abras un issue público.** Usa *Security › Report a vulnerability* en GitHub
(avisos privados de seguridad). Incluye:

- Descripción y componente afectado (app de Windows, agente de macOS, protocolo de handoff…).
- Pasos para reproducirlo y su impacto.

Intentaremos responder en un plazo de 7 días.

## Superficie de ataque relevante

- **Protocolo de handoff** (TCP 47821 en la red local): autenticado con HMAC-SHA256 sobre un
  código de enlace compartido, con protección contra repetición y desfase de reloj. El tráfico
  no va cifrado. Ver [docs/handoff-protocol.md](docs/handoff-protocol.md).
- **Código de enlace**: en Windows se guarda cifrado con DPAPI (por usuario); en macOS en
  `~/Library/Application Support/WinPods/agent.json` (protégelo con permisos de usuario).
- La app de Windows no requiere privilegios de administrador.
