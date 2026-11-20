using S7.Net;
using WpfLibrary1;
public class SiemensS7Client : IDisposable
{
    private Plc _plc;
    private readonly string _ip;
    private readonly CpuType _cpuType;
    private readonly short _rack;
    private readonly short _slot;
    public bool IsConnected => _plc?.IsConnected ?? false;

    public SiemensS7Client(string? ip, CpuType cpuType = CpuType.S71200, short rack = 0, short slot = 1)
    {
        // 注意：S7-1200 / S7-1500 的 Rack 固定為 0，Slot 固定為 1
        // 若是舊款 S7-300 / S7-400，Slot 通常為 2
        ArgumentNullException.ThrowIfNull(ip);
        _plc = new Plc(cpuType, ip, rack, slot);
        _ip = ip;
        _cpuType = cpuType;
        _rack = rack;
        _slot = slot;
    }

    public async Task ConnectAsync()
    {
        //await _plc.OpenAsync();
        _plc = new Plc(_cpuType, _ip, _rack, _slot);

        // 設定讀寫階段的逾時時間 (單位: 毫秒)
        _plc.ReadTimeout = 2000;
        _plc.WriteTimeout = 2000;

        // 建立 2 秒連線逾時控制器
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await _plc.OpenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _plc.Close();
            //_plc = null;
            throw new TimeoutException($"西門子 PLC ({_ip}) 連線逾時（超過 2 秒無回應）。");
        }
        catch (Exception)
        {
            _plc.Close();
            //_plc = null;
            throw;
        }
    }

    public void Dispose()
    {
        _plc?.Close();
        //_plc = null;
    }

/// <summary>
/// 讀取 DB1 區塊內的資料 (例如讀取 DB1.DBW0 的 16-bit 整數)
/// </summary>
public async Task<short> ReadDbWordAsync(int dbNumber, int startByteAddress)
    {
        // VarType.Int 對應西門子的 16-bit 有號整數 (Word / Int)
        var result = await _plc.ReadAsync(S7.Net.DataType.DataBlock, dbNumber, startByteAddress, VarType.Int, 1);
        return Convert.ToInt16(result);
    }

    /// <summary>
    /// 寫入 DB1 區塊內的整數 (例如設定目標轉速到 DB1.DBW2)
    /// </summary>
    public async Task WriteDbWordAsync(int dbNumber, int startByteAddress, short value)
    {
        await _plc.WriteAsync(S7.Net.DataType.DataBlock, dbNumber, startByteAddress, value);
    }

    /// <summary>
    /// 寫入 DB 區塊內的單一布林值 (Bool / Bit，例如 DB1.DBX4.0 啟動旗標)
    /// </summary>
    public async Task WriteDbBitAsync(int dbNumber, int startByteAddress, int bitIndex, bool value)
    {
        await _plc.WriteBitAsync(S7.Net.DataType.DataBlock, dbNumber, startByteAddress, bitIndex, value);
    }
}
/*
public class SiemensS7Client : IDisposable
{
    private Plc? _plc;
    private readonly string _ip;
    private readonly CpuType _cpuType;
    private readonly short _rack;
    private readonly short _slot;

    public bool IsConnected => _plc?.IsConnected ?? false;

    public SiemensS7Client(string ip, CpuType cpuType = CpuType.S71200, short rack = 0, short slot = 1)
    {
        _ip = ip;
        _cpuType = cpuType;
        _rack = rack;
        _slot = slot;
    }

    public async Task ConnectAsync()
    {
        _plc = new Plc(_cpuType, _ip, _rack, _slot);
        
        // 設定讀寫階段的逾時時間 (單位: 毫秒)
        _plc.ReadTimeout = 2000;
        _plc.WriteTimeout = 2000;

        // 建立 2 秒連線逾時控制器
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await _plc.OpenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _plc.Close();
            _plc = null;
            throw new TimeoutException($"西門子 PLC ({_ip}) 連線逾時（超過 2 秒無回應）。");
        }
        catch (Exception)
        {
            _plc.Close();
            _plc = null;
            throw;
        }
    }

    public void Dispose()
    {
        _plc?.Close();
        _plc = null;
    }
}

 */
public class SiemensDriver : IPlcDriver
{
    private readonly SiemensS7Client _client;
    private const int DbNum = 1; // 統一對接先前規劃的 DB1 (SCADA_Comm)

    public bool IsConnected => _client.IsConnected;

    public SiemensDriver(string ip)
    {
        _client = new SiemensS7Client(ip);
    }

    public Task ConnectAsync() => _client.ConnectAsync();

    public async Task<MachineStatus> ReadMachineStatusAsync()
    {
        // 讀取 DB1.DBW0 (實際轉速)
        short speed = await _client.ReadDbWordAsync(DbNum, 0);
        return new MachineStatus
        {
            ActualSpeed = speed
            // 可依序讀取 DB1 內的其他偏移位址
        };
    }

    public Task WriteTargetSpeedAsync(short speed) => _client.WriteDbWordAsync(DbNum, 2, speed);
    public Task WriteStartTriggerAsync(bool isOn) => _client.WriteDbBitAsync(DbNum, 4, 0, isOn);
    public Task WriteHeartbeatAsync(bool state) => _client.WriteDbBitAsync(DbNum, 4, 2, state);

    public void Dispose() => _client.Dispose();
}
