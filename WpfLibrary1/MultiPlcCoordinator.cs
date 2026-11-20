using System;
using System.Collections.Concurrent;
using WpfLibrary1;

public class MultiPlcCoordinator
{
    // Key = 設備唯一編號 (如 "Mitsu_01", "Siemens_02"), Value = 獨立的通訊引擎
    private readonly ConcurrentDictionary<string, ScadaEngine> _engines = new();

    public void RegisterMachine(string deviceId, string brand, string ip, int port)
    {
        IPlcDriver driver = PlcDriverFactory.CreateDriver(brand, ip, port);
        var engine = new ScadaEngine(driver);

        // 訂閱數據時，透過閉包 (Closure) 綁定 deviceId，精準分辨封包來源！
        engine.OnDataUpdated += data =>
        {
            Console.WriteLine($"[來源設備: {deviceId} ({brand})] 轉速: {data["Speed"]} | 溫度: {data["Temp"]}");
        };

        engine.OnConnectionChanged += connected =>
        {
            Console.WriteLine($"[連線狀態: {deviceId}] {(connected ? "已連線" : "斷線中")}");
        };

        _engines[deviceId] = engine;
        engine.Start();
    }

    // 精準對指定機台下發控制指令
    public void SendCommandToMachine(string deviceId, string address, object value)
    {
        if (_engines.TryGetValue(deviceId, out var engine))
        {
            engine.EnqueueWriteCommand(address, value);
        }
    }
    public void exa()
    {
        var coordinator = new MultiPlcCoordinator();

        // 同時掛載三台不同品牌的 PLC，各自獨立執行緒互不干擾
        coordinator.RegisterMachine("Station01_Mitsu", "MITSUBISHI", "192.168.1.10", 5000);
        coordinator.RegisterMachine("Station02_Siemens", "SIEMENS", "192.168.1.11", 102);
        coordinator.RegisterMachine("Station03_Fatek", "FATEK", "192.168.1.12", 502);

        // 指定只對 2 號西門子站點下發指令
        coordinator.SendCommandToMachine("Station02_Siemens", "DB1.DBW2", 1800);

    }
}
