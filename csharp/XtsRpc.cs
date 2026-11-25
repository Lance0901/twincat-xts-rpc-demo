using System.Text;
using TwinCAT.Ads;

namespace XtsRpcClient;

/// <summary>一次 RPC 呼叫的結果：回傳值、輸出參數。</summary>
internal sealed record RpcResult(object? ReturnValue, object[] Outputs)
{
    public bool Ok => ReturnValue is bool b && b;

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

    public RpcResult Call(string path, string method, params object[] args)
    {
        object? ret = _client.InvokeRpcMethod(path, method, args, out object[]? outs);
        return new RpcResult(ret, outs ?? Array.Empty<object>());
    }

    // ---------- 總控 MAIN.fbXts ----------

    public RpcResult InitialXts() => Call(_opt.ControlPath, "M_InitialXTS");

    /// <summary>輸出：bIsReady, eStep, sStepMsg, bIsError, nErrId, eModeNow</summary>
    public XtsReadyStatus ReadXtsStatus()
    {
        var r = Call(_opt.ControlPath, "M_ReadXTS_Status");
        return new XtsReadyStatus(
            r.Out<bool>(0), Convert.ToInt32(r.Outputs[1]), r.Msg(2),
            r.Out<bool>(3), Convert.ToUInt32(r.Outputs[4]), Convert.ToInt32(r.Outputs[5]));
    }

    public RpcResult SetMode(short mode) => Call(_opt.ControlPath, "M_SetMode", mode);

    public RpcResult StartCycle(int mover) => Call(_opt.ControlPath, "M_StartCycle", (ushort)mover);

    public RpcResult StopAll() => Call(_opt.ControlPath, "M_Stop");

    public RpcResult SendToStation(int mover, int station) =>
        Call(_opt.ControlPath, "M_SendToStation", (ushort)mover, (ushort)station);

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
        ? Call(_opt.ControlPath, "M_MoveTo", (ushort)mover, position, velocity)
        : Call(_opt.MoverPath(mover), "M_MoveTo", position, velocity);

    public RpcResult MoverStop(int mover) => Call(_opt.MoverPath(mover), "M_Stop");

    public RpcResult MoverReset(int mover) => Call(_opt.MoverPath(mover), "M_Reset");

    /// <summary>輸出：fPosition, fVelocity, bBusy, bError, nErrorId, bHasPart, bPressed, bPowered</summary>
    public RpcResult MoverGetStatus(int mover) => Call(_opt.MoverPath(mover), "M_GetStatus");

    // ---------- 單站 ----------

    public RpcResult StationStart(int station) => _opt.StationViaControl
        ? Call(_opt.ControlPath, "M_StartStation", (ushort)station)
        : Call(_opt.StationPath(station), "M_Start");

    public RpcResult StationRelease(int station) => _opt.StationViaControl
        ? Call(_opt.ControlPath, "M_ReleaseStation", (ushort)station)
        : Call(_opt.StationPath(station), "M_Release");

    public RpcResult StationReset(int station) => Call(_opt.StationPath(station), "M_Reset");

    /// <summary>輸出：nState, nStepNow, bOccupiedNow, nMoverNow, bErrorNow, nErrorIdNow, nDone, fAxisX, fAxisY, fAxisZ</summary>
    public RpcResult StationGetStatus(int station) => Call(_opt.StationPath(station), "M_GetStatus");
}

internal sealed record XtsReadyStatus(bool Ready, int Step, string StepMsg, bool Error, uint ErrorId, int Mode);

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
