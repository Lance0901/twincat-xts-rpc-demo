using TwinCAT.Ads;
using XtsRpcClient;
using XtsWeb;

// 命令列：與 csharp/ console 相同的 --netid / --port / --mover-path / --station-path /
// --mover-via-control / --station-via-control / --poll-ms / --retry-ms / --retry-timeout-ms；--no-detail-status 關掉 mover / 站 M_GetStatus 輪詢；
// 其餘 (例如 --urls) 交給 ASP.NET Core。
var (optArgs, webArgs, detail) = SplitArgs(args);
Options opt;
try
{
    opt = Options.Parse(optArgs);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var builder = WebApplication.CreateBuilder(webArgs);
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://localhost:5080");      // 只綁 localhost：這頁能對 PLC 下命令
}
builder.Services.AddSingleton(opt);
builder.Services.AddSingleton(new WebOptions(detail));
builder.Services.AddSingleton<PlcConnection>();
builder.Services.AddSingleton<StatusPoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusPoller>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- 狀態 ----
app.MapGet("/api/info", (Options o) => new
{
    mode = "live", netId = o.NetId ?? "local", port = o.Port, controlPath = o.ControlPath,
    moverPath = o.MoverPathTemplate, stationPath = o.StationPathTemplate,
    moverViaControl = o.MoverViaControl, stationViaControl = o.StationViaControl, pollMs = o.PollMs,
});
app.MapGet("/api/status", (StatusPoller p) => Results.Text(p.Latest.Json, "application/json"));
app.MapGet("/api/events", async (HttpContext ctx, StatusPoller p, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    long last = -1;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var (ver, json) = p.Latest;
            if (ver != last)
            {
                last = ver;
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            await Task.Delay(100, ct);
        }
    }
    catch (OperationCanceledException)
    {
    }
});

// ---- 命令：每個按鈕一個 endpoint，回 { accepted, sMsg } ----
IResult Cmd(PlcConnection plc, Func<XtsRpc, RpcResult> call)
{
    try
    {
        var r = plc.Run(call);
        string msg = r.RetryNote.Length > 0 ? $"{r.Msg(0)} [{r.RetryNote}]" : r.Msg(0);
        return Results.Ok(new CmdResult(r.Ok, msg, r.Attempts));
    }
    catch (Exception ex)
    {
        var hint = ex is AdsErrorException ? " (symbol not found? try --mover-path / --mover-via-control / --station-via-control)" : "";
        return Results.Json(new CmdResult(false, $"RPC failed: {ex.Message}{hint}", 0), statusCode: 502);
    }
}

app.MapPost("/api/initial", (PlcConnection plc) => Cmd(plc, x => x.InitialXts()));
app.MapPost("/api/mode", (PlcConnection plc, ModeReq b) => Cmd(plc, x => x.SetMode((short)b.Mode)));
app.MapPost("/api/cycle/start", (PlcConnection plc, MoverReq b) => Cmd(plc, x => x.StartCycle(b.Mover)));
app.MapPost("/api/stop", (PlcConnection plc) => Cmd(plc, x => x.StopAll()));

app.MapPost("/api/mover/{n:int}/move-to", (PlcConnection plc, int n, MoveReq b) => Cmd(plc, x => x.MoverMoveTo(n, b.Position, b.Velocity)));
app.MapPost("/api/mover/{n:int}/stop", (PlcConnection plc, int n) => Cmd(plc, x => x.MoverStop(n)));
app.MapPost("/api/mover/{n:int}/reset", (PlcConnection plc, int n) => Cmd(plc, x => x.MoverReset(n)));
app.MapPost("/api/mover/{n:int}/send-to-station", (PlcConnection plc, int n, StationReq b) => Cmd(plc, x => x.SendToStation(n, b.Station)));

app.MapPost("/api/station/{n:int}/start", (PlcConnection plc, int n) => Cmd(plc, x => x.StationStart(n)));
app.MapPost("/api/station/{n:int}/release", (PlcConnection plc, int n) => Cmd(plc, x => x.StationRelease(n)));
app.MapPost("/api/station/{n:int}/reset", (PlcConnection plc, int n) => Cmd(plc, x => x.StationReset(n)));

app.Run();
return 0;

static (string[] Opt, string[] Web, bool Detail) SplitArgs(string[] all)
{
    string[] withValue = { "--netid", "--port", "--control-path", "--mover-path", "--station-path", "--init-timeout", "--step-timeout", "--poll-ms", "--retry-ms", "--retry-timeout-ms" };
    string[] flags = { "--mover-via-control", "--station-via-control" };
    var o = new List<string>();
    var w = new List<string>();
    bool detail = true;
    for (int i = 0; i < all.Length; i++)
    {
        if (withValue.Contains(all[i]) && i + 1 < all.Length) { o.Add(all[i]); o.Add(all[++i]); }
        else if (flags.Contains(all[i])) o.Add(all[i]);
        else if (all[i] == "--no-detail-status") detail = false;
        else w.Add(all[i]);
    }
    if (!o.Contains("--poll-ms")) { o.Add("--poll-ms"); o.Add("250"); }    // 網頁要比 console 更新得快
    return (o.ToArray(), w.ToArray(), detail);
}

internal sealed record ModeReq(int Mode);
internal sealed record MoverReq(int Mover);
internal sealed record StationReq(int Station);
internal sealed record MoveReq(double Position, double Velocity);
