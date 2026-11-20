using NModbus;
using System.Net.Sockets;
using WpfLibrary1;

public class FatekDriver : IPlcDriver
{
    private TcpClient? _tcpClient;
    private IModbusMaster? _modbusMaster;
    private readonly string _ip;
    private readonly int _port;
    private readonly byte _slaveId;

    public bool IsConnected => _tcpClient?.Connected ?? false;

    public FatekDriver(string ip, int port = 502, byte slaveId = 1)
    {
        _ip = ip;
        _port = port;
        _slaveId = slaveId;
    }

    public async Task ConnectAsync()
    {
        _tcpClient = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            // 1. 強制控制 TCP 握手階段最多等 2 秒
            await _tcpClient.ConnectAsync(_ip, _port, cts.Token);

            // 2. 建立 Modbus Master 並設定讀寫封包逾時 (NModbus 此處有效)
            var factory = new ModbusFactory();
            _modbusMaster = factory.CreateMaster(_tcpClient);
            _modbusMaster.Transport.ReadTimeout = 2000;
            _modbusMaster.Transport.WriteTimeout = 2000;
            _modbusMaster.Transport.Retries = 1; // 失敗時最多重試 1 次，避免卡頓太久
        }
        catch (OperationCanceledException)
        {
            Dispose();
            throw new TimeoutException($"永宏 PLC ({_ip}:{_port}) 連線逾時（超過 2 秒無回應）。");
        }
        catch (Exception)
        {
            Dispose();
            throw;
        }
    }
    // =========================================================================
    // 實作 IPlcDriver 標準介面四個方法 (對接永宏 R 與 M 暫存器)
    // =========================================================================

    public async Task<MachineStatus> ReadMachineStatusAsync()
    {
        if (!IsConnected || _modbusMaster == null)
            throw new InvalidOperationException("永宏 PLC 尚未連線。");

        // 1. 批量讀取 R0 與 R1 (R0 = 實際轉速, R1 = 馬達溫度)
        // 永宏 R0 對應 Modbus Holding Register 起始位址 0
        ushort[] regs = await _modbusMaster.ReadHoldingRegistersAsync(_slaveId, 0, 2);

        // 2. 批量讀取 M0 ~ M2 (M0 = 運轉中, M1 = 過熱警報, M2 = 指令完成確認)
        // 永宏 M0 對應 Modbus Coil 起始位址 2000
        bool[] coils = await _modbusMaster.ReadCoilsAsync(_slaveId, 2000, 3);

        return new MachineStatus
        {
            ActualSpeed = (short)regs[0],
            Temperature = (short)regs[1],
            IsRunning = coils[0],
            IsOverheatAlarm = coils[1],
            IsCmdAckDone = coils[2]
        };
    }

    public async Task WriteTargetSpeedAsync(short speed)
    {
        if (!IsConnected || _modbusMaster == null) return;
        // 寫入永宏 R100 (Modbus Holding Register 位址 100)
        await _modbusMaster.WriteSingleRegisterAsync(_slaveId, 100, (ushort)speed);
    }

    public async Task WriteStartTriggerAsync(bool isOn)
    {
        if (!IsConnected || _modbusMaster == null) return;
        // 寫入永宏 M100 (Modbus Coil 位址 2000 + 100 = 2100)
        await _modbusMaster.WriteSingleCoilAsync(_slaveId, 2100, isOn);
    }

    public async Task WriteHeartbeatAsync(bool state)
    {
        if (!IsConnected || _modbusMaster == null) return;
        // 寫入永宏 M500 (Modbus Coil 位址 2000 + 500 = 2500)
        await _modbusMaster.WriteSingleCoilAsync(_slaveId, 2500, state);
    }

    // =========================================================================
    // 保留通用字串位址讀寫方法 (供日後擴充動態點位使用)
    // =========================================================================

    public async Task<double> ReadTagValueAsync(string address, string dataType)
    {
        if (_modbusMaster == null) throw new InvalidOperationException("PLC 尚未連線。");
        address = address.ToUpper().Trim();

        if (address.StartsWith("R"))
        {
            ushort offset = ushort.Parse(address.Substring(1));
            ushort[] regs = await _modbusMaster.ReadHoldingRegistersAsync(_slaveId, offset, 1);
            return (short)regs[0];
        }
        else if (address.StartsWith("D"))
        {
            ushort offset = (ushort)(6000 + ushort.Parse(address.Substring(1)));
            ushort[] regs = await _modbusMaster.ReadHoldingRegistersAsync(_slaveId, offset, 1);
            return (short)regs[0];
        }
        else if (address.StartsWith("M"))
        {
            ushort offset = (ushort)(2000 + ushort.Parse(address.Substring(1)));
            bool[] coils = await _modbusMaster.ReadCoilsAsync(_slaveId, offset, 1);
            return coils[0] ? 1.0 : 0.0;
        }

        throw new NotSupportedException($"不支援的永宏位址格式: {address}");
    }

    public async Task WriteTagValueAsync(string address, object value)
    {
        if (_modbusMaster == null) return;
        address = address.ToUpper().Trim();

        if (address.StartsWith("R"))
        {
            ushort offset = ushort.Parse(address.Substring(1));
            await _modbusMaster.WriteSingleRegisterAsync(_slaveId, offset, (ushort)Convert.ToInt16(value));
        }
        else if (address.StartsWith("D"))
        {
            ushort offset = (ushort)(6000 + ushort.Parse(address.Substring(1)));
            await _modbusMaster.WriteSingleRegisterAsync(_slaveId, offset, (ushort)Convert.ToInt16(value));
        }
        else if (address.StartsWith("M"))
        {
            ushort offset = (ushort)(2000 + ushort.Parse(address.Substring(1)));
            await _modbusMaster.WriteSingleCoilAsync(_slaveId, offset, Convert.ToBoolean(value));
        }
    }

    public void Dispose()
    {
        _modbusMaster?.Dispose();
        _tcpClient?.Dispose();
        _modbusMaster = null;
        _tcpClient = null;
    }
}
