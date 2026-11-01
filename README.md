# WinPods

App para usar **AirPods Pro 2** (y otros AirPods) en **Windows**: batería en la bandeja del
sistema, controles de ruido y **paso del audio entre Windows y Mac**, al estilo del cambio
automático entre iPhone, iPad y Mac.

> Proyecto independiente, sin relación con Apple. AirPods es marca de Apple Inc.

## Qué funciona hoy

- 🔋 **Batería** de cada auricular y del estuche, estado de carga y detección en el oído
  (vía anuncios Bluetooth LE, sin drivers).
- 🟦 **Icono en la bandeja** con el % más bajo de los auriculares; clic para abrir la ventana.
- 🔀 **Traer / enviar el audio** entre Windows y el Mac con un clic (app de Windows + agente de
  barra de menús en macOS). Ver [protocolo](docs/handoff-protocol.md).
- 🚀 Arranque con Windows y una sola instancia.

## Qué falta: cancelación de ruido

Cambiar entre **Cancelación / Adaptativo / Transparencia / Desactivado**, la detección de
conversaciones y el volumen personalizado ya están implementados a nivel de protocolo
(`src/WinPods.Core/Aap`, con tests), pero Windows **no permite abrir el canal Bluetooth (L2CAP)**
que usan los AirPods para estos ajustes desde una app normal. Hace falta un pequeño driver de
kernel; ver la [hoja de ruta](docs/roadmap.md). Mientras tanto, esos controles aparecen
deshabilitados con una explicación.

## Estructura

```
src/WinPods.Core/        Lógica multiplataforma (.NET 8) + tests
  Ble/                   Parser de anuncios BLE de proximidad de Apple (batería, modelo…)
  Aap/                   Apple Accessory Protocol: comandos ANC, eventos, sesión
  Handoff/               Protocolo firmado Windows ⇄ Mac, cliente/servidor TCP, coordinador
src/WinPods.App/         App de Windows (WPF + bandeja del sistema)
  Services/              Escáner BLE, dispositivos emparejados, conexión de audio (KS IOCTL)
mac/WinPodsAgent/        Agente de barra de menús para macOS (Swift)
tests/WinPods.Core.Tests xUnit
```

## Windows

Requisitos: Windows 10 2004+ / Windows 11, Bluetooth con soporte LE, .NET 8 SDK.

```powershell
dotnet build WinPods.sln -c Release
dotnet run --project src/WinPods.App
```

Cada push compila en GitHub Actions y deja el ejecutable como artefacto (`WinPods-win-x64`).

1. Empareja los AirPods en *Configuración › Bluetooth* (si no lo están ya).
2. Abre WinPods: elige tus AirPods en la lista. La batería aparece al abrir el estuche cerca o al ponértelos.

## Pasar el audio entre Windows y Mac

Los AirPods deben estar **emparejados con los dos equipos** y ambos en la misma red local.

**En el Mac** (macOS 12+, Xcode Command Line Tools):

```bash
cd mac/WinPodsAgent
./build-app.sh                       # crea WinPodsAgent.app
swift run WinPodsAgent list          # muestra la dirección Bluetooth de tus AirPods
swift run WinPodsAgent init          # crea ~/Library/Application Support/WinPods/agent.json
```

Edita `agent.json`:

```json
{
  "name": "MacBook",
  "secret": "k7qm-2xwd-9fha-c3tp",
  "airpodsAddress": "A4:C6:F0:12:34:56",
  "peerHost": "MI-PC.local",
  "peerPort": 47821,
  "listenPort": 47821
}
```

Abre `WinPodsAgent.app` (aparece un icono de AirPods en la barra de menús) y añádelo a
*Ajustes del Sistema › General › Ítems de inicio de sesión*.

**En Windows**, en *Cambio automático con el Mac › Configuración*:

1. Marca *Activar handoff*, escribe el nombre del Mac (p. ej. `MacBook-de-Ana.local`) o su IP.
2. Pulsa *Generar* y copia el mismo código en `secret` del Mac.
3. *Guardar* → acepta el aviso del Firewall de Windows (red privada) → *Probar conexión*.

Después: **Traer audio a este PC** / **Enviar audio al Mac** desde la ventana o el menú del
icono de la bandeja; en el Mac, desde el menú de la barra (también `swift run WinPodsAgent pull|push`).

## Desarrollo

La lógica está en `WinPods.Core` y se puede probar en cualquier SO:

```bash
dotnet test tests/WinPods.Core.Tests
```

## Créditos

El formato de los anuncios BLE y del protocolo AAP proviene de la ingeniería inversa publicada
por proyectos como LibrePods, OpenPods y AirPodsDesktop.
