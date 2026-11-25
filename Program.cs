using System.Text;
using MeshChat.Network;
using MeshChat.Protocol;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

string nodeId = GetArg(args, "--id") ?? $"Node-{Random.Shared.Next(100, 999)}";
int tcpPort = int.TryParse(GetArg(args, "--port"), out int p) ? p : 0;
int discoveryPort = int.TryParse(GetArg(args, "--discovery-port"), out int dp) ? dp : 50500;
string secret = GetArg(args, "--key") ?? "agatu-mesh-secret-2026";
int ttlSeconds = int.TryParse(GetArg(args, "--ttl"), out int t) ? t : 10;
int webPort = int.TryParse(GetArg(args, "--web-port"), out int wp) ? wp : 0;
var allowPeers = GetArg(args, "--allow-peers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

await using var node = new MeshNode(new MeshNodeOptions
{
    NodeId = nodeId,
    TcpPort = tcpPort,
    DiscoveryPort = discoveryPort,
    SharedSecret = secret,
    PeerTtl = TimeSpan.FromSeconds(ttlSeconds),
    AllowedPeers = allowPeers
});

node.OnLog += line => Console.WriteLine(line);
await node.StartAsync();

WebApplication? webApp = null;
if (webPort > 0)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    webApp = builder.Build();
    webApp.Urls.Add($"http://0.0.0.0:{webPort}");

    webApp.MapGet("/", () => Results.Content(BuildWebDashboardHtml(node.NodeId, node.ActualTcpPort), "text/html; charset=utf-8"));
    webApp.MapGet("/api/state", () => Results.Ok(new
    {
        node.NodeId,
        node.ActualTcpPort,
        Peers = node.Peers.GetActivePeers(),
        Inbox = node.GetInbox(),
        Logs = node.GetLogs().TakeLast(50)
    }));
    webApp.MapPost("/api/send", async (SendMessageRequest req) =>
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest();
        var id = string.IsNullOrWhiteSpace(req.To) || req.To == "*"
            ? await node.BroadcastAsync(req.Text)
            : await node.SendDirectAsync(req.To, req.Text);
        return Results.Ok(new { msgId = id });
    });

    await webApp.StartAsync();
    Console.WriteLine($"[WEB] Веб-интерфейс топологии доступен: http://localhost:{webPort}");
}

PrintHelp();

while (true)
{
    var line = Console.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (string.IsNullOrEmpty(line)) continue;

    if (line.Equals("/quit", StringComparison.OrdinalIgnoreCase) || line.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (line.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        PrintHelp();
        continue;
    }

    if (line.Equals("/peers", StringComparison.OrdinalIgnoreCase))
    {
        var peers = node.Peers.GetActivePeers();
        Console.WriteLine($"--- Активные соседи ({peers.Count}) ---");
        foreach (var peer in peers)
            Console.WriteLine($"  {peer.NodeId,-14} {peer.Address}:{peer.TcpPort} (seen {(DateTime.UtcNow - peer.LastSeenUtc).TotalSeconds:0.0}s ago)");
        continue;
    }

    if (line.Equals("/history", StringComparison.OrdinalIgnoreCase))
    {
        var msgs = node.GetInbox();
        Console.WriteLine($"--- Входящие сообщения ({msgs.Count}) ---");
        foreach (var m in msgs)
            Console.WriteLine($"  [{m.ReceivedAtUtc:HH:mm:ss}] {m.From} -> {m.To} (путь: {string.Join("->", m.Path)}): {m.Text}");
        continue;
    }

    if (line.StartsWith("/send ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            Console.WriteLine("Использование: /send <id> <text>");
            continue;
        }
        await node.SendDirectAsync(parts[1], parts[2]);
        Console.WriteLine($"[MSG->] Отправлено узлу '{parts[1]}'");
        continue;
    }

    if (line.StartsWith("/broadcast ", StringComparison.OrdinalIgnoreCase))
    {
        var text = line[11..].Trim();
        if (string.IsNullOrEmpty(text)) continue;
        await node.BroadcastAsync(text);
        Console.WriteLine("[MSG->] Широковещательная рассылка отправлена");
        continue;
    }

    Console.WriteLine("Неизвестная команда. Введите /help.");
}

if (webApp is not null)
    await webApp.StopAsync();

static string? GetArg(string[] args, string key)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static void PrintHelp()
{
    Console.WriteLine("Команды: /peers | /send <id> <text> | /broadcast <text> | /history | /help | /quit");
}

static string BuildWebDashboardHtml(string nodeId, int tcpPort) => $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="UTF-8"><title>MeshNode: {{nodeId}}</title>
<style>
body { font-family: 'Segoe UI', sans-serif; background: #0f172a; color: #f1f5f9; margin: 20px; }
.card { background: #1e293b; border: 1px solid #334155; border-radius: 10px; padding: 14px; margin-bottom: 14px; }
h1, h2 { margin: 0 0 10px; color: #38bdf8; }
input, button { padding: 7px 10px; border-radius: 6px; border: 1px solid #475569; background: #0b1220; color: #f1f5f9; }
button { background: #0284c7; cursor: pointer; }
pre { background: #0b1220; padding: 10px; border-radius: 6px; max-height: 220px; overflow: auto; font-size: 12px; }
</style>
</head>
<body>
<h1>Mesh Node: {{nodeId}} (TCP :{{tcpPort}})</h1>
<div class="card">
  <h2>Отправить сообщение</h2>
  <input id="to" placeholder="ID узла или *" value="*">
  <input id="txt" placeholder="Текст сообщения" style="width:320px">
  <button onclick="sendMsg()">Отправить</button>
</div>
<div class="card"><h2>Таблица соседей (Peer Table)</h2><pre id="peers"></pre></div>
<div class="card"><h2>История сообщений</h2><pre id="inbox"></pre></div>
<div class="card"><h2>Журнал узла</h2><pre id="logs"></pre></div>
<script>
async function refresh() {
  const s = await (await fetch('/api/state')).json();
  document.getElementById('peers').textContent = s.peers.map(p => `${p.nodeId} (${p.address}:${p.tcpPort})`).join('\n') || 'Нет соседей';
  document.getElementById('inbox').textContent = s.inbox.map(m => `[${m.from} -> ${m.to}] (${m.path.join(' -> ')}) ${m.text}`).join('\n') || 'Пусто';
  document.getElementById('logs').textContent = s.logs.join('\n');
}
async function sendMsg() {
  await fetch('/api/send', { method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ to: document.getElementById('to').value, text: document.getElementById('txt').value }) });
  document.getElementById('txt').value = '';
  refresh();
}
refresh(); setInterval(refresh, 1500);
</script>
</body></html>
""";

public sealed record SendMessageRequest(string? To, string Text);
