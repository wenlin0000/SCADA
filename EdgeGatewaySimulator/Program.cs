using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Server;

class EdgeGatewaySimulator
{
    static async Task Main(string[] args)
    {
        // 1. 狀態變數放在最上方（取代原本的寫死數值）
        int targetSpeed = 1500; // 預設目標轉速
        bool isRunning = true;  // 預設運轉狀態

        var factory = new MqttClientFactory();
        var mqttClient = factory.CreateMqttClient();

        // 2. 在 ConnectAsync 之前，先註冊「收到 SCADA 控制指令」的處理邏輯
        mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            string cmdJson = e.ApplicationMessage.ConvertPayloadToString();

            // 如果是 PC 心跳訊號 (M500)，不洗版，只印一個小點或直接忽略
            if (cmdJson.Contains("\"M500\""))
            {
                return Task.CompletedTask;
            }
            //string cmdJson = e.ApplicationMessage.ConvertPayloadToString();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[收到 SCADA 下發指令] {cmdJson}");
            Console.ResetColor();

            try
            {
                using var doc = JsonDocument.Parse(cmdJson);

                // 若收到修改轉速指令，更新目標轉速
                if (doc.RootElement.TryGetProperty("Target_Speed", out var speedProp) ||
                    doc.RootElement.TryGetProperty("D100", out speedProp))
                {
                    targetSpeed = speedProp.GetInt32();
                }

                // 若收到啟動/停止指令，更新運轉狀態
                if (doc.RootElement.TryGetProperty("Cmd_Start", out var startProp))
                {
                    isRunning = startProp.GetInt32() == 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[指令解析錯誤] {ex.Message}");
            }

            return Task.CompletedTask;
        };

        // 連線至 MQTT Broker
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", 1883)
            .WithClientId("EdgeGateway_Simulator_01")
            .Build();

        await mqttClient.ConnectAsync(options);
        Console.WriteLine("✅ EdgeGatewaySimulator 已連上 MQTT Broker！");

        // 3. 連線成功後，立刻訂閱下行指令主題（緊接在 ConnectAsync 後面）
        var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic("factory/line1/plc01/command"))
            .Build();
        await mqttClient.SubscribeAsync(subscribeOptions);
        Console.WriteLine("📡 已訂閱控制指令頻道: factory/line1/plc01/command");

        // 4. 在原本的 while 迴圈裡面，替換成動態計算數值的邏輯
        while (true)
        {
            // 根據目前的 isRunning 與 targetSpeed 動態計算即時數值
            int currentSpeed = isRunning ? targetSpeed + Random.Shared.Next(-15, 16) : 0;
            int currentTemp = isRunning ? 45 + (currentSpeed / 500) + Random.Shared.Next(-1, 2) : 28;

            var telemetry = new
            {
                Spindle_Speed = currentSpeed,
                Motor_Temp = currentTemp,
                Is_Running = isRunning ? 1 : 0,
                Overheat_Alarm = 0,
                Cmd_Ack_Done = 1
            };

            string jsonPayload = JsonSerializer.Serialize(telemetry);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic("factory/line1/plc01/status")
                .WithPayload(jsonPayload)
                .Build();

            await mqttClient.PublishAsync(message);
            Console.WriteLine($"[Gateway 推送數據] {jsonPayload}");

            await Task.Delay(1000);
        }
    }
}
