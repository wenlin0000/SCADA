using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WpfLibrary1;
namespace MyFactoryScada
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 建立依賴注入 (DI) 服務容器
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // 2. 啟動背景 PLC 通訊引擎
            var scadaEngine = _serviceProvider.GetRequiredService<ScadaEngine>();
            scadaEngine.Start();

            // 3. 解析並顯示主戰情室視窗
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // A. 讀取外部設定檔 (Config/tags.json)
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "tags.json");
            ScadaConfig config = LoadOrCreateDefaultConfig(configPath);
            services.AddSingleton(config);

            // B. 註冊資料庫服務 (可依現場需求切換 SQLite / SQL Server / PostgreSQL)
            string dbConnStr = "Server=localhost;Database=FactoryScadaDB;Trusted_Connection=True;TrustServerCertificate=True;";
            services.AddSingleton(new SqlServerSecurityAndLogService(dbConnStr));
            services.AddSingleton(new SqlServerTelemetryHistorian(dbConnStr));

            // C. 透過工廠模式動態建立對應品牌的 PLC 驅動 (Mitsubishi / Siemens / Rockwell / Fatek / Mqtt / OpcUa)
            services.AddSingleton<IPlcDriver>(sp =>
                PlcDriverFactory.CreateDriver(
                    config.PlcConnection.Brand,
                    config.PlcConnection.IpAddress,
                    config.PlcConnection.Port
                ));

            // D. 註冊核心通訊引擎
            services.AddSingleton<ScadaEngine>();

            // E. 註冊 MVVM 視圖模型與主視窗
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }

        /// <summary>
        /// 安全載入設定檔：若現場首次啟動找不到檔案，自動產生預設範本避免閃退
        /// </summary>
        private ScadaConfig LoadOrCreateDefaultConfig(string path)
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var defaultConfig = new ScadaConfig
                {
                    PlcConnection = new PlcConnectionConfig
                    {
                        Brand = "MITSUBISHI",
                        IpAddress = "192.168.1.10",
                        Port = 5000
                    }
                };
                string defaultJson = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, defaultJson);
                return defaultConfig;
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ScadaConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new ScadaConfig();
        }

        /// <summary>
        /// 程式關閉時，依序安全釋放 PLC 連線與寫入資料庫剩餘緩衝數據
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}
