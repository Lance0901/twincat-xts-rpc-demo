using System.Text;

namespace XtsRpcClient;

/// <summary>console 輸出語言 (--lang zh|en)。</summary>
internal enum Lang
{
    Zh,
    En,
}

/// <summary>
/// console / 共用層自己印出的所有文字，中英文集中在這裡；呼叫點只用 Text.Xxx，不判斷語言。
/// PLC 回傳的 sMsg 本來就是英文，原樣印出，不經過這裡。
/// </summary>
internal static class Text
{
    /// <summary>目前語言；Options.Parse 依 --lang 設定，預設中文。</summary>
    public static Lang Lang { get; set; } = Lang.Zh;

    private static string P(string zh, string en) => Lang == Lang.En ? en : zh;

    // ---------- console 編碼 ----------

    /// <summary>
    /// 把 console 輸入 / 輸出改成 UTF-8，cp950 的 conhost / Windows Terminal 不用先 chcp 65001。
    /// 程式結束時還原原本的 codepage，不改變使用者的終端機設定。
    /// </summary>
    public static void UseUtf8Console()
    {
        try
        {
            var outEnc = Console.OutputEncoding;
            Console.OutputEncoding = Encoding.UTF8;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryRestore(() => Console.OutputEncoding = outEnc);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // 沒有 console (例如輸出被導向且沒有附著的視窗)：維持預設編碼
        }
        try
        {
            var inEnc = Console.InputEncoding;
            Console.InputEncoding = Encoding.UTF8;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryRestore(() => Console.InputEncoding = inEnc);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }
    }

    private static void TryRestore(Action restore)
    {
        try { restore(); } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
    }

    // ---------- 命令列參數 (Options) ----------

    public static string MissingValue(string arg) => P($"{arg} 缺少值", $"{arg} needs a value");

    public static string UnknownArg(string arg) => P($"未知參數 {arg}，用 --help 看說明", $"Unknown argument {arg}, see --help");

    public static string BadLang(string value) => P($"--lang 只接受 zh 或 en，收到 {value}", $"--lang accepts zh or en, got {value}");

    public static string RetryMsPositive => P("--retry-ms 必須 > 0", "--retry-ms must be > 0");

    public static string RetryTimeoutNonNegative => P("--retry-timeout-ms 必須 >= 0", "--retry-timeout-ms must be >= 0");

    public static string Help => P("""
        XtsRpcClient — 用 InvokeRpcMethod 控制 XTS 模擬 (TwinCAT PLC {attribute 'TcRpcEnable'} method)

          --netid <AmsNetId>         預設 AmsNetId.Local；連 UmRT 要給 UmRT 的 AmsNetId (否則 ADS error 6)
          --port <n>                 預設 851
          --control-path <path>      預設 MAIN.fbXts
          --mover-path <template>    預設 MAIN.fbXts.aMover[{0}]；備案 MAIN.refMover{0}
          --station-path <template>  預設 MAIN.fbXts.{0} ({0} = fbLoad / fbPress / fbUnload)
          --mover-via-control        mover 命令改走總控 M_MoveTo
          --station-via-control      站命令改走總控 M_StartStation / M_ReleaseStation
          --init-timeout <s>         預設 900 (UmRT 比實際慢，放寬)
          --step-timeout <s>         預設 900
          --poll-ms <ms>             預設 500
          --retry-ms <ms>            PLC 回「上一個命令還沒被收下」時的重送間隔，預設 200
          --retry-timeout-ms <ms>    重送總上限，預設 5000；0 = 不重試
          --lang <zh|en>             console 輸出語言，預設 zh
        """, """
        XtsRpcClient — controls the XTS simulation through InvokeRpcMethod (TwinCAT PLC {attribute 'TcRpcEnable'} methods)

          --netid <AmsNetId>         default AmsNetId.Local; for UmRT pass the UmRT AmsNetId (otherwise ADS error 6)
          --port <n>                 default 851
          --control-path <path>      default MAIN.fbXts
          --mover-path <template>    default MAIN.fbXts.aMover[{0}]; fallback MAIN.refMover{0}
          --station-path <template>  default MAIN.fbXts.{0} ({0} = fbLoad / fbPress / fbUnload)
          --mover-via-control        send mover commands through the control FB's M_MoveTo
          --station-via-control      send station commands through M_StartStation / M_ReleaseStation
          --init-timeout <s>         default 900 (UmRT runs slower than real time)
          --step-timeout <s>         default 900
          --poll-ms <ms>             default 500
          --retry-ms <ms>            resend interval when the PLC has not taken the previous command, default 200
          --retry-timeout-ms <ms>    total resend limit, default 5000; 0 = no retry
          --lang <zh|en>             console output language, default zh
        """);

    // ---------- 連線與錯誤 ----------

    public static string Connected(object netId, int port, object adsState) =>
        P($"連線 {netId}:{port}，ADS state = {adsState}", $"Connected to {netId}:{port}, ADS state = {adsState}");

    public static string AdsError(object code, string message) => P($"ADS 錯誤：{code} {message}", $"ADS error: {code} {message}");

    public static string AdsErrorHint => P(
        "若是 symbol 找不到，試 --mover-path \"MAIN.refMover{0}\"、--mover-via-control 或 --station-via-control",
        "If the symbol is not found, try --mover-path \"MAIN.refMover{0}\", --mover-via-control or --station-via-control");

    // ---------- 示範段落 ----------

    public static string Step1Title => P("1. ready 前對 mover 2 下 M_MoveTo，預期被拒", "1. M_MoveTo on mover 2 before ready, expected to be rejected");

    public static string AlreadyReady => P("XTS 已 ready，略過這一步", "XTS is already ready, skipping this step");

    public static string Step2Title => P("2. M_InitialXTS，輪詢 M_ReadXTS_Status 直到 ready", "2. M_InitialXTS, poll M_ReadXTS_Status until ready");

    public static string Step3Title => "3. M_SetMode(0) Manual";

    public static string NoClearMover => P(
        "找不到路徑淨空的 mover (每台前方到 Unload + CA gap 都有車)，請先手動排開",
        "No mover with a clear path (every mover has another one ahead before Unload + CA gap); move them apart manually first");

    public static string PickedMover(int mover) => P($"選 mover {mover}：前方到 Unload 沒有其他 mover", $"Picked mover {mover}: no other mover ahead up to Unload");

    public static string ViaControl(string path, string method) => P($"{path} ({method} 備案)", $"{path} ({method} fallback)");

    public static string StationPathEtc(string path) => P($"{path} 等", $"{path}, ...");

    public static string Step4Title(string path) => P($"4. 單一 mover 路徑 {path}", $"4. Single mover, path {path}");

    public static string InvalidParamDemo => P(
        "參數不合法示範：fPosition = 5000 (超出軌道)、fVelocity = -1",
        "Invalid parameter demo: fPosition = 5000 (off the track), fVelocity = -1");

    public static string MoverStopDemo => P("mover M_Stop (已停止，示範 Stop 永遠接受)", "mover M_Stop (already stopped, Stop is always accepted)");

    public static string Step5Title(string path) => P($"5. 單站路徑 {path}", $"5. Single station, path {path}");

    public static string StationCmd(int station, string method) => P($"站 {station} {method}", $"station {station} {method}");

    public static string MoverPartState(int mover, bool hasPart, bool pressed) =>
        P($"mover {mover}：bHasPart = {hasPart}，bPressed = {pressed}", $"mover {mover}: bHasPart = {hasPart}, bPressed = {pressed}");

    public static string ParkMover(int mover, double posMm) => P($"mover {mover} 停到 {posMm} mm，讓出路徑", $"Parking mover {mover} at {posMm} mm to clear the path");

    public static string Step6Title => P("6. M_SetMode(1) Auto；Auto 下直接對 mover 的命令應被拒", "6. M_SetMode(1) Auto; direct mover commands must be rejected in Auto");

    public static string MoverMoveToInAuto => P("mover M_MoveTo (Auto 下)", "mover M_MoveTo (in Auto)");

    public static string NoClearMoverSkipCycle => P("沒有路徑淨空的 mover，略過自動循環", "No mover with a clear path, skipping the auto cycle");

    public static string Step7Title(int mover) => P($"7. M_StartCycle({mover})：Load → Press → Unload", $"7. M_StartCycle({mover}): Load → Press → Unload");

    public static string Step8Title => P("8. M_Stop (總控)", "8. M_Stop (control FB)");

    // ---------- 命令結果 ----------

    public static string CmdResult(string name, bool ok, string sMsg, string retryNote) =>
        P($"{name} -> {ok}，sMsg = \"{sMsg}\"", $"{name} -> {ok}, sMsg = \"{sMsg}\"")
        + (retryNote.Length > 0 ? $"  [{retryNote}]" : "");

    public static string CmdNotTaken(string name) => P(
        $"{name} 失敗：PLC 一直沒取走上一個命令 (PLC task 卡住或太慢？UmRT 可試加大 --retry-timeout-ms)",
        $"{name} failed: the PLC never took the previous command (PLC task stuck or too slow? On UmRT try a larger --retry-timeout-ms)");

    // ---------- 等待 / 輪詢 ----------

    public static string InitStep(TimeSpan elapsed, int step, string stepMsg) =>
        P($"[{elapsed:mm\\:ss}] step {step}：{stepMsg}", $"[{elapsed:mm\\:ss}] step {step}: {stepMsg}");

    public static string InitFailed(uint errorId, string stepMsg) =>
        P($"初始化失敗：nErrorId = 16#{errorId:X}，{stepMsg}", $"Initialization failed: nErrorId = 16#{errorId:X}, {stepMsg}");

    public static string InitTimeout(int sec, int step, string stepMsg) =>
        P($"逾時 {sec} s，最後 step {step}：{stepMsg}", $"Timed out after {sec} s, last step {step}: {stepMsg}");

    public static string NoInitTrace => P("(PLC 版本沒有各步完成時間輸出)", "(this PLC version does not report per-step completion times)");

    public static string InitTraceLine(InitTrace trace) => P($"初始化各步完成：{trace}", $"Init steps completed: {trace}");

    public static string MoverError(int mover, uint errorId) => $"mover {mover} error 16#{errorId:X}";

    public static string MoverMoveTimeout(int mover, int sec) => P($"mover {mover} 逾時 {sec} s 仍在移動", $"mover {mover} still moving after {sec} s timeout");

    public static string MoverNotAtStation(int mover, int station) => P($"mover {mover} 逾時沒到站 {station}", $"mover {mover} timed out before reaching station {station}");

    public static string StationStep(int station, int step, double x, double y, double z) =>
        P($"站 {station} step {step}  XYZ = ({x:F1}, {y:F1}, {z:F1})", $"station {station} step {step}  XYZ = ({x:F1}, {y:F1}, {z:F1})");

    public static string StationError(int station, uint errorId) => P($"站 {station} error 16#{errorId:X}", $"station {station} error 16#{errorId:X}");

    public static string StationTimeout(int station) => P($"站 {station} 逾時", $"station {station} timed out");

    public static string StationDone(string name, double wallSec, double plcSec) =>
        P($"{name,-6} 完成：牆鐘 {wallSec,7:F1} s，PLC {plcSec,7:F1} s", $"{name,-6} done: wall clock {wallSec,7:F1} s, PLC {plcSec,7:F1} s");

    public static string CycleDone(string msg, uint count) => P($"一輪完成 ({msg})，nCycleCount = {count}", $"Cycle complete ({msg}), nCycleCount = {count}");

    public static string CycleTimeNote => P(
        "註：UmRT 的 PLC 時間比牆鐘慢約 4.25 倍，兩個時間都不代表實機節拍",
        "Note: UmRT PLC time runs about 4.25x slower than wall clock; neither time represents real machine cycle time");

    public static string CycleAborted(short state, string msg) => P($"循環中止，eCycleState = {state}，{msg}", $"Cycle aborted, eCycleState = {state}, {msg}");

    public static string CycleTimeout(int sec) => P($"循環逾時 {sec} s", $"Cycle timed out after {sec} s");

    // ---------- 共用 RPC 層 (XtsRpc，console 與 web 都用) ----------

    public static string RetryGaveUp(int retries) => P(
        $"重試 {retries} 次後放棄：PLC 在逾時內一直沒收下上一個命令",
        $"gave up after {retries} retries: the PLC did not take the previous command within the timeout");

    public static string RetryTaken(int retries) => P($"重試 {retries} 次後收下", $"accepted after {retries} retries");

    public static string RetryLimitLog(string method, int attempts, string sMsg, int limitMs) => P(
        $"{method}: {attempts} 次都回 \"{sMsg}\"，已達 {limitMs} ms 上限，放棄",
        $"{method}: {attempts} attempts all returned \"{sMsg}\", {limitMs} ms limit reached, giving up");

    public static string RetryAttemptLog(string method, int attempt, long elapsedMs, bool ok, string sMsg) => P(
        $"{method}: 第 {attempt} 次 ({elapsedMs} ms) -> {ok}，sMsg = \"{sMsg}\"",
        $"{method}: attempt {attempt} ({elapsedMs} ms) -> {ok}, sMsg = \"{sMsg}\"");

    public static string GetStatusTypeMismatch(string typeName) => P(
        $"M_GetStatus stLine 回傳型別是 {typeName}，預期 byte[]",
        $"M_GetStatus stLine returned type {typeName}, expected byte[]");

    public static string LineStatusLength(int length, int expected) => P(
        $"ST_LineStatus 長度 {length}，預期 {expected}",
        $"ST_LineStatus length {length}, expected {expected}");

    public static string NotDone => P("未完成", "not done");

    public static string InitTraceText(string find, string powerOn, string group) => P(
        $"FindMover1 {find}；PowerOn {powerOn}；EnableGroup {group} (PLC 時間)",
        $"FindMover1 {find}; PowerOn {powerOn}; EnableGroup {group} (PLC time)");
}
