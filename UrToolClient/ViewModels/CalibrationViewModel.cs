using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using UrToolClient.Models;
using UrToolClient.Services;

namespace UrToolClient.ViewModels
{
    public partial class CalibrationViewModel : ObservableObject
    {
        private readonly UrRobotControl _robot;
        private readonly ILogger<CalibrationViewModel> _logger;

        // 预分配，避免记录时 GC
        private readonly double[] _snapBuf = new double[6];

        // ── 状态 ────────────────────────────────────────────────
        [ObservableProperty] private bool _isFreedriveActive;
        [ObservableProperty] private string _freedriveButtonText = "开启自由驱动";
        [ObservableProperty] private string _statusText = "就绪。请开启自由驱动后拖动机械臂到目标位置，再点击「记录当前点位」。";
        [ObservableProperty] private TeachPoint? _selectedPoint;
        [ObservableProperty] private string _newPointName = "Point_1";

        // ── 点位列表 ─────────────────────────────────────────────
        public ObservableCollection<TeachPoint> Points { get; } = new();

        public CalibrationViewModel(UrRobotControl robot, ILogger<CalibrationViewModel> logger)
        {
            _robot  = robot;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════
        // 自由驱动开关
        // ══════════════════════════════════════════════════════════

        [RelayCommand]
        private void ToggleFreedriveMode()
        {
            if (!_robot.IsConnected)
            {
                StatusText = "⚠ 机器人未连接，请先连接后再操作。";
                return;
            }

            if (!IsFreedriveActive)
            {
                bool ok = _robot.FreedriveModeDefault();
                if (ok)
                {
                    IsFreedriveActive   = true;
                    FreedriveButtonText = "关闭自由驱动";
                    StatusText          = "✅ 自由驱动已开启，可拖动机械臂到目标位置。";
                    _logger.LogInformation("Freedrive mode enabled");
                }
                else
                {
                    StatusText = "❌ 开启自由驱动失败，请检查机器人状态。";
                }
            }
            else
            {
                bool ok = _robot.EndFreedriveMode();
                if (ok)
                {
                    IsFreedriveActive   = false;
                    FreedriveButtonText = "开启自由驱动";
                    StatusText          = "自由驱动已关闭。";
                    _logger.LogInformation("Freedrive mode disabled");
                }
                else
                {
                    StatusText = "❌ 关闭自由驱动失败。";
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        // 记录当前点位
        // ══════════════════════════════════════════════════════════

        [RelayCommand]
        private void RecordPoint()
        {
            if (!_robot.IsConnected)
            {
                StatusText = "⚠ 机器人未连接。";
                return;
            }

            _robot.GetActualQ(_snapBuf);

            string name = string.IsNullOrWhiteSpace(NewPointName)
                ? $"Point_{Points.Count + 1}"
                : NewPointName;

            var pt = new TeachPoint(Points.Count + 1, name, _snapBuf);
            Points.Add(pt);
            SelectedPoint = pt;
            NewPointName  = $"Point_{Points.Count + 1}";
            StatusText    = $"✅ 已记录点位 [{pt.Name}]：{pt.ToDegreesString()}";
            CopyAllPointsCommand.NotifyCanExecuteChanged();
            _logger.LogInformation("Recorded teach point [{Name}] {Joints}", pt.Name, pt.ToUrScriptRad());
        }

        // ══════════════════════════════════════════════════════════
        // 删除选中点位
        // ══════════════════════════════════════════════════════════

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private void DeletePoint()
        {
            if (SelectedPoint is not { } pt) return;
            Points.Remove(pt);
            for (int i = 0; i < Points.Count; i++) Points[i].Index = i + 1;
            SelectedPoint = null;
            StatusText    = $"已删除点位 [{pt.Name}]。";
            CopyAllPointsCommand.NotifyCanExecuteChanged();
            _logger.LogInformation("Deleted teach point [{Name}]", pt.Name);
        }

        // ══════════════════════════════════════════════════════════
        // 复制选中点位到剪贴板
        // ══════════════════════════════════════════════════════════

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private void CopyPoint()
        {
            if (SelectedPoint is not { } pt) return;
            string text = $"# {pt.Name}  ({pt.RecordedAt:HH:mm:ss})\n"
                        + $"deg: {pt.ToDegreesString()}\n"
                        + $"rad: {pt.ToUrScriptRad()}";
            Clipboard.SetText(text);
            StatusText = $"✅ 已复制点位 [{pt.Name}] 到剪贴板。";
        }

        // ══════════════════════════════════════════════════════════
        // 复制全部点位（URScript 格式）
        // ══════════════════════════════════════════════════════════

        [RelayCommand(CanExecute = nameof(HasAnyPoints))]
        private void CopyAllPoints()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 仓位标定示教点位列表");
            foreach (var pt in Points)
                sb.AppendLine($"{pt.Name} = {pt.ToUrScriptRad()}  # {pt.ToDegreesString()}");
            Clipboard.SetText(sb.ToString());
            StatusText = $"✅ 已复制全部 {Points.Count} 个点位到剪贴板。";
        }

        // ══════════════════════════════════════════════════════════
        // 回放选中点位（MoveJ 异步，不阻塞 UI）
        // ══════════════════════════════════════════════════════════

        [RelayCommand(CanExecute = nameof(CanPlayback))]
        private Task PlaybackPoint()
        {
            if (SelectedPoint is not { } pt) return Task.CompletedTask;
            bool ok = _robot.MoveJ(pt.JointRad, 0.3, 0.3);
            StatusText = ok
                ? $"▶ 正在运动到点位 [{pt.Name}]…"
                : "❌ 发送 MoveJ 失败，请检查机器人状态。";
            if (ok) _logger.LogInformation("MoveJ to [{Name}] {Joints}", pt.Name, pt.ToUrScriptRad());

            return Task.CompletedTask;
        }

        // ── CanExecute ──────────────────────────────────────────
        private bool HasSelection() => SelectedPoint != null;
        private bool HasAnyPoints() => Points.Count > 0;
        private bool CanPlayback()  => SelectedPoint != null && _robot.IsConnected;

        partial void OnSelectedPointChanged(TeachPoint? value)
        {
            DeletePointCommand.NotifyCanExecuteChanged();
            CopyPointCommand.NotifyCanExecuteChanged();
            PlaybackPointCommand.NotifyCanExecuteChanged();
        }
    }
}
