using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundBridge.Infrastructure;
using SoundBridge.Models;
using SoundBridge.Services;
using SoundBridge.Views;

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
    private bool _limitVolume = true;

    private readonly DispatcherTimer _sleepTimer;
    private SleepTimerOption _selectedSleepTimer = SleepTimerOption.Off;
    private bool _sleepTimerChime;

    /// <summary>到点时刻。用绝对时间而不是自减计数，机器睡过去再醒来也不会算错。</summary>
    private DateTime _sleepDeadline;
    private string _sleepTimerText = string.Empty;
    private string _sleepTimerTrayText = string.Empty;

    private CancellationTokenSource? _retryCts;
    private int _retryAttempt;

    /// <summary>用户在启动自连过程中手动断开，就别再自动连回去了。</summary>
    private bool _userCancelledStartup;

    /// <summary>休眠前是否处于已连接状态，唤醒后据此决定要不要恢复。</summary>
    private bool _connectedBeforeSuspend;

    /// <summary>唤醒恢复流程正在跑，避免连续几次 Resume 触发重复重连。</summary>
    private bool _resumeInProgress;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _connection = new ConnectionService();
        _connection.StateChanged += OnConnectionStateChanged;
        _connection.DroppedUnexpectedly += OnDroppedUnexpectedly;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        RefreshCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        PrimaryCommand = new AsyncRelayCommand(PrimaryActionAsync, () => !IsBusy && (IsConnected || SelectedDevice is not null));
        OpenLogCommand = new RelayCommand(OpenLog);
        OpenPrivacyCommand = new RelayCommand(ShowPrivacy);
        OpenHelpCommand = new RelayCommand(ShowHelp);

        // 设置在这里就读进来，而不是等 InitializeAsync：
        // 首次引导的判断要赶在窗口显示时发生，那时初始化还没跑完。
        _settingsService.Load();

        SleepTimerOptions = new ObservableCollection<SleepTimerOption>
        {
            SleepTimerOption.Off,
            new() { Minutes = 15, Label = "15 分钟" },
            new() { Minutes = 30, Label = "30 分钟" },
            new() { Minutes = 45, Label = "45 分钟" },
            new() { Minutes = 60, Label = "1 小时" },
            new() { Minutes = 90, Label = "1 小时 30 分" },
            new() { Minutes = 120, Label = "2 小时" }
        };

        _sleepTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sleepTimer.Tick += OnSleepTimerTick;
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
    public RelayCommand OpenHelpCommand { get; }

    /// <summary>第一次打开软件，还没看过使用说明。</summary>
    public bool ShouldShowWelcome => !_settingsService.Current.HasSeenWelcome;

    public void MarkWelcomeSeen()
    {
        if (_settingsService.Current.HasSeenWelcome) return;

        _settingsService.Current.HasSeenWelcome = true;
        _settingsService.Save();
        Log.Info("首次使用说明已阅");
    }

    private void ShowHelp()
    {
        try
        {
            var window = new WelcomeWindow(firstRun: false);
            var owner = System.Windows.Application.Current?.MainWindow;

            if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, window))
                window.Owner = owner;

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error("打开使用说明失败", ex);
        }
    }

    public string VersionText => "v1.4.0";

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

    /// <summary>
    /// 连接时把系统音量压到上限以内。
    /// 手机那头一旦开始播放就直接出声，音量是上次留下的，很容易轰一下。
    /// 这是安全措施，不是体验功能，所以默认开。
    /// </summary>
    public bool LimitVolume
    {
        get => _limitVolume;
        set
        {
            if (!SetProperty(ref _limitVolume, value)) return;
            _settingsService.Current.VolumeLimitEnabled = value;
            _settingsService.Save();
        }
    }

    public ObservableCollection<SleepTimerOption> SleepTimerOptions { get; }

    /// <summary>
    /// 定时关闭。选一个时长就开始倒数，到点断开连接——手机那边的播放器
    /// 大多会跟着停下，这比在手机上翻出来点暂停可靠得多。
    /// </summary>
    public SleepTimerOption SelectedSleepTimer
    {
        get => _selectedSleepTimer;
        set
        {
            if (value is null || !SetProperty(ref _selectedSleepTimer, value)) return;

            if (value.Minutes <= 0)
            {
                StopSleepTimer();
            }
            else
            {
                StartSleepTimer(value.Minutes);
            }
        }
    }

    public bool SleepTimerChime
    {
        get => _sleepTimerChime;
        set
        {
            if (!SetProperty(ref _sleepTimerChime, value)) return;
            _settingsService.Current.SleepTimerChime = value;
            _settingsService.Save();
        }
    }

    /// <summary>界面上的倒计时，形如「12:34 后断开」；未启用时为空。</summary>
    public string SleepTimerText => _sleepTimerText;

    /// <summary>托盘提示用的粗粒度倒计时，一分钟才变一次，免得每秒都去改托盘文字。</summary>
    public string SleepTimerTrayText => _sleepTimerTrayText;

    public bool IsSleepTimerRunning => _sleepTimerText.Length > 0;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var settings = _settingsService.Current;

        _autoStart = AutoStartService.IsEnabled();

        // 自启项里记的是绝对路径，程序挪了位置就会指向一个不存在的文件，
        // 表现为「开机没反应」。所以每次启动顺便刷一遍，成本为零。
        if (_autoStart) AutoStartService.Set(true);

        _minimizeToTray = settings.MinimizeToTray;
        _autoReconnect = settings.AutoReconnect;
        _autoConnectOnStart = settings.AutoConnectOnStart;
        _limitVolume = settings.VolumeLimitEnabled;
        _sleepTimerChime = settings.SleepTimerChime;
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(AutoReconnect));
        OnPropertyChanged(nameof(AutoConnectOnStart));
        OnPropertyChanged(nameof(LimitVolume));
        OnPropertyChanged(nameof(SleepTimerChime));

        Log.Info(PackageIdentity.IsPackaged ? "应用启动（打包/商店版）" : "应用启动（免安装版）");

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

    /// <summary>反复扫描，直到蓝牙栈就绪或超时（默认最多等 2 分钟）。</summary>
    private async Task WaitForDevicesAsync(int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
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
                Log.Warn($"等待蓝牙就绪超时（{timeoutSeconds} 秒），仍未扫描到设备");
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
        var savedName = _settingsService.Current.OutputDeviceName;

        // 睡眠唤醒后 USB 声卡会重新枚举，endpoint 的 GUID 可能整个变掉，
        // 只按 ID 找会失配，所以再按名字兜一层。
        var pick = (savedId is not null ? Outputs.FirstOrDefault(o => o.Id == savedId) : null)
                   ?? (savedName is not null ? Outputs.FirstOrDefault(o => o.Name == savedName) : null)
                   ?? Outputs.FirstOrDefault(o => o.IsDefault)
                   ?? Outputs.FirstOrDefault(o => o.Name.Contains("扬声器") || o.Name.Contains("Speakers", StringComparison.OrdinalIgnoreCase))
                   ?? Outputs.FirstOrDefault();

        // 走了名字兜底说明 ID 变了，把新 ID 记回去，下次就不用再猜
        if (pick is not null && !string.IsNullOrEmpty(savedId) && !string.Equals(pick.Id, savedId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"播放设备 ID 已变化，按名称重新匹配: {pick.Name}");
            _settingsService.Current.OutputDeviceId = pick.Id;
            _settingsService.Current.OutputDeviceName = pick.Name;
            _settingsService.Save();
        }

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
        var alreadyDefault = current is not null &&
                             string.Equals(current.Id, target.Id, StringComparison.OrdinalIgnoreCase);

        if (!alreadyDefault)
        {
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

        // 即便设备本来就是默认的，音量也照样要压一遍——上次用完留下的音量跟这次无关。
        ApplyVolumeLimit(target);
    }

    /// <summary>把设备音量压到上限以内。已经比上限低就不动。</summary>
    private void ApplyVolumeLimit(AudioOutput target)
    {
        if (!LimitVolume) return;

        var cap = _settingsService.Current.VolumeCapPercent;
        if (cap <= 0) return;

        if (AudioOutputService.TryCapVolume(target.Id, cap, out var before, out var error))
        {
            Log.Info($"音量 {before * 100:F0}% 超过上限，已调到 {cap}%（{target.Name}）");
        }
        else if (!string.IsNullOrEmpty(error))
        {
            Log.Warn($"限制音量失败: {error}");
        }
    }

    private void StartSleepTimer(int minutes)
    {
        _sleepDeadline = DateTime.Now.AddMinutes(minutes);
        _sleepTimer.Start();
        UpdateSleepTimerText();
        Log.Info($"定时关闭已设定：{minutes} 分钟后断开");
    }

    private void StopSleepTimer()
    {
        if (_sleepTimer.IsEnabled)
        {
            _sleepTimer.Stop();
            Log.Info("定时关闭已取消");
        }

        _sleepDeadline = default;
        SetSleepTimerText(string.Empty, string.Empty);
    }

    private void OnSleepTimerTick(object? sender, EventArgs e)
    {
        var left = _sleepDeadline - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _ = FireSleepTimerAsync();
            return;
        }

        UpdateSleepTimerText();
    }

    private void UpdateSleepTimerText()
    {
        var left = _sleepDeadline - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;

        var mmss = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        var minutesLeft = (int)Math.Ceiling(left.TotalMinutes);

        SetSleepTimerText(
            $"{mmss} 后断开",
            minutesLeft <= 1 ? "即将断开" : $"{minutesLeft} 分钟后断开");
    }

    private void SetSleepTimerText(string window, string tray)
    {
        if (_sleepTimerText != window)
        {
            _sleepTimerText = window;
            OnPropertyChanged(nameof(SleepTimerText));
            OnPropertyChanged(nameof(IsSleepTimerRunning));
        }

        if (_sleepTimerTrayText != tray)
        {
            _sleepTimerTrayText = tray;
            OnPropertyChanged(nameof(SleepTimerTrayText));
        }
    }

    private async Task FireSleepTimerAsync()
    {
        _sleepTimer.Stop();
        _sleepDeadline = default;

        // 下拉框要回到「关闭」：停在旧时长上会让人以为下一次还会自动倒数。
        if (_selectedSleepTimer.Minutes > 0)
        {
            _selectedSleepTimer = SleepTimerOptions[0];
            OnPropertyChanged(nameof(SelectedSleepTimer));
        }

        SetSleepTimerText(string.Empty, string.Empty);

        var wasConnected = IsConnected;
        if (wasConnected)
        {
            // DisconnectAsync 会顺手打上「用户主动断开」的标记，
            // 否则「断线自动重连」会马上把它接回来，定时就成了摆设。
            await DisconnectAsync();

            // 断开时服务层还会回抛一次状态事件，排队排在前面；
            // 用 Background 优先级落到队列末尾，才能把提示文字换成我们这句。
            _ = _dispatcher.InvokeAsync(
                () => StatusDetail = "定时关闭已执行。想继续听，点一下「连接」",
                DispatcherPriority.Background);
        }
        else
        {
            StatusDetail = "定时到点，但当时并没有连着手机";
        }

        Log.Info($"定时关闭到点：{(wasConnected ? "已断开连接" : "本来未连接")}");

        if (SleepTimerChime) PlayChime();
    }

    private static void PlayChime()
    {
        try
        {
            // 跟着系统提示音的方案走，用户把系统静音或换了音效都算他的选择。
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"提示音播放失败: {ex.Message}");
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

        // 手动连接等于重新表态：之前那次「用户主动断开」的标记要清掉，
        // 否则后面的重试循环会以为自己被取消了。
        _userCancelledStartup = false;

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

    /// <summary>
    /// 系统睡眠 / 休眠会拆掉蓝牙链路，唤醒后旧的 AudioPlaybackConnection 句柄已经作废，
    /// 它既不会抛异常，也不一定触发 Closed —— 表现就是界面还显示「已连接」但没有声音。
    /// 所以唤醒后必须把对象丢掉重建，不能指望那条 StateChanged 事件醒来。
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _connectedBeforeSuspend = _connection.State == ConnState.Connected;
            Log.Info($"系统即将睡眠/休眠，当前状态 = {_connection.State}");
            CancelRetry();
            return;
        }

        if (e.Mode != PowerModes.Resume) return;

        Log.Info($"系统已唤醒（睡眠前 {(_connectedBeforeSuspend ? "已连接" : "未连接")}）");
        _dispatcher.InvokeAsync(() => _ = HandleResumeAsync());
    }

    private async Task HandleResumeAsync()
    {
        if (_resumeInProgress) return;
        _resumeInProgress = true;

        try
        {
            var wasConnected = _connectedBeforeSuspend;
            _connectedBeforeSuspend = false;

            // 睡眠中可能已经启动了断线重连循环（它的延时会在唤醒后继续跑），先掐掉。
            CancelRetry();

            // 唤醒瞬间蓝牙适配器还在重新初始化，这时候碰它只会拿到一串失败。
            // 2.5 秒是经验值：绝大多数适配器在这个窗口内恢复广播与端点枚举。
            await Task.Delay(2500);

            // 丢掉那张作废的「连接凭证」
            await _connection.DisconnectAsync(silent: true);
            ApplyState(ConnState.Idle, "未连接", "系统已唤醒，正在恢复蓝牙连接…");

            // 睡前连着，醒来就必须接回去；睡前没连，但开了自动重连且有上次的设备，也试一次
            var shouldReconnect = wasConnected
                                  || (AutoReconnect && !string.IsNullOrEmpty(_settingsService.Current.LastDeviceName));

            if (!shouldReconnect)
            {
                await RefreshDevicesAsync();
                Log.Info("唤醒后未恢复连接：睡眠前未连接且未开启自动重连");
                return;
            }

            _userCancelledStartup = false;

            // 唤醒后蓝牙栈就绪比开机时还慢，给足 90 秒，别只扫一次就判死
            await WaitForDevicesAsync(90);

            if (Devices.Count == 0)
            {
                ApplyState(ConnState.Error, "未连接", "唤醒后没有扫描到手机，请确认手机蓝牙已打开，然后点「刷新设备」。");
                Log.Warn("唤醒后恢复失败：没有可用设备");
                return;
            }

            var target = Devices.FirstOrDefault(d => d.Name == _settingsService.Current.LastDeviceName)
                         ?? SelectedDevice
                         ?? Devices[0];

            StatusDetail = $"系统已唤醒，正在重新连接 {target.Name}…";
            Log.Info($"唤醒后重新连接: {target.Name}");

            await ConnectWithRetryAsync(target);

            if (_connection.State != ConnState.Connected)
            {
                // 重连窗口用尽了：别停在「已连接」的假象上，交给用户一键重试
                ApplyState(ConnState.Error, "未连接", "唤醒后没能自动接上，点「连接」再试一次。");
                Log.Warn("唤醒后重连失败");
            }
        }
        catch (Exception ex)
        {
            Log.Error("唤醒后恢复连接出错", ex);
            ApplyState(ConnState.Error, "未连接", "唤醒后恢复失败，点「连接」再试一次。");
        }
        finally
        {
            _resumeInProgress = false;
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
        _sleepTimer.Stop();
        _sleepTimer.Tick -= OnSleepTimerTick;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _connection.StateChanged -= OnConnectionStateChanged;
        _connection.DroppedUnexpectedly -= OnDroppedUnexpectedly;
        _connection.Dispose();
    }
}
