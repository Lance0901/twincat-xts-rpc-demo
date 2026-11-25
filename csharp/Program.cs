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
const double StageHeadBackMm = 200.0;                  // 等待區最前面一格在 Load 後面多遠 (第 4 段往前 150 mm 不會越過 Load)
const double StagePitchMm = 150.0;                     // 等待區格距 (> CA gap)
const double PosTolMm = 1.0;                           // 位置容差 (與 PLC cAtStationTolMm 一致)

Text.UseUtf8Console();     // cp950 console 直接 dotnet run 中文也不亂碼

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
Console.WriteLine(Text.Connected(opt.AmsNetId, opt.Port, state.AdsState));
var xts = new XtsRpc(client, opt) { Log = Console.WriteLine };

try
{
    return Run(xts, opt);
}
catch (AdsErrorException ex)
{
    Console.Error.WriteLine(Text.AdsError(ex.ErrorCode, ex.Message));
    Console.Error.WriteLine(Text.AdsErrorHint);
    return 1;
}

int Run(XtsRpc xts, Options opt)
{
    // 1. ready 前：mover 命令應被拒
    Step(Text.Step1Title);
    var early = xts.ReadXtsStatus();
    if (!early.Ready)
    {
        PrintCmd("M_MoveTo", xts.MoverMoveTo(2, 500.0, 200.0));
    }
    else
    {
        Say(Text.AlreadyReady);
    }

    // 2. 初始化
    if (!early.Ready)
    {
        Step(Text.Step2Title);
        var init = xts.InitialXts();
        PrintCmd("M_InitialXTS", init);
        if (!init.Ok) return 1;
        if (!WaitReady(xts, opt)) return 1;
    }

    // 3. Manual：單一 mover
    Step(Text.Step3Title);
    PrintCmd("M_SetMode(0)", xts.SetMode(0));
    // mover 只能往前走、CA 不能超車：上一輪留在 Unload / 2900 的 mover 會擋住下一輪，先依環上順序排到 Load 前面
    if (!StageMovers(xts, opt)) return 1;
    var line = xts.GetLineStatus().Line;
    int front = FindFrontMover(line);
    if (front == 0)
    {
        Say(Text.NoClearMover);
        return 1;
    }
    Say(Text.PickedMover(front));

    Step(Text.Step4Title(opt.MoverViaControl ? Text.ViaControl(opt.ControlPath, "M_MoveTo") : opt.MoverPath(front)));
    PrintMover(xts, opt, front);
    Say(Text.InvalidParamDemo);
    PrintCmd("M_MoveTo(5000, 100)", xts.MoverMoveTo(front, 5000.0, 100.0));
    PrintCmd("M_MoveTo(100, -1)", xts.MoverMoveTo(front, 100.0, -1.0));
    // 往前最多 150 mm，不越過 Load (越過的話送 Load 要繞一整圈，會被其他 mover 擋住)
    double frontPos = line.Movers[front - 1].PositionMm;
    double target = Ring(frontPos + Math.Max(0.0, Math.Min(150.0, AheadDist(stationPosMm[StationLoad - 1], frontPos))));
    if (!Cmd($"M_MoveTo({target:F1}, 300)", xts.MoverMoveTo(front, target, 300.0))) return 1;
    if (!WaitMoverIdle(xts, opt, front)) return 1;
    PrintMover(xts, opt, front);
    if (!opt.MoverViaControl)
    {
        PrintCmd(Text.MoverStopDemo, xts.MoverStop(front));
    }

    // 5. Manual：單站，一站一站送
    Step(Text.Step5Title(opt.StationViaControl ? Text.ViaControl(opt.ControlPath, "M_StartStation") : Text.StationPathEtc(opt.StationPath(StationLoad))));
    foreach (int st in new[] { StationLoad, StationPress, StationUnload })
    {
        if (!Cmd($"M_SendToStation({front}, {st})", xts.SendToStation(front, st))) return 1;
        if (!WaitMoverAtStation(xts, opt, front, st)) return 1;
        if (!Cmd(Text.StationCmd(st, "M_Start"), xts.StationStart(st))) return 1;
        if (!WaitStationDone(xts, opt, st)) return 1;
        if (!Cmd(Text.StationCmd(st, "M_Release"), xts.StationRelease(st))) return 1;
        var m = xts.GetLineStatus().Line.Movers[front - 1];
        Say(Text.MoverPartState(front, m.HasPart, m.Pressed));
    }
    Say(Text.ParkMover(front, ParkPosMm));
    if (!Cmd($"M_MoveTo({ParkPosMm}, 400)", xts.MoverMoveTo(front, ParkPosMm, 400.0))) return 1;
    if (!WaitMoverIdle(xts, opt, front)) return 1;

    // 6. Auto：整輪
    Step(Text.Step6Title);
    if (!Cmd("M_SetMode(1)", xts.SetMode(1))) return 1;
    Thread.Sleep(opt.PollMs);   // 模式在下一個 PLC cycle 生效
    PrintCmd(Text.MoverMoveToInAuto, xts.MoverMoveTo(front, 100.0, 100.0));
    line = xts.GetLineStatus().Line;
    int cycleMover = FindFrontMover(line);
    if (cycleMover == 0)
    {
        Say(Text.NoClearMoverSkipCycle);
        return 1;
    }
    Step(Text.Step7Title(cycleMover));
    if (!Cmd($"M_StartCycle({cycleMover})", xts.StartCycle(cycleMover))) return 1;
    if (!WaitCycle(xts, opt)) return 1;

    Step(Text.Step8Title);
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
            Say(Text.InitStep(sw.Elapsed, s.Step, s.StepMsg));
            lastStep = s.Step;
        }
        if (s.Ready)
        {
            PrintInitTrace(s);
            return true;
        }
        if (s.Error)
        {
            Say(Text.InitFailed(s.ErrorId, s.StepMsg));
            PrintInitTrace(s);
            return false;
        }
        Thread.Sleep(opt.PollMs);
    }
    var last = xts.ReadXtsStatus();
    Say(Text.InitTimeout(opt.InitTimeoutSec, last.Step, last.StepMsg));
    PrintInitTrace(last);
    return false;
}

// 500 ms 輪詢抓不到太快的步驟，改看 PLC 記下的各步完成時間
static void PrintInitTrace(XtsReadyStatus s) => Say(s.Trace is null ? Text.NoInitTrace : Text.InitTraceLine(s.Trace));

bool WaitMoverIdle(XtsRpc xts, Options opt, int mover)
{
    var sw = Stopwatch.StartNew();
    Thread.Sleep(opt.PollMs);   // 命令在下一個 PLC cycle 才被取走
    while (sw.Elapsed.TotalSeconds < opt.StepTimeoutSec)
    {
        var m = xts.GetLineStatus().Line.Movers[mover - 1];
        if (m.Error)
        {
            Say(Text.MoverError(mover, m.ErrorId));
            return false;
        }
        if (!m.Busy) return true;
        Thread.Sleep(opt.PollMs);
    }
    Say(Text.MoverMoveTimeout(mover, opt.StepTimeoutSec));
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
            Say(Text.MoverError(mover, m.ErrorId));
            return false;
        }
        if (m.Station == station) return true;
        Thread.Sleep(opt.PollMs);
    }
    Say(Text.MoverNotAtStation(mover, station));
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
            Say(Text.StationStep(station, s.Step, s.AxisXMm, s.AxisYMm, s.AxisZMm));
            lastStep = s.Step;
        }
        if (s.State == 20) return true;         // E_StationState.Done
        if (s.State == 900)
        {
            Say(Text.StationError(station, s.ErrorId));
            return false;
        }
        Thread.Sleep(opt.PollMs);
    }
    Say(Text.StationTimeout(station));
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
                Say(Text.StationDone(names[i], sw.Elapsed.TotalSeconds, line.StationDoneMs[i] / 1000.0));
            }
        }
        switch (line.CycleState)
        {
            case 100:
                Say(Text.CycleDone(msg, line.CycleCount));
                Say(Text.CycleTimeNote);
                return true;
            case 800:
            case 900:
                Say(Text.CycleAborted(line.CycleState, msg));
                return false;
        }
        Thread.Sleep(opt.PollMs);
    }
    Say(Text.CycleTimeout(opt.StepTimeoutSec));
    return false;
}

// 依環上順序把所有 mover 往前排到 Load 後面的等待區 (Load - 200, -350, -500, ...)。
// 以「往前到 Load 的距離」排序，最接近 Load 的排最前面；環上順序不變，每台目標都在前車目標後面，只往前走、不會被擋。
// 已在等待區內 (離 Load 比目標格還近) 的 mover 不往回走，原地不動。
bool StageMovers(XtsRpc xts, Options opt)
{
    var movers = xts.GetLineStatus().Line.Movers;
    double load = stationPosMm[StationLoad - 1];
    var order = Enumerable.Range(0, LineStatus.MoverCount)
        .OrderBy(k => AheadDist(load, movers[k].PositionMm)).ToArray();
    double prevBack = double.NegativeInfinity;   // 前一台停在 Load 後面多遠 (mm)
    var moved = new List<int>();
    foreach (int k in order)
    {
        double dist = AheadDist(load, movers[k].PositionMm);
        double back = Math.Min(Math.Max(prevBack + StagePitchMm, StageHeadBackMm), dist);
        prevBack = back;
        if (dist - back <= PosTolMm) continue;
        double target = Ring(load - back);
        if (!Cmd($"mover {k + 1} M_MoveTo({target:F1}, 400)", xts.MoverMoveTo(k + 1, target, 400.0))) return false;
        moved.Add(k + 1);
    }
    if (moved.Count == 0)
    {
        Say(Text.StageAlreadyDone);
        return true;
    }
    Say(Text.StageMovers(moved));
    foreach (int n in moved)
    {
        if (!WaitMoverIdle(xts, opt, n)) return false;
    }
    return true;
}

// 從 fromMm 往前走到 toMm 的距離 (mm)；在 toMm 容差內 (含剛好超過一點) 回 0 附近，不當成要繞一圈
double AheadDist(double toMm, double fromMm) => Ring(toMm - fromMm + PosTolMm) - PosTolMm;

// 前方沒有其他 mover 的那台：mover 只往前走，示範與 M_StartCycle 都先到 Load 再到 Unload，
// 所以要看「往前經 Load 到 Unload + CA gap」整段 (CA 會讓 mover 排在前車後面)
int FindFrontMover(LineStatus line)
{
    for (int k = 0; k < LineStatus.MoverCount; k++)
    {
        double path = AheadDist(stationPosMm[StationLoad - 1], line.Movers[k].PositionMm)
            + (stationPosMm[StationUnload - 1] - stationPosMm[StationLoad - 1]) + CaGapMm;
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

static void Say(string line) => Console.WriteLine("   " + line);

static void PrintCmd(string name, RpcResult r)
{
    Say(Text.CmdResult(name, r.Ok, r.Msg(0), r.RetryNote));
}

static bool Cmd(string name, RpcResult r)
{
    PrintCmd(name, r);
    if (r.NotTakenTimeout)
    {
        Say(Text.CmdNotTaken(name));
    }
    return r.Ok;
}
