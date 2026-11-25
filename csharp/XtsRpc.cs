using System.Text;
using TwinCAT.Ads;

namespace XtsRpcClient;

/// <summary>一次 RPC 呼叫的結果：回傳值、輸出參數。</summary>
internal sealed record RpcResult(object? ReturnValue, object[] Outputs)
{
    public bool Ok => ReturnValue is bool b && b;

    /// <summary>實際送出的次數 (1 = 沒重試)。</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>重試到上限，PLC 仍未收下上一個命令。</summary>
    public bool NotTakenTimeout { get; init; }

    /// <summary>重試狀況的一行說明；沒重試時是空字串。</summary>
    public string RetryNote => NotTakenTimeout
        ? $"重試 {Attempts - 1} 次後放棄：PLC 在逾時內一直沒收下上一個命令"
        : Attempts > 1 ? $"重試 {Attempts - 1} 次後收下" : "";

    /// <summary>第 index 個 VAR_OUTPUT (順序 = PLC 宣告順序)。</summary>
    public T Out<T>(int index) => (T)Convert.ChangeType(Outputs[index], typeof(T));

    public string Msg(int index) => Outputs[index]?.ToString()?.TrimEnd('\0') ?? "";
}

/// <summary>
/// InvokeRpcMethod 的薄包裝。用 symbolPath 版本：型別由 PLC 端的 RPC 描述決定，
/// LREAL→double、UINT→ushort、INT→short、BOOL→bool、STRING→string；STRUCT 輸出會是 byte[]。
/// </summary>
internal sealed class XtsRpc
{
    private readonly AdsClient _client;
    private readonly Options _opt;

    public XtsRpc(AdsClient client, Options opt)
    {
        _client = client;
        _opt = opt;
    }

    /// <summary>重試時每次呼叫一行訊息 (console 印出、web 寫 log)；null = 不印。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>單次呼叫，不重試。狀態讀取用這個。</summary>
    public RpcResult Call(string path, string method, params object[] args)
    {
        object? ret = _client.InvokeRpcMethod(path, method, args, out object[]? outs);
        return new RpcResult(ret, outs ?? Array.Empty<object>());
    }

    /// <summary>
    /// 命令呼叫 (Outputs[0] = sMsg)：PLC 回「上一個命令還沒被收下」時每 RetryMs 重送一次，
    /// 總時間上限 RetryTimeoutMs。其他拒絕 (未 ready、Auto 模式、參數錯) 不重試，照原樣回傳。
    /// </summary>
    public RpcResult Command(string path, string method, params object[] args)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int attempts = 1;
        var r = Call(path, method, args);
        while (PlcReject.IsNotTaken(r))
        {
            if (sw.ElapsedMilliseconds + _opt.RetryMs > _opt.RetryTimeoutMs)
            {
                Log?.Invoke($"   {method}: {attempts} 次都回 \"{r.Msg(0)}\"，已達 {_opt.RetryTimeoutMs} ms 上限，放棄");
                return r with { Attempts = attempts, NotTakenTimeout = true };
            }
            Thread.Sleep(_opt.RetryMs);
            attempts++;
            r = Call(path, method, args);
            Log?.Invoke($"   {method}: 第 {attempts} 次 ({sw.ElapsedMilliseconds} ms) -> {r.Ok}，sMsg = \"{r.Msg(0)}\"");
        }
        return r with { Attempts = attempts };
    }

    // ---------- 總控 MAIN.fbXts ----------

    public RpcResult InitialXts() => Command(_opt.ControlPath, "M_InitialXTS");

    /// <summary>
    /// 輸出：bIsReady, eStep, sStepMsg, bIsError, nErrId, eModeNow,
    /// nFindMover1Ms, bFindMover1Detected, nPowerOnMs, nGroupMs (後 4 個是各步完成時間；舊版 PLC 沒有就是 null)
    /// </summary>
    public XtsReadyStatus ReadXtsStatus()
    {
        var r = Call(_opt.ControlPath, "M_ReadXTS_Status");
        InitTrace? trace = r.Outputs.Length >= 10
            ? new InitTrace(Convert.ToUInt32(r.Outputs[6]), r.Out<bool>(7),
                Convert.ToUInt32(r.Outputs[8]), Convert.ToUInt32(r.Outputs[9]))
            : null;
        return new XtsReadyStatus(
            r.Out<bool>(0), Convert.ToInt32(r.Outputs[1]), r.Msg(2),
            r.Out<bool>(3), Convert.ToUInt32(r.Outputs[4]), Convert.ToInt32(r.Outputs[5]), trace);
    }

    public RpcResult SetMode(short mode) => Command(_opt.ControlPath, "M_SetMode", mode);

    public RpcResult StartCycle(int mover) => Command(_opt.ControlPath, "M_StartCycle", (ushort)mover);

    public RpcResult StopAll() => Command(_opt.ControlPath, "M_Stop");

    public RpcResult SendToStation(int mover, int station) =>
        Command(_opt.ControlPath, "M_SendToStation", (ushort)mover, (ushort)station);

    /// <summary>輸出：stLine (byte[]，Pack = 1), sCycle</summary>
    public (LineStatus Line, string CycleMsg) GetLineStatus()
    {
        var r = Call(_opt.ControlPath, "M_GetStatus");
        if (r.Outputs[0] is not byte[] raw)
        {
            throw new InvalidOperationException(
                $"M_GetStatus stLine 回傳型別是 {r.Outputs[0]?.GetType().Name ?? "null"}，預期 byte[]");
        }
        return (LineStatus.Parse(raw), r.Msg(1));
    }

    // ---------- 單一 mover ----------

    public RpcResult MoverMoveTo(int mover, double position, double velocity) => _opt.MoverViaControl
        ? Command(_opt.ControlPath, "M_MoveTo", (ushort)mover, position, velocity)
        : Command(_opt.MoverPath(mover), "M_MoveTo", position, velocity);

    public RpcResult MoverStop(int mover) => Command(_opt.MoverPath(mover), "M_Stop");

    public RpcResult MoverReset(int mover) => Command(_opt.MoverPath(mover), "M_Reset");

    /// <summary>輸出：fPosition, fVelocity, bBusy, bError, nErrorId, bHasPart, bPressed, bPowered</summary>
    public RpcResult MoverGetStatus(int mover) => Call(_opt.MoverPath(mover), "M_GetStatus");

    // ---------- 單站 ----------

    public RpcResult StationStart(int station) => _opt.StationViaControl
        ? Command(_opt.ControlPath, "M_StartStation", (ushort)station)
        : Command(_opt.StationPath(station), "M_Start");

    public RpcResult StationRelease(int station) => _opt.StationViaControl
        ? Command(_opt.ControlPath, "M_ReleaseStation", (ushort)station)
        : Command(_opt.StationPath(station), "M_Release");

    public RpcResult StationReset(int station) => Command(_opt.StationPath(station), "M_Reset");

    /// <summary>輸出：nState, nStepNow, bOccupiedNow, nMoverNow, bErrorNow, nErrorIdNow, nDone, fAxisX, fAxisY, fAxisZ</summary>
    public RpcResult StationGetStatus(int station) => Call(_opt.StationPath(station), "M_GetStatus");
}

internal sealed record XtsReadyStatus(
    bool Ready, int Step, string StepMsg, bool Error, uint ErrorId, int Mode, InitTrace? Trace);

/// <summary>
/// 初始化各步完成時間 (PLC ms，從 M_InitialXTS 被取走起算；0 = 這次初始化還沒完成該步)。
/// 給輪詢抓不到的快步驟留證據。FindMover1Detected = FALSE 且 FindMover1Ms > 0 表示 IdDetectionMode 不是 Mover1、跳過。
/// </summary>
internal sealed record InitTrace(uint FindMover1Ms, bool FindMover1Detected, uint PowerOnMs, uint GroupMs)
{
    public override string ToString()
    {
        static string T(uint ms) => ms == 0 ? "未完成" : $"{ms / 1000.0:F2} s";
        string find = FindMover1Ms == 0 ? "未完成" : $"{T(FindMover1Ms)} ({(FindMover1Detected ? "detection valid" : "skipped")})";
        return $"FindMover1 {find}；PowerOn {T(PowerOnMs)}；EnableGroup {T(GroupMs)} (PLC 時間)";
    }
}

/// <summary>PLC 拒絕原因的判斷集中在這裡。RPC method 只回 BOOL + sMsg，沒有結構化錯誤碼，只能比對字串。</summary>
internal static class PlcReject
{
    /// <summary>FB_XTS_Control / FB_Mover / FB_Station 在上一個 bReq* 還沒被 body 取走時回的 sMsg 開頭。</summary>
    public const string NotTakenPrefix = "Previous command not yet taken by PLC";

    public static bool IsNotTaken(RpcResult r) =>
        !r.Ok && r.Outputs.Length > 0 && r.Msg(0).StartsWith(NotTakenPrefix, StringComparison.Ordinal);
}

internal sealed record MoverStatus(
    double PositionMm, double VelocityMmS, bool Powered, bool Busy, bool Done, bool Error,
    uint ErrorId, bool HasPart, bool Pressed, short Station);

internal sealed record StationStatus(
    short State, short Step, bool Occupied, short Mover, bool Error, uint ErrorId, uint DoneCount,
    double AxisXMm, double AxisYMm, double AxisZMm, bool GripClosed);

/// <summary>
/// ST_LineStatus 的 C# 對應。PLC 端 {attribute 'pack_mode' := '1'}，總長 267 bytes
/// (偏移量照 Build 產生的 TMC：aMover 從 32、aStation 從 144)。
/// </summary>
internal sealed record LineStatus(
    bool Ready, short Mode, short InitStep, bool Error, uint ErrorId,
    short CycleState, short CycleMover, short CycleStation, uint CycleCount,
    uint[] StationDoneMs, MoverStatus[] Movers, StationStatus[] Stations)
{
    public const int MoverCount = 4;
    public const int StationCount = 3;
    public const int Size = 267;

    public static LineStatus Parse(byte[] raw)
    {
        if (raw.Length < Size)
        {
            throw new InvalidOperationException($"ST_LineStatus 長度 {raw.Length}，預期 {Size}");
        }
        using var br = new BinaryReader(new MemoryStream(raw), Encoding.ASCII);
        bool ready = br.ReadBoolean();
        short mode = br.ReadInt16();
        short initStep = br.ReadInt16();
        bool error = br.ReadBoolean();
        uint errorId = br.ReadUInt32();
        short cycleState = br.ReadInt16();
        short cycleMover = br.ReadInt16();
        short cycleStation = br.ReadInt16();
        uint cycleCount = br.ReadUInt32();
        var doneMs = new uint[StationCount];
        for (int i = 0; i < StationCount; i++) doneMs[i] = br.ReadUInt32();
        var movers = new MoverStatus[MoverCount];
        for (int i = 0; i < MoverCount; i++)
        {
            movers[i] = new MoverStatus(
                br.ReadDouble(), br.ReadDouble(), br.ReadBoolean(), br.ReadBoolean(), br.ReadBoolean(),
                br.ReadBoolean(), br.ReadUInt32(), br.ReadBoolean(), br.ReadBoolean(), br.ReadInt16());
        }
        var stations = new StationStatus[StationCount];
        for (int i = 0; i < StationCount; i++)
        {
            stations[i] = new StationStatus(
                br.ReadInt16(), br.ReadInt16(), br.ReadBoolean(), br.ReadInt16(), br.ReadBoolean(),
                br.ReadUInt32(), br.ReadUInt32(), br.ReadDouble(), br.ReadDouble(), br.ReadDouble(),
                br.ReadBoolean());
        }
        return new LineStatus(ready, mode, initStep, error, errorId, cycleState, cycleMover, cycleStation,
            cycleCount, doneMs, movers, stations);
    }
}
