# Contribuir a WinPods

¡Gracias por ayudar!

## Entorno

- **Núcleo y tests** (cualquier SO): .NET 8 SDK → `dotnet test tests/WinPods.Core.Tests`
- **App de Windows**: Windows 10 2004+ con .NET 8 SDK → `dotnet run --project src/WinPods.App`
- **Agente de macOS**: macOS 12+ con Xcode Command Line Tools → `cd mac/WinPodsAgent && swift build`

## Normas

- Ramas desde `main` y pull request; el CI (Windows + macOS) debe pasar.
- La lógica que no dependa de la plataforma va en `WinPods.Core`, con tests.
- Si cambias el protocolo de handoff, actualiza **a la vez** la versión C#, la Swift,
  `docs/handoff-protocol.md` y el vector de prueba.
- Commits en presente y descriptivos (p. ej. «Añade control de volumen adaptativo»).
- Sigue `.editorconfig`.

## Informar de errores

Usa las plantillas de issues. Para problemas de seguridad, mira [SECURITY.md](SECURITY.md).
