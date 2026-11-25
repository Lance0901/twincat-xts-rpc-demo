using System.Diagnostics;
using TwinCAT.Ads;
using XtsRpcClient;

// 流程：ready 前被拒 → M_InitialXTS → 輪詢 M_ReadXTS_Status → Manual：單一 mover 移動 / 參數被拒 / 停止
//      → 單站手動 Load、Press、Unload → Auto：M_StartCycle 跑一輪 → M_Stop
// UmRT 的 PLC 時間比牆鐘慢約 4.25 倍，PLC 回報的 ms 與錄影速度都不代表實機。

const int StationLoad = 1, StationPress = 2, StationUnload = 3;
const double TrackLengthMm = 3000.0;
const double CaGapMm = 110.0;
double[] stationPosMm = { 1600.0, 2100.0, 2600.0 };    // 與 PLC GVL_Param.cStationPosMm 一致
const double ParkPosMm = 2900.0;                       // 示範後把 mover 停到 Unload 後面

Options opt;
try
{
    opt = Options.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

using var client = new AdsClient();
client.Connect(opt.AmsNetId, opt.Port);
var state = client.ReadState();
Console.WriteLine($"連線 {opt.AmsNetId}:{opt.Port}，ADS state = {state.AdsState}");
var xts = new XtsRpc(client, opt) { Log = Console.WriteLine };

try
{
    return Run(xts, opt);
}
catch (AdsErrorException ex)
{
    Console.Error.WriteLine($"ADS 錯誤：{ex.ErrorCode} {ex.Message}");
    Console.Error.WriteLine("若是 symbol 找不到，試 --mover-path \"MAIN.refMover{0}\"、--mover-via-control 或 --station-via-control");
    return 1;
}

int Run(XtsRpc xts, Options opt)
{
    // 1. ready 前：mover 命令應被拒
    Step("1. ready 前對 mover 2 下 M_MoveTo，預期被拒");
    var early = xts.ReadXtsStatus();
    if (!early.Ready)
    {
        var r = xts.MoverMoveTo(2, 500.0, 200.0);
        Console.WriteLine($"   M_MoveTo -> {r.Ok}，sMsg = \"{r.Msg(0)}\"");
    }
    else
    {
        Console.WriteLine("   XTS 已 ready，略過這一步");
    }

    // 2. 初始化
    if (!early.Ready)
    {
        Step("2. M_InitialXTS，輪詢 M_ReadXTS_Status 直到 ready");
        var init = xts.InitialXts();
        Console.WriteLine($"   M_InitialXTS -> {init.Ok}，sMsg = \"{init.Msg(0)}\"");
        if (!init.Ok) return 1;
        if (!WaitReady(xts, opt)) return 1;
    }

    // 3. Manual：單一 mover
    Step("3. M_SetMode(0) Manual");
    PrintCmd("M_SetMode(0)", xts.SetMode(0));
    var line = xts.GetLineStatus().Line;
    int front = FindFrontMover(line);
    if (front == 0)
    {
        Console.WriteLine("   找不到路徑淨空的 mover (每台前方到 Unload + CA gap 都有車)，請先手動排開");
        return 1;
    }
    Console.WriteLine($"   選 mover {front}：前方到 Unload 沒有其他 mover");

    Step($"4. 單一 mover 路徑 {(opt.MoverViaControl ? opt.ControlPath + " (M_MoveTo 備案)" : opt.MoverPath(front))}");
    PrintMover(xts, opt, front);
    Console.WriteLine("   參數不合法示範：fPosition = 5000 (超出軌道)、fVelocity = -1");
    PrintCmd("M_MoveTo(5000, 100)", xts.MoverMoveTo(front, 5000.0, 100.0));
    PrintCmd("M_MoveTo(100, -1)", xts.MoverMoveTo(front, 100.0, -1.0));
    double target = Ring(line.Movers[front - 1].PositionMm + 150.0);
    if (!Cmd($"M_MoveTo({target:F1}, 300)", xts.MoverMoveTo(front, target, 300.0))) return 1;
    if (!WaitMoverIdle(xts, opt, front)) return 1;
    PrintMover(xts, opt, front);
    if (!opt.MoverViaControl)
    {
        PrintCmd("mover M_Stop (已停止，示範 Stop 永遠接受)", xts.MoverStop(front));
    }

    // 5. Manual：單站，一站一站送
    Step($"5. 單站路徑 {(opt.StationViaControl ? opt.ControlPath + " (M_StartStation 備案)" : opt.StationPath(StationLoad) + " 等")}");
    foreach (int st in new[] { StationLoad, StationPress, StationUnload })
    {
        if (!Cmd($"M_SendToStation({front}, {st})", xts.SendToStation(front, st))) return 1;
        if (!WaitMoverAtStation(xts, opt, front, st)) return 1;
        if (!Cmd($"站 {st} M_Start", xts.StationStart(st))) return 1;
        if (!WaitStationDone(xts, opt, st)) return 1;
        if (!Cmd($"站 {st} M_Release", xts.StationRelease(st))) return 1;
        var m = xts.GetLineStatus().Line.Movers[front - 1];
        Console.WriteLine($"   mover {front}：bHasPart = {m.HasPart}，bPressed = {m.Pressed}");
    }
    Console.WriteLine($"   mover {front} 停到 {ParkPosMm} mm，讓出路徑");
    if (!Cmd($"M_MoveTo({ParkPosMm}, 400)", xts.MoverMoveTo(front, ParkPosMm, 400.0))) return 1;
    if (!WaitMoverIdle(xts, opt, front)) return 1;

    // 6. Auto：整輪
    Step("6. M_SetMode(1) Auto；Auto 下直接對 mover 的命令應被拒");
    if (!Cmd("M_SetMode(1)", xts.SetMode(1))) return 1;
    Thread.Sleep(opt.PollMs);   // 模式在下一個 PLC cycle 生效
    PrintCmd("mover M_MoveTo (Auto 下)", xts.MoverMoveTo(front, 100.0, 100.0));
    line = xts.GetLineStatus().Line;
    int cycleMover = FindFrontMover(line);
    if (cycleMover == 0)
    {
        Console.WriteLine("   沒有路徑淨空的 mover，略過自動循環");
        return 1;
    }
    Step($"7. M_StartCycle({cycleMover})：Load → Press → Unload");
    if (!Cmd($"M_StartCycle({cycleMover})", xts.StartCycle(cycleMover))) return 1;
    if (!WaitCycle(xts, opt)) return 1;

    Step("8. M_Stop (總控)");
    PrintCmd("M_Stop", xts.StopAll());
    return 0;
}

bool WaitReady(XtsRpc xts, Options opt)
{
    var sw = Stopwatch.StartNew();
    int lastStep = -1;
    while (sw.Elapsed.TotalSeconds < opt.InitTimeoutSec)
    {
        var s = xts.ReadXtsStatus();
        if (s.Step != lastStep)
        {
            Console.WriteLine($"   [{sw.Elapsed:mm\\:ss}] step {s.Step}：{s.StepMsg}");
            lastStep = s.Step;
        }
        if (s.Ready)
        {
            PrintInitTrace(s);
            return true;
        }
        if (s.Error)
        {
            Console.WriteLine($"   初始化失敗：nErrorId = 16#{s.ErrorId:X}，{s.StepMsg}");
            PrintInitTrace(s);
            return false;
        }
        Thread.Sleep(opt.PollMs);
    }
    var last = xts.ReadXtsStatus();
    Console.WriteLine($"   逾時 {opt.InitTimeoutSec} s，最後 step {last.Step}：{last.StepMsg}");
    PrintInitTrace(last);
    return false;
}

// 500 ms 輪詢抓不到太快的步驟，改看 PLC 記下的各步完成時間
static void PrintInitTrace(XtsReadyStatus s) => Console.WriteLine(s.Trace is null
    ? "   (PLC 版本沒有各步完成時間輸出)"
    : $"   初始化各步完成：{s.Trace}");

bool WaitMoverIdle(XtsRpc xts, Options opt, int mover)
{
    var sw = Stopwatch.StartNew();
    Thread.Sleep(opt.PollMs);   // 命令在下一個 PLC cycle 才被取走
    while (sw.Elapsed.TotalSeconds < opt.StepTimeoutSec)
    {
        var m = xts.GetLineStatus().Line.Movers[mover - 1];
        if (m.Error)
        {
            Console.WriteLine($"   mover {mover} error 16#{m.ErrorId:X}");
            return false;
        }
        if (!m.Busy) return true;
        Thread.Sleep(opt.PollMs);
    }
    Console.WriteLine($"   mover {mover} 逾時 {opt.StepTimeoutSec} s 仍在移動");
    return false;
}

bool WaitMoverAtStation(XtsRpc xts, Options opt, int mover, int station)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < opt.StepTimeoutSec)
    {
        var m = xts.GetLineStatus().Line.Movers[mover - 1];
        if (m.Error)
        {
            Console.WriteLine($"   mover {mover} error 16#{m.ErrorId:X}");
            return false;
        }
        if (m.Station == station) return true;
        Thread.Sleep(opt.PollMs);
    }
    Console.WriteLine($"   mover {mover} 逾時沒到站 {station}");
    return false;
}

bool WaitStationDone(XtsRpc xts, Options opt, int station)
{
    var sw = Stopwatch.StartNew();
    int lastStep = -1;
    while (sw.Elapsed.TotalSeconds < opt.StepTimeoutSec)
    {
        var s = xts.GetLineStatus().Line.Stations[station - 1];
        if (s.Step != lastStep && s.Step != 0)
        {
            Console.WriteLine($"   站 {station} step {s.Step}  XYZ = ({s.AxisXMm:F1}, {s.AxisYMm:F1}, {s.AxisZMm:F1})");
            lastStep = s.Step;
        }
        if (s.State == 20) return true;         // E_StationState.Done
        if (s.State == 900)
        {
            Console.WriteLine($"   站 {station} error 16#{s.ErrorId:X}");
            return false;
        }
        Thread.Sleep(opt.PollMs);
    }
    Console.WriteLine($"   站 {station} 逾時");
    return false;
}

bool WaitCycle(XtsRpc xts, Options opt)
{
    var sw = Stopwatch.StartNew();
    var printed = new bool[LineStatus.StationCount];
    string[] names = { "Load", "Press", "Unload" };
    while (sw.Elapsed.TotalSeconds < opt.StepTimeoutSec)
    {
        var (line, msg) = xts.GetLineStatus();
        for (int i = 0; i < LineStatus.StationCount; i++)
        {
            if (!printed[i] && line.StationDoneMs[i] > 0)
            {
                printed[i] = true;
                Console.WriteLine($"   {names[i],-6} 完成：牆鐘 {sw.Elapsed.TotalSeconds,7:F1} s，PLC {line.StationDoneMs[i] / 1000.0,7:F1} s");
            }
        }
        switch (line.CycleState)
        {
            case 100:
                Console.WriteLine($"   一輪完成 ({msg})，nCycleCount = {line.CycleCount}");
                Console.WriteLine("   註：UmRT 的 PLC 時間比牆鐘慢約 4.25 倍，兩個時間都不代表實機節拍");
                return true;
            case 800:
            case 900:
                Console.WriteLine($"   循環中止，eCycleState = {line.CycleState}，{msg}");
                return false;
        }
        Thread.Sleep(opt.PollMs);
    }
    Console.WriteLine($"   循環逾時 {opt.StepTimeoutSec} s");
    return false;
}

// 前方到 Unload + CA gap 之間沒有其他 mover 的那台 (CA 會讓 mover 排在前車後面)
int FindFrontMover(LineStatus line)
{
    for (int k = 0; k < LineStatus.MoverCount; k++)
    {
        double path = Ring(stationPosMm[StationUnload - 1] - line.Movers[k].PositionMm) + CaGapMm;
        bool clear = true;
        for (int j = 0; j < LineStatus.MoverCount; j++)
        {
            if (j != k && Ring(line.Movers[j].PositionMm - line.Movers[k].PositionMm) < path) clear = false;
        }
        if (clear && !line.Movers[k].HasPart) return k + 1;
    }
    return 0;
}

double Ring(double p) => ((p % TrackLengthMm) + TrackLengthMm) % TrackLengthMm;

void PrintMover(XtsRpc xts, Options opt, int mover)
{
    if (opt.MoverViaControl)
    {
        var m = xts.GetLineStatus().Line.Movers[mover - 1];
        Console.WriteLine($"   mover {mover}: pos {m.PositionMm:F1} mm, vel {m.VelocityMmS:F1} mm/s, busy {m.Busy}, error {m.Error}");
        return;
    }
    var r = xts.MoverGetStatus(mover);
    Console.WriteLine($"   M_GetStatus mover {mover}: pos {r.Out<double>(0):F1} mm, vel {r.Out<double>(1):F1} mm/s, " +
        $"busy {r.Out<bool>(2)}, error {r.Out<bool>(3)} (16#{r.Out<uint>(4):X}), part {r.Out<bool>(5)}, pressed {r.Out<bool>(6)}");
}

static void Step(string title) => Console.WriteLine($"\n== {title}");

static void PrintCmd(string name, RpcResult r)
{
    string note = r.RetryNote.Length > 0 ? $"  [{r.RetryNote}]" : "";
    Console.WriteLine($"   {name} -> {r.Ok}，sMsg = \"{r.Msg(0)}\"{note}");
}

static bool Cmd(string name, RpcResult r)
{
    PrintCmd(name, r);
    if (r.NotTakenTimeout)
    {
        Console.WriteLine($"   {name} 失敗：PLC 一直沒取走上一個命令 (PLC task 卡住或太慢？UmRT 可試加大 --retry-timeout-ms)");
    }
    return r.Ok;
}
