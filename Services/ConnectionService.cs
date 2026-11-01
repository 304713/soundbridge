using SoundBridge.Infrastructure;
using Windows.Media.Audio;

namespace SoundBridge.Services;

public enum ConnState
{
    Idle,
    Connecting,
    Connected,
    Disconnecting,
    Error
}

public sealed class ConnectionStateEventArgs : EventArgs
{
    public ConnState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
}

/// <summary>
/// 封装 AudioPlaybackConnection：让本机以 A2DP Sink 角色接收手机音频。
/// 注意：所有事件都在非 UI 线程触发，由 ViewModel 负责调度。
/// </summary>
public sealed class ConnectionService : IDisposable
{
    private AudioPlaybackConnection? _connection;
    private bool _intentionalClose;

    public ConnState State { get; private set; } = ConnState.Idle;

    public string? CurrentDeviceId { get; private set; }

    public event EventHandler<ConnectionStateEventArgs>? StateChanged;

    /// <summary>非用户操作导致的断开（用于自动重连）。</summary>
    public event EventHandler<string>? DroppedUnexpectedly;

    public async Task ConnectAsync(string deviceId)
    {
        if (State == ConnState.Connecting) return;

        await DisconnectAsync(silent: true).ConfigureAwait(true);

        _intentionalClose = false;
        CurrentDeviceId = deviceId;
        Report(ConnState.Connecting, "连接中", "等手机确认，一般两秒。");

        try
        {
            var connection = AudioPlaybackConnection.TryCreateFromId(deviceId)
                             ?? throw new InvalidOperationException("系统没有为该设备提供音频接收接口");

            connection.StateChanged += OnConnectionStateChanged;
            _connection = connection;

            Log.Info($"准备接收: {Log.MaskId(deviceId)}");

            // 关键顺序：先把系统置为「允许远端音频接入」(StartAsync)，
            // 再主动发起连接 (OpenAsync)。少了第一步，Open 会被系统直接拒掉。
            // 蓝牙栈偶尔会卡在这里不返回：不设超时的话，命令会永远处于「执行中」，
            // 界面上那个按钮从此点不动，用户只会觉得「点了没反应」。
            var startTask = connection.StartAsync().AsTask();
            if (!await FinishesWithin(startTask, 10))
            {
                Log.Warn("StartAsync 超过 10 秒没有返回");
                Cleanup();
                Report(ConnState.Error, "连接失败",
                    "系统那边一直没响应。把蓝牙关一下再打开，然后重试。");
                return;
            }

            AudioPlaybackConnectionOpenResultStatus? openStatus = null;
            try
            {
                var result = await connection.OpenAsync();
                openStatus = result.Status;
                Log.Info($"OpenAsync 结果 = {result.Status}，当前状态 = {connection.State}");
            }
            catch (Exception ex)
            {
                // StartAsync 已经让接收端就绪时，Open 可能抛出空引用之类的异常，这里不算失败
                Log.Warn($"OpenAsync 抛出异常（忽略，以状态为准）: {ex.Message}");
            }

            if (connection.State == AudioPlaybackConnectionState.Opened
                || openStatus == AudioPlaybackConnectionOpenResultStatus.Success)
            {
                Report(ConnState.Connected, "已连接", "声音已接到电脑");
                return;
            }

            var detail = openStatus switch
            {
                AudioPlaybackConnectionOpenResultStatus.RequestTimedOut =>
                    "手机一直没回应。确认手机蓝牙开着，人就在电脑旁边。",
                AudioPlaybackConnectionOpenResultStatus.DeniedBySystem =>
                    "系统拒绝了。多半是手机还连着耳机或音箱，或者别的接收软件占着位置。",
                AudioPlaybackConnectionOpenResultStatus.UnknownFailure => "这次没连上，再试一次。",
                _ => "没连上，再试一次。"
            };

            Cleanup();
            Report(ConnState.Error, "连接失败", detail);
        }
        catch (Exception ex)
        {
            Log.Error("连接失败", ex);
            Cleanup();
            Report(ConnState.Error, "连接失败", Describe(ex));
        }
    }

    public async Task DisconnectAsync(bool silent = false)
    {
        _intentionalClose = true;

        var connection = _connection;
        _connection = null;
        CurrentDeviceId = null;

        if (connection is not null)
        {
            if (!silent) Report(ConnState.Disconnecting, "断开中", null);

            try
            {
                connection.StateChanged -= OnConnectionStateChanged;
                connection.Dispose();
                Log.Info("连接已关闭");
            }
            catch (Exception ex)
            {
                Log.Error("断开连接时出错", ex);
            }
        }

        State = ConnState.Idle;
        if (!silent)
        {
            Report(ConnState.Idle, "未连接", "已断开。声音回到手机外放。");
        }

        await Task.CompletedTask;
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        // 已经被换掉或关掉的旧对象，它的回调可能晚一步才到。
        // 让它继续改状态，界面就会出现「明明断开了又变回已连接」。
        if (!ReferenceEquals(sender, _connection))
        {
            Log.Info($"忽略旧连接的状态回调: {sender.State}");
            return;
        }

        Log.Info($"连接状态变化: {sender.State}");

        if (sender.State == AudioPlaybackConnectionState.Opened)
        {
            Report(ConnState.Connected, "已连接", "声音已接到电脑");
            return;
        }

        var wasConnected = State == ConnState.Connected;
        Report(ConnState.Idle, "未连接", "连接断了");

        if (wasConnected && !_intentionalClose)
        {
            Log.Warn("连接被非预期断开");
            DroppedUnexpectedly?.Invoke(this, CurrentDeviceId ?? string.Empty);
        }
    }

    private void Report(ConnState state, string message, string? detail)
    {
        State = state;
        StateChanged?.Invoke(this, new ConnectionStateEventArgs { State = state, Message = message, Detail = detail });
    }

    private void Cleanup()
    {
        if (_connection is null) return;

        try
        {
            _connection.StateChanged -= OnConnectionStateChanged;
            _connection.Dispose();
        }
        catch
        {
            // 清理阶段忽略异常
        }

        _connection = null;
    }

    /// <summary>等一个任务，超时就当它没完成。用来兜住卡死的 WinRT 异步调用。</summary>
    private static async Task<bool> FinishesWithin(Task task, int seconds)
        => await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) == task;

    private static string Describe(Exception ex)
    {
        var hr = ex.HResult;

        return hr switch
        {
            unchecked((int)0x80070490) => "找不到这台手机。看看手机蓝牙开没开、在不在附近。",
            unchecked((int)0x8007048F) => "手机和电脑还没连上。先去系统的蓝牙设置里连一下。",
            unchecked((int)0x80070005) => "系统挡下了这次连接，检查一下蓝牙权限。",
            unchecked((int)0x8007048E) => "电脑的蓝牙是关着的，先打开它。",
            _ => ex.Message
        };
    }

    public void Dispose() => Cleanup();
}
