# 🏭 MyFactoryScada - 工業物聯網與多協定產線戰情室系統

**MyFactoryScada** 是一套基於 **.NET 8 (WPF)** 與 **MVVM 架構**開發的現代化工業監控與數據採集（SCADA）戰情室系統。系統採用「通訊驅動抽象化」與「非同步背景引擎」設計，支援多種主流工業 PLC 與物聯網通訊協定，並具備高速歷史數據歸檔與操作稽核能力。

---

## ✨ 核心特色

* **多協定統一驅動架構 (`IPlcDriver`)**：透過工廠模式（`PlcDriverFactory`）動態載入驅動，支援無縫切換 **MQTT (JSON)**、**Sparkplug B**、**OPC UA**、**Mitsubishi (MC Protocol)**、**Siemens (S7)**、**Rockwell (EtherNet/IP)** 與 **Fatek (永宏)**。
* **高併發雙向控制與心跳機制 (`ScadaEngine`)**：
  * 背景執行緒高速輪詢機台狀態（主軸轉速、馬達繞組溫度、運轉與警報狀態）。
  * 內建優先寫入佇列（Priority Write Queue），確保操作員下發的「目標轉速修改」與「機台啟停指令」優先於週期性「PC 看門狗心跳（`M500`）」送達現場設備。
* **現代化深色工業戰情室介面 (WPF + ScottPlot 5)**：
  * 採用 `CommunityToolkit.Mvvm` 實現前後端解耦與跨執行緒安全 UI 刷新。
  * 整合 **ScottPlot 5** 高效能即時趨勢曲線（支援繁體中文字型渲染），即時追蹤轉速與溫度變化。
* **企業級資料庫歸檔與稽核 (SQL Server + SqlBulkCopy)**：
  * **高速歷史趨勢寫入 (`SqlServerTelemetryHistorian`)**：透過記憶體緩衝佇列（`ConcurrentQueue`）與 `SqlBulkCopy` 批次寫入，避免高頻採樣造成資料庫 I/O 瓶頸。
  * **操作安全與稽核日誌 (`SqlServerSecurityAndLogService`)**：自動建立資料表並完整記錄每一次參數修改、啟停操作與異常警報。

---

## 🏗️ 系統架構與專案結構

```text
FactoryScadaSolution/
├── MyFactoryScada/                  # WPF 前端戰情室主程式 (.NET 8.0-windows)
│   ├── Config/
│   │   └── tags.json                # 外部設備連線與動態點位設定檔
│   ├── App.xaml / App.xaml.cs       # 依賴注入 (DI) 容器組裝與生命週期管理
│   ├── MainWindow.xaml / .cs        # 戰情室 UI 佈局與 ScottPlot 趨勢圖初始化
│   └── MainViewModel.cs             # 畫面狀態繫結、安全範圍防呆與控制指令
│
├── WpfLibrary1/                     # 核心通訊引擎與資料庫類別庫 (.NET 8.0-windows)
│   ├── Interfaces/
│   │   └── IPlcDriver.cs            # 統一通訊驅動標準介面
│   ├── Drivers/
│   │   ├── MqttGatewayDriver.cs     # MQTT JSON 雙向通訊驅動 (MQTTnet v5)
│   │   ├── SparkplugDriver.cs       # Sparkplug B 工業二進位規範驅動
│   │   ├── OpcUaDriver.cs           # OPC UA Client 驅動 (OPCFoundation 1.5.x)
│   │   └── ...                      # Mitsubishi / Siemens / Rockwell / Fatek 驅動
│   ├── Engine/
│   │   ├── ScadaEngine.cs           # 核心背景輪詢、斷線自動重連與指令排程引擎
│   │   └── PlcDriverFactory.cs      # 驅動程式工廠
│   └── Database/
│       ├── SqlServerTelemetryHistorian.cs      # SqlBulkCopy 高速歷史數據歸檔器
│       └── SqlServerSecurityAndLogService.cs   # 操作稽核與警報日誌服務
│
└── EdgeGatewaySimulator/            # 邊緣閘道器與機台雙向模擬器 (Console App)
    └── Program.cs                   # 模擬每秒推播機台遙測數據並接收控制指令
```

---

## 🛠️ 環境需求與技術棧

* **開發環境**：Visual Studio 2022 / .NET 8.0 SDK (`net8.0-windows`)
* **資料庫**：Microsoft SQL Server（支援本機預設執行個體 `localhost` 或 `SQLEXPRESS`）
* **MQTT Broker**：Eclipse Mosquitto 或 EMQX（預設埠號 `1883`）
* **主要 NuGet 套件**：
  * `CommunityToolkit.Mvvm` (MVVM 框架)
  * `ScottPlot.WPF` (5.x 即時高效能圖表)
  * `Microsoft.Extensions.DependencyInjection` (依賴注入容器)
  * `MQTTnet` (5.x MQTT 通訊協定)
  * `OPCFoundation.NetStandard.Opc.Ua.Client` (OPC UA 客戶端)
  * `Microsoft.Data.SqlClient` & `Dapper` (資料庫連線與高速批次寫入)

---

## 🚀 快速啟動與雙向測試指南

### 1. 設定外部參數 (`Config/tags.json`)
確認 `MyFactoryScada/Config/tags.json` 的屬性「複製到輸出目錄」已設為**「有較新版本時才複製」**，並配置連線目標（以 MQTT 模擬器為例）：

```json
{
  "PlcConnection": {
    "Brand": "MQTT",
    "IpAddress": "127.0.0.1",
    "Port": 1883
  },
  "Tags": [
    {
      "TagId": "Speed",
      "Description": "主軸轉速",
      "Address": "Spindle_Speed",
      "DataType": "Int16",
      "Scale": 1.0,
      "Unit": "RPM"
    },
    {
      "TagId": "Temp",
      "Description": "馬達繞組溫度",
      "Address": "Motor_Temp",
      "DataType": "Int16",
      "Scale": 1.0,
      "Unit": "°C"
    }
  ]
}
```

### 2. 初始化 SQL Server 資料表
在 SQL Server Management Studio (SSMS) 中連線至 `localhost` 並執行以下 T-SQL 建立歷史數據表（`AuditLogs` 與 `AlarmLogs` 將於程式啟動時自動建立）：

```sql
CREATE TABLE TelemetryHistory (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    Timestamp DATETIME2(3) NOT NULL,
    StationId NVARCHAR(50) NOT NULL,
    TagName NVARCHAR(50) NOT NULL,
    Value FLOAT NOT NULL
);
CREATE INDEX IX_Telemetry_Station_Time ON TelemetryHistory (StationId, TagName, Timestamp DESC);
```

### 3. 啟動順序與閉環驗證
1. **啟動 MQTT Broker**：確認本機 `1883` 埠處於監聽狀態（`netstat -ano | findstr 1883`）。
2. **啟動邊緣閘道模擬器 (`EdgeGatewaySimulator`)**：終端機將開始每秒發送 JSON 遙測數據至 `factory/line1/plc01/status`，並監聽 `factory/line1/plc01/command`。
3. **啟動戰情室主程式 (`MyFactoryScada`)**：
   * 確認右上角指示燈轉為 **🟢 PLC 已連線**，即時轉速、溫度與 ScottPlot 曲線開始動態更新。
   * 在右側控制面板輸入新目標轉速（例如 `1880`）並點擊下發按鈕，模擬器將即時接收 `{"D100":1880}`（或 `{"Target_Speed":1880}`）並拉升模擬轉速，完成雙向閉環控制驗證。

---

## 🗄️ 資料庫查詢範例

```sql
-- 查詢最新 50 筆機台歷史趨勢數據
SELECT TOP 50 
    FORMAT(Timestamp, 'yyyy-MM-dd HH:mm:ss.fff') AS [記錄時間],
    StationId AS [機台編號],
    TagName AS [點位名稱],
    Value AS [數值]
FROM TelemetryHistory
ORDER BY Timestamp DESC;

-- 查詢操作員控制稽核軌跡
SELECT TOP 20 
    FORMAT(Timestamp, 'yyyy-MM-dd HH:mm:ss') AS [操作時間],
    Username AS [操作人員],
    Action AS [執行動作],
    TargetAddress AS [目標位址],
    NewValue AS [寫入新值]
FROM AuditLogs
ORDER BY Timestamp DESC;
```

---

## 💡 開發與部署注意事項

* **RAMDisk (`Z:`) 開發備份**：若將專案放置於記憶體虛擬磁碟（RAMDisk）進行高速編譯，請務必定期將解決方案同步備份至實體硬碟（如 `C:\SCADA\`），避免系統重啟導致原始碼遺失。
* **ScottPlot 中文字型**：圖表初始化時需指定 `TrendPlot.Plot.Font.Set("Microsoft JhengHei")`，以確保標題與座標軸繁體中文正常顯示。
