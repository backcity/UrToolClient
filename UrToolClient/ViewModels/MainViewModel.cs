using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using UrToolClient.Services;
using UrToolClient.Views;

namespace UrToolClient.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly UrRobotControl _urRobotControl;
        private readonly ILogger<MainViewModel> _logger;
        private readonly IServiceProvider _serviceProvider;

        // ── 导航 ──────────────────────────────────────────────
        [ObservableProperty] private object? _currentView = null;
        // 预分配读取缓冲，与 UrRobotControl 配合实现零 GC
        private readonly double[] _tcpBuf = new double[6];
        private readonly double[] _jointBuf = new double[6];

        private CancellationTokenSource? _pollCts;

        // ── 连接状态 ──────────────────────────────────────────
        [ObservableProperty] private bool _isConnected;
        [ObservableProperty] private string _connectionStatus = "未连接";
        [ObservableProperty] private string _ipAddress = "10.60.52.2";

        [ObservableProperty] private string _tcpCopyHint = "";
        [ObservableProperty] private string _jointCopyHint = "";

        // ── TCP 位姿属性 ───────────────────────────────────────
        [ObservableProperty] private double _tcpX;
        [ObservableProperty] private double _tcpY;
        [ObservableProperty] private double _tcpZ;
        [ObservableProperty] private double _tcpRX;
        [ObservableProperty] private double _tcpRY;
        [ObservableProperty] private double _tcpRZ;

        // ── 关节角度属性 ───────────────────────────────────────
        [ObservableProperty] private double _jointBase;
        [ObservableProperty] private double _jointShoulder;
        [ObservableProperty] private double _jointElbow;
        [ObservableProperty] private double _jointWrist1;
        [ObservableProperty] private double _jointWrist2;
        [ObservableProperty] private double _jointWrist3;

        public MainViewModel(UrRobotControl urRobotControl, ILogger<MainViewModel> logger, IServiceProvider serviceProvider)
        {
            _urRobotControl = urRobotControl;
            _logger = logger;
            _serviceProvider = serviceProvider;
            CurrentView = _serviceProvider.GetRequiredService<CalibrationPage>();
        }

        // ══════════════════════════════════════════════════════
        // 连接 / 断开
        // ══════════════════════════════════════════════════════

        [RelayCommand]
        private async Task ConnectAsync()
        {
            ConnectionStatus = "连接中…";
            bool ok = await Task.Run(() => _urRobotControl.Connect(IpAddress));
            IsConnected = ok;
            ConnectionStatus = ok ? $"已连接 {IpAddress}" : "连接失败";

            if (ok)
                StartPolling();
        }

        [RelayCommand]
        private void Disconnect()
        {
            StopPolling();
            _urRobotControl.Disconnect();
            IsConnected = false;
            ConnectionStatus = "未连接";
        }

        [RelayCommand]
        private void CopyTcp()
        {
            var text = $"p[{TcpX:F4},{TcpY:F4},{TcpZ:F4},{TcpRX:F4},{TcpRY:F4},{TcpRZ:F4}]";
            Clipboard.SetText(text);
            TcpCopyHint = "✓ 已复制";
            Task.Delay(2000).ContinueWith(_ =>
                Application.Current.Dispatcher.Invoke(() => TcpCopyHint = ""));
        }

        [RelayCommand]
        private void CopyJoint()
        {
            // 度数（界面显示单位）
            var deg = $"[{JointBase:F2},{JointShoulder:F2},{JointElbow:F2},{JointWrist1:F2},{JointWrist2:F2},{JointWrist3:F2}]";

            // rad（度数转弧度）
            static double ToRad(double d) => d * Math.PI / 180.0;
            var rad = $"[{ToRad(JointBase):F4},{ToRad(JointShoulder):F4},{ToRad(JointElbow):F4},{ToRad(JointWrist1):F4},{ToRad(JointWrist2):F4},{ToRad(JointWrist3):F4}]";

            Clipboard.SetText($"{deg}\n{rad}");

            JointCopyHint = "✓ 已复制";
            Task.Delay(2000).ContinueWith(_ =>
                Application.Current.Dispatcher.Invoke(() => JointCopyHint = ""));
        }

        [RelayCommand]
        private void ChangePage(string pageName)
        {
            CurrentView = pageName switch
            {
                "CalibrationView" => _serviceProvider.GetRequiredService<CalibrationPage>(),
                "VariablesView" => _serviceProvider.GetRequiredService<VariablesPage>(),
                "SimulationView" => _serviceProvider.GetRequiredService<SimulationPage>(),
                // 其他页面可以在这里添加分支
                _ => CurrentView
            };
        }

        // ══════════════════════════════════════════════════════
        // 状态轮询（后台线程，固定 100 ms 间隔，零 GC）
        // ══════════════════════════════════════════════════════

        private void StartPolling()
        {
            StopPolling();
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            _ = Task.Run(() => PollLoop(token), token);
        }

        private void StopPolling()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        private async Task PollLoop(CancellationToken token)
        {

            while (!token.IsCancellationRequested)
            {
                if (_urRobotControl.IsConnected)
                {
                    _urRobotControl.PollState();

                    // 零拷贝读入预分配缓冲
                    _urRobotControl.GetTCPPose(_tcpBuf);
                    _urRobotControl.GetActualQ(_jointBuf);

                    // 切回 UI 线程更新属性（只在此处产生一次调度）
                    Application.Current?.Dispatcher.BeginInvoke(UpdateProperties);
                }
                else
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        IsConnected = false;
                        ConnectionStatus = "连接已断开";
                    });
                    break;
                }

                try { await Task.Delay(100, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        // 仅在 UI 线程调用，直接赋值避免装箱
        private void UpdateProperties()
        {
            static double ToDeg(double r) => r * 180.0 / Math.PI;
            TcpX = _tcpBuf[0] * 1000; TcpY = _tcpBuf[1] * 1000; TcpZ = _tcpBuf[2] * 1000;
            TcpRX = _tcpBuf[3]; TcpRY = _tcpBuf[4]; TcpRZ = _tcpBuf[5];

            JointBase = ToDeg(_jointBuf[0]); JointShoulder = ToDeg(_jointBuf[1]);
            JointElbow = ToDeg(_jointBuf[2]); JointWrist1 = ToDeg(_jointBuf[3]);
            JointWrist2 = ToDeg(_jointBuf[4]); JointWrist3 = ToDeg(_jointBuf[5]);
        }
    }
}
