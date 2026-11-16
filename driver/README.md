# WinPodsL2cap — driver de canal L2CAP para AirPods

> ⚠️ **Experimental.** Probado con unos AirPods Pro (Windows 11, modo de pruebas): carga, abre el
> canal L2CAP y la app cambia los modos de audio. Es un driver de kernel: un error puede
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
   $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=WinPodsTest" -CertStoreLocation Cert:\LocalMachine\My
   Export-Certificate -Cert $c -FilePath WinPodsTest.cer
   Import-Certificate -FilePath WinPodsTest.cer -CertStoreLocation Cert:\LocalMachine\Root
   Import-Certificate -FilePath WinPodsTest.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
   inf2cat /driver:. /os:10_X64
   signtool sign /v /sm /s My /sha1 $($c.Thumbprint) /fd sha256 WinPodsL2cap.sys WinPodsL2cap.cat
   ```
   (`inf2cat` y `signtool` vienen con el SDK/WDK; el certificado está en el almacén del equipo,
   por eso `signtool` necesita `/sm /s My`.)
3. Instala: `pnputil /add-driver WinPodsL2cap.inf /install`
4. Reconecta los AirPods y reinicia WinPods.

## Hardware ID (verificado)

El INF se engancha al dispositivo `BTHENUM` que Windows crea para el registro SDP propio de Apple
(`74ec2172-0bad-4d01-8f77-997b2be0722a`, aparece como «AAP Server»). En unos AirPods Pro (PID 2014):

- Id. de hardware: `BTHENUM\{74ec2172-…}_VID&0001004c_PID&2014` y `…_LOCALMFG&0046`
- Id. compatibles: `BTHENUM\{74ec2172-…}` y `…_GENERIC`

El INF usa el id. compatible (`BTHENUM\{74ec2172-…}`), así que sirve para cualquier modelo. Ojo: el
id. `…_VID&0001004c` a secas **no** existe y no enlaza. Para comprobarlo en tu equipo:
`Get-PnpDevice | Where-Object InstanceId -like 'BTHENUM*' | Format-List FriendlyName, InstanceId`.

## Desinstalar

`pnputil /enum-drivers` para ver el `oemNN.inf`, luego `pnputil /delete-driver oemNN.inf /uninstall /force`
y `bcdedit /set testsigning off` (reinicia).

## Distribución

Para usuarios finales haría falta firmar con certificado EV y pasar por el portal de atestación de
Microsoft. Mientras tanto, solo modo de pruebas.
