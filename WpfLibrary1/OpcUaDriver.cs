using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using WpfLibrary1;

public class OpcUaDriver : IPlcDriver
{
    private ISession? _session;
    private readonly string _endpointUrl;

    public bool IsConnected => _session != null && _session.Connected;

    /// <param name="endpointUrl">OPC UA 伺服器網址，例如 "opc.tcp://192.168.1.10:4840"</param>
    public OpcUaDriver(string endpointUrl)
    {
        _endpointUrl = endpointUrl;
    }

#pragma warning disable CS0618 // 關閉 ApplicationInstance 的過時警告提示
    public async Task ConnectAsync()
    {
        // 1. 建立 ApplicationInstance 並透過 Build() 產生組態 (這是此版本正確的建構方式)
        var application = new ApplicationInstance
        {
            ApplicationName = "CSharp_SCADA_Client",
            ApplicationType = ApplicationType.Client
        };

        var config = await application.Build(
                applicationUri: Utils.Format(@"urn:{0}:CSharp_SCADA_Client", System.Net.Dns.GetHostName()),
                productUri: "urn:MyFactory:CSharpScadaClient")
            .AsClient()
            .AddSecurityConfiguration(
                subjectName: "CSharp_SCADA_Client")
            //storePath: @"%CommonApplicationData%\OPC Foundation\CertificateStores")
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(default);

        // 2. 自動檢查並建立本機憑證
        //await application.CheckApplicationInstanceCertificate(silent: true, minimumKeySize: 2048);
        // 新版改為非同步複數名稱 CheckApplicationInstanceCertificatesAsync
        await application.CheckApplicationInstanceCertificatesAsync(silent: true);

        // 3. 探索伺服器端點
        EndpointDescription? selectedEndpoint = CoreClientUtils.SelectEndpoint(config, _endpointUrl, useSecurity: false);
        var endpointConfiguration = new EndpointConfiguration();
#pragma warning disable CS8604 // 可能有 Null 參考引數。
        ConfiguredEndpoint endpoint = new ConfiguredEndpoint(null, description: selectedEndpoint, endpointConfiguration);
#pragma warning restore CS8604 // 可能有 Null 參考引數。

        // 4. 建立並開啟 OPC UA Session 連線
        _session = await DefaultSessionFactory.Instance.CreateAsync(
            config,
            endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "SCADA_Main_Session",
            sessionTimeout: 60000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: default
        );
    }
#pragma warning restore CS0618

    // =========================================================================
    // 實作 IPlcDriver 標準介面方法
    // =========================================================================

    // =========================================================================
    // 實作 IPlcDriver 標準介面方法 (全面改用新版標準陣列，移除舊版 Collection)
    // =========================================================================

    public async Task<MachineStatus> ReadMachineStatusAsync()
    {
        if (!IsConnected || _session == null)
            throw new InvalidOperationException("OPC UA 尚未連線。");

        // 1. 直接使用標準陣列 ReadValueId[]，會自動隱含轉換為 ArrayOf<ReadValueId>
        NodeId nodeId1 = new("ns=2;s=Channel1.Device1.Spindle_Speed", 1);//這裡的1 2 3 4 5 待check
        NodeId nodeId2 = new("ns=2;s=Channel1.Device1.Motor_Temp", 2);
        NodeId nodeId3 = new("ns=2;s=Channel1.Device1.Is_Running", 3);
        NodeId nodeId4 = new("ns=2;s=Channel1.Device1.Overheat_Alarm", 4);
        NodeId nodeId5 = new("ns=2;s=Channel1.Device1.Cmd_Ack_Done", 5);
        var nodesToRead = new ReadValueId[]
        {
            new() { NodeId = nodeId1, AttributeId = Attributes.Value },
            new() { NodeId = nodeId2, AttributeId = Attributes.Value },
            new() { NodeId = nodeId3, AttributeId = Attributes.Value },
            new() { NodeId = nodeId4, AttributeId = Attributes.Value },
            new() { NodeId = nodeId5, AttributeId = Attributes.Value },
        };

        ReadResponse? response = await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, nodesToRead, default);
        if (response == null) return new MachineStatus();
        // 2. 直接透過 response.Results[索引] 取值，不需宣告 DataValueCollection
        var results = response.Results;
        try
        {
            return new MachineStatus
            {
                ActualSpeed = Convert.ToInt16(results[0]),
                Temperature = Convert.ToInt16(results[1]),
                IsRunning = Convert.ToBoolean(results[2]),
                IsOverheatAlarm = Convert.ToBoolean(results[3]),
                IsCmdAckDone = Convert.ToBoolean(results[4])
            };
        }
        catch (Exception) { return new MachineStatus(); }//Is here need Warnning?
    }

    public Task WriteTargetSpeedAsync(short speed)
    {
        return WriteNodeValueAsync("ns=2;s=Channel1.Device1.Target_Speed", speed);
    }

    public Task WriteStartTriggerAsync(bool isOn)
    {
        return WriteNodeValueAsync("ns=2;s=Channel1.Device1.Cmd_Start", isOn);
    }

    public Task WriteHeartbeatAsync(bool state)
    {
        return WriteNodeValueAsync("ns=2;s=Channel1.Device1.PC_Heartbeat", state);
    }

    /// <summary>
    /// 通用 OPC UA 節點寫入方法
    /// </summary>
    private async Task WriteNodeValueAsync(string nodeIdString, object value)
    {
        if (!IsConnected || _session == null) return;

        // 3. 直接使用標準陣列 WriteValue[]，會自動隱含轉換為 ArrayOf<WriteValue>
        // 依據具體型別建立強型別 Variant，避免 new Variant(object) 的過時警告
        Variant variantValue = value switch
        {
            short s => new Variant(s),
            int i => new Variant(i),
            bool b => new Variant(b),
            float f => new Variant(f),
            double d => new Variant(d),
            string str => new Variant(str),
            _ => throw new NotSupportedException($"不支援的 OPC UA 寫入資料型別: {typeof(object).Name}")
        };
        var nodesToWrite = new WriteValue[]
        {
                new WriteValue
                {
                    NodeId = new NodeId(nodeIdString, 1),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(variantValue)
                }
        };

        var response = await _session.WriteAsync(null, nodesToWrite, default);

        // 4. 檢查第 0 筆回傳狀態碼
        if (StatusCode.IsBad(response.Results[0]))
        {
            throw new Exception($"OPC UA 寫入失敗 ({nodeIdString})，錯誤碼: {response.Results[0]}");
        }
    }

    public void Dispose()
    {
        if (_session != null)
        {
            // 新版 ISession 改用非同步關閉方法 CloseAsync()
            _session.CloseAsync().GetAwaiter().GetResult();
            _session.Dispose();
            _session = null;
        }
    }
}
