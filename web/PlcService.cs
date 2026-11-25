using System.Text.Json;
using TwinCAT.Ads;
using XtsRpcClient;

namespace XtsWeb;

/// <summary>命令結果：照 PLC 的 BOOL 回傳值與 sMsg。</summary>
public sealed record CmdResult(bool Accepted, string SMsg);

/// <summary>web 專用命令列參數 (其餘參數交給 XtsRpcClient.Options 與 ASP.NET Core)。</summary>
internal sealed record WebOptions(bool DetailStatus);

/// <summary>
/// 一條 ADS 連線，所有 RPC 呼叫序列化 (一次一個)。連不上時下次呼叫會重連；
/// 命令與狀態輪詢共用同一條連線。
/// </summary>
internal sealed class PlcConnection : IDisposable
{
    private readonly Options _opt;
    private readonly object _gate = new();
    private AdsClient? _client;
    private XtsRpc? _rpc;

    public PlcConnection(Options opt) => _opt = opt;

    public Options Options => _opt;

    public T Run<T>(Func<XtsRpc, T> action)
    {
        lock (_gate)
        {
            try
            {
                if (_rpc is null)
                {
                    _client = new AdsClient();
                    _client.Connect(_opt.AmsNetId, _opt.Port);
                    _rpc = new XtsRpc(_client, _opt);
                }
                return action(_rpc);
            }
            catch (AdsErrorException)
            {
                // RPC 層錯誤 (symbol 找不到等)：連線還在，交給呼叫端回報
                throw;
            }
            catch
            {
                Close();
                throw;
            }
        }
    }

    private void Close()
    {
        _client?.Dispose();
        _client = null;
        _rpc = null;
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }
}

/// <summary>
/// 背景輪詢：每 PollMs 讀 M_ReadXTS_Status 與總控 M_GetStatus，
/// 每 4 次再讀各 mover / 各站的 M_GetStatus (順便驗證陣列元素路徑與繼承 method)。
/// 結果存成 JSON 字串，SSE 端點只負責轉送。
/// </summary>
internal sealed class StatusPoller : BackgroundService
{
    private static readonly string[] InitStepNames = new string[1000];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PlcConnection _plc;
    private readonly bool _detail;
    private readonly object _lock = new();
    private long _version;
    private string _json = "{\"connected\":false,\"error\":\"not polled yet\"}";
    private List<string> _detailWarnings = new();

    static StatusPoller()
    {
        InitStepNames[0] = "Idle"; InitStepNames[5] = "Reset"; InitStepNames[10] = "InitEnvironment";
        InitStepNames[20] = "WaitMovers"; InitStepNames[21] = "FindMover1"; InitStepNames[22] = "FindMover1Wait";
        InitStepNames[30] = "PowerOn"; InitStepNames[40] = "EnableGroup"; InitStepNames[100] = "Ready";
        InitStepNames[900] = "Error";
    }

    public StatusPoller(PlcConnection plc, WebOptions web)
    {
        _plc = plc;
        _detail = web.DetailStatus;
    }

    public (long Version, string Json) Latest
    {
        get { lock (_lock) return (_version, _json); }
    }

    private void Publish(object snapshot)
    {
        lock (_lock)
        {
            _json = JsonSerializer.Serialize(snapshot, Json);
            _version++;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int n = 0;
        while (!ct.IsCancellationRequested)
        {
            int delay = _plc.Options.PollMs;
            try
            {
                bool doDetail = _detail && n++ % 4 == 0;
                Publish(_plc.Run(rpc => Read(rpc, doDetail)));
            }
            catch (Exception ex)
            {
                Publish(new { connected = false, error = ex.Message });
                delay = 1500;
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private object Read(XtsRpc rpc, bool detail)
    {
        var rd = rpc.ReadXtsStatus();
        var (line, cycleMsg) = rpc.GetLineStatus();
        if (detail)
        {
            var warnings = new List<string>();
            for (int i = 1; i <= LineStatus.MoverCount; i++)
            {
                try { rpc.MoverGetStatus(i); }
                catch (Exception ex) { warnings.Add($"mover[{i}].M_GetStatus: {ex.Message}"); break; }
            }
            for (int i = 1; i <= LineStatus.StationCount; i++)
            {
                try { rpc.StationGetStatus(i); }
                catch (Exception ex) { warnings.Add($"station[{i}].M_GetStatus: {ex.Message}"); break; }
            }
            _detailWarnings = warnings;
        }
        var movers = line.Movers.Select((m, i) => new
        {
            id = i + 1, pos = m.PositionMm, vel = m.VelocityMmS, powered = m.Powered, busy = m.Busy, done = m.Done,
            error = m.Error, errorId = m.ErrorId, hasPart = m.HasPart, pressed = m.Pressed, station = (int)m.Station,
        }).ToArray();
        var stations = line.Stations.Select((s, i) => new
        {
            id = i + 1, state = (int)s.State, step = (int)s.Step, occupied = s.Occupied, mover = (int)s.Mover,
            error = s.Error, errorId = s.ErrorId, doneCount = s.DoneCount, x = s.AxisXMm, y = s.AxisYMm, z = s.AxisZMm,
            grip = s.GripClosed,
        }).ToArray();
        string name = rd.Step >= 0 && rd.Step < InitStepNames.Length ? InitStepNames[rd.Step] ?? "Unknown" : "Unknown";
        return new
        {
            source = "live", connected = true,
            ready = rd.Ready, mode = rd.Mode, initStep = rd.Step, initStepName = name, initMsg = rd.StepMsg,
            initError = rd.Error, initErrorId = rd.ErrorId,
            cycleState = (int)line.CycleState, cycleMover = (int)line.CycleMover, cycleStation = (int)line.CycleStation,
            cycleCount = line.CycleCount, cycleMsg, stationDoneMs = line.StationDoneMs,
            movers, stations, detailWarnings = _detailWarnings,
        };
    }
}
