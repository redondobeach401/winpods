# WinPodsL2cap — driver de canal L2CAP para AirPods

> ⚠️ **Experimental y sin probar con hardware real.** Es un driver de kernel: un error puede
> provocar una pantalla azul. Pruébalo en una máquina de pruebas o virtual primero.

Windows no deja abrir canales L2CAP de Bluetooth Clásico desde modo usuario. Este driver KMDF
(basado en el ejemplo `bthecho` del WDK) abre el canal **PSM 0x1001** de los AirPods y lo expone a
la app con 4 IOCTLs (`public.h`): `CONNECT`, `DISCONNECT`, `READ`, `WRITE`.
La app lo usa a través de `DriverAapTransport` → `AapSession` (cancelación de ruido, etc.).

## Compilar

Requisitos: Visual Studio 2022 + Windows SDK + **Windows Driver Kit (WDK)**.

```powershell
msbuild driver\WinPodsL2cap\WinPodsL2cap.vcxproj /p:Configuration=Release /p:Platform=x64
```

El CI también lo compila (job `driver`) y sube el resultado sin firmar como artefacto.

## Instalar (máquina de pruebas)

1. Modo de pruebas (admin, reinicia después): `bcdedit /set testsigning on`
2. Firma de pruebas del paquete (`.sys` + `.cat`) con un certificado de pruebas, por ejemplo:
   ```powershell
   makecert -r -pe -ss PrivateCertStore -n CN=WinPodsTest WinPodsTest.cer   # o New-SelfSignedCertificate
   inf2cat /driver:. /os:10_X64
   signtool sign /v /s PrivateCertStore /n WinPodsTest /fd sha256 WinPodsL2cap.sys WinPodsL2cap.cat
   ```
3. Instala: `pnputil /add-driver WinPodsL2cap.inf /install`
4. Reconecta los AirPods y reinicia WinPods.

## Punto a verificar: el hardware ID

El INF se engancha al dispositivo `BTHENUM` que Windows crea para el registro SDP propio de Apple
(`74ec2172-0bad-4d01-8f77-997b2be0722a`, fabricante `0x004C`). **Esto es una suposición** basada en
otros proyectos. Compruébalo en *Administrador de dispositivos › AirPods › Detalles › Id. de hardware*
y ajusta la línea `[WinPods.NTamd64]` si es distinto.

## Distribución

Para usuarios finales haría falta firmar con certificado EV y pasar por el portal de atestación de
Microsoft. Mientras tanto, solo modo de pruebas.
