using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Windows;
using WpfLibrary1;

namespace MyFactoryScada
{
    public class MainViewModel : ObservableObject
    {
        private readonly ScadaEngine _scadaEngine;
        private readonly SqlServerSecurityAndLogService _logService;
        private readonly SqlServerTelemetryHistorian _historian;

        // =====================================================================
        // 1. 明確定義綁定至畫面的屬性 (使用 SetProperty，100% 不會產生器失效)
        // =====================================================================

        private bool _isConnected;
        // 供 MainWindow.xaml 指示燈綁定的別名屬性
        public bool IsPlcConnected => IsConnected;
        public double Temperature => MotorTemp;
        public IRelayCommand SendSpeedCommandCommand { get; }
        public bool IsConnected
        {
            get => _isConnected;
            //set => SetProperty(ref _isConnected, value);
            set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    // 同步通知 XAML 裡綁定 IsPlcConnected 的指示燈更新變色
                    OnPropertyChanged(nameof(IsPlcConnected));
                }
            }

        }

        private string _connectionStatusText = "🔴 PLC 斷線中";
        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            set => SetProperty(ref _connectionStatusText, value);
        }

        private double _actualSpeed;
        public double ActualSpeed
        {
            get => _actualSpeed;
            set => SetProperty(ref _actualSpeed, value);
        }

        private double _motorTemp;
        public double MotorTemp
        {
            get => _motorTemp;
            //set => SetProperty(ref _motorTemp, value);
            set
            {
                if (SetProperty(ref _motorTemp, value))
                {
                    OnPropertyChanged(nameof(Temperature)); // 同步通知溫度顯示更新
                }
            }
        }

        private short _targetSpeedInput = 1500;
        public short TargetSpeedInput
        {
            get => _targetSpeedInput;
            //set => SetProperty(ref _targetSpeedInput, value);
            set
            {
                if (SetProperty(ref _targetSpeedInput, value))
                {
                    OnPropertyChanged(nameof(InputTargetSpeed)); // 同步通知輸入框更新
                }
            }
        }
        public short InputTargetSpeed
        {
            get => TargetSpeedInput;
            set => TargetSpeedInput = value;
        }
        private string _systemMessage = "系統就緒，等待連線...";
        public string SystemMessage
        {
            get => _systemMessage;
            set => SetProperty(ref _systemMessage, value);
        }

        // 供 MainWindow.xaml.cs 圖表訂閱的即時數據更新事件
        public event Action<double, double>? OnTrendDataUpdated;

        // =====================================================================
        // 2. 明確定義畫面按鈕綁定指令 (I RelayCommand)
        // =====================================================================

        public IRelayCommand SetTargetSpeedCommand { get; }
        public IRelayCommand StartMachineCommand { get; }
        public IRelayCommand StopMachineCommand { get; }

        // =====================================================================
        // 3. 建構子
        // =====================================================================

        public MainViewModel(
            ScadaEngine scadaEngine,
            SqlServerSecurityAndLogService logService,
            SqlServerTelemetryHistorian historian)
        {
            _scadaEngine = scadaEngine;
            _logService = logService;
            _historian = historian;

            // 初始化按鈕指令
            SetTargetSpeedCommand = new RelayCommand(SetTargetSpeed);
            StartMachineCommand = new RelayCommand(StartMachine);
            StopMachineCommand = new RelayCommand(StopMachine);
            SendSpeedCommandCommand = SetTargetSpeedCommand;

            // 訂閱底層通訊引擎的狀態與數據事件
            _scadaEngine.OnConnectionChanged += OnConnectionChanged;
            _scadaEngine.OnDataUpdated += OnDataUpdated;
        }

        // =====================================================================
        // 4. 事件處理與控制邏輯
        // =====================================================================

        private void OnConnectionChanged(bool connected)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = connected;
                ConnectionStatusText = connected ? "🟢 PLC 已連線" : "🔴 PLC 斷線中 (自動重連...)";
                SystemMessage = connected ? "通訊恢復正常" : "警告：與現場設備失去連線！";
            });
        }

        private void OnDataUpdated(Dictionary<string, double> data)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (data.TryGetValue("Speed", out double speed))
                {
                    ActualSpeed = speed;
                    _historian.EnqueueData("Station01_Mitsu", Param.Spindle_Speed, speed);
                }

                if (data.TryGetValue("Temp", out double temp))
                {
                    MotorTemp = temp;
                    _historian.EnqueueData("Station01_Mitsu", Param.Motor_Temp, temp);
                }

                OnTrendDataUpdated?.Invoke(ActualSpeed, MotorTemp);
            });
        }

        private void SetTargetSpeed()
        {
            if (!IsConnected)
            {
                SystemMessage = "⚠️ 操作失敗：PLC 尚未連線！";
                return;
            }

            if (TargetSpeedInput < 0 || TargetSpeedInput > 3000)
            {
                SystemMessage = "⚠️ 安全攔截：目標轉速必須介於 0 ~ 3000 RPM！";
                return;
            }

            _logService.LogAction("修改目標轉速", "Target_Speed", TargetSpeedInput.ToString());
            _scadaEngine.EnqueueWriteCommand("D100", TargetSpeedInput);
            SystemMessage = $"✅ 已下發目標轉速: {TargetSpeedInput} RPM";
        }

        private void StartMachine()
        {
            if (!IsConnected) return;
            _logService.LogAction("啟動機台", "Cmd_Start", "1");
            _scadaEngine.EnqueueWriteCommand("Cmd_Start", 1);
            SystemMessage = "✅ 已發送啟動指令 (Cmd_Start = 1)";
        }

        private void StopMachine()
        {
            if (!IsConnected) return;
            _logService.LogAction("停止機台", "Cmd_Start", "0");
            _scadaEngine.EnqueueWriteCommand("Cmd_Start", 0);
            SystemMessage = "🛑 已發送停止指令 (Cmd_Start = 0)";
        }
    }
}
