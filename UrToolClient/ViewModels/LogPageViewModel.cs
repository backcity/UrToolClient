using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using UrToolClient.Services.Log;

namespace UrToolClient.ViewModels
{
    public partial class LogPageViewModel : ObservableObject
    {
        private const int MaxRows = 500;

        public ObservableCollection<LogEntry> Logs { get; } = new ObservableCollection<LogEntry>();

        // View 订阅此事件来触发滚动
        public event Action? ScrollToBottomRequested;
        public LogPageViewModel()
        {
            // 订阅日志服务的事件
            LogBroker.Instance.LogReceived += AddLogToUI;
        }

        private void AddLogToUI(LogEntry logEntry)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Logs.Add(logEntry);
                if (Logs.Count > MaxRows)
                    Logs.RemoveAt(0);

                ScrollToBottomRequested?.Invoke();
            });
        }

        [RelayCommand]
        private void ClearLogs()
        {
            Logs.Clear();
        }
    }
}
