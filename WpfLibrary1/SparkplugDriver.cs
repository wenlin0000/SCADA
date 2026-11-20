using MQTTnet;
using S7.Net; // 安裝 NuGet: S7NetPlus
using SparkplugNet.Core.Application;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;
using System.Collections.Concurrent;
using WpfLibrary1;

public class SparkplugDriver : IPlcDriver
{
    private SparkplugApplication? _application;
    private readonly string _brokerIp;
    private readonly int _port;
    private readonly string _groupId = "Line1";
    private readonly string _edgeNodeId = "Gateway01";
    private readonly string _deviceId = "Mitsubishi_FX5U";

    private readonly ConcurrentDictionary<string, double> _metricsCache = new();

    public bool IsDeviceOnline { get; private set; } = false;
    public bool IsConnected => _application?.IsConnected ?? false;

    public SparkplugDriver(string brokerIp, int port = 1883)
    {
        _brokerIp = brokerIp;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        var knownMetrics = new List<Metric>
        {
            new Metric(Param.Spindle_Speed, SparkplugNet.VersionB.Data.DataType.Int16, (short)0),
            new Metric(Param.Motor_Temp, SparkplugNet.VersionB.Data.DataType.Int16, (short)0),
            new Metric(Param.Is_Running, SparkplugNet.VersionB.Data.DataType.Boolean, false),
            new Metric(Param.Overheat_Alarm, SparkplugNet.VersionB.Data.DataType.Boolean, false),
            new Metric(Param.Cmd_Ack_Done, SparkplugNet.VersionB.Data.DataType.Boolean, false)
        };

        // 新版 SparkplugNet 已預設採用 3.0 規範，不需再傳入 SparkplugSpecificationVersion
        _application = new SparkplugApplication(knownMetrics, SparkplugSpecificationVersion.Version22);

        // 1. 監聽設備上線 (DBIRTH) 與數據更新 (DDATA)
        _application.DeviceBirthReceived += args =>
        {
            IsDeviceOnline = true;
            UpdateMetricsCache(args.Metrics);
            return Task.CompletedTask;
        };

        _application.DeviceDataReceived += args =>
        {
            UpdateMetricsCache(args.Metrics);
            return Task.CompletedTask;
        };

        // 2. 監聽設備離線遺囑 (DDEATH)
        _application.DeviceDeathReceived += args =>
        {
            IsDeviceOnline = false;
            Console.WriteLine($"⚠️ [Sparkplug 警報] 設備 {args.DeviceIdentifier} 已離線，數據標記為無效！");
            return Task.CompletedTask;
        };

        // 3. 新版 SparkplugApplicationOptions 參數配置
        bool enableTls = false; // 可從 appsettings.json 讀取
        var options = new SparkplugApplicationOptions(
            brokerAddress: _brokerIp,
            port: enableTls ? 8883 : 1883, // 啟用 TLS 時通常為 8883 埠（若不開 TLS 則填 1883）
            clientId: $"CSharp_SCADA_Host_{Guid.NewGuid():N}",
            userName: "scada_user",
            password: "your_password",
            scadaHostIdentifier: "Main_SCADA_Host",
            reconnectInterval: TimeSpan.FromSeconds(3),
            mqttProtocolVersion: SparkplugMqttProtocolVersion.V311,
            isPrimaryApplication: true,

            // 精準對應你貼出的原始碼參數名稱：mqttTlsOptions
            mqttTlsOptions: new MqttClientTlsOptions
            {
                UseTls = true,
                // 開發測試階段若使用自簽憑證可開啟以下選項，正式環境建議改為 false 並匯入合法憑證
                AllowUntrustedCertificates = true,
                IgnoreCertificateChainErrors = true,
                IgnoreCertificateRevocationErrors = true
            }
        );

        await _application.Start(options);
    }

    private void UpdateMetricsCache(IEnumerable<Metric> metrics)
    {
        foreach (var m in metrics)
        {
            if (m.Value is bool bVal)
            {
                _metricsCache[m.Name] = bVal ? 1.0 : 0.0;
            }
            else if (m.Value != null && double.TryParse(m.Value.ToString(), out double val))
            {
                _metricsCache[m.Name] = val;
            }
        }
    }

    public Task<MachineStatus> ReadMachineStatusAsync()
    {
        if (!IsDeviceOnline)
            throw new Exception("Sparkplug 目標設備處於離線 (DDEATH) 狀態。");

        _metricsCache.TryGetValue(Param.Spindle_Speed, out double speed);
        _metricsCache.TryGetValue(Param.Motor_Temp, out double temp);
        _metricsCache.TryGetValue(Param.Is_Running, out double running);
        _metricsCache.TryGetValue(Param.Overheat_Alarm, out double alarm);
        _metricsCache.TryGetValue(Param.Cmd_Ack_Done, out double ack);

        return Task.FromResult(new MachineStatus
        {
            ActualSpeed = (short)speed,
            Temperature = (short)temp,
            IsRunning = running > 0,
            IsOverheatAlarm = alarm > 0,
            IsCmdAckDone = ack > 0
        });
    }

    public Task WriteTargetSpeedAsync(short speed)
    {
        return PublishDeviceCommandAsync(new Metric("Target_Speed", SparkplugNet.VersionB.Data.DataType.Int16, speed));
    }

    public Task WriteStartTriggerAsync(bool isOn)
    {
        return PublishDeviceCommandAsync(new Metric("Cmd_Start", SparkplugNet.VersionB.Data.DataType.Boolean, isOn));
    }

    public Task WriteHeartbeatAsync(bool state)
    {
        return PublishDeviceCommandAsync(new Metric("PC_Heartbeat", SparkplugNet.VersionB.Data.DataType.Boolean, state));
    }

    private async Task PublishDeviceCommandAsync(Metric commandMetric)
    {
        if (!IsConnected || _application == null) return;

        await _application.PublishDeviceCommand(
            metrics: new List<Metric> { commandMetric },
            groupIdentifier: _groupId,
            edgeNodeIdentifier: _edgeNodeId,
            deviceIdentifier: _deviceId
        );
    }

    public void Dispose()
    {
        _application?.Stop().Wait(1000);
    }
}
