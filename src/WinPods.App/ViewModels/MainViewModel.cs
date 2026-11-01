using System.Collections.ObjectModel;
using System.Windows.Threading;
using WinPods.App.Services;
using WinPods.Core;
using WinPods.Core.Aap;
using WinPods.Core.Ble;
using WinPods.Core.Handoff;

namespace WinPods.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly BleBatteryMonitor _ble = new();
    private readonly PairedDeviceService _paired = new();
    private readonly IAudioRouteController _route = new KsAudioRouteController();
    private readonly Func<IAapTransport> _transportFactory;
    private AapSession? _aap;
    private TcpHandoffServer? _server;
    private HandoffSigner? _serverSigner;

    public MainViewModel(AppSettings settings, Dispatcher dispatcher, Func<IAapTransport>? transportFactory = null)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _transportFactory = transportFactory ?? (() => new UnsupportedAapTransport());

        var probe = _transportFactory();
        AapSupported = probe.IsSupported;
        AapUnavailableReason = probe.UnavailableReason;

        _peerHost = settings.PeerHost ?? "";
        _peerPort = settings.PeerPort;
        _sharedSecret = settings.SharedSecret ?? "";
        _handoffEnabled = settings.HandoffEnabled;
        _startWithWindows = StartupRegistration.IsEnabled;

        _ble.MinimumRssi = settings.MinimumRssi;
        _ble.Updated += m => _dispatcher.BeginInvoke(() => ApplyAdvert(m));
        _paired.ConnectionChanged += c => _dispatcher.BeginInvoke(() => OnConnectionChanged(c));

        RefreshDevicesCommand = new AsyncCommand(_ => RefreshDevicesAsync());
        SetNoiseModeCommand = new AsyncCommand(p => SetNoiseModeAsync(p), _ => AapConnected);
        PullHereCommand = new AsyncCommand(_ => RunHandoffAsync(c => c.PullHereAsync()), _ => CanHandoff);
        PushToPeerCommand = new AsyncCommand(_ => RunHandoffAsync(c => c.PushToPeerAsync()), _ => CanHandoff);
        TestPeerCommand = new AsyncCommand(_ => TestPeerAsync(), _ => CanHandoff);
        GenerateSecretCommand = new AsyncCommand(_ => { SharedSecret = AppSettings.GenerateSecret(); return Task.CompletedTask; });
        SaveHandoffCommand = new AsyncCommand(_ => SaveHandoffAsync());
    }

    public async Task InitializeAsync()
    {
        try { _ble.Start(); }
        catch (Exception ex) { StatusText = $"No se pudo iniciar el escáner Bluetooth LE: {ex.Message}"; }

        await RefreshDevicesAsync();
        RestartHandoffServer();
    }

    #region Device & battery

    public ObservableCollection<PairedAudioDevice> PairedDevices { get; } = [];

    private PairedAudioDevice? _selectedDevice;
    public PairedAudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value) || value is null) return;
            _settings.DeviceAddress = value.Address;
            _settings.Save();
            _ = SelectDeviceAsync(value.Address);
        }
    }

    private string _deviceName = "AirPods";
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }

    private string _modelName = "";
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (Set(ref _isConnected, value)) OnPropertyChanged(nameof(ConnectionText)); }
    }

    public string ConnectionText => IsConnected ? "Conectados a este PC" : "No conectados a este PC";

    public BatteryViewModel Left { get; } = new("Izquierdo");
    public BatteryViewModel Right { get; } = new("Derecho");
    public BatteryViewModel Case { get; } = new("Estuche");

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>Short summary for the tray tooltip (max 63 chars on Windows).</summary>
    public string TraySummary
    {
        get
        {
            string s = $"{DeviceName}: I {Left.Text} · D {Right.Text} · E {Case.Text}";
            return s.Length > 63 ? s[..63] : s;
        }
    }

    /// <summary>Lowest earbud battery, used to draw the tray icon.</summary>
    public int? LowestBudBattery => new[] { Left.Percent, Right.Percent }.Where(p => p.HasValue).Min();

    public event Action? BatteryChanged;

    public AsyncCommand RefreshDevicesCommand { get; }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await PairedDeviceService.GetPairedDevicesAsync();
            PairedDevices.Clear();
            foreach (var d in devices) PairedDevices.Add(d);

            var preferred = devices.FirstOrDefault(d => d.Address == BluetoothAddress.Normalize(_settings.DeviceAddress))
                            ?? devices.FirstOrDefault(d => d.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) SelectedDevice = preferred;
            else StatusText = "No hay AirPods emparejados. Empareja los AirPods en Configuración › Bluetooth.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error al listar dispositivos Bluetooth: {ex.Message}";
        }
    }

    private async Task SelectDeviceAsync(string address)
    {
        DeviceName = await _paired.SelectAsync(address) ?? "AirPods";
        OnPropertyChanged(nameof(TraySummary));
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        if (connected) _ = StartAapAsync();
        else _ = StopAapAsync();
    }

    private void ApplyAdvert(ProximityPairingMessage m)
    {
        if (m.Model != AirPodsModel.Unknown) ModelName = m.Model.DisplayName();
        // Prefer exact AAP values (1 %) when that channel is open.
        if (AapConnected) return;
        Left.Update(m.Left.BatteryPercent, m.Left.Charging, m.Left.InEar);
        Right.Update(m.Right.BatteryPercent, m.Right.Charging, m.Right.InEar);
        Case.Update(m.CaseBatteryPercent, m.CaseCharging, null);
        NotifyBattery();
    }

    private void NotifyBattery()
    {
        OnPropertyChanged(nameof(TraySummary));
        OnPropertyChanged(nameof(LowestBudBattery));
        BatteryChanged?.Invoke();
    }

    #endregion

    #region Noise control (AAP)

    public bool AapSupported { get; }
    public string? AapUnavailableReason { get; }

    private bool _aapConnected;
    public bool AapConnected { get => _aapConnected; private set => Set(ref _aapConnected, value); }

    private NoiseControlMode? _noiseMode;
    public NoiseControlMode? NoiseMode
    {
        get => _noiseMode;
        private set
        {
            if (!Set(ref _noiseMode, value)) return;
            OnPropertyChanged(nameof(IsAnc));
            OnPropertyChanged(nameof(IsTransparency));
            OnPropertyChanged(nameof(IsAdaptive));
            OnPropertyChanged(nameof(IsOff));
        }
    }

    public bool IsAnc => NoiseMode == NoiseControlMode.NoiseCancellation;
    public bool IsTransparency => NoiseMode == NoiseControlMode.Transparency;
    public bool IsAdaptive => NoiseMode == NoiseControlMode.Adaptive;
    public bool IsOff => NoiseMode == NoiseControlMode.Off;

    private bool _conversationalAwareness;
    public bool ConversationalAwareness
    {
        get => _conversationalAwareness;
        set { if (Set(ref _conversationalAwareness, value) && _aap is not null) _ = Try(() => _aap.SetConversationalAwarenessAsync(value)); }
    }

    private bool _adaptiveVolume;
    public bool AdaptiveVolume
    {
        get => _adaptiveVolume;
        set { if (Set(ref _adaptiveVolume, value) && _aap is not null) _ = Try(() => _aap.SetAdaptiveVolumeAsync(value)); }
    }

    private int _adaptiveNoise = 50;
    public int AdaptiveNoise
    {
        get => _adaptiveNoise;
        set { if (Set(ref _adaptiveNoise, value) && _aap is not null) _ = Try(() => _aap.SetAdaptiveAudioNoiseAsync(value)); }
    }

    public AsyncCommand SetNoiseModeCommand { get; }

    private async Task SetNoiseModeAsync(object? parameter)
    {
        if (_aap is null || parameter is not string s || !Enum.TryParse<NoiseControlMode>(s, out var mode)) return;
        await Try(() => _aap.SetNoiseControlAsync(mode));
        NoiseMode = mode; // optimistic; the AirPods confirm with a notification
    }

    private async Task StartAapAsync()
    {
        if (!AapSupported || _aap is not null || !BluetoothAddress.TryParse(_settings.DeviceAddress, out var raw)) return;
        var session = new AapSession(_transportFactory());
        session.EventReceived += e => _dispatcher.BeginInvoke(() => ApplyAap(session.State, e));
        session.Closed += error => _dispatcher.BeginInvoke(new Action(() => { _ = StopAapAsync(); }));
        try
        {
            await session.StartAsync(raw);
            _aap = session;
            AapConnected = true;
        }
        catch (Exception ex)
        {
            StatusText = $"No se pudo abrir el canal de control: {ex.Message}";
            await session.DisposeAsync();
        }
    }

    private async Task StopAapAsync()
    {
        var session = _aap;
        _aap = null;
        AapConnected = false;
        if (session is not null) await session.DisposeAsync();
    }

    private void ApplyAap(AapState s, AapEvent e)
    {
        switch (e)
        {
            case BatteryEvent:
                Left.Update(s.LeftBattery, s.LeftCharging, Left.InEar);
                Right.Update(s.RightBattery, s.RightCharging, Right.InEar);
                Case.Update(s.CaseBattery, s.CaseCharging, null);
                NotifyBattery();
                break;
            case SettingEvent:
                NoiseMode = s.NoiseControl;
                if (s.ConversationalAwareness is bool ca) Set(ref _conversationalAwareness, ca, nameof(ConversationalAwareness));
                if (s.AdaptiveVolume is bool av) Set(ref _adaptiveVolume, av, nameof(AdaptiveVolume));
                if (s.AdaptiveAudioNoise is int an) Set(ref _adaptiveNoise, an, nameof(AdaptiveNoise));
                break;
        }
    }

    #endregion

    #region Handoff (Windows ⇄ Mac)

    private string _peerHost;
    public string PeerHost { get => _peerHost; set => Set(ref _peerHost, value); }

    private int _peerPort;
    public int PeerPort { get => _peerPort; set => Set(ref _peerPort, value); }

    private string _sharedSecret;
    public string SharedSecret { get => _sharedSecret; set => Set(ref _sharedSecret, value); }

    private bool _handoffEnabled;
    public bool HandoffEnabled { get => _handoffEnabled; set => Set(ref _handoffEnabled, value); }

    private string _handoffStatus = "";
    public string HandoffStatus { get => _handoffStatus; set => Set(ref _handoffStatus, value); }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value)) return;
            try { StartupRegistration.IsEnabled = value; }
            catch (Exception ex) { StatusText = $"No se pudo cambiar el inicio automático: {ex.Message}"; }
        }
    }

    public bool CanHandoff => _settings.HandoffEnabled && !string.IsNullOrWhiteSpace(_settings.PeerHost)
                              && !string.IsNullOrWhiteSpace(_settings.SharedSecret) && _settings.DeviceAddress is not null;

    public AsyncCommand PullHereCommand { get; }
    public AsyncCommand PushToPeerCommand { get; }
    public AsyncCommand TestPeerCommand { get; }
    public AsyncCommand GenerateSecretCommand { get; }
    public AsyncCommand SaveHandoffCommand { get; }

    private Task SaveHandoffAsync()
    {
        _settings.PeerHost = PeerHost.Trim();
        _settings.PeerPort = PeerPort;
        _settings.SharedSecret = SharedSecret.Trim();
        _settings.HandoffEnabled = HandoffEnabled;
        _settings.Save();
        RestartHandoffServer();
        HandoffStatus = HandoffEnabled ? "Guardado. Escuchando peticiones del Mac." : "Guardado. Handoff desactivado.";
        OnPropertyChanged(nameof(CanHandoff));
        return Task.CompletedTask;
    }

    private HandoffCoordinator CreateCoordinator(HandoffSigner signer) => new(
        _route,
        new TcpHandoffClient(signer, _settings.ComputerName, _settings.PeerHost!, _settings.PeerPort, () => _settings.DeviceAddress),
        () => BluetoothAddress.Normalize(_settings.DeviceAddress));

    private async Task RunHandoffAsync(Func<HandoffCoordinator, Task<HandoffResult>> action)
    {
        HandoffStatus = "Cambiando…";
        try
        {
            var result = await action(CreateCoordinator(_serverSigner ?? new HandoffSigner(_settings.SharedSecret!)));
            HandoffStatus = result.Message;
        }
        catch (Exception ex)
        {
            HandoffStatus = $"Error: {ex.Message}";
        }
    }

    private async Task TestPeerAsync()
    {
        HandoffStatus = "Probando conexión…";
        try
        {
            var signer = _serverSigner ?? new HandoffSigner(_settings.SharedSecret!);
            var client = new TcpHandoffClient(signer, _settings.ComputerName, _settings.PeerHost!, _settings.PeerPort, () => _settings.DeviceAddress);
            var reply = await client.RequestAsync(HandoffMessageTypes.Ping, CancellationToken.None);
            HandoffStatus = $"✔ {reply.From} responde ({(reply.Detail == "connected" ? "tiene los AirPods" : "sin AirPods")}).";
        }
        catch (Exception ex)
        {
            HandoffStatus = $"✖ {ex.Message}";
        }
    }

    private async void RestartHandoffServer()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
        _serverSigner = null;
        if (!_settings.HandoffEnabled || string.IsNullOrWhiteSpace(_settings.SharedSecret)) return;

        try
        {
            var signer = new HandoffSigner(_settings.SharedSecret);
            _serverSigner = signer;
            var coordinator = CreateCoordinator(signer);
            _server = new TcpHandoffServer(signer, _settings.ComputerName, _settings.ListenPort, async (request, ct) =>
            {
                var result = await coordinator.HandleRequestAsync(request, ct);
                if (request.Type != HandoffMessageTypes.Ping)
                    _ = _dispatcher.BeginInvoke(() => HandoffStatus = $"{request.From} pidió '{request.Type}': {result.Detail}");
                return result;
            });
            _server.Log += msg => _dispatcher.BeginInvoke(() => HandoffStatus = msg);
            _server.Start();
        }
        catch (Exception ex)
        {
            HandoffStatus = $"No se pudo escuchar en el puerto {_settings.ListenPort}: {ex.Message}";
        }
    }

    #endregion

    private async Task Try(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    public async ValueTask DisposeAsync()
    {
        _ble.Dispose();
        _paired.Dispose();
        await StopAapAsync();
        if (_server is not null) await _server.DisposeAsync();
    }
}

public sealed class BatteryViewModel : ObservableObject
{
    public BatteryViewModel(string label) => Label = label;

    public string Label { get; }

    private int? _percent;
    public int? Percent { get => _percent; private set => Set(ref _percent, value); }

    private bool _charging;
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }

    private bool? _inEar;
    public bool? InEar { get => _inEar; private set => Set(ref _inEar, value); }

    public string Text => Percent is int p ? $"{p} %" : "—";
    public double Fraction => (Percent ?? 0) / 100.0;
    public string Detail => (Charging ? "⚡ cargando" : "") + (InEar == true ? (Charging ? " · " : "") + "en el oído" : "");

    public void Update(int? percent, bool charging, bool? inEar)
    {
        Percent = percent;
        Charging = charging;
        InEar = inEar;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(Detail));
    }
}
