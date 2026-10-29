using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SoundBridge.Infrastructure;
using SoundBridge.Models;
using SoundBridge.Services;

namespace SoundBridge.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string ColorIdle = "#6C757E";
    private const string ColorBusy = "#E0A33E";
    private const string ColorOk = "#35C08A";
    private const string ColorError = "#E5675F";

    private readonly DeviceService _deviceService = new();
    private readonly ConnectionService _connection;
    private readonly SettingsService _settingsService = new();
    private readonly Dispatcher _dispatcher;

    private SinkDevice? _selectedDevice;
    private ConnState _state = ConnState.Idle;
    private string _statusText = "未连接";
    private string _statusColorHex = ColorIdle;
    private string _statusDetail = "选择一台手机，然后点「连接」。";
    private bool _hasDevices;
    private bool _initialized;

    private bool _autoStart;
    private bool _minimizeToTray = true;
    private bool _autoReconnect = true;
    private bool _autoConnectOnStart;

    private CancellationTokenSource? _retryCts;
    private int _retryAttempt;

    /// <summary>用户在启动自连过程中手动断开，就别再自动连回去了。</summary>
    private bool _userCancelledStartup;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _connection = new ConnectionService();
        _connection.StateChanged += OnConnectionStateChanged;
        _connection.DroppedUnexpectedly += OnDroppedUnexpectedly;

        RefreshCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        PrimaryCommand = new AsyncRelayCommand(PrimaryActionAsync, () => !IsBusy && (IsConnected || SelectedDevice is not null));
        OpenLogCommand = new RelayCommand(OpenLog);
        OpenPrivacyCommand = new RelayCommand(ShowPrivacy);
    }

    public ObservableCollection<SinkDevice> Devices { get; } = new();

    /// <summary>可选的播放设备（系统里已启用的）。</summary>
    public ObservableCollection<AudioOutput> Outputs { get; } = new();

    private AudioOutput? _selectedOutput;

    public AudioOutput? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!SetProperty(ref _selectedOutput, value)) return;
            if (value is null) return;

            _settingsService.Current.OutputDeviceId = value.Id;
            _settingsService.Current.OutputDeviceName = value.Name;
            _settingsService.Save();

            // 正在播放时切换，立刻生效，不用重连
            if (IsConnected) ApplyOutputToSystem();
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PrimaryCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand OpenPrivacyCommand { get; }

    public string VersionText => "v1.1.1";

    public SinkDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            PrimaryCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ActionHint));
        }
    }

    public bool HasDevices
    {
        get => _hasDevices;
        private set => SetProperty(ref _hasDevices, value);
    }

    public bool IsConnected => _state == ConnState.Connected;

    public bool IsBusy => _state is ConnState.Connecting or ConnState.Disconnecting;

    public string ActionText => _state switch
    {
        ConnState.Connecting => "连接中…",
        ConnState.Connected => "断开",
        ConnState.Disconnecting => "断开中…",
        _ => "连接"
    };

    public string ActionHint => _state switch
    {
        ConnState.Connecting => "正在等待手机接受连接，请稍候。",
        ConnState.Connected => "断开后声音回到手机，电脑这边的播放不受影响。",
        ConnState.Disconnecting => "正在断开连接…",
        _ when SelectedDevice is null => "先在上面选一台手机。",
        _ => $"连接 {SelectedDevice?.Name} 后，手机上播放的音乐、视频、语音都会从电脑出声。"
    };

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusColorHex
    {
        get => _statusColorHex;
        private set => SetProperty(ref _statusColorHex, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string EmptyHint { get; private set; } =
        "先在 Windows 的「蓝牙和其他设备」里把手机和电脑配对，再点右上角「刷新设备」。";

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!SetProperty(ref _autoStart, value)) return;
            AutoStartService.Set(value);
            _settingsService.Current.AutoStart = value;
            _settingsService.Save();
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!SetProperty(ref _minimizeToTray, value)) return;
            _settingsService.Current.MinimizeToTray = value;
            _settingsService.Save();
        }
    }

    public bool AutoReconnect
    {
        get => _autoReconnect;
        set
        {
            if (!SetProperty(ref _autoReconnect, value)) return;
            _settingsService.Current.AutoReconnect = value;
            _settingsService.Save();
        }
    }

    public bool AutoConnectOnStart
    {
        get => _autoConnectOnStart;
        set
        {
            if (!SetProperty(ref _autoConnectOnStart, value)) return;
            _settingsService.Current.AutoConnectOnStart = value;
            _settingsService.Save();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        _settingsService.Load();
        var settings = _settingsService.Current;

        _autoStart = AutoStartService.IsEnabled();

        // 自启项里记的是绝对路径，程序挪了位置就会指向一个不存在的文件，
        // 表现为「开机没反应」。所以每次启动顺便刷一遍，成本为零。
        if (_autoStart) AutoStartService.Set(true);

        _minimizeToTray = settings.MinimizeToTray;
        _autoReconnect = settings.AutoReconnect;
        _autoConnectOnStart = settings.AutoConnectOnStart;
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(AutoReconnect));
        OnPropertyChanged(nameof(AutoConnectOnStart));

        Log.Info("应用启动");

        // 刚登录时蓝牙栈常常还没就绪，第一次扫描会返回空列表。
        // 这里给它一段时间反复扫，而不是扫一次就放弃。
        await WaitForDevicesAsync();

        if (!AutoConnectOnStart)
        {
            Log.Info("启动自动连接：未开启");
            return;
        }

        if (Devices.Count == 0)
        {
            Log.Warn("启动自动连接跳过：没有扫描到可接收的手机");
            return;
        }

        var last = settings.LastDeviceName;
        var device = Devices.FirstOrDefault(d => d.Name == last) ?? Devices[0];
        Log.Info($"启动自动连接: {device.Name}");
        await ConnectWithRetryAsync(device);
    }

    /// <summary>反复扫描，直到蓝牙栈就绪或超时（最多等 2 分钟）。</summary>
    private async Task WaitForDevicesAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        var attempt = 0;

        while (true)
        {
            await RefreshDevicesAsync();
            if (Devices.Count > 0)
            {
                if (attempt > 0) Log.Info($"蓝牙已就绪，第 {attempt + 1} 次扫描拿到设备");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warn("等待蓝牙就绪超时（120 秒），仍未扫描到设备");
                return;
            }

            attempt++;
            if (attempt == 1) Log.Info("暂时没有设备，蓝牙可能还在启动，每 3 秒重试");
            await Task.Delay(3000);
        }
    }

    /// <summary>开机自连的容错：手机可能还没和电脑建立链路，多试几次。</summary>
    private async Task ConnectWithRetryAsync(SinkDevice device)
    {
        int[] delays = { 0, 3000, 5000, 8000, 12000, 15000, 20000 };

        for (var i = 0; i < delays.Length; i++)
        {
            if (i > 0)
            {
                await Task.Delay(delays[i]);
                if (_userCancelledStartup || _connection.State == ConnState.Connected) return;
                Log.Info($"启动自动连接重试（第 {i} 次）");
            }

            if (_userCancelledStartup) return;

            await ConnectAsync(device);

            if (_connection.State == ConnState.Connected)
            {
                Log.Info(i == 0 ? "启动自动连接成功" : $"启动自动连接成功（第 {i + 1} 次）");
                return;
            }
        }

        Log.Warn("启动自动连接失败：多次尝试仍未连上，可在界面点「连接」重试");
    }

    public async Task RefreshDevicesAsync()
    {
        var devices = await _deviceService.GetSinkDevicesAsync();
        var selectedId = SelectedDevice?.Id;

        // 差量同步，避免选中项闪烁
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (devices.All(d => d.Id != Devices[i].Id)) Devices.RemoveAt(i);
        }

        for (var i = 0; i < devices.Count; i++)
        {
            if (Devices.Any(d => d.Id == devices[i].Id)) continue;
            Devices.Insert(Math.Min(i, Devices.Count), devices[i]);
        }

        HasDevices = Devices.Count > 0;

        if (!HasDevices)
        {
            EmptyHint = "先在 Windows 的「蓝牙和其他设备」里把手机和电脑配对，再点右上角「刷新设备」。\n" +
                        "如果已经配对却看不到，说明这台蓝牙适配器没有开放音频接收能力。";
        }

        var target = selectedId is not null ? Devices.FirstOrDefault(d => d.Id == selectedId) : null;
        SelectedDevice = target ?? Devices.FirstOrDefault();

        RefreshOutputs();

        Log.Info($"设备列表已更新，共 {Devices.Count} 台");
    }

    /// <summary>刷新可选的播放设备，并保持当前选择。</summary>
    private void RefreshOutputs()
    {
        var outputs = AudioOutputService.GetOutputs();

        Outputs.Clear();
        foreach (var output in outputs) Outputs.Add(output);

        var savedId = _settingsService.Current.OutputDeviceId;
        var pick = (savedId is not null ? Outputs.FirstOrDefault(o => o.Id == savedId) : null)
                   ?? Outputs.FirstOrDefault(o => o.IsDefault)
                   ?? Outputs.FirstOrDefault(o => o.Name.Contains("扬声器") || o.Name.Contains("Speakers", StringComparison.OrdinalIgnoreCase))
                   ?? Outputs.FirstOrDefault();

        // 直接写字段，避免 setter 把这次「恢复选择」当成用户操作再存一遍盘
        _selectedOutput = pick;
        OnPropertyChanged(nameof(SelectedOutput));

        Log.Info($"播放设备 {Outputs.Count} 个，当前选择: {pick?.Name ?? "(无)"}");
    }

    /// <summary>把系统默认播放设备切到用户选的那台。</summary>
    private void ApplyOutputToSystem()
    {
        var target = _selectedOutput;
        if (target is null) return;

        var current = AudioOutputService.GetOutputs().FirstOrDefault(o => o.IsDefault);
        if (current is not null &&
            string.Equals(current.Id, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;   // 已经是目标设备，不折腾系统
        }

        if (AudioOutputService.TrySetDefault(target.Id, out var error))
        {
            Log.Info($"默认播放设备已切到: {target.Name}");
            StatusDetail = $"声音正送往：{target.Name}";
        }
        else
        {
            Log.Warn($"切换声音输出失败: {error}");
            StatusDetail = $"声音输出切换失败：{error}";
        }
    }

    private async Task PrimaryActionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (SelectedDevice is null) return;
        await ConnectAsync(SelectedDevice);
    }

    public async Task ConnectAsync(SinkDevice device)
    {
        CancelRetry();
        _retryAttempt = 0;
        SelectedDevice = device;

        _settingsService.Current.LastDeviceName = device.Name;
        _settingsService.Save();

        await _connection.ConnectAsync(device.Id);
    }

    public async Task DisconnectAsync()
    {
        _userCancelledStartup = true;
        CancelRetry();
        await _connection.DisconnectAsync();
        ApplyState(ConnState.Idle, "未连接", "已断开，手机的声音回到手机");
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateEventArgs e)
    {
        _dispatcher.InvokeAsync(() => ApplyState(e.State, e.Message, e.Detail));
    }

    private void OnDroppedUnexpectedly(object? sender, string deviceId)
    {
        _dispatcher.InvokeAsync(() => _ = HandleDroppedAsync());
    }

    private async Task HandleDroppedAsync()
    {
        ApplyState(ConnState.Idle, "未连接", "连接已断开");

        if (!AutoReconnect || SelectedDevice is null) return;

        CancelRetry();
        _retryCts = new CancellationTokenSource();
        var token = _retryCts.Token;

        int[] delays = { 800, 1600, 3000, 5000, 8000, 12000, 15000 };

        try
        {
            foreach (var delay in delays)
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested || _connection.State == ConnState.Connected) return;

                _retryAttempt++;
                StatusDetail = $"连接已断开，正在自动重连（第 {_retryAttempt} 次）…";

                await _connection.ConnectAsync(SelectedDevice.Id);

                if (_connection.State == ConnState.Connected)
                {
                    _retryAttempt = 0;
                    return;
                }
            }

            ApplyState(ConnState.Error, "自动重连失败", "点「连接」再试一次，或确认手机还在电脑附近。");
        }
        catch (TaskCanceledException)
        {
            // 用户主动中断
        }
        finally
        {
            _retryAttempt = 0;
        }
    }

    private void ApplyState(ConnState state, string statusText, string? detail)
    {
        _state = state;
        StatusText = statusText;
        StatusColorHex = state switch
        {
            ConnState.Connected => ColorOk,
            ConnState.Connecting or ConnState.Disconnecting => ColorBusy,
            ConnState.Error => ColorError,
            _ => ColorIdle
        };

        if (detail is not null) StatusDetail = detail;

        foreach (var device in Devices)
        {
            device.IsActive = state == ConnState.Connected && device.Id == SelectedDevice?.Id;
        }

        // 连上之后把声音送到用户指定的设备，避免系统默认设备失联时全程没声音
        if (state == ConnState.Connected) ApplyOutputToSystem();

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ActionHint));
        PrimaryCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private void CancelRetry()
    {
        try
        {
            _retryCts?.Cancel();
            _retryCts?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _retryCts = null;
    }

    private void OpenLog()
    {
        try
        {
            if (System.IO.File.Exists(AppPaths.LogFile))
                Process.Start("explorer.exe", $"/select,\"{AppPaths.LogFile}\"");
            else
                Process.Start("explorer.exe", AppPaths.DataDir);
        }
        catch (Exception ex)
        {
            Log.Error("打开日志失败", ex);
        }
    }

    private void ShowPrivacy()
    {
        System.Windows.MessageBox.Show(
            "声桥只做一件事：调用 Windows 自带的蓝牙音频接收接口，把手机正在播放的声音转到电脑上播放。\n\n" +
            "· 不录音，不保存任何音频内容\n" +
            "· 不联网，不上传任何数据\n" +
            "· 本机只保存一个设置文件和一个日志文件，位置在 %APPDATA%\\SoundBridge\n",
            "隐私说明",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    public void Dispose()
    {
        CancelRetry();
        _connection.StateChanged -= OnConnectionStateChanged;
        _connection.DroppedUnexpectedly -= OnDroppedUnexpectedly;
        _connection.Dispose();
    }
}
