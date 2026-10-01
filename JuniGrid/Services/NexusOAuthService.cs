using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Nexus Mods OAuth2 登录 —— 授权码流程 + PKCE（官方指南 https://modding.wiki/en/api/oauth2-guide）。
/// 流程：系统浏览器打开 users.nexusmods.com/oauth/authorize → 用户登录并授权 →
/// Nexus 重定向到已注册的 loopback 回调 → 本地临时监听器接住唯一一次回调后关闭 →
/// 用 code 到 /oauth/token 换 access/refresh token → 之后 API v1 调用带 "Authorization: Bearer …"。
/// 这是应用内唯一的登录路径：不使用、也不索要个人 API Key（Nexus AUP 对分发应用的要求）。
/// 桌面端是公开客户端，证明身份由 PKCE(code_verifier) 完成，Client Secret 不内嵌、不使用。
/// token 仅持久化在本机配置文件。
/// </summary>
public sealed class NexusOAuthService
{
    /// <summary>OAuth2 client id —— Nexus 验证应用后下发（应用名 JuniGrid）。</summary>
    public const string ClientId = "junigrid";

    private const string AuthorizeUrl = "https://users.nexusmods.com/oauth/authorize";
    private const string TokenUrl = "https://users.nexusmods.com/oauth/token";
    private const int CallbackPort = 49162;   // 已注册的回调端口
    public const string CallbackUrl = "http://localhost:49162/auth/callback";
    private const int LoginTimeoutMinutes = 5;

    public string? LastError { get; private set; }

    private readonly ConfigService _cfg;
    private static readonly HttpClient Http = CreateClient();

    public NexusOAuthService(ConfigService cfg) => _cfg = cfg;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"JuniGrid/{AppInfo.Version}");
        h.Timeout = TimeSpan.FromSeconds(20);
        return h;
    }

    /// <summary>启动恢复：载入持久化 token、过期则刷新、并把 access token 挂到 NexusService。
    /// 不抛异常 —— 恢复失败只表示未登录。</summary>
    public void RestoreSession()
    {
        var c = _cfg.Current;
        if (string.IsNullOrEmpty(c.NexusRefreshToken)) return;
        NexusService.BearerToken = c.NexusAccessToken;
        if (TokenExpired(c)) _ = RefreshAsync();   // 后台刷新；期间请求可能 401 一次并优雅降级
    }

    private static bool TokenExpired(JuniGridConfig c) =>
        c.NexusTokenExpiresAt is null || c.NexusTokenExpiresAt <= DateTime.Now.AddMinutes(5);

    /// <summary>存储的 token 是否（即将）过期、需要刷新。</summary>
    public bool NeedsRefresh => TokenExpired(_cfg.Current);

    /// <summary>主动刷新过期的 access token。返回 false 表示会话已失效
    /// （刷新返回 4xx = 用户撤销了授权 —— 按指南视为已登出）。</summary>
    public async Task<bool> RefreshAsync()
    {
        LastError = null;
        var c = _cfg.Current;
        var rt = c.NexusRefreshToken;
        if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(rt)) return false;
        try
        {
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = rt,
                ["client_id"] = ClientId,
            });
            using var res = await Http.PostAsync(TokenUrl, body);
            if ((int)res.StatusCode is >= 400 and < 500)
            {
                // 4xx = 授权已没了（用户撤销 / refresh token 过期）—— 视为已登出。
                // 5xx / 网络失败保留会话，下次重试可能成功。
                Logout();
                return false;
            }
            if (!res.IsSuccessStatusCode) return false;
            SaveTokens(await res.Content.ReadAsStringAsync());
            return NexusService.BearerToken is not null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>跑完整的浏览器授权。返回登录用户，失败/取消返回 null（LastError 带原因）。</summary>
    public async Task<NexusUser?> LoginAsync(CancellationToken ct = default)
    {
        LastError = null;
        if (string.IsNullOrEmpty(ClientId))
        {
            LastError = LocService.Tr("OAuth2 尚未在 Nexus 完成注册 —— 下发 client id 后登录会自动开放");
            return null;
        }

        // PKCE (S256)：随机 verifier（≥43 字符），challenge = BASE64URL(SHA256(verifier))
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            LastError = LocService.Tr("无法打开本地回调端口 ") + CallbackPort + ": " + ex.Message;
            return null;
        }

        try
        {
            var auth = AuthorizeUrl + "?client_id=" + Uri.EscapeDataString(ClientId)
                     + "&redirect_uri=" + Uri.EscapeDataString(CallbackUrl)
                     + "&response_type=code&scope="   // 官方指南：scope 留空（用户信息走 validate.json，不读 JWT）
                     + "&state=" + Uri.EscapeDataString(state)
                     + "&code_challenge=" + Uri.EscapeDataString(challenge)
                     + "&code_challenge_method=S256";
            AppLog.Warn("NOAUTH", "打开浏览器进行 OAuth2 授权");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(auth) { UseShellExecute = true });

            // 接住唯一一次重定向：解析 GET /auth/callback?code=…&state=…，回应一次后关闭。
            var (code, gotState, error) = await CaptureCallbackAsync(listener, ct);
            if (!string.IsNullOrEmpty(error)) { LastError = error; return null; }
            if (string.IsNullOrEmpty(code)) { LastError = LocService.Tr("授权未完成"); return null; }
            if (gotState != state) { LastError = LocService.Tr("state 不匹配 —— 回调并非来自本次登录"); return null; }

            using var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId,
                ["redirect_uri"] = CallbackUrl,
                ["code_verifier"] = verifier,
            });
            using var tokenRes = await Http.PostAsync(TokenUrl, tokenBody, ct);
            if (!tokenRes.IsSuccessStatusCode)
            {
                LastError = LocService.Tr("token 交换失败: HTTP ") + (int)tokenRes.StatusCode;
                return null;
            }
            SaveTokens(await tokenRes.Content.ReadAsStringAsync());
            if (NexusService.BearerToken is null) { LastError = LocService.Tr("token 响应里没有 access_token"); return null; }

            var user = await _nexus.ValidateAsync();
            if (user is null) { LastError = LocService.Tr("已登录，但拉取账号信息失败"); return null; }

            var c = _cfg.Current;
            c.NexusUserName = user.Name ?? "";
            c.NexusUserEmail = user.Email ?? "";
            c.NexusProfileUrl = user.ProfileUrl ?? "";
            c.NexusIsPremium = user.IsPremium;
            _cfg.Save(c);
            return user;
        }
        catch (OperationCanceledException)
        {
            LastError = LocService.Tr("已取消");
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private static readonly NexusService _nexus = new();   // 无状态 API 门面 —— 仅用于登录后那次 validate 调用

    /// <summary>登出：清空持久化 token 与已挂的 Bearer token。
    /// （服务端撤销入口在 users.nexusmods.com/oauth/authorized_applications。）</summary>
    public void Logout()
    {
        NexusService.BearerToken = null;
        var c = _cfg.Current;
        c.NexusAccessToken = "";
        c.NexusRefreshToken = "";
        c.NexusTokenExpiresAt = null;
        c.NexusUserName = "";
        c.NexusUserEmail = "";
        c.NexusProfileUrl = "";
        c.NexusIsPremium = false;
        c.NexusAvatarDataUri = "";
        _cfg.Save(c);
    }

    private void SaveTokens(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var refresh = r.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : null;
        var expiresIn = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetDouble() : 0;
        if (string.IsNullOrEmpty(access)) return;
        var c = _cfg.Current;
        c.NexusAccessToken = access;
        if (!string.IsNullOrEmpty(refresh)) c.NexusRefreshToken = refresh;   // 重新授权可能省略 refresh —— 那时保留旧的
        c.NexusTokenExpiresAt = DateTime.Now.AddSeconds(expiresIn > 0 ? expiresIn - 60 : 3600);
        _cfg.Save(c);
        NexusService.BearerToken = access;
    }

    /// <summary>在 loopback 监听器上等待并返回 OAuth 回调参数。
    /// 用裸 TcpListener（无需 URL ACL / 管理员权限）。浏览器会额外开若干「预连接」空 socket
    /// （不发任何请求）以及 favicon 等杂项请求，若只 Accept 一次很可能先接到空连接、
    /// 在 ReadAsync 上一直卡到超时，而真正带 code 的回调请求却被晾在 backlog 里没人读。
    /// 因此这里循环接受多条连接、每条丢到独立任务里处理，只认真正命中回调路径的那条，
    /// 其余（空连接 / 杂项请求）回应一句后关掉、不影响等待。</summary>
    private static async Task<(string? Code, string? State, string? Error)> CaptureCallbackAsync(
        TcpListener listener, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(LoginTimeoutMinutes));
        var tcs = new TaskCompletionSource<(string?, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (timeout.Token.Register(() => tcs.TrySetCanceled()))
        {
            _ = Task.Run(async () =>
            {
                while (!timeout.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(timeout.Token); }
                    catch { return; }   // 监听器已停 / 超时 / 取消 —— 退出接受循环
                    _ = ServeOneAsync(client, tcs, timeout.Token);
                }
            });

            try
            {
                return await tcs.Task;   // 真回调到达即返回；超时/中止则抛 OperationCanceledException
            }
            finally
            {
                timeout.Cancel();   // 停掉接受循环与仍在挂着的空连接读取
                try { listener.Stop(); } catch { }
            }
        }
    }

    private const string CallbackPath = "/auth/callback";

    /// <summary>处理单条连接：读到完整请求头后，命中回调路径才解析 code/state/error 并回填 tcs，
    /// 其余请求（预连接读到 0 字节、favicon 等）回一句友好提示后关掉，绝不阻塞其它连接。</summary>
    private static async Task ServeOneAsync(
        TcpClient client, TaskCompletionSource<(string?, string?, string?)> tcs, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                var sb = new StringBuilder();
                while (!sb.ToString().Contains("\r\n\r\n"))
                {
                    int n;
                    try { n = await stream.ReadAsync(buffer, ct); }
                    catch { return; }
                    if (n == 0) return;   // 对端没发请求就关了（预连接）—— 忽略
                    sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    if (sb.Length > 65536) return;   // 防御：回调请求很小
                }

                var head = sb.ToString();
                var line = head.Split('\n')[0];   // 形如 "GET /auth/callback?code=…&state=… HTTP/1.1"
                var seg = line.Split(' ');
                var target = seg.Length >= 2 && seg[1].StartsWith('/') ? seg[1] : "/";
                var path = target.Split('?')[0];

                // 只有注册的回调路径才携带 OAuth 结果；favicon 等杂项请求回一句后忽略
                if (!path.StartsWith(CallbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(stream, "JuniGrid", ct);
                    return;
                }

                var args = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + target).Query);

                var err = args["error"];
                if (!string.IsNullOrEmpty(err))
                {
                    await RespondAsync(stream, "授权失败", ct);
                    tcs.TrySetResult((null, null, "授权页返回: " + err));
                    return;
                }

                await RespondAsync(stream, "授权完成", ct);
                tcs.TrySetResult((args["code"], args["state"], null));
            }
            catch { /* 单条连接失败不致命：接受循环仍在等真正的回调 */ }
        }
    }

    /// <summary>回一个最小合法响应，让浏览器标签页显示友好的「可关闭」提示。</summary>
    private static async Task RespondAsync(NetworkStream stream, string title, CancellationToken ct)
    {
        var page = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>JuniGrid</title></head>" +
                   "<body style=\"font-family:system-ui;background:#1b1d22;color:#e7e9ee;display:flex;" +
                   "align-items:center;justify-content:center;height:100vh;margin:0\">" +
                   "<div style=\"text-align:center\"><div style=\"font-size:22px;font-weight:600\">" +
                   title + "</div><div style=\"opacity:.6\">可以关闭此窗口并回到 JuniGrid。</div></div></body></html>";
        var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: "
                 + Encoding.UTF8.GetByteCount(page) + "\r\n\r\n" + page;
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct); } catch { }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
