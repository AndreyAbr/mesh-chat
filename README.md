# MeshChat — Децентрализованная Mesh-сеть обмена сообщениями

Консольное + веб приложение на **C# (.NET 10)**, реализующее mesh-сеть с автоматическим обнаружением узлов по UDP, обменом сообщениями по TCP с шифрованием **AES-256-GCM**, транзитной маршрутизацией (flooding с TTL и дедупликацией) и подтверждением доставки (**ACK**).

## Архитектура протокола

1. **Peer Discovery (UDP `50500`)**:
   - Периодический JSON-маячок (`239.255.42.99` + `255.255.255.255`):
     `{ "nodeId": "NodeA", "tcpPort": 5001, "protocolVersion": 1 }`
   - Таблица соседей `PeerTable` с автоматическим удалением узлов по таймауту `TTL`.

2. **Обмен сообщениями (TCP + AES-256-GCM)**:
   - Фрейм на проводе: `[4-byte big-endian length][12-byte nonce][16-byte GCM tag][ciphertext]`.
   - Полезная нагрузка (JSON): `{ "msgId": "...", "type": "CHAT|ACK", "from": "...", "to": "...", "text": "...", "ts": ..., "ttl": 5, "path": ["NodeA", "NodeB"] }`.
   - В логах транзитной пересылки открытый текст сообщений не выводится.

3. **Дополнительные возможности**:
   - Подтверждение доставки (`ACK`) от получателя к отправителю.
   - Встроенный веб-интерфейс топологии и истории сообщений (флаг `--web-port`).
   - Ограничение видимости соседей (`--allow-peers`) для демонстрации многошаговой маршрутизации (`A -> B -> C`) на одном ПК.

## Запуск 3 узлов локально (пример)

Терминал 1:
```powershell
dotnet run --project MeshChat.csproj -- --id NodeA --port 5001 --allow-peers NodeB --web-port 8081
```
Терминал 2:
```powershell
dotnet run --project MeshChat.csproj -- --id NodeB --port 5002 --allow-peers NodeA,NodeC --web-port 8082
```
Терминал 3:
```powershell
dotnet run --project MeshChat.csproj -- --id NodeC --port 5003 --allow-peers NodeB --web-port 8083
```

Команды в консоли: `/peers`, `/send NodeC Привет!`, `/broadcast Всем привет`, `/history`, `/quit`.

## Тестирование

```powershell
dotnet build MeshChat.csproj
dotnet test MeshChat.Tests\MeshChat.Tests.csproj
```
