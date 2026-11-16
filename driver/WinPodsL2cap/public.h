/*
 * Interface shared by the WinPods L2CAP driver and the user-mode app.
 * Keep in sync with src/WinPods.App/Services/DriverAapTransport.cs.
 */
#pragma once

#ifndef CTL_CODE
#include <winioctl.h>
#endif

// Device interface exposed by the driver: {C8A7F1A2-3D2B-4F0E-9A55-5B0E4A6D1F77}
DEFINE_GUID(GUID_DEVINTERFACE_WINPODS_L2CAP,
    0xc8a7f1a2, 0x3d2b, 0x4f0e, 0x9a, 0x55, 0x5b, 0x0e, 0x4a, 0x6d, 0x1f, 0x77);

#define WINPODS_AAP_PSM 0x1001

// All use METHOD_BUFFERED / FILE_ANY_ACCESS.
// CONNECT:    in  = WINPODS_CONNECT_REQUEST
// DISCONNECT: no buffers
// READ:       out = one L2CAP SDU; pends until the AirPods send something. Returns bytes read.
// WRITE:      in  = one L2CAP SDU
#define IOCTL_WINPODS_CONNECT     CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_WINPODS_DISCONNECT  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_WINPODS_READ        CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_WINPODS_WRITE       CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)

#pragma pack(push, 1)
typedef struct _WINPODS_CONNECT_REQUEST {
    unsigned __int64 BluetoothAddress;  // 48-bit BD_ADDR in the low bytes
    unsigned short   Psm;               // 0 = WINPODS_AAP_PSM
    unsigned short   Reserved[3];
} WINPODS_CONNECT_REQUEST;              // 16 bytes
#pragma pack(pop)
