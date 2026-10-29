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
        Report(ConnState.Connecting, "正在连接…", "正在等待手机接受连接…");

        try
        {
            var connection = AudioPlaybackConnection.TryCreateFromId(deviceId)
                             ?? throw new InvalidOperationException("系统没有为该设备提供音频接收接口");

            connection.StateChanged += OnConnectionStateChanged;
            _connection = connection;

            Log.Info($"准备接收: {deviceId}");

            // 关键顺序：先把系统置为「允许远端音频接入」(StartAsync)，
            // 再主动发起连接 (OpenAsync)。少了第一步，Open 会被系统直接拒掉。
            await connection.StartAsync();

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
                Report(ConnState.Connected, "已连接", "手机的声音正从电脑播放");
                return;
            }

            var detail = openStatus switch
            {
                AudioPlaybackConnectionOpenResultStatus.RequestTimedOut =>
                    "连接超时：手机没有在时限内回应。请确认手机蓝牙已打开、就在电脑附近。",
                AudioPlaybackConnectionOpenResultStatus.DeniedBySystem =>
                    "系统拒绝了这次连接。常见原因是手机正连着别的音频设备，或已有另一个蓝牙音频程序占用了接收端。",
                AudioPlaybackConnectionOpenResultStatus.UnknownFailure => "连接失败，请重试。",
                _ => "连接没有建立，请重试。"
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
            if (!silent) Report(ConnState.Disconnecting, "正在断开…", null);

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
            Report(ConnState.Idle, "未连接", "已断开，手机的声音回到手机");
        }

        await Task.CompletedTask;
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        Log.Info($"连接状态变化: {sender.State}");

        if (sender.State == AudioPlaybackConnectionState.Opened)
        {
            Report(ConnState.Connected, "已连接", "手机的声音正从电脑播放");
            return;
        }

        var wasConnected = State == ConnState.Connected;
        Report(ConnState.Idle, "未连接", "连接已断开");

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

    private static string Describe(Exception ex)
    {
        var hr = ex.HResult;

        return hr switch
        {
            unchecked((int)0x80070490) => "系统里找不到这台设备，请确认手机蓝牙已打开并且就在附近。",
            unchecked((int)0x8007048F) => "设备未连接，请先在系统蓝牙设置里连上手机。",
            unchecked((int)0x80070005) => "系统拒绝了连接请求，请检查蓝牙权限。",
            unchecked((int)0x8007048E) => "蓝牙已关闭，请先打开电脑的蓝牙开关。",
            _ => ex.Message
        };
    }

    public void Dispose() => Cleanup();
}
