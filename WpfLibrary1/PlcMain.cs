using Dapper;
using libplctag;
using libplctag.DataTypes;
using Microsoft.Data.SqlClient; // 替換為 SQL Server 官方驅動
using Microsoft.Data.Sqlite;
using MQTTnet;
using NModbus; // 安裝 NuGet: NModbus
using Npgsql; // PostgreSQL 官方驅動
using NpgsqlTypes;
using Opc.Ua;//安裝 OPCFoundation.NetStandard.Opc.Ua.Client
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using S7.Net; // 安裝 NuGet: S7NetPlus
using SparkplugNet.Core;
using SparkplugNet.Core.Application;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.VersionB;
using SparkplugNet.VersionB.Data;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq.Expressions;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace WpfLibrary1
{
    /*
    public static void start()
    {
        // 讀取設定檔（可使用 Microsoft.Extensions.Configuration）
        string brand = config["PlcSettings:Brand"]; // 填入 "Mitsubishi" 或 "Siemens"
        string ip = config["PlcSettings:IpAddress"];
        int port = int.Parse(config["PlcSettings:Port"]);

        // 動態建立對應品牌驅動並注入管理器
        IPlcDriver driver = PlcDriverFactory.CreateDriver(brand, ip, port);
        var scadaManager = new UnifiedScadaManager(driver);
    }
    public void LoadConfig(string jsonPath = "tags.json")
{
    string jsonContent = File.ReadAllText(jsonPath);
    _config = JsonSerializer.Deserialize<ScadaConfig>(jsonContent, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    // 加上這一行保護：若 JSON 解析失敗或為空，直接安全返回，編譯器就會知道下方 _config 絕對不為 null
    if (_config == null) return;

    foreach (var tag in _config.Tags)
    {
        LiveValues[tag.TagId] = 0;
        _alarmStates[tag.TagId] = false;
    }
}

    */
    public class ScadaSecurityAndLogService
    {
        private readonly string _connectionString;

        // 當前登入的使用者狀態
        public string CurrentUser { get; private set; } = "Guest";
        public UserRole CurrentRole { get; private set; } = UserRole.Guest;

        public ScadaSecurityAndLogService(string dbPath = "ScadaHistory.db")
        {
            _connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
        }

        /// <summary>
        /// 自動建立操作日誌與警報資料表
        /// </summary>
        private void InitializeDatabase()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // 1. 操作稽核日誌表 (Audit Log)
            conn.Execute(@"
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Username TEXT NOT NULL,
                Role TEXT NOT NULL,
                Action TEXT NOT NULL,
                TargetAddress TEXT NOT NULL,
                NewValue TEXT NOT NULL
            );");

            // 2. 機台警報歷史表 (Alarm Log)
            conn.Execute(@"
            CREATE TABLE IF NOT EXISTS AlarmLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TriggerTime TEXT NOT NULL,
                AlarmCode TEXT NOT NULL,
                Description TEXT NOT NULL,
                Status TEXT NOT NULL
            );");
        }

        /// <summary>
        /// 模擬使用者登入（實務上可改為查詢資料庫或串接工廠 AD / 員工證刷卡機）
        /// </summary>
        public bool Login(string username, string password)
        {
            if (username == "op1" && password == "1234")
            {
                CurrentUser = "作業員_王小明";
                CurrentRole = UserRole.Operator;
                LogAction("系統登入", "System", "Success");
                return true;
            }
            if (username == "eng1" && password == "admin888")
            {
                CurrentUser = "工程師_李大華";
                CurrentRole = UserRole.Engineer;
                LogAction("系統登入", "System", "Success");
                return true;
            }
            return false;
        }

        public void Logout()
        {
            LogAction("系統登出", "System", "Logout");
            CurrentUser = "Guest";
            CurrentRole = UserRole.Guest;
        }

        /// <summary>
        /// 寫入操作紀錄（每次下指令給 PLC 前必呼叫）
        /// </summary>
        public void LogAction(string action, string targetAddress, string newValue)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Execute(@"
            INSERT INTO AuditLogs (Timestamp, Username, Role, Action, TargetAddress, NewValue)
            VALUES (@Timestamp, @Username, @Role, @Action, @TargetAddress, @NewValue)",
                new
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Username = CurrentUser,
                    Role = CurrentRole.ToString(),
                    Action = action,
                    TargetAddress = targetAddress,
                    NewValue = newValue
                });
        }

        /// <summary>
        /// 寫入機台警報紀錄
        /// </summary>
        public void LogAlarm(string alarmCode, string description, bool isTriggered)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Execute(@"
            INSERT INTO AlarmLogs (TriggerTime, AlarmCode, Description, Status)
            VALUES (@TriggerTime, @AlarmCode, @Description, @Status)",
                new
                {
                    TriggerTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    AlarmCode = alarmCode,
                    Description = description,
                    Status = isTriggered ? "發生 (Triggered)" : "解除 (Cleared)"
                });
        }
    }
    public class DynamicTagEngine
    {
        private readonly IPlcDriver _driver;
        private readonly ScadaSecurityAndLogService _logService;
        private ScadaConfig? _config;

        // 存放所有 Tag 的即時數值 (Key = TagId, Value = 換算後的實際數值)
        public ConcurrentDictionary<string, double> LiveValues { get; } = new();

        // 記錄每個 Tag 目前是否處於警報中，避免重複發報
        private readonly Dictionary<string, bool> _alarmStates = [];

        public event Action<string, double>? OnTagValueChanged;
        public event Action<string, string, bool>? OnAlarmTriggered;

        public DynamicTagEngine(IPlcDriver driver, ScadaSecurityAndLogService logService)
        {
            _driver = driver;
            _logService = logService;
        }

        /// <summary>
        /// 載入或熱重載 (Hot-Reload) JSON 點位設定檔
        /// </summary>
        public void LoadConfig(string jsonPath = "tags.json")
        {
            string jsonContent = File.ReadAllText(jsonPath);
            _config = JsonSerializer.Deserialize<ScadaConfig>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new System.IO.InvalidDataException("tags.json 設定檔內容為空或格式錯誤！");

            foreach (var tag in _config.Tags)
            {
                LiveValues[tag.TagId] = 0;
                _alarmStates[tag.TagId] = false;
            }
        }

        /// <summary>
        /// 執行一次全點位掃描與警報自動判定
        /// </summary>
        public async Task PollAllTagsAsync()
        {
            if (_config?.Tags == null) return;

            foreach (var tag in _config.Tags)
            {
                // 1. 讀取原始數值並套用倍率換算
                double rawVal = await _driver.ReadTagValueAsync(tag.Address, tag.DataType);
                double scaledVal = Math.Round(rawVal * tag.Scale, 2);

                LiveValues[tag.TagId] = scaledVal;
                OnTagValueChanged?.Invoke(tag.TagId, scaledVal);

                // 2. 自動檢查高低限警報條件
                EvaluateAlarm(tag, scaledVal);
            }
        }

        private void EvaluateAlarm(TagDefinition tag, double currentValue)
        {
            if (tag.Alarm == null || !tag.Alarm.Enable) return;

            bool isOutOfRange = currentValue > tag.Alarm.HighLimit || currentValue < tag.Alarm.LowLimit;
            bool wasInAlarm = _alarmStates[tag.TagId];

            // 只有在警報狀態發生變化時才寫入資料庫與通知 UI
            if (isOutOfRange != wasInAlarm)
            {
                _alarmStates[tag.TagId] = isOutOfRange;
                string detailMsg = $"{tag.Alarm.Message} (目前數值: {currentValue} {tag.Unit})";

                _logService.LogAlarm(tag.Alarm.AlarmCode, detailMsg, isOutOfRange);
                OnAlarmTriggered?.Invoke(tag.Alarm.AlarmCode, detailMsg, isOutOfRange);
            }
        }
        public void Dispose() { }
    }
    public class PlcConnectionConfig
    {
        public string Brand { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
    }
    /*
    * CREATE TABLE IF NOT EXISTS telemetry_history (
    id BIGSERIAL PRIMARY KEY,
    record_time TIMESTAMP(3) NOT NULL,
    device_id VARCHAR(50) NOT NULL,
    tag_id VARCHAR(50) NOT NULL,
    tag_value DOUBLE PRECISION NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_telemetry_device_time ON telemetry_history(device_id, record_time DESC);
    */

    // =========================================================================
    // 3. 生產級 SCADA 單一執行緒通訊引擎 (佇列 + 心跳 + 自動重連)
    // =========================================================================
    public class ScadaEngine : IDisposable
    {
        private readonly IPlcDriver _driver;
        private readonly ConcurrentQueue<Func<Task>> _commandQueue = new();
        private CancellationTokenSource? _cts;
        private Task? _workerTask;

        public event Action<bool>? OnConnectionChanged;
        public event Action<Dictionary<string, double>>? OnDataUpdated;

        private bool _lastConnState = false;
        private bool _heartbeatBit = false;
        private DateTime _lastHeartbeat = DateTime.MinValue;

        public ScadaEngine(IPlcDriver driver)
        {
            _driver = driver;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }

        public void EnqueueWriteCommand(string address, object value)
        {
            _commandQueue.Enqueue(async () => await _driver.WriteTagAsync(address, value));
        }
        private async Task WorkerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. 檢查並維持連線
                    if (!_driver.IsConnected)
                    {
                        UpdateConnectionState(false);
                        await _driver.ConnectAsync();
                        UpdateConnectionState(true);
                    }

                    // 2. 優先執行所有控制寫入指令 (確保按鈕零延遲)
                    while (_commandQueue.TryDequeue(out var cmd))
                    {
                        await cmd();
                    }

                    // 3. 每 1 秒發送一次 Watchdog 心跳包至 M500
                    if ((DateTime.Now - _lastHeartbeat).TotalSeconds >= 1.0)
                    {
                        _heartbeatBit = !_heartbeatBit;
                        await _driver.WriteTagAsync("M500", _heartbeatBit);
                        _lastHeartbeat = DateTime.Now;
                    }

                    // 4. 週期性讀取機台狀態數據
                    //var data = await _driver.ReadAllMonitorTagsAsync();
                    //OnDataUpdated?.Invoke(data);
                    MachineStatus status = await _driver.ReadMachineStatusAsync();
                    // 2. 打包成統一鍵值的字典，廣播給 MainViewModel.OnDataUpdated
                    var dataDict = new Dictionary<string, double>
                    {
                        ["Speed"] = status.ActualSpeed,
                        ["Temp"] = status.Temperature
                    };
                    OnDataUpdated?.Invoke(dataDict);
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    UpdateConnectionState(false);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 通訊異常: {ex.Message} (2秒後重試)");
                    _driver.Dispose();
                    await Task.Delay(2000, token);
                }
            }
        }
        private void UpdateConnectionState(bool connected)
        {
            if (_lastConnState != connected)
            {
                _lastConnState = connected;
                OnConnectionChanged?.Invoke(connected);
            }
        }
        public void Dispose()
        {
            _cts?.Cancel();
            _workerTask?.Wait(1000);
            _driver.Dispose();
        }
    }

    // 統一的機台即時狀態模型（UI 畫面與資料庫只認這個結構）

    // 2. 通訊管理器現在只依賴 IPlcDriver 介面
    // 1. 驅動工廠：根據設定檔決定建立哪一牌的 PLC 連線
    public static class PlcDriverFactory
    {
        public static IPlcDriver CreateDriver(string brand, string ip, int port)
        {
            return brand.ToUpper() switch
            {
                "MITSUBISHI" => new MitsubishiDriver(ip, port),
                "SIEMENS" => new SiemensDriver(ip),
                "ROCKWELL" => new global::RockwellDriver(ip),
                "MQTT" => new MqttGatewayDriver(ip, port),
                "FATEK" => new FatekDriver(ip, port),
                "SPARKPLUG" => new SparkplugDriver(ip, 1883),
                _ => throw new NotSupportedException($"不支援的 PLC 品牌: {brand}")
            };
        }
    }

    // 2. 通訊管理器現在只依賴 IPlcDriver 介面
    public class UnifiedScadaManager
    {
        private readonly IPlcDriver _driver;

        public UnifiedScadaManager(IPlcDriver driver)
        {
            _driver = driver;
        }

        // 在原本的 Background Loop 中，只需統一呼叫：
        // await _driver.WriteHeartbeatAsync(_heartbeatState);
        // MachineStatus status = await _driver.ReadMachineStatusAsync();
    }

    // 統一的 PLC 驅動介面
    public interface IPlcDriver : IDisposable
    {
        bool IsConnected { get; }
        Task ConnectAsync();

        // 讀取整包標準機台狀態
        Task<MachineStatus> ReadMachineStatusAsync();

        // 下發業務控制指令
        Task WriteTargetSpeedAsync(short speed);
        Task WriteStartTriggerAsync(bool isOn);
        Task WriteHeartbeatAsync(bool state);

        //新增：供 DynamicTagEngine 呼叫的動態點位讀寫方法 (附帶預設實作，不影響舊 Driver)
        // =========================================================================
        // 補上 IPlcDriver 動態點位讀寫實作 (消除 NotImplementedException)
        // =========================================================================
        public Task<double> ReadTagValueAsync(string address, string dataType)
        {
            throw new NotImplementedException($"此驅動 ({GetType().Name}) 尚未實作運動功能。");
        }

        public Task WriteTagValueAsync(string address, object value)
        {
            throw new NotImplementedException($"此驅動 ({GetType().Name}) 尚未實作Tag。");
        }
        Task<Dictionary<string, double>> ReadAllMonitorTagsAsync()
        {
            throw new NotImplementedException($"此驅動 ({GetType().Name}) 尚未實作運動功能。");
        }
        Task WriteTagAsync(string address, object value)
        {
            throw new NotImplementedException($"此驅動 ({GetType().Name}) 尚未實作Tag。");
        }
    }
    public class ScadaConfig
    {
        public PlcConnectionConfig PlcConnection { get; set; } = new();
        public List<TagDefinition> Tags { get; set; } = [];
    }
    public class TagItemModel
    {
    }

}
