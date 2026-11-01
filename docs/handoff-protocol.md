# Protocolo de handoff de WinPods (v1)

Permite pasar los AirPods entre un PC con Windows (app WinPods) y un Mac (WinPodsAgent),
imitando el cambio automático de Apple entre dispositivos con el mismo Apple ID.

## Por qué hace falta

El cambio automático de Apple se coordina vía iCloud: los AirPods saben qué dispositivos
comparten tu Apple ID y estos se avisan entre sí. Windows no forma parte de ese sistema,
así que WinPods coordina los dos equipos por la red local:

1. El equipo que **quiere** el audio pide al otro que suelte los AirPods (`release`).
2. El otro desconecta su enlace Bluetooth con los AirPods y responde.
3. El primero conecta los AirPods (Windows: `KSPROPERTY_ONESHOT_RECONNECT`; macOS: `IOBluetoothDevice.openConnection()`).

Para enviar el audio al otro equipo se hace al revés: desconectar aquí y mandar `acquire`.
Si el otro equipo falla, el audio vuelve al de origen.

## Transporte

- TCP, puerto **47821** por defecto, en ambos equipos.
- Una petición por conexión: una línea JSON terminada en `\n`, una línea JSON de respuesta, cierre.

## Mensaje

```json
{"v":1,"type":"release","from":"MI-PC","ts":1700000000,"nonce":"00112233445566778899aabb",
 "addr":"A4:C6:F0:12:34:56","sig":"…"}
```

| Campo      | Descripción |
|------------|-------------|
| `v`        | Versión del protocolo (1). |
| `type`     | `ping`, `release`, `acquire` o `reply`. |
| `from`     | Nombre del equipo que envía. |
| `ts`       | Hora Unix (s). Se rechaza si difiere más de 60 s. |
| `nonce`    | 12 bytes aleatorios en hex. Se rechazan nonces repetidos. |
| `addr`     | Dirección Bluetooth de los AirPods (`AA:BB:CC:DD:EE:FF`), informativa. |
| `reply_to` | En `reply`: el `nonce` de la petición. |
| `ok`       | En `reply`: resultado. |
| `detail`   | Texto libre, **no firmado**. |
| `sig`      | HMAC-SHA256 en hex (minúsculas). |

## Firma

```
key       = SHA-256( UTF8("winpods-v1:" + trim(código_de_enlace)) )
canonical = "v1" \n type \n from \n ts \n nonce \n addr \n reply_to \n ok
            (campos ausentes = cadena vacía; ok = "1" | "0" | "")
sig       = hex( HMAC-SHA256(key, UTF8(canonical)) )
```

Vector de prueba (comprobado en `HandoffTests.MatchesReferenceVector` y `WinPodsAgent selftest`):

- código: `correct horse battery staple`
- mensaje: `type=release from=PC ts=1700000000 nonce=00112233445566778899aabb addr=A4:C6:F0:12:34:56`
- firma: `f7a722d01d20327edaed7381a343a57b31ffecc1cec7e31147e5f106668e1575`

## Seguridad

Cualquiera en tu red sin el código de enlace no puede mover tus AirPods. El tráfico no va
cifrado (solo contiene órdenes y nombres de equipo), pero sí autenticado y protegido
contra repetición.
