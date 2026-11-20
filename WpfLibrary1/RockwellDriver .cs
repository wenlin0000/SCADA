using libplctag;
using libplctag.DataTypes;
using WpfLibrary1;

public class RockwellDriver : IPlcDriver
{
    private readonly Tag<IntPlcMapper, short> _actualSpeedTag;
    private readonly Tag<IntPlcMapper, short> _targetSpeedTag;
    private readonly Tag<BoolPlcMapper, bool> _startTriggerTag;
    private readonly Tag<BoolPlcMapper, bool> _heartbeatTag;

    public bool IsConnected { get; private set; }

    public RockwellDriver(string ip, string cpuPath = "1,0")
    {
        // 注意 Path 參數：
        // CompactLogix (如 5069/1769) 通常為 "1,0"
        // ControlLogix (1756) 需填寫 CPU 所在的背板插槽號，例如在 Slot 2 則填 "1,2"
        var protocol = Protocol.ab_eip;
        var plcType = PlcType.ControlLogix; // 同時相容 CompactLogix

        _actualSpeedTag = new Tag<IntPlcMapper, short>()
        {
            Name = "SCADA_Actual_Speed", // 直接對應 Studio 5000 裡的 Tag 名稱！
            Gateway = ip,
            Path = cpuPath,
            PlcType = plcType,
            Protocol = protocol,
            Timeout = TimeSpan.FromSeconds(2)
        };

        _targetSpeedTag = new Tag<IntPlcMapper, short>()
        {
            Name = "SCADA_Target_Speed",
            Gateway = ip,
            Path = cpuPath,
            PlcType = plcType,
            Protocol = protocol,
            Timeout = TimeSpan.FromSeconds(2)
        };

        _startTriggerTag = new Tag<BoolPlcMapper, bool>()
        {
            Name = "SCADA_Cmd_Start",
            Gateway = ip,
            Path = cpuPath,
            PlcType = plcType,
            Protocol = protocol,
            Timeout = TimeSpan.FromSeconds(2)
        };

        _heartbeatTag = new Tag<BoolPlcMapper, bool>()
        {
            Name = "SCADA_PC_Heartbeat",
            Gateway = ip,
            Path = cpuPath,
            PlcType = plcType,
            Protocol = protocol,
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public async Task ConnectAsync()
    {
        // libplctag 在首次 Initialize 或 Read 時會自動建立 CIP Session 連線
        await _actualSpeedTag.InitializeAsync();
        await _targetSpeedTag.InitializeAsync();
        await _startTriggerTag.InitializeAsync();
        await _heartbeatTag.InitializeAsync();
        IsConnected = true;
    }

    public async Task<MachineStatus> ReadMachineStatusAsync()
    {
        short speed = await _actualSpeedTag.ReadAsync();
        return new MachineStatus
        {
            ActualSpeed = speed
        };
    }

    public async Task WriteTargetSpeedAsync(short speed)
    {
        _targetSpeedTag.Value = speed;
        await _targetSpeedTag.WriteAsync();
    }

    public async Task WriteStartTriggerAsync(bool isOn)
    {
        _startTriggerTag.Value = isOn;
        await _startTriggerTag.WriteAsync();
    }

    public async Task WriteHeartbeatAsync(bool state)
    {
        _heartbeatTag.Value = state;
        await _heartbeatTag.WriteAsync();
    }

    public void Dispose()
    {
        _actualSpeedTag?.Dispose();
        _targetSpeedTag?.Dispose();
        _startTriggerTag?.Dispose();
        _heartbeatTag?.Dispose();
        IsConnected = false;
    }
}
