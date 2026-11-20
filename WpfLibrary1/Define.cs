using WpfLibrary1;

public enum UserRole
{
    Guest = 0,      // 訪客：僅能觀看畫面，不可操作任何按鈕
    Operator = 1,   // 作業員：可執行一般啟停、切換配方
    Engineer = 2    // 工程師：可修改機台參數（如轉速、溫度上限）與復歸嚴重警報
}
public static class Param
{
    public static string Spindle_Speed = "Spindle_Speed";
    public static string Motor_Temp = "Motor_Temp";
    public static string Is_Running = "Is_Running";
    public static string Overheat_Alarm = "Overheat_Alarm";
    public static string Cmd_Ack_Done = "Cmd_Ack_Done";

}
public class TagDefinition
{
    public string TagId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;    // 例如三菱 "D0"、西門子 "DB1.DBW0"、AB "Tag_Speed"
    public string DataType { get; set; } = string.Empty;
    public double Scale { get; set; } = 1.0;
    public string Unit { get; set; } = string.Empty;
    public bool Writable { get; set; } = false;
    public AlarmRule Alarm { get; set; } = new();
}
public class AlarmRule
{
    public bool Enable { get; set; }
    public double HighLimit { get; set; }
    public double LowLimit { get; set; }
    public string AlarmCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
public class TelemetryRecord
{
    public DateTime RecordTime { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;
    public double TagValue { get; set; }
}
public class MachineStatus
{
    public short ActualSpeed { get; set; }      // 實際轉速
    public short Temperature { get; set; }      // 機台溫度
    public bool IsRunning { get; set; }         // 運轉中狀態
    public bool IsOverheatAlarm { get; set; }   // 過熱警報
    public bool IsCmdAckDone { get; set; }      // 指令交握完成旗標
    public MachineStatus()
    {
        ActualSpeed = 0;
        Temperature = 0;
        IsRunning = false;
        IsOverheatAlarm = false;
        IsCmdAckDone = false;
    }
}
