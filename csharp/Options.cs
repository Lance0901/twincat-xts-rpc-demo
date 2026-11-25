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

    /// <summary>PLC 回「上一個命令還沒被收下」時的重送間隔 (ms)。</summary>
    public int RetryMs { get; private set; } = 200;

    /// <summary>重送的總時間上限 (ms，牆鐘)；到了仍沒被收下就當失敗回報。0 = 不重試。</summary>
    public int RetryTimeoutMs { get; private set; } = 5000;

    /// <summary>console 輸出語言 (--lang zh|en)；Parse 會同步設定 Text.Lang。</summary>
    public Lang Lang { get; private set; } = Lang.Zh;

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
        // 先抓 --lang，讓後面的錯誤訊息與 --help 不管參數順序都用指定語言
        int langAt = Array.LastIndexOf(args, "--lang");
        if (langAt >= 0 && langAt + 1 < args.Length && TryParseLang(args[langAt + 1], out var early)) Text.Lang = early;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(Text.MissingValue(args[i]));
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
                case "--retry-ms": o.RetryMs = int.Parse(Next()); break;
                case "--retry-timeout-ms": o.RetryTimeoutMs = int.Parse(Next()); break;
                case "--lang":
                    string v = Next();
                    o.Lang = TryParseLang(v, out var lang) ? lang : throw new ArgumentException(Text.BadLang(v));
                    Text.Lang = lang;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException(Text.UnknownArg(args[i]));
            }
        }
        if (o.RetryMs <= 0) throw new ArgumentException(Text.RetryMsPositive);
        if (o.RetryTimeoutMs < 0) throw new ArgumentException(Text.RetryTimeoutNonNegative);
        return o;
    }

    private static bool TryParseLang(string value, out Lang lang)
    {
        switch (value.ToLowerInvariant())
        {
            case "zh": lang = Lang.Zh; return true;
            case "en": lang = Lang.En; return true;
            default: lang = Lang.Zh; return false;
        }
    }

    private static void PrintHelp() => Console.WriteLine(Text.Help);
}
