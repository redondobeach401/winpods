/*
 * WinPodsL2cap — KMDF Bluetooth profile driver that opens an L2CAP channel (PSM 0x1001, the
 * Apple Accessory Protocol) to AirPods and exposes it to user mode through four IOCTLs.
 * Modelled on the Windows-driver-samples "bthecho" client.
 *
 * User-mode I/O requests are forwarded to the Bluetooth stack as BRBs; reads pend until the
 * AirPods send a packet. Only one reader and one writer at a time are supported.
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

#define POOL_TAG 'dPnW'
#define MAX_PACKET 4096

typedef struct _DEVICE_CONTEXT {
    BTHDDI_PROFILE_DRIVER_INTERFACE Profile;
    WDFSPINLOCK Lock;
    BOOLEAN Connected;
    L2CAP_CHANNEL_HANDLE Channel;
    WDFREQUEST ReadChild;   // BRB request currently owned by the stack for a pending read
    WDFREQUEST ReadUser;    // the user's pending read request
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

typedef struct _IO_OP_CONTEXT {
    WDFREQUEST User;
    PBRB Brb;
    WDFDEVICE Device;
    BOOLEAN IsRead;
} IO_OP_CONTEXT, *PIO_OP_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(IO_OP_CONTEXT, GetOpContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;
EVT_WDF_REQUEST_COMPLETION_ROUTINE TransferCompletion;
EVT_WDF_REQUEST_CANCEL EvtReadCancel;

static NTSTATUS SubmitBrbSync(_In_ WDFDEVICE Device, _In_ PBRB Brb, _In_ ULONG BrbSize)
{
    NTSTATUS status;
    WDFMEMORY memory;
    WDF_REQUEST_SEND_OPTIONS options;
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Device;
    status = WdfMemoryCreatePreallocated(&attributes, Brb, BrbSize, &memory);
    if (!NT_SUCCESS(status)) return status;

    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SYNCHRONOUS | WDF_REQUEST_SEND_OPTION_TIMEOUT);
    options.Timeout = WDF_REL_TIMEOUT_IN_SEC(10);

    status = WdfIoTargetSendInternalIoctlOthersSynchronously(
        WdfDeviceGetIoTarget(Device), NULL, IOCTL_INTERNAL_BTH_SUBMIT_BRB,
        &(WDF_MEMORY_DESCRIPTOR){ .Type = WdfMemoryDescriptorTypeHandle, .u.HandleType = { memory, NULL } }, NULL,
        NULL, NULL, &options, NULL);
    WdfObjectDelete(memory);
    return status;
}

static VOID OnIndication(_In_ PVOID Context, _In_ INDICATION_CODE Code, _In_ PINDICATION_PARAMETERS Parameters)
{
    UNREFERENCED_PARAMETER(Parameters);
    PDEVICE_CONTEXT ctx = GetDeviceContext((WDFDEVICE)Context);
    if (Code == IndicationRemoteDisconnect) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->Connected = FALSE;
        ctx->Channel = NULL;
        WdfSpinLockRelease(ctx->Lock);
    }
}

static NTSTATUS DoConnect(_In_ WDFDEVICE Device, _In_ const WINPODS_CONNECT_REQUEST* Request)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(Device);
    NTSTATUS status;
    BOOLEAN already;

    WdfSpinLockAcquire(ctx->Lock);
    already = ctx->Connected;
    WdfSpinLockRelease(ctx->Lock);
    if (already) return STATUS_SUCCESS;

    struct _BRB_L2CA_OPEN_CHANNEL* brb =
        (struct _BRB_L2CA_OPEN_CHANNEL*)ctx->Profile.BthAllocateBrb(BRB_L2CA_OPEN_CHANNEL, POOL_TAG);
    if (brb == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    brb->BtAddress = Request->BluetoothAddress;
    brb->Psm = Request->Psm != 0 ? Request->Psm : WINPODS_AAP_PSM;
    brb->ChannelFlags = CF_ROLE_EITHER;
    brb->ConfigOut.Flags = 0;
    brb->ConfigIn.Flags = 0;
    brb->ConfigOut.Mtu.Max = L2CAP_MAX_MTU;
    brb->ConfigOut.Mtu.Min = L2CAP_MIN_MTU;
    brb->ConfigOut.Mtu.Preferred = L2CAP_DEFAULT_MTU;
    brb->ConfigIn.Mtu.Max = L2CAP_MAX_MTU;
    brb->ConfigIn.Mtu.Min = L2CAP_MIN_MTU;
    brb->ConfigIn.Mtu.Preferred = L2CAP_DEFAULT_MTU;
    brb->CallbackFlags = CALLBACK_DISCONNECT;
    brb->Callback = &OnIndication;
    brb->CallbackContext = Device;
    brb->ReferenceObject = (PVOID)WdfDeviceWdmGetDeviceObject(Device);

    status = SubmitBrbSync(Device, (PBRB)brb, sizeof(*brb));
    if (NT_SUCCESS(status)) status = brb->Hdr.Status;
    if (NT_SUCCESS(status)) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->Channel = brb->ChannelHandle;
        ctx->Connected = TRUE;
        WdfSpinLockRelease(ctx->Lock);
    }
    ctx->Profile.BthFreeBrb((PBRB)brb);
    return status;
}

static NTSTATUS DoDisconnect(_In_ WDFDEVICE Device)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(Device);
    L2CAP_CHANNEL_HANDLE channel;
    WDFREQUEST pending;

    WdfSpinLockAcquire(ctx->Lock);
    channel = ctx->Channel;
    ctx->Channel = NULL;
    ctx->Connected = FALSE;
    pending = ctx->ReadChild;
    WdfSpinLockRelease(ctx->Lock);

    if (pending != NULL) WdfRequestCancelSentRequest(pending);
    if (channel == NULL) return STATUS_SUCCESS;

    struct _BRB_L2CA_CLOSE_CHANNEL* brb =
        (struct _BRB_L2CA_CLOSE_CHANNEL*)ctx->Profile.BthAllocateBrb(BRB_L2CA_CLOSE_CHANNEL, POOL_TAG);
    if (brb == NULL) return STATUS_INSUFFICIENT_RESOURCES;
    brb->ChannelHandle = channel;
    NTSTATUS status = SubmitBrbSync(Device, (PBRB)brb, sizeof(*brb));
    ctx->Profile.BthFreeBrb((PBRB)brb);
    return status;
}

VOID TransferCompletion(_In_ WDFREQUEST Request, _In_ WDFIOTARGET Target,
                        _In_ PWDF_REQUEST_COMPLETION_PARAMS Params, _In_ WDFCONTEXT Context)
{
    UNREFERENCED_PARAMETER(Target);
    UNREFERENCED_PARAMETER(Context);
    PIO_OP_CONTEXT op = GetOpContext(Request);
    PDEVICE_CONTEXT ctx = GetDeviceContext(op->Device);
    struct _BRB_L2CA_ACL_TRANSFER* brb = (struct _BRB_L2CA_ACL_TRANSFER*)op->Brb;
    NTSTATUS status = Params->IoStatus.Status;
    if (NT_SUCCESS(status)) status = brb->Hdr.Status;
    ULONG transferred = NT_SUCCESS(status) ? brb->BufferSize : 0;
    if (NT_SUCCESS(status) && brb->BytesTransferred != 0) transferred = brb->BytesTransferred;

    WDFREQUEST user = op->User;
    if (op->IsRead) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->ReadChild = NULL;
        ctx->ReadUser = NULL;
        WdfSpinLockRelease(ctx->Lock);
        if (WdfRequestUnmarkCancelable(user) == STATUS_CANCELLED) {
            // EvtReadCancel completes the user request.
            ctx->Profile.BthFreeBrb(op->Brb);
            WdfObjectDelete(Request);
            return;
        }
    }
    ctx->Profile.BthFreeBrb(op->Brb);
    WdfObjectDelete(Request);
    WdfRequestCompleteWithInformation(user, status, transferred);
}

VOID EvtReadCancel(_In_ WDFREQUEST UserRequest)
{
    WDFDEVICE device = WdfIoQueueGetDevice(WdfRequestGetIoQueue(UserRequest));
    PDEVICE_CONTEXT ctx = GetDeviceContext(device);
    WDFREQUEST child;

    WdfSpinLockAcquire(ctx->Lock);
    child = ctx->ReadChild;
    WdfSpinLockRelease(ctx->Lock);
    if (child != NULL) WdfRequestCancelSentRequest(child);
    WdfRequestComplete(UserRequest, STATUS_CANCELLED);
}

static VOID StartTransfer(_In_ WDFDEVICE Device, _In_ WDFREQUEST User, _In_ BOOLEAN IsRead, _In_ size_t Length)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(Device);
    NTSTATUS status;
    PMDL mdl = NULL;
    WDFREQUEST child = NULL;
    WDFMEMORY memory = NULL;
    L2CAP_CHANNEL_HANDLE channel;
    BOOLEAN connected;

    WdfSpinLockAcquire(ctx->Lock);
    channel = ctx->Channel;
    connected = ctx->Connected;
    WdfSpinLockRelease(ctx->Lock);
    if (!connected) { WdfRequestComplete(User, STATUS_DEVICE_NOT_CONNECTED); return; }
    if (Length == 0 || Length > MAX_PACKET) { WdfRequestComplete(User, STATUS_INVALID_BUFFER_SIZE); return; }

    status = IsRead ? WdfRequestRetrieveOutputWdmMdl(User, &mdl) : WdfRequestRetrieveInputWdmMdl(User, &mdl);
    if (!NT_SUCCESS(status)) goto fail;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, IO_OP_CONTEXT);
    attributes.ParentObject = Device;
    status = WdfRequestCreate(&attributes, WdfDeviceGetIoTarget(Device), &child);
    if (!NT_SUCCESS(status)) goto fail;

    PIO_OP_CONTEXT op = GetOpContext(child);
    op->User = User;
    op->Device = Device;
    op->IsRead = IsRead;

    struct _BRB_L2CA_ACL_TRANSFER* brb =
        (struct _BRB_L2CA_ACL_TRANSFER*)ctx->Profile.BthAllocateBrb(BRB_L2CA_ACL_TRANSFER, POOL_TAG);
    if (brb == NULL) { status = STATUS_INSUFFICIENT_RESOURCES; goto fail; }
    op->Brb = (PBRB)brb;

    brb->ChannelHandle = channel;
    brb->TransferFlags = IsRead ? (ACL_TRANSFER_DIRECTION_IN | ACL_SHORT_TRANSFER_OK) : 0;
    brb->BufferSize = (ULONG)Length;
    brb->Buffer = NULL;
    brb->BufferMDL = mdl;

    WDF_OBJECT_ATTRIBUTES memAttr;
    WDF_OBJECT_ATTRIBUTES_INIT(&memAttr);
    memAttr.ParentObject = child;
    status = WdfMemoryCreatePreallocated(&memAttr, brb, sizeof(*brb), &memory);
    if (!NT_SUCCESS(status)) goto fail;

    status = WdfIoTargetFormatRequestForInternalIoctlOthers(
        WdfDeviceGetIoTarget(Device), child, IOCTL_INTERNAL_BTH_SUBMIT_BRB, memory, NULL, NULL, NULL, NULL, NULL);
    if (!NT_SUCCESS(status)) goto fail;
    WdfRequestSetCompletionRoutine(child, TransferCompletion, NULL);

    if (IsRead) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->ReadChild = child;
        ctx->ReadUser = User;
        WdfSpinLockRelease(ctx->Lock);
        status = WdfRequestMarkCancelableEx(User, EvtReadCancel);
        if (!NT_SUCCESS(status)) goto fail_read;
    }

    if (!WdfRequestSend(child, WdfDeviceGetIoTarget(Device), WDF_NO_SEND_OPTIONS)) {
        status = WdfRequestGetStatus(child);
        if (IsRead) { WdfRequestUnmarkCancelable(User); }
        goto fail_read;
    }
    return;

fail_read:
    if (IsRead) {
        WdfSpinLockAcquire(ctx->Lock);
        ctx->ReadChild = NULL;
        ctx->ReadUser = NULL;
        WdfSpinLockRelease(ctx->Lock);
    }
fail:
    if (child != NULL) {
        PIO_OP_CONTEXT failed = GetOpContext(child);
        if (failed->Brb != NULL) ctx->Profile.BthFreeBrb(failed->Brb);
        WdfObjectDelete(child);
    }
    WdfRequestComplete(User, status);
}

VOID EvtIoDeviceControl(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t OutputBufferLength,
                        _In_ size_t InputBufferLength, _In_ ULONG IoControlCode)
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PDEVICE_CONTEXT ctx = GetDeviceContext(device);
    NTSTATUS status;

    switch (IoControlCode) {
    case IOCTL_WINPODS_CONNECT: {
        WINPODS_CONNECT_REQUEST* input;
        if (InputBufferLength < sizeof(*input)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(*input), (PVOID*)&input, NULL);
        if (NT_SUCCESS(status)) status = DoConnect(device, input);
        break;
    }
    case IOCTL_WINPODS_DISCONNECT:
        status = DoDisconnect(device);
        break;
    case IOCTL_WINPODS_READ: {
        BOOLEAN busy;
        WdfSpinLockAcquire(ctx->Lock);
        busy = ctx->ReadChild != NULL;
        WdfSpinLockRelease(ctx->Lock);
        if (busy) { status = STATUS_DEVICE_BUSY; break; }
        StartTransfer(device, Request, TRUE, OutputBufferLength);
        return;
    }
    case IOCTL_WINPODS_WRITE:
        StartTransfer(device, Request, FALSE, InputBufferLength);
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

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    PDEVICE_CONTEXT ctx = GetDeviceContext(device);
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
