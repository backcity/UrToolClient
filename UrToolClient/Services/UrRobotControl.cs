using CodingBase.DataAccess.URtde;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace UrToolClient.Services;

/// <summary>
/// 高性能零 GC 封装。所有热路径缓冲区均为预分配实例字段，不在调用时产生堆分配。
/// 线程安全：Connect/Disconnect 使用锁，状态读取/运动指令可在单一控制线程中无锁调用。
/// </summary>
public sealed class UrRobotControl : IDisposable
{
    // ── 依赖 ────────────────────────────────────────────────
    private readonly ILogger<UrRobotControl> _logger;

    // ── 机器人连接 ──────────────────────────────────────────
    private UrRobot? _robot;
    private readonly object _connectLock = new();
    private bool _disposed;

    // ── 预分配缓冲区（零 GC 核心）──────────────────────────
    // 关节空间 6 轴
    private readonly double[] _actualQ   = new double[6];
    private readonly double[] _actualQd  = new double[6];
    private readonly double[] _targetQ   = new double[6];
    // TCP 空间
    private readonly double[] _tcpPose   = new double[6];
    private readonly double[] _tcpSpeed  = new double[6];
    private readonly double[] _tcpForce  = new double[6];
    // 运动指令复用缓冲（调用方写入后由本类转发，无需再拷贝）
    private readonly double[] _cmdBuf    = new double[6];
    // 力控
    private readonly double[] _wrench    = new double[6];
    private readonly double[] _ftLimits  = new double[6];
    private readonly double[] _taskFrame = new double[6];
    private readonly int[]    _selVec    = new int[6];

    // ── 缓存的状态（供 UI 轮询，避免每帧都调 P/Invoke）─────
    private volatile RobotMode    _robotMode    = RobotMode.Disconnected;
    private volatile SafetyMode   _safetyMode   = SafetyMode.Unknown;
    private volatile RuntimeState _runtimeState = RuntimeState.Stopped;

    // ── 公开属性 ────────────────────────────────────────────
    public bool IsConnected
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _robot is { } r && r.Receive.IsConnected;
    }

    public RobotMode    RobotMode    => _robotMode;
    public SafetyMode   SafetyMode   => _safetyMode;
    public RuntimeState RuntimeState => _runtimeState;

    public UrRobotControl(ILogger<UrRobotControl> logger)
    {
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════
    // 连接管理
    // ══════════════════════════════════════════════════════════

    public bool Connect(string ipAddress)
    {
        lock (_connectLock)
        {
            if (_robot != null) Disconnect();
            try
            {
                _robot = new UrRobot(ipAddress);
                _logger.LogInformation("Connected to UR robot at {IpAddress}", ipAddress);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to UR robot at {IpAddress}", ipAddress);
                return false;
            }
        }
    }

    public void Disconnect()
    {
        lock (_connectLock)
        {
            _robot?.Dispose();
            _robot = null;
            _robotMode    = RobotMode.Disconnected;
            _safetyMode   = SafetyMode.Unknown;
            _runtimeState = RuntimeState.Stopped;
            _logger.LogInformation("Disconnected from UR robot");
        }
    }

    // ══════════════════════════════════════════════════════════
    // 状态轮询（在控制循环中以固定频率调用，结果写入预分配缓冲）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 一次性刷新所有常用状态到内部缓冲区，供后续 GetXxx 零拷贝读取。
    /// 典型用法：在 500 Hz 控制线程开头调用一次。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PollState()
    {
        if (_robot is not { } r) return false;
        var recv = r.Receive;

        recv.TryGetActualQ(_actualQ);
        recv.TryGetActualQd(_actualQd);
        recv.TryGetActualTCPPose(_tcpPose);
        recv.TryGetActualTCPSpeed(_tcpSpeed);
        recv.TryGetActualTCPForce(_tcpForce);

        _robotMode    = recv.GetRobotMode();
        _safetyMode   = recv.GetSafetyMode();
        _runtimeState = recv.GetRuntimeState();

        return true;
    }

    // ── 零拷贝读取（调用方传入自己的 Span，避免额外分配）──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetActualQ(Span<double> dest)   => _actualQ.AsSpan().CopyTo(dest);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetActualQd(Span<double> dest)  => _actualQd.AsSpan().CopyTo(dest);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetTCPPose(Span<double> dest)   => _tcpPose.AsSpan().CopyTo(dest);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetTCPSpeed(Span<double> dest)  => _tcpSpeed.AsSpan().CopyTo(dest);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetTCPForce(Span<double> dest)  => _tcpForce.AsSpan().CopyTo(dest);

    // ══════════════════════════════════════════════════════════
    // 运动指令（ReadOnlySpan 入参，内部复用 _cmdBuf，零 GC）
    // ══════════════════════════════════════════════════════════

    /// <param name="q">目标关节角 [6]，rad</param>
    public bool MoveJ(ReadOnlySpan<double> q, double speed = 1.05, double acceleration = 1.4)
    {
        if (!EnsureControl(out var ctrl)) return false;
        q.CopyTo(_cmdBuf);
        return ctrl.MoveJ(_cmdBuf, speed, acceleration);
    }

    /// <param name="q">目标关节角 [6]，rad（异步，不阻塞）</param>
    public bool MoveJAsync(ReadOnlySpan<double> q, double speed = 1.05, double acceleration = 1.4)
    {
        if (!EnsureControl(out var ctrl)) return false;
        q.CopyTo(_cmdBuf);
        return ctrl.MoveJAsync(_cmdBuf, speed, acceleration);
    }

    /// <param name="pose">目标 TCP 位姿 [6]，m / rad</param>
    public bool MoveL(ReadOnlySpan<double> pose, double speed = 0.25, double acceleration = 1.2)
    {
        if (!EnsureControl(out var ctrl)) return false;
        pose.CopyTo(_cmdBuf);
        return ctrl.MoveL(_cmdBuf, speed, acceleration);
    }

    public bool MoveLAsync(ReadOnlySpan<double> pose, double speed = 0.25, double acceleration = 1.2)
    {
        if (!EnsureControl(out var ctrl)) return false;
        pose.CopyTo(_cmdBuf);
        return ctrl.MoveLAsync(_cmdBuf, speed, acceleration);
    }

    /// <summary>关节速度控制（ServoJ 之前的预备帧）</summary>
    public bool ServoJ(ReadOnlySpan<double> q, double speed, double acceleration,
                       double time, double lookaheadTime, double gain)
    {
        if (!EnsureControl(out var ctrl)) return false;
        q.CopyTo(_cmdBuf);
        return ctrl.ServoJ(_cmdBuf, speed, acceleration, time, lookaheadTime, gain);
    }

    /// <summary>关节速度流控</summary>
    public bool SpeedJ(ReadOnlySpan<double> qd, double acceleration = 0.5, double time = 0.0)
    {
        if (!EnsureControl(out var ctrl)) return false;
        qd.CopyTo(_cmdBuf);
        return ctrl.SpeedJ(_cmdBuf, acceleration, time);
    }

    public bool StopJ(double deceleration = 2.0)
        => EnsureControl(out var ctrl) && ctrl.StopJ(deceleration);

    public bool StopL(double deceleration = 2.0)
        => EnsureControl(out var ctrl) && ctrl.StopL(deceleration);

    public bool ServoStop(double a = 10.0)
        => EnsureControl(out var ctrl) && ctrl.ServoStop(a);

    public bool SpeedStop(double a = 10.0)
        => EnsureControl(out var ctrl) && ctrl.SpeedStop(a);

    public bool IsSteady()
        => EnsureControl(out var ctrl) && ctrl.IsSteady();

    // ══════════════════════════════════════════════════════════
    // 力控模式
    // ══════════════════════════════════════════════════════════

    /// <param name="taskFrame">[6] 力控参考系位姿</param>
    /// <param name="selectionVector">[6] 顺从轴选择 0/1</param>
    /// <param name="wrench">[6] 目标力/力矩</param>
    /// <param name="type">力框架解释方式 1/2/3</param>
    /// <param name="limits">[6] 顺从轴最大速度/非顺从轴最大偏差</param>
    public bool ForceMode(ReadOnlySpan<double> taskFrame, ReadOnlySpan<int> selectionVector,
                          ReadOnlySpan<double> wrench, int type, ReadOnlySpan<double> limits)
    {
        if (!EnsureControl(out var ctrl)) return false;
        taskFrame.CopyTo(_taskFrame);
        selectionVector.CopyTo(_selVec);
        wrench.CopyTo(_wrench);
        limits.CopyTo(_ftLimits);
        return ctrl.ForceMode(_taskFrame, _selVec, _wrench, type, _ftLimits);
    }

    public bool ForceModeStop()
        => EnsureControl(out var ctrl) && ctrl.ForceModeStop();

    // ══════════════════════════════════════════════════════════
    // 示教 / 自由驱动
    // ══════════════════════════════════════════════════════════

    public bool TeachMode()         => EnsureControl(out var ctrl) && ctrl.TeachMode();
    public bool EndTeachMode()      => EnsureControl(out var ctrl) && ctrl.EndTeachMode();
    public bool FreedriveModeDefault() => EnsureControl(out var ctrl) && ctrl.FreedriveModeDefault();
    public bool EndFreedriveMode()  => EnsureControl(out var ctrl) && ctrl.EndFreedriveMode();

    // ══════════════════════════════════════════════════════════
    // IO
    // ══════════════════════════════════════════════════════════

    public bool SetSpeedSlider(double speed)
        => _robot?.IO?.SetSpeedSlider(speed) ?? false;

    public bool SetDigitalOut(int id, bool high)
        => _robot?.IO?.SetDigitalOut(id, high) ?? false;

    public bool SetConfigDO(int id, bool high)
        => _robot?.IO?.SetConfigurableDigitalOut(id, high) ?? false;

    public bool SetToolDO(int id, bool high)
        => _robot?.IO?.SetToolDigitalOut(id, high) ?? false;

    public bool SetAnalogVoltage(int id, double ratio)
        => _robot?.IO?.SetAnalogOutputVoltage(id, ratio) ?? false;

    public bool SetAnalogCurrent(int id, double ratio)
        => _robot?.IO?.SetAnalogOutputCurrent(id, ratio) ?? false;

    // ══════════════════════════════════════════════════════════
    // 内部辅助
    // ══════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureControl(out UrControl ctrl)
    {
        ctrl = _robot?.Control!;
        return ctrl != null;
    }

    // ══════════════════════════════════════════════════════════
    // IDisposable
    // ══════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}