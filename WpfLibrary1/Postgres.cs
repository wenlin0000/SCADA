//string pgConnStr = "Host=192.168.1.200;Port=5432;Database=factory_scada_db;Username=postgres;Password=YourPassword!;";
using Dapper;
using Npgsql;
using NpgsqlTypes;
using System.Collections.Concurrent;
using WpfLibrary1;

public class PostgresTelemetryHistorian : IDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentQueue<TelemetryRecord> _bufferQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;

    private const int BatchSize = 100;
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(5);

    public PostgresTelemetryHistorian(string connectionString)
    {
        _connectionString = connectionString;
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

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

    private async Task FlushLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, token);
                if (!_bufferQueue.IsEmpty)
                {
                    await BinaryBulkInsertAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PostgreSQL 批量寫入異常] {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 使用 PostgreSQL 原生 Binary COPY 執行極速批量寫入
    /// </summary>
    private async Task BinaryBulkInsertAsync()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // 啟動二進位高速匯入串流
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY telemetry_history (record_time, device_id, tag_id, tag_value) FROM STDIN (FORMAT BINARY)");

        int count = 0;
        while (count < BatchSize && _bufferQueue.TryDequeue(out var item))
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(item.RecordTime, NpgsqlDbType.Timestamp);
            await writer.WriteAsync(item.DeviceId, NpgsqlDbType.Varchar);
            await writer.WriteAsync(item.TagId, NpgsqlDbType.Varchar);
            await writer.WriteAsync(item.TagValue, NpgsqlDbType.Double);
            count++;
        }

        // 提交整批資料
        await writer.CompleteAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _flushTask.Wait(2000);
        if (!_bufferQueue.IsEmpty)
        {
            BinaryBulkInsertAsync().Wait(2000);
        }
    }
}
public class PostgresSecurityAndLogService
{
    private readonly string _connectionString;

    public string CurrentUser { get; private set; } = "Guest";
    public UserRole CurrentRole { get; private set; } = UserRole.Guest;

    public PostgresSecurityAndLogService(string connectionString)
    {
        _connectionString = connectionString;
        InitializeDatabase();
    }

    /// <summary>
    /// 自動檢查並建立 PostgreSQL 資料表
    /// </summary>
    private void InitializeDatabase()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        // 1. 操作稽核日誌表 (audit_logs)
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS audit_logs (
                id SERIAL PRIMARY KEY,
                timestamp TIMESTAMP NOT NULL,
                username VARCHAR(50) NOT NULL,
                role VARCHAR(20) NOT NULL,
                action VARCHAR(100) NOT NULL,
                target_address VARCHAR(50) NOT NULL,
                new_value VARCHAR(100) NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_logs_time ON audit_logs(timestamp DESC);
        ");

        // 2. 機台警報歷史表 (alarm_logs)
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS alarm_logs (
                id SERIAL PRIMARY KEY,
                trigger_time TIMESTAMP NOT NULL,
                alarm_code VARCHAR(30) NOT NULL,
                description VARCHAR(200) NOT NULL,
                status VARCHAR(30) NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_alarm_logs_time ON alarm_logs(trigger_time DESC);
        ");
    }

    public async void LogAction(string action, string targetAddress, string newValue)
    {
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(@"
                INSERT INTO audit_logs (timestamp, username, role, action, target_address, new_value)
                VALUES (@Timestamp, @Username, @Role, @Action, @TargetAddress, @NewValue)",
                new
                {
                    Timestamp = DateTime.Now,
                    Username = CurrentUser,
                    Role = CurrentRole.ToString(),
                    Action = action,
                    TargetAddress = targetAddress,
                    NewValue = newValue
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgreSQL 日誌寫入失敗] {ex.Message}");
        }
    }

    public async void LogAlarm(string alarmCode, string description, bool isTriggered)
    {
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync(@"
                INSERT INTO alarm_logs (trigger_time, alarm_code, description, status)
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
            Console.WriteLine($"[PostgreSQL 警報寫入失敗] {ex.Message}");
        }
    }
}
