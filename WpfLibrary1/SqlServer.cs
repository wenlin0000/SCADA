/*
    string connStr = "Server=192.168.1.200;Database=FactoryScadaDB;User Id=scada_admin;Password=YourStrongPassword!;TrustServerCertificate=True;";
    string connStr = "Server=localhost\\SQLEXPRESS;Database=FactoryScadaDB;Trusted_Connection=True;TrustServerCertificate=True;";
    var logService = new SqlServerSecurityAndLogService(connStr);
 */
using Dapper;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using WpfLibrary1;

public class SqlServerSecurityAndLogService
{
    private readonly string _connectionString;

    public string CurrentUser { get; private set; } = "Guest";
    public UserRole CurrentRole { get; private set; } = UserRole.Guest;

    public SqlServerSecurityAndLogService(string connectionString)
    {
        _connectionString = connectionString;
        InitializeDatabase();
    }

    /// <summary>
    /// 自動檢查並建立 SQL Server 資料表 (T-SQL 語法)
    /// </summary>
    private void InitializeDatabase()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            // 1. 建立操作稽核日誌表 (AuditLogs)
            conn.Execute(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AuditLogs')
            BEGIN
                CREATE TABLE AuditLogs (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Timestamp DATETIME2 NOT NULL,
                    Username NVARCHAR(50) NOT NULL,
                    Role NVARCHAR(20) NOT NULL,
                    Action NVARCHAR(100) NOT NULL,
                    TargetAddress NVARCHAR(50) NOT NULL,
                    NewValue NVARCHAR(100) NOT NULL
                );
                CREATE INDEX IX_AuditLogs_Timestamp ON AuditLogs(Timestamp DESC);
            END");

            // 2. 建立機台警報歷史表 (AlarmLogs)
            conn.Execute(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AlarmLogs')
            BEGIN
                CREATE TABLE AlarmLogs (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    TriggerTime DATETIME2 NOT NULL,
                    AlarmCode NVARCHAR(30) NOT NULL,
                    Description NVARCHAR(200) NOT NULL,
                    Status NVARCHAR(30) NOT NULL
                );
                CREATE INDEX IX_AlarmLogs_TriggerTime ON AlarmLogs(TriggerTime DESC);
            END");
        }
        catch (Exception ex)
        {            
            // 加上這個 catch：就算資料庫暫時連不上或還沒建好，程式也不會崩潰閃退
            System.Diagnostics.Debug.WriteLine($"[資料庫初始化警告] {ex.Message}");
        }
    }

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
    /// 寫入操作稽核紀錄 (建議改為非同步寫入，避免資料庫網路延遲影響 UI)
    /// </summary>
    public async void LogAction(string action, string targetAddress, string newValue)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(@"
                INSERT INTO AuditLogs (Timestamp, Username, Role, Action, TargetAddress, NewValue)
                VALUES (@Timestamp, @Username, @Role, @Action, @TargetAddress, @NewValue)",
                new
                {
                    Timestamp = DateTime.Now, // 直接寫入 SQL Server 原生 DATETIME2 格式，方便日後按時間區間查詢報表
                    Username = CurrentUser,
                    Role = CurrentRole.ToString(),
                    Action = action,
                    TargetAddress = targetAddress,
                    NewValue = newValue
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SQL Server 日誌寫入失敗] {ex.Message}");
        }
    }

    /// <summary>
    /// 寫入機台警報紀錄
    /// </summary>
    public async void LogAlarm(string alarmCode, string description, bool isTriggered)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(@"
                INSERT INTO AlarmLogs (TriggerTime, AlarmCode, Description, Status)
                VALUES (@TriggerTime, @AlarmCode, @Description, @Status)",
                new
                {
                    TriggerTime = DateTime.Now,
                    AlarmCode = alarmCode,
                    Description = description,
                    Status = isTriggered ? "發生 (Triggered)" : "解除 (Cleared)"
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SQL Server 警報寫入失敗] {ex.Message}");
        }
    }
}
public class SqlServerTelemetryHistorian : IDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentQueue<TelemetryRecord> _bufferQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;

    // 設定觸發寫入的閾值：每累積滿 100 筆，或每經過 5 秒就寫入一次
    private const int BatchSize = 100;
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(5);

    public SqlServerTelemetryHistorian(string connectionString)
    {
        _connectionString = connectionString;
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 供 ScadaEngine 呼叫：將即時採集到的數據丟進記憶體緩衝區 (微秒級完成，零阻塞)
    /// </summary>
    public void EnqueueData(string deviceId, string tagId, double value)
    {
        _bufferQueue.Enqueue(new TelemetryRecord
        {
            RecordTime = DateTime.Now,
            DeviceId = deviceId,
            TagId = tagId,
            TagValue = value
        });
    }

    /// <summary>
    /// 背景定時批量寫入迴圈
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, token);

                if (!_bufferQueue.IsEmpty)
                {
                    await BulkInsertToSqlServerAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Historian 批量寫入異常] {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 核心高速寫入引擎：使用 SqlBulkCopy 一次寫入整批資料
    /// </summary>
    private async Task BulkInsertToSqlServerAsync()
    {
        // 1. 建立記憶體 DataTable 結構 (欄位順序必須與資料庫完全一致)
        using var dataTable = new DataTable();
        dataTable.Columns.Add("Id", typeof(long)); // 資料庫為 IDENTITY 自動遞增，此欄留空即可
        dataTable.Columns.Add("RecordTime", typeof(DateTime));
        dataTable.Columns.Add("DeviceId", typeof(string));
        dataTable.Columns.Add("TagId", typeof(string));
        dataTable.Columns.Add("TagValue", typeof(double));

        // 2. 從佇列取出最多 BatchSize 筆資料裝入 DataTable
        int count = 0;
        while (count < BatchSize && _bufferQueue.TryDequeue(out var item))
        {
            dataTable.Rows.Add(DBNull.Value, item.RecordTime, item.DeviceId, item.TagId, item.TagValue);
            count++;
        }

        if (count == 0) return;

        // 3. 透過 SqlBulkCopy 執行高速二進位寫入
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "TelemetryHistory",
            BatchSize = count,
            BulkCopyTimeout = 10
        };

        // 明確對應欄位名稱，避免順序錯位
        bulkCopy.ColumnMappings.Add("RecordTime", "RecordTime");
        bulkCopy.ColumnMappings.Add("DeviceId", "DeviceId");
        bulkCopy.ColumnMappings.Add("TagId", "TagId");
        bulkCopy.ColumnMappings.Add("TagValue", "TagValue");

        await bulkCopy.WriteToServerAsync(dataTable);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _flushTask.Wait(2000);
        // 程式關閉前，將緩衝區內剩餘的最後幾筆資料全數寫入，確保不掉資料
        BulkInsertToSqlServerAsync().Wait(2000);
    }
}
