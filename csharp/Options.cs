using TwinCAT.Ads;

namespace XtsRpcClient;

/// <summary>
/// 命令列參數。預設連本機 (AmsNetId.Local) 的 PLC port 851。
/// 範例：dotnet run -- --netid 5.1.2.3.1.1 --mover-path "MAIN.refMover{0}"
/// </summary>
internal sealed class Options
{
    /// <summary>目標 AmsNetId；null = AmsNetId.Local。</summary>
    public string? NetId { get; private set; }

    /// <summary>PLC ADS port。</summary>
    public int Port { get; private set; } = 851;

    /// <summary>總控 instance 路徑。</summary>
    public string ControlPath { get; private set; } = "MAIN.fbXts";

    /// <summary>
    /// 單一 mover 的 instance 路徑，{0} 換成 mover 編號。
    /// 主路徑：陣列元素 MAIN.fbXts.aMover[{0}]；備案：MAIN.refMover{0}；
    /// 兩個都不行時加 --mover-via-control，改呼叫總控的 M_MoveTo(nMover, ...)。
    /// </summary>
    public string MoverPathTemplate { get; private set; } = "MAIN.fbXts.aMover[{0}]";

    /// <summary>單站 instance 路徑，{0} 換成 fbLoad / fbPress / fbUnload。</summary>
    public string StationPathTemplate { get; private set; } = "MAIN.fbXts.{0}";

    /// <summary>mover 命令改走總控 M_MoveTo (陣列元素與 REFERENCE 路徑都不能用時)。</summary>
    public bool MoverViaControl { get; private set; }

    /// <summary>站命令改走總控 M_StartStation / M_ReleaseStation (子類別繼承的 RPC 不能用時)。</summary>
    public bool StationViaControl { get; private set; }

    /// <summary>等 M_InitialXTS 完成的上限 (秒，牆鐘)。</summary>
    public int InitTimeoutSec { get; private set; } = 900;

    /// <summary>等單一動作 (移動、站、整輪循環) 的上限 (秒，牆鐘)。</summary>
    public int StepTimeoutSec { get; private set; } = 900;

    /// <summary>輪詢間隔 (ms)。</summary>
    public int PollMs { get; private set; } = 500;

    public AmsNetId AmsNetId => NetId is null ? AmsNetId.Local : new AmsNetId(NetId);

    public string MoverPath(int mover) => string.Format(MoverPathTemplate, mover);

    public string StationPath(int station) => string.Format(StationPathTemplate, station switch
    {
        1 => "fbLoad",
        2 => "fbPress",
        3 => "fbUnload",
        _ => throw new ArgumentOutOfRangeException(nameof(station)),
    });

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} 缺少值");
            switch (args[i])
            {
                case "--netid": o.NetId = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--control-path": o.ControlPath = Next(); break;
                case "--mover-path": o.MoverPathTemplate = Next(); break;
                case "--station-path": o.StationPathTemplate = Next(); break;
                case "--mover-via-control": o.MoverViaControl = true; break;
                case "--station-via-control": o.StationViaControl = true; break;
                case "--init-timeout": o.InitTimeoutSec = int.Parse(Next()); break;
                case "--step-timeout": o.StepTimeoutSec = int.Parse(Next()); break;
                case "--poll-ms": o.PollMs = int.Parse(Next()); break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"未知參數 {args[i]}，用 --help 看說明");
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            XtsRpcClient — 用 InvokeRpcMethod 控制 XTS 模擬 (TwinCAT PLC {attribute 'TcRpcEnable'} method)

              --netid <AmsNetId>         預設 AmsNetId.Local (本機 UmRT)
              --port <n>                 預設 851
              --control-path <path>      預設 MAIN.fbXts
              --mover-path <template>    預設 MAIN.fbXts.aMover[{0}]；備案 MAIN.refMover{0}
              --station-path <template>  預設 MAIN.fbXts.{0} ({0} = fbLoad / fbPress / fbUnload)
              --mover-via-control        mover 命令改走總控 M_MoveTo
              --station-via-control      站命令改走總控 M_StartStation / M_ReleaseStation
              --init-timeout <s>         預設 900 (UmRT 比實際慢，放寬)
              --step-timeout <s>         預設 900
              --poll-ms <ms>             預設 500
            """);
    }
}
