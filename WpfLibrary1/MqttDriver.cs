using MQTTnet; // v5.x 統一使用此命名空間，不再需要 MQTTnet.Client
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfLibrary1;

public class MqttDriver : IPlcDriver
{
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _mqttOptions;
    private readonly string _brokerIp;
    private readonly int _port;

    // 訂閱與發佈的 MQTT 主題 (設為 "#" 代表接收模擬器發出的所有頻道)
    private readonly string _statusTopic = "factory/line1/plc01/status";
    private readonly string _commandTopic = "factory/line1/plc01/command";

    // 執行緒安全的記憶體快取
    private readonly ConcurrentDictionary<string, double> _latestData = new();

    public bool IsConnected => _mqttClient?.IsConnected ?? false;

    public MqttDriver(string brokerIp, int port = 1883)
    {
        _brokerIp = brokerIp;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        // v5.x 改用 MqttClientFactory
        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        // 1. 註冊收到 MQTT 訊息的非同步回呼事件
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        // 2. 配置連線選項
        _mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_brokerIp, _port)
            .WithClientId($"CSharp_SCADA_{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(5))
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _mqttClient.ConnectAsync(_mqttOptions, cts.Token);

        // 3. 連線成功後，立即訂閱機台狀態主題
        var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_statusTopic))
            .Build();

        await _mqttClient.SubscribeAsync(subscribeOptions, CancellationToken.None);
    }

    /// <summary>
    /// 解析 Edge Gateway 推播過來的 JSON 封包並更新快取
    /// </summary>
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string payload = e.ApplicationMessage.ConvertPayloadToString();

            if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith('{'))
            {
                return Task.CompletedTask;
            }

            using JsonDocument doc = JsonDocument.Parse(payload);

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double numVal))
                {
                    _latestData[prop.Name] = numVal;
                }
                else if (prop.Value.ValueKind == JsonValueKind.True)
                {
                    _latestData[prop.Name] = 1.0;
                }
                else if (prop.Value.ValueKind == JsonValueKind.False)
                {
                    _latestData[prop.Name] = 0.0;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MQTT JSON 解析異常] {ex.Message}");
        }

        return Task.CompletedTask;
    }

    // =========================================================================
    // 1. 實作固定業務方法
    // =========================================================================

    public Task<MachineStatus> ReadMachineStatusAsync()
    {
        if (!IsConnected)
            throw new InvalidOperationException("MQTT Broker 尚未連線。");

        _latestData.TryGetValue("Spindle_Speed", out double speed);
        _latestData.TryGetValue("Motor_Temp", out double temp);
        _latestData.TryGetValue("Is_Running", out double running);
        _latestData.TryGetValue("Overheat_Alarm", out double alarm);
        _latestData.TryGetValue("Cmd_Ack_Done", out double ack);

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
        return PublishJsonCommandAsync(new { Target_Speed = speed });
    }

    public Task WriteStartTriggerAsync(bool isOn)
    {
        return PublishJsonCommandAsync(new { Cmd_Start = isOn ? 1 : 0 });
    }

    public Task WriteHeartbeatAsync(bool state)
    {
        return PublishJsonCommandAsync(new { PC_Heartbeat = state ? 1 : 0 });
    }

    // =========================================================================
    // 2. 實作動態點位讀寫方法
    // =========================================================================

    public Task<double> ReadTagAsync(string address, string dataType)
    {
        if (!IsConnected)
            throw new InvalidOperationException("MQTT Broker 尚未連線。");

        // 直接從最新收到的 JSON 快取字典中，根據點位名稱 (address) 取出數值
        _latestData.TryGetValue(address, out double val);
        return Task.FromResult(val);
    }

    public Task WriteTagAsync(string address, object value)
    {
        if (!IsConnected)
            throw new InvalidOperationException("MQTT Broker 尚未連線。");

        // 依據寫入的位址，組裝對應的 JSON 指令發送給 Edge Gateway
        var payloadDict = new System.Collections.Generic.Dictionary<string, object>
        {
            [address] = value
        };

        return PublishJsonCommandAsync(payloadDict);
    }

    // =========================================================================
    // 3. 通用 JSON 指令發佈器
    // =========================================================================

    private async Task PublishJsonCommandAsync(object payloadObject)
    {
        if (!IsConnected || _mqttClient == null) return;

        string jsonString = JsonSerializer.Serialize(payloadObject);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(_commandTopic)
            .WithPayload(jsonString)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(message, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_mqttClient != null)
        {
            _mqttClient.DisconnectAsync().Wait(1000);
            _mqttClient.Dispose();
            _mqttClient = null;
        }
    }
}
