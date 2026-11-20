using System.Net.Sockets;
using WpfLibrary1;

public class MitsubishiMcClient : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly string _ip;
    private readonly int _port;

    public bool IsConnected => _tcpClient?.Connected ?? false;

    public MitsubishiMcClient(string ip, int port = 5000)
    {
        _ip = ip;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        _tcpClient = new TcpClient { ReceiveTimeout = 2000, SendTimeout = 2000 };

        // 建立一個 2 秒 (2000ms) 自動觸發取消的計時器中斷連線嘗試
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            // 傳入 cts.Token，只要超過 2 秒沒連上，系統會立刻中斷連線嘗試
            await _tcpClient.ConnectAsync(_ip, _port, cts.Token);
            _stream = _tcpClient.GetStream();
        }
        catch (OperationCanceledException)
        {
            // 逾時發生時，務必釋放未完成連線的 Socket 資源，避免連接埠耗盡 (Socket Exhaustion)
            _tcpClient.Dispose();
            _tcpClient = null;
            throw new TimeoutException($"連線至 PLC ({_ip}:{_port}) 逾時（超過 2 秒無回應）。");
        }
        catch (Exception)
        {
            _tcpClient.Dispose();
            _tcpClient = null;
            throw;
        }
    }
    /// <summary>
    /// 批量讀取 D 暫存器 (16-bit 整數)
    /// </summary>
    public async Task<short[]> ReadDRegistersAsync(int startAddress, ushort count)
    {
        byte[] command = Build3EFrameCommand(
            command: 0x0401,      // 批量讀取指令
            subCommand: 0x0000,   // 以 Word 為單位
            deviceCode: 0xA8,     // D 暫存器代碼
            startAddress: startAddress,
            count: count
        );

        byte[] response = await SendAndReceiveAsync(command);
        short[] result = new short[count];

        // 回應封包的前 11 個 Byte 為表頭與結束碼，第 11 Byte 開始才是實際數據
        for (int i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToInt16(response, 11 + i * 2);
        }
        return result;
    }

    /// <summary>
    /// 寫入單一 D 暫存器 (16-bit 整數)
    /// </summary>
    public async Task WriteDRegisterAsync(int address, short value)
    {
        byte[] dataBytes = BitConverter.GetBytes(value);
        byte[] command = Build3EFrameCommand(
            command: 0x1401,      // 批量寫入指令
            subCommand: 0x0000,   // 以 Word 為單位
            deviceCode: 0xA8,     // D 暫存器代碼
            startAddress: address,
            count: 1,
            writeData: dataBytes
        );

        await SendAndReceiveAsync(command);
    }

    /// <summary>
    /// 寫入單一 M 繼電器 (True = ON, False = OFF)
    /// </summary>
    public async Task WriteMBitAsync(int address, bool isOn)
    {
        // 位元寫入時，1 點佔用 4 bits (Half-byte)，ON 為 0x10，OFF 為 0x00
        byte[] dataBytes = new byte[] { isOn ? (byte)0x10 : (byte)0x00 };
        byte[] command = Build3EFrameCommand(
            command: 0x1401,      // 批量寫入指令
            subCommand: 0x0001,   // 以 Bit 為單位
            deviceCode: 0x90,     // M 繼電器代碼 (X=0x9C, Y=0x9D)
            startAddress: address,
            count: 1,
            writeData: dataBytes
        );

        await SendAndReceiveAsync(command);
    }

    private byte[] Build3EFrameCommand(ushort command, ushort subCommand, byte deviceCode, int startAddress, ushort count, byte[]? writeData = null)
    {
        int dataLength = 12 + (writeData?.Length ?? 0); // 監視定時器(2) + 指令(2) + 子指令(2) + 起始位址(3) + 軟元件代碼(1) + 點數(2) + 寫入資料
        byte[] frame = new byte[9 + dataLength];

        // 1. 副表頭 (3E Frame 固定為 0x50, 0x00)
        frame[0] = 0x50; frame[1] = 0x00;
        // 2. 網路編號 (0x00) & PC 編號 (0xFF)
        frame[2] = 0x00; frame[3] = 0xFF;
        // 3. 請求目標模組 I/O 編號 (0x03FF -> Little Endian: 0xFF, 0x03)
        frame[4] = 0xFF; frame[5] = 0x03;
        // 4. 請求目標模組站號 (0x00)
        frame[6] = 0x00;
        // 5. 請求資料長度 (2 Bytes)
        Array.Copy(BitConverter.GetBytes((ushort)dataLength), 0, frame, 7, 2);
        // 6. CPU 監視定時器 (2 Bytes, 0x0010 代表 250ms * 16 = 4秒)
        frame[9] = 0x10; frame[10] = 0x00;
        // 7. 指令 & 子指令
        Array.Copy(BitConverter.GetBytes(command), 0, frame, 11, 2);
        Array.Copy(BitConverter.GetBytes(subCommand), 0, frame, 13, 2);
        // 8. 起始軟元件位址 (3 Bytes, Little Endian)
        frame[15] = (byte)(startAddress & 0xFF);
        frame[16] = (byte)((startAddress >> 8) & 0xFF);
        frame[17] = (byte)((startAddress >> 16) & 0xFF);
        // 9. 軟元件代碼 (1 Byte)
        frame[18] = deviceCode;
        // 10. 軟元件點數 (2 Bytes)
        Array.Copy(BitConverter.GetBytes(count), 0, frame, 19, 2);

        // 11. 附加寫入資料 (若有)
        if (writeData != null && writeData.Length > 0)
        {
            Array.Copy(writeData, 0, frame, 21, writeData.Length);
        }

        return frame;
    }
    public async Task<Dictionary<string, double>> ReadAllMonitorTagsAsync()
    {
        // 批量讀取 D0 ~ D3 (共 4 個 Word)
        short[] dRegs = await ReadDRegistersAsync(0, 4);

        // 情況 1：直接將 16-bit 整數 (short) 轉成 double (C# 會自動隱含轉換)
        double speed = dRegs[0];

        // 情況 2：整數帶小數倍率 (例如 D1 讀到 455，乘以 0.1 變成 45.5°C)
        double temp = Math.Round(dRegs[1] * 0.1, 1);

        // 情況 3：兩個連續暫存器 (D2 + D3) 合併為 32-bit 浮點數 (Float) 再轉成 double
        byte[] floatBytes = new byte[4];
        BitConverter.GetBytes(dRegs[2]).CopyTo(floatBytes, 0);
        BitConverter.GetBytes(dRegs[3]).CopyTo(floatBytes, 2);
        double pressure = Math.Round((double)BitConverter.ToSingle(floatBytes, 0), 2);

        return new Dictionary<string, double>
        {
            ["Speed"] = speed,
            ["Temp"] = temp,
            ["Pressure"] = pressure
        };
    }
    public async Task WriteTagAsync(string address, object value)
    {
        address = address.ToUpper().Trim();
        if (address.StartsWith("D"))
        {
            int addr = int.Parse(address.Substring(1));
            await WriteDRegisterAsync(addr, Convert.ToInt16(value));
        }
        else if (address.StartsWith("M"))
        {
            int addr = int.Parse(address.Substring(1));
            await WriteMBitAsync(addr, Convert.ToBoolean(value));
        }
    }
    private async Task<byte[]> SendAndReceiveAsync(byte[] sendBuffer)
    {
        if (!IsConnected) throw new InvalidOperationException("PLC 尚未連線。");
        using var ioCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _stream!.WriteAsync(sendBuffer, 0, sendBuffer.Length, ioCts.Token);

        byte[] receiveBuffer = new byte[1024];
        int bytesRead = await _stream!.ReadAsync(receiveBuffer, 0, receiveBuffer.Length, ioCts.Token);

        if (bytesRead < 11)
            throw new Exception("PLC 回應封包長度異常。");

        // 檢查結束碼 (第 9, 10 Byte，0x0000 代表成功)
        ushort endCode = BitConverter.ToUInt16(receiveBuffer, 9);
        if (endCode != 0)
            throw new Exception($"PLC 通訊報錯，錯誤代碼 (End Code): 0x{endCode:X4}");

        byte[] result = new byte[bytesRead];
        Array.Copy(receiveBuffer, result, bytesRead);
        return result;
    }
    public void Dispose()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _stream = null;
        _tcpClient = null;
    }
}
public class MitsubishiDriver : IPlcDriver
{
    private readonly MitsubishiMcClient _client;

    public bool IsConnected => _client?.IsConnected ?? false;

    public MitsubishiDriver(string ip, int port = 5000)
    {
        _client = new MitsubishiMcClient(ip, port);
    }

    public Task ConnectAsync() => _client.ConnectAsync();

    public async Task<MachineStatus> ReadMachineStatusAsync()
    {
        // 批量讀取 D0 ~ D10
        short[] d = await _client.ReadDRegistersAsync(0, 11);
        return new MachineStatus
        {
            ActualSpeed = d[0],
            Temperature = d[1],
            IsOverheatAlarm = (d[10] & 0x01) != 0
            // 實務上可再批量讀取 M 點補齊 IsRunning 與 IsCmdAckDone
        };
    }

    public Task WriteTargetSpeedAsync(short speed) => _client.WriteDRegisterAsync(100, speed);
    public Task WriteStartTriggerAsync(bool isOn) => _client.WriteMBitAsync(100, isOn);
    public Task WriteHeartbeatAsync(bool state) => _client.WriteMBitAsync(500, state);

    public void Dispose() => _client.Dispose();
}
