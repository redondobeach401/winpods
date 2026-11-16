# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versionado [SemVer](https://semver.org/lang/es/).

## [Sin publicar]

### Añadido
- Driver KMDF `WinPodsL2cap` (experimental, modo de pruebas): abre el canal L2CAP de los AirPods
  y permite cambiar Cancelación / Adaptativo / Transparencia / Desactivado desde la app.

### Corregido
- El INF del driver ahora enlaza con el dispositivo real de los AirPods (id. compatible
  `BTHENUM\{74ec2172-…}`); antes usaba un id. de hardware que no existe.

## [0.1.0] - 2026-11-01

### Añadido
- Batería de auriculares y estuche, carga y detección en el oído vía BLE.
- Icono en la bandeja con el % de batería, arranque con Windows.
- Paso del audio entre Windows y Mac (app + agente de barra de menús para macOS).
- Protocolo AAP (cancelación de ruido, transparencia, adaptativo…) en `WinPods.Core`,
  pendiente del driver L2CAP para Windows.
