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
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>MeshChat — {{nodeId}}</title>
<style>
:root {
  --bg: #f6f7f9;
  --surface: #ffffff;
  --sidebar: #f3f4f6;
  --border: #d8dee4;
  --text: #111827;
  --muted: #57606a;
  --primary: #1d4ed8;
  --mono: 'Cascadia Mono', 'Consolas', monospace;
}
* { box-sizing: border-box; }
::selection { background: #dbeafe; color: #1e3a8a; }
body {
  margin: 0;
  font-family: 'Segoe UI', system-ui, sans-serif;
  font-size: 13px;
  color: var(--text);
  background: var(--bg);
  height: 100vh;
  display: flex;
  flex-direction: column;
}
.topbar {
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  padding: 10px 16px;
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.topbar-title { display: flex; align-items: center; gap: 10px; font-weight: 600; font-size: 14px; }
.pill {
  font-family: var(--mono);
  font-size: 11.5px;
  padding: 2px 8px;
  border-radius: 4px;
  background: #f3f4f6;
  border: 1px solid var(--border);
  color: var(--muted);
}
.layout { flex: 1; display: grid; grid-template-columns: 260px 1fr; min-height: 0; }
.sidebar {
  background: var(--sidebar);
  border-right: 1px solid var(--border);
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  overflow-y: auto;
}
.section-label { font-size: 11.5px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: 0.03em; }
.peer-btn {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  text-align: left;
  padding: 7px 10px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 5px;
  cursor: pointer;
  font-size: 12.5px;
  color: var(--text);
}
.peer-btn:hover { background: #f9fafb; border-color: #9ca3af; }
.peer-btn.active { border-color: var(--primary); background: #eff6ff; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: #16a34a; display: inline-block; margin-right: 6px; }
.mono { font-family: var(--mono); font-size: 11.5px; color: var(--muted); }
.main { display: flex; flex-direction: column; background: var(--surface); min-height: 0; }
.messages { flex: 1; padding: 14px 18px; overflow-y: auto; display: flex; flex-direction: column; gap: 8px; }
.msg-row {
  padding: 8px 12px;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
  background: #fafafa;
}
.msg-meta { display: flex; justify-content: space-between; font-size: 11.5px; color: var(--muted); margin-bottom: 3px; }
.msg-route { font-family: var(--mono); }
.msg-body { font-size: 13.5px; color: var(--text); }
.empty-state { color: var(--muted); padding: 20px 0; }
.composer {
  display: flex;
  gap: 8px;
  padding: 10px 16px;
  border-top: 1px solid var(--border);
  background: var(--surface);
}
input {
  padding: 7px 10px;
  border: 1px solid var(--border);
  border-radius: 4px;
  font-size: 13px;
  font-family: inherit;
  color: var(--text);
}
input:focus-visible, button:focus-visible { outline: 2px solid var(--primary); outline-offset: 1px; }
.send-btn {
  padding: 7px 16px;
  background: var(--primary);
  color: #ffffff;
  border: 1px solid var(--primary);
  border-radius: 4px;
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
}
.send-btn:hover { background: #1e40af; }
.logs-strip {
  border-top: 1px solid var(--border);
  background: #f9fafb;
  padding: 8px 16px;
  height: 150px;
  overflow-y: auto;
}
.logs-strip pre { margin: 4px 0 0; font-family: var(--mono); font-size: 11.5px; color: #374151; white-space: pre-wrap; }
</style>
</head>
<body>
<header class="topbar">
  <div class="topbar-title">
    <span>MeshChat — {{nodeId}}</span>
    <span class="pill">TCP :{{tcpPort}}</span>
    <span class="pill">AES-256-GCM</span>
  </div>
  <span class="mono" id="peerCount">Соседей: 0</span>
</header>
<div class="layout">
  <aside class="sidebar">
    <div class="section-label">Соседи (Peer Table)</div>
    <button class="peer-btn" onclick="selectTarget('*')"><span>Все узлы (Broadcast)</span><span class="mono">*</span></button>
    <div id="peers" style="display:flex;flex-direction:column;gap:6px;"></div>
  </aside>
  <main class="main">
    <div class="messages" id="inbox"></div>
    <form class="composer" onsubmit="sendMsg(event)">
      <input id="to" placeholder="ID узла или *" value="*" style="width:140px">
      <input id="txt" placeholder="Сообщение..." style="flex:1" required>
      <button class="send-btn" type="submit">Отправить</button>
    </form>
    <div class="logs-strip">
      <div class="section-label">Журнал узла</div>
      <pre id="logs"></pre>
    </div>
  </main>
</div>
<script>
function selectTarget(id) {
  document.getElementById('to').value = id;
  document.getElementById('txt').focus();
}
function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
async function refresh() {
  const s = await (await fetch('/api/state')).json();
  const currentTo = document.getElementById('to').value;
  document.getElementById('peerCount').textContent = `Соседей: ${s.peers.length}`;
  document.getElementById('peers').innerHTML = s.peers.length
    ? s.peers.map(p => `<button class="peer-btn ${currentTo === p.nodeId ? 'active' : ''}" onclick="selectTarget('${escapeHtml(p.nodeId)}')"><span><span class="dot"></span>${escapeHtml(p.nodeId)}</span><span class="mono">${p.address}:${p.tcpPort}</span></button>`).join('')
    : '<div class="mono">Поиск соседей...</div>';

  const inbox = document.getElementById('inbox');
  inbox.innerHTML = s.inbox.length
    ? s.inbox.map(m => `<div class="msg-row"><div class="msg-meta"><strong>${escapeHtml(m.from)} → ${escapeHtml(m.to)}</strong><span class="msg-route">${m.path.map(escapeHtml).join(' → ')}</span></div><div class="msg-body">${escapeHtml(m.text)}</div></div>`).join('')
    : '<div class="empty-state">Входящих сообщений пока нет. Выберите соседа слева или отправьте broadcast (*).</div>';

  document.getElementById('logs').textContent = s.logs.join('\n');
}
async function sendMsg(e) {
  e.preventDefault();
  const txt = document.getElementById('txt');
  if (!txt.value.trim()) return;
  await fetch('/api/send', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ to: document.getElementById('to').value, text: txt.value })
  });
  txt.value = '';
  refresh();
}
refresh();
setInterval(refresh, 1500);
</script>
</body>
</html>
""";

public sealed record SendMessageRequest(string? To, string Text);
