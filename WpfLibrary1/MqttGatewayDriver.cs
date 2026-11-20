using MQTTnet;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WpfLibrary1;

public class MqttGatewayDriver : IPlcDriver
{
    private IMqttClient? _mqttClient;
    private readonly string _brokerIp;
    private readonly int _port;

    // 暫存從 Edge Gateway 推播過來的最新點位數值
    private readonly ConcurrentDictionary<string, double> _latestValues = new();

    public bool IsConnected => _mqttClient?.IsConnected ?? false;

    public MqttGatewayDriver(string brokerIp, int port = 1883)
    {
        _brokerIp = brokerIp;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_brokerIp, _port)
            .WithClientId($"CSharp_SCADA_{Guid.NewGuid():N}")
            .Build();

        // 監聽 Edge Gateway 推送過來的 JSON 數據
        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            try
            {
                //var dataDict = JsonSerializer.Deserialize<ConcurrentDictionary<string, double>>(payload);
                using var doc = JsonDocument.Parse(payload);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double numVal))
                    {
                        _latestValues[prop.Name] = numVal;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.True)
                    {
                        _latestValues[prop.Name] = 1.0;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.False)
                    {
                        _latestValues[prop.Name] = 0.0;
                    }
                }
                /*
                if (dataDict != null)
                {
                    foreach (var kv in dataDict)
                    {
                        _latestValues[kv.Key] = kv.Value;
                    }
                }
                */
            }
            catch
            {
                // 忽略非 JSON 格式的雜訊封包
            }
            return Task.CompletedTask;
        };

        await _mqttClient.ConnectAsync(options);
        await _mqttClient.SubscribeAsync("factory/line1/#");
    }

    // =========================================================================
    // 以下為補齊的 IPlcDriver 四個標準介面方法
    // =========================================================================

    public Task<MachineStatus> ReadMachineStatusAsync()
    {
        // 從快取字典取出最新數值並組裝成標準 MachineStatus
        _latestValues.TryGetValue(Param.Spindle_Speed, out double speed);
        _latestValues.TryGetValue(Param.Motor_Temp, out double temp);
        _latestValues.TryGetValue(Param.Is_Running, out double running);
        _latestValues.TryGetValue(Param.Overheat_Alarm, out double alarm);
        _latestValues.TryGetValue(Param.Cmd_Ack_Done, out double ack);

        var status = new MachineStatus
        {
            ActualSpeed = (short)speed,
            Temperature = (short)temp,
            IsRunning = running > 0,
            IsOverheatAlarm = alarm > 0,
            IsCmdAckDone = ack > 0
        };

        return Task.FromResult(status);
    }
    public Task WriteTargetSpeedAsync(short speed)
    {
        return PublishCommandAsync("Target_Speed", speed);
    }

    public Task WriteStartTriggerAsync(bool isOn)
    {
        return PublishCommandAsync("Cmd_Start", isOn);
    }

    public Task WriteHeartbeatAsync(bool state)
    {
        return PublishCommandAsync("PC_Heartbeat", state);
    }
    /*
    public Task<double> ReadTagAsync(string address, string dataType)
    {
        if (!IsConnected)
            throw new InvalidOperationException("MQTT Broker 尚未連線。");

        // 直接從最新收到的 JSON 快取字典中，根據點位名稱 (address) 取出數值
        _latestData.TryGetValue(address, out double val);
        return Task.FromResult(val);
    }
    */
    public async Task WriteTagAsync(string address, object value)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
            throw new InvalidOperationException("MQTT Gateway 尚未連線。");

        // 1. 將位址與數值組裝成 JSON 字串，例如 {"PC_Heartbeat": true} 或 {"D100": 1500}
        var payloadDict = new System.Collections.Generic.Dictionary<string, object>
        {
            [address] = value is bool b ? (b ? 1 : 0) : value
        };
        string jsonString = System.Text.Json.JsonSerializer.Serialize(payloadDict);

        // 2. 組裝並發送 MQTT 訊息 (請確認 Topic 名稱與你 EdgeGatewaySimulator 監聽的一致)
        var message = new MQTTnet.MqttApplicationMessageBuilder()
            .WithTopic("factory/line1/plc01/command")
            .WithPayload(jsonString)
            .Build();

        await _mqttClient.PublishAsync(message, System.Threading.CancellationToken.None);
    }

    // 統一將控制指令轉成 JSON 發布給 Edge Gateway
    private async Task PublishCommandAsync(string tagName, object value)
    {
        if (!IsConnected || _mqttClient == null) return;

        var cmdPayload = JsonSerializer.Serialize(new { Tag = tagName, Value = value });
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("factory/line1/command")
            .WithPayload(cmdPayload)
            .Build();

        await _mqttClient.PublishAsync(message);
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }
}
