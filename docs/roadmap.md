# Hoja de ruta

## Estado actual (v0.1)

| Función | Windows | Cómo |
|---|---|---|
| Batería izquierdo / derecho / estuche, carga, en el oído | ✅ | Anuncios BLE de Apple (pasos de 10 %) |
| Icono en la bandeja con el % de batería | ✅ | |
| Pasar audio Windows ⇄ Mac (manual, botón o menú) | ✅ | Protocolo de handoff + agente para macOS |
| Cancelación de ruido / transparencia / adaptativo | 🧪 | Protocolo + driver `driver/WinPodsL2cap` + `DriverAapTransport` escritos; **sin probar con hardware** |
| Detección de conversaciones, volumen personalizado, nivel adaptativo | ⏳ | Igual |
| Batería exacta (1 %) | ⏳ | Igual (viene por AAP) |

## Fase 2 — Driver L2CAP para Windows (necesario para ANC)

Los AirPods se configuran con el *Apple Accessory Protocol* (AAP) sobre un canal
**L2CAP PSM 0x1001** de Bluetooth Clásico. Windows no ofrece ninguna API de usuario para
abrir canales L2CAP de Bluetooth Clásico (Winsock `AF_BTH` solo soporta RFCOMM), por lo que
hace falta un **driver de perfil Bluetooth en modo kernel (KMDF)** que:

1. Se instale como servicio de perfil sobre el dispositivo de los AirPods.
2. Abra el canal con `BRB_L2CA_OPEN_CHANNEL` (PSM 0x1001), basado en el ejemplo `bthecho` del WDK.
3. Exponga una interfaz de dispositivo con IOCTLs de lectura/escritura de paquetes.

Hecho (sin probar): `driver/WinPodsL2cap` y `DriverAapTransport`. Ver `driver/README.md`.

Requisitos prácticos: WDK + Visual Studio, firma de pruebas (`bcdedit /set testsigning on`)
mientras no haya firma EV/atestación de Microsoft.

## Fase 3 — Cambio automático

- Windows: detectar que empieza a sonar audio (sesiones WASAPI activas) y pedir los AirPods
  automáticamente, como hace iOS.
- macOS: lo mismo con `kAudioDevicePropertyDeviceIsRunningSomewhere`.
- Opcional: descubrimiento automático del otro equipo por Bonjour (`_winpods._tcp`).
