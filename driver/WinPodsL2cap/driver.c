/*
 * WinPodsL2cap — KMDF Bluetooth profile driver that opens an L2CAP channel (PSM 0x1001, the
 * Apple Accessory Protocol) to AirPods and exposes it to user mode through four IOCTLs.
 * Structure and BRB usage follow the Windows-driver-samples "bthecho" client.
 *
 * READ/WRITE requests from user mode are formatted with an ACL-transfer BRB and forwarded to the
 * Bluetooth stack as-is; a READ pends until the AirPods send a packet and is cancelled with the
 * handle (or when the channel closes).
 */
#include <ntddk.h>
#include <wdf.h>
#include <initguid.h>
#include <bthdef.h>
#include <ntstrsafe.h>
#include <bthioctl.h>
#include <sdpnode.h>
#include <bthddi.h>
#include <bthguid.h>
#include <bthsdpddi.h>
#include <bthsdpdef.h>
#include "public.h"

#define WP_POOL_TAG 'dPnW'
#define WP_MAX_PACKET 4096

typedef struct _WP_DEVICE_CONTEXT {
    BTH_PROFILE_DRIVER_INTERFACE Profile;
    WDFSPINLOCK Lock;
    BOOLEAN Connected;
    BTH_ADDR RemoteAddress;
    L2CAP_CHANNEL_HANDLE Channel;
} WP_DEVICE_CONTEXT, *PWP_DEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(WP_DEVICE_CONTEXT, GetWpContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;
EVT_WDF_REQUEST_COMPLETION_ROUTINE TransferCompletion;

/* Formats Request with the BRB (the memory object is parented to the request). */
static NTSTATUS FormatRequestWithBrb(_In_ WDFIOTARGET Target, _In_ WDFREQUEST Request, _In_ PBRB Brb, _In_ size_t BrbSize)
{
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDFMEMORY memory = NULL;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Request;
    status = WdfMemoryCreatePreallocated(&attributes, Brb, BrbSize, &memory);
    if (!NT_SUCCESS(status)) return status;

    return WdfIoTargetFormatRequestForInternalIoctlOthers(
        Target, Request, IOCTL_INTERNAL_BTH_SUBMIT_BRB, memory, NULL, NULL, NULL, NULL, NULL);
}

static NTSTATUS SubmitBrbSync(_In_ WDFDEVICE Device, _In_ PBRB Brb, _In_ ULONG BrbSize)
{
    WDF_MEMORY_DESCRIPTOR arg1;
    WDF_REQUEST_SEND_OPTIONS options;

    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&arg1, Brb, BrbSize);
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_TIMEOUT);
    options.Timeout = WDF_REL_TIMEOUT_IN_SEC(10);

    return WdfIoTargetSendInternalIoctlOthersSynchronously(
        WdfDeviceGetIoTarget(Device), NULL, IOCTL_INTERNAL_BTH_SUBMIT_BRB, &arg1, NULL, NULL, &options, NULL);
}

static VOID OnIndication(_In_ PVOID Context, _In_ INDICATION_CODE Code, _In_ PINDICATION_PARAMETERS_ENHANCED Parameters)
{
    UNREFERENCED_PARAMETER(Parameters);
    PWP_DEVICE_CONTEXT ctx = GetWpContext((WDFDEVICE)Context);

    if (Code == IndicationRemoteDisconnect) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->Connected = FALSE;
        ctx->Channel = NULL;
        WdfSpinLockRelease(ctx->Lock);
    }
}

static NTSTATUS DoConnect(_In_ WDFDEVICE Device, _In_ const WINPODS_CONNECT_REQUEST* Request)
{
    PWP_DEVICE_CONTEXT ctx = GetWpContext(Device);
    NTSTATUS status;
    BOOLEAN already;

    WdfSpinLockAcquire(ctx->Lock);
    already = ctx->Connected;
    WdfSpinLockRelease(ctx->Lock);
    if (already) return STATUS_SUCCESS;

    struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL* brb =
        (struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL*)ctx->Profile.BthAllocateBrb(BRB_L2CA_OPEN_ENHANCED_CHANNEL, WP_POOL_TAG);
    if (brb == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    brb->BtAddress = Request->BluetoothAddress;
    brb->Psm = Request->Psm != 0 ? Request->Psm : WINPODS_AAP_PSM;
    brb->ChannelFlags = CF_ROLE_EITHER;

    brb->ConfigOut.Flags = CFG_ENHANCED | CFG_MTU;
    brb->ConfigOut.Mtu.Max = L2CAP_DEFAULT_MTU;
    brb->ConfigOut.Mtu.Min = L2CAP_MIN_MTU;
    brb->ConfigOut.Mtu.Preferred = L2CAP_DEFAULT_MTU;

    brb->ConfigIn.Flags = CFG_MTU;
    brb->ConfigIn.Mtu.Max = brb->ConfigOut.Mtu.Max;
    brb->ConfigIn.Mtu.Min = brb->ConfigOut.Mtu.Min;
    brb->ConfigIn.Mtu.Preferred = brb->ConfigOut.Mtu.Max;

    brb->CallbackFlags = CALLBACK_DISCONNECT;
    brb->Callback = &OnIndication;
    brb->CallbackContext = Device;
    brb->ReferenceObject = (PVOID)WdfDeviceWdmGetDeviceObject(Device);
    brb->IncomingQueueDepth = 50;

    status = SubmitBrbSync(Device, (PBRB)brb, sizeof(*brb));
    if (NT_SUCCESS(status)) status = brb->Hdr.Status;
    if (NT_SUCCESS(status)) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->Channel = brb->ChannelHandle;
        ctx->RemoteAddress = brb->BtAddress;
        ctx->Connected = TRUE;
        WdfSpinLockRelease(ctx->Lock);
    }
    ctx->Profile.BthFreeBrb((PBRB)brb);
    return status;
}

static NTSTATUS DoDisconnect(_In_ WDFDEVICE Device)
{
    PWP_DEVICE_CONTEXT ctx = GetWpContext(Device);
    L2CAP_CHANNEL_HANDLE channel;
    BTH_ADDR address;

    WdfSpinLockAcquire(ctx->Lock);
    channel = ctx->Channel;
    address = ctx->RemoteAddress;
    ctx->Channel = NULL;
    ctx->Connected = FALSE;
    WdfSpinLockRelease(ctx->Lock);

    if (channel == NULL) return STATUS_SUCCESS;

    struct _BRB_L2CA_CLOSE_CHANNEL* brb =
        (struct _BRB_L2CA_CLOSE_CHANNEL*)ctx->Profile.BthAllocateBrb(BRB_L2CA_CLOSE_CHANNEL, WP_POOL_TAG);
    if (brb == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    brb->BtAddress = address;
    brb->ChannelHandle = channel;
    NTSTATUS status = SubmitBrbSync(Device, (PBRB)brb, sizeof(*brb));
    ctx->Profile.BthFreeBrb((PBRB)brb);
    return status;
}

/* Context = the ACL-transfer BRB. For transfers, BufferSize holds the byte count when done. */
VOID TransferCompletion(_In_ WDFREQUEST Request, _In_ WDFIOTARGET Target,
                        _In_ PWDF_REQUEST_COMPLETION_PARAMS Params, _In_ WDFCONTEXT Context)
{
    UNREFERENCED_PARAMETER(Target);
    struct _BRB_L2CA_ACL_TRANSFER* brb = (struct _BRB_L2CA_ACL_TRANSFER*)Context;
    PWP_DEVICE_CONTEXT ctx = GetWpContext(WdfIoQueueGetDevice(WdfRequestGetIoQueue(Request)));
    NTSTATUS status = Params->IoStatus.Status;
    size_t information = brb->BufferSize;

    ctx->Profile.BthFreeBrb((PBRB)brb);
    WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? information : 0);
}

static VOID StartTransfer(_In_ WDFDEVICE Device, _In_ WDFREQUEST Request, _In_ BOOLEAN IsRead)
{
    PWP_DEVICE_CONTEXT ctx = GetWpContext(Device);
    NTSTATUS status;
    WDFMEMORY memory = NULL;
    size_t length = 0;
    L2CAP_CHANNEL_HANDLE channel;
    BTH_ADDR address;
    BOOLEAN connected;
    struct _BRB_L2CA_ACL_TRANSFER* brb = NULL;

    WdfSpinLockAcquire(ctx->Lock);
    channel = ctx->Channel;
    address = ctx->RemoteAddress;
    connected = ctx->Connected;
    WdfSpinLockRelease(ctx->Lock);
    if (!connected) { WdfRequestComplete(Request, STATUS_DEVICE_NOT_CONNECTED); return; }

    status = IsRead ? WdfRequestRetrieveOutputMemory(Request, &memory) : WdfRequestRetrieveInputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) goto fail;

    PVOID buffer = WdfMemoryGetBuffer(memory, &length);
    if (length == 0 || length > WP_MAX_PACKET) { status = STATUS_INVALID_BUFFER_SIZE; goto fail; }

    brb = (struct _BRB_L2CA_ACL_TRANSFER*)ctx->Profile.BthAllocateBrb(BRB_L2CA_ACL_TRANSFER, WP_POOL_TAG);
    if (brb == NULL) { status = STATUS_INSUFFICIENT_RESOURCES; goto fail; }

    brb->BtAddress = address;
    brb->ChannelHandle = channel;
    brb->TransferFlags = IsRead ? (ACL_TRANSFER_DIRECTION_IN | ACL_SHORT_TRANSFER_OK) : ACL_TRANSFER_DIRECTION_OUT;
    brb->BufferMDL = NULL;
    brb->Buffer = buffer;
    brb->BufferSize = (ULONG)length;

    status = FormatRequestWithBrb(WdfDeviceGetIoTarget(Device), Request, (PBRB)brb, sizeof(*brb));
    if (!NT_SUCCESS(status)) goto fail;

    WdfRequestSetCompletionRoutine(Request, TransferCompletion, brb);
    if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(Device), WDF_NO_SEND_OPTIONS)) {
        status = WdfRequestGetStatus(Request);
        goto fail;
    }
    return;

fail:
    if (brb != NULL) ctx->Profile.BthFreeBrb((PBRB)brb);
    WdfRequestComplete(Request, status);
}

VOID EvtIoDeviceControl(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t OutputBufferLength,
                        _In_ size_t InputBufferLength, _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    NTSTATUS status;

    switch (IoControlCode) {
    case IOCTL_WINPODS_CONNECT: {
        WINPODS_CONNECT_REQUEST* input = NULL;
        if (InputBufferLength < sizeof(*input)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(*input), (PVOID*)&input, NULL);
        if (NT_SUCCESS(status)) status = DoConnect(device, input);
        break;
    }
    case IOCTL_WINPODS_DISCONNECT:
        status = DoDisconnect(device);
        break;
    case IOCTL_WINPODS_READ:
        StartTransfer(device, Request, TRUE);
        return;
    case IOCTL_WINPODS_WRITE:
        StartTransfer(device, Request, FALSE);
        return;
    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }
    WdfRequestComplete(Request, status);
}

NTSTATUS EvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, WP_DEVICE_CONTEXT);
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    PWP_DEVICE_CONTEXT ctx = GetWpContext(device);
    WDF_OBJECT_ATTRIBUTES lockAttr;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttr);
    lockAttr.ParentObject = device;
    status = WdfSpinLockCreate(&lockAttr, &ctx->Lock);
    if (!NT_SUCCESS(status)) return status;

    status = WdfFdoQueryForInterface(device, &GUID_BTHDDI_PROFILE_DRIVER_INTERFACE,
                                     (PINTERFACE)&ctx->Profile, sizeof(ctx->Profile),
                                     BTHDDI_PROFILE_DRIVER_INTERFACE_VERSION_FOR_QI, NULL);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    return WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_WINPODS_L2CAP, NULL);
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}
