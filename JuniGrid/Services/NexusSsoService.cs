using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Nexus Mods 官方 SSO 登录（wss://sso.nexusmods.com，protocol 2）。
/// 流程：连接 WebSocket → 发 {id:uuid} → 系统浏览器打开授权页 →
/// 收到 connection_token 回发 → 收到 api_key 完成。
/// v0.68.4：按官方协议补齐「每 30 秒一次 WebSocket ping 保活」——
/// 官方要求从连接建立到关闭期间持续 ping，否则授权页停留稍久
/// 服务端会判定闲置并断开（表现为用户授权完成却登录失败）。
/// .NET 侧无需手写 ping 循环：KeepAliveInterval 会让底层自动发 ping 帧。
/// </summary>
public class NexusSsoService
{
    public string? LastError { get; private set; }

    public async Task<string?> LoginAsync(string applicationSlug, CancellationToken ct = default)
    {
        LastError = null;
        var uuid = Guid.NewGuid().ToString();
        try
        {
            using var ws = new ClientWebSocket();
            // v0.68.4：官方 SSO 协议硬性要求「每 30 秒一次 ping」保活。
            // KeepAliveInterval 由 .NET 底层按间隔自动发送 WebSocket ping 帧，
            // 等效于官方 Node 示例里的 ws.ping() 定时器（.NET 6+ 生效）。
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await ws.ConnectAsync(new Uri("wss://sso.nexusmods.com"), ct);

            var hello = JsonSerializer.Serialize(new { id = uuid, token = (string?)null, protocol = 2 });
            await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);

            AppLog.Warn("NSSO", "SSO 连接已建立（30s 心跳保活），等待用户授权: " + uuid);   // v0.69.0：AppLog 只有 Warn/Error，无 Info
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://www.nexusmods.com/sso?id={uuid}&application={applicationSlug}")
            { UseShellExecute = true });

            var buffer = new byte[16384];
            var sb = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;   // 分片消息：继续拼接直到完整
                var msg = sb.ToString(); sb.Clear();

            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var ok) && ok.GetBoolean()
                    && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("api_key", out var key))
                        return key.GetString();
                    if (data.TryGetProperty("connection_token", out var token))
                    {
                        var auth = JsonSerializer.Serialize(new { id = uuid, token = token.GetString(), protocol = 2 });
                        await ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, ct);
                    }
                }
                else if (root.TryGetProperty("error", out var err))
                {
                    LastError = err.GetString();
                    AppLog.Warn("NSSO", "授权页返回错误: " + err.GetString());
                }
            }
            catch (JsonException)
            {
                // 非 JSON（可能是心跳等）忽略，继续读。但若每次都失败太吵，仅记录一次
                AppLog.Warn("NSSO", "收到无法解析的消息: " + msg[..Math.Min(msg.Length, 120)]);
            }
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        return null;
    }
}
