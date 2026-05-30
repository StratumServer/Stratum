using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;

namespace Vintagestory.Server.Nimbus;

/// <summary>
/// Signed HTTP client for the Nimbus registry. Wraps a single HttpClient + per-request HMAC
/// signing. All methods return null/false on error and log via <see cref="StratumRuntime.LogWarning"/>
/// The backend never crashes because the registry is unreachable.
/// </summary>
internal sealed class NimbusRegistryClient
{
    private readonly HttpClient _http;
    private readonly NimbusBackendConfig _cfg;

    public NimbusRegistryClient(NimbusBackendConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient
        {
            BaseAddress = new Uri(cfg.RegistryUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(cfg.RegistryHttpTimeoutSeconds)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nimbus-Backend/{NimbusProtocol.NimbusVersion}");
    }

    public async Task<BackendHeartbeatResponse> HeartbeatAsync(BackendHeartbeat hb, CancellationToken ct)
    {
        var resp = await PostJsonAsync<BackendHeartbeatResponse>("api/heartbeat", hb, ct);
        return resp ?? new BackendHeartbeatResponse { Ok = false, Message = "no response" };
    }

    public async Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct)
        => await GetJsonAsync<NetworkSnapshot>("api/servers", ct);

    public async Task<ReservationResponse?> MintReservationAsync(ReservationRequest req, CancellationToken ct)
        => await PostJsonAsync<ReservationResponse>("api/reservations", req, ct);

    public async Task<ReservationResponse?> ConsumeReservationAsync(string id, string targetServerId, CancellationToken ct)
        => await PostJsonAsync<ReservationResponse>($"api/reservations/{Uri.EscapeDataString(id)}/consume?target={Uri.EscapeDataString(targetServerId)}", new { }, ct);

    public async Task<ReservationResponse?> ConsumeReservationByUidAsync(string playerUid, string targetServerId, CancellationToken ct)
        => await PostJsonAsync<ReservationResponse>($"api/reservations/consume-by-uid?uid={Uri.EscapeDataString(playerUid)}&target={Uri.EscapeDataString(targetServerId)}", new { }, ct);

    public async Task<TransferIntentResponse?> PostTransferIntentAsync(TransferIntentRequest req, CancellationToken ct)
        => await PostJsonAsync<TransferIntentResponse>("api/transfer-intents", req, ct);

    private async Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        try
        {
            byte[] bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body);
            using var msg = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent(bodyBytes)
            };
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            ApplySignedHeaders(msg, "POST", "/" + path, bodyBytes);
            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                StratumRuntime.LogWarning($"Nimbus {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StratumRuntime.LogWarning($"Nimbus POST {path} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, path);
            ApplySignedHeaders(msg, "GET", "/" + path, Array.Empty<byte>());
            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                StratumRuntime.LogWarning($"Nimbus GET {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StratumRuntime.LogWarning($"Nimbus GET {path} failed: {ex.Message}");
            return null;
        }
    }

    private void ApplySignedHeaders(HttpRequestMessage msg, string method, string canonicalPath, byte[] body)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = HmacSigner.NewNonce();
        // Strip query string from canonical path (path-only signing).
        int q = canonicalPath.IndexOf('?');
        string pathForSig = q >= 0 ? canonicalPath.Substring(0, q) : canonicalPath;
        string canonical = HmacSigner.CanonicalString(method, pathForSig, NimbusProtocol.ProtocolVersion, ts, nonce, body);
        string sig = HmacSigner.Sign(_cfg.SharedSecret, canonical);
        msg.Headers.Add(NimbusProtocol.SignatureHeader, sig);
        msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        msg.Headers.Add(NimbusProtocol.NonceHeader, nonce);
        msg.Headers.Add(NimbusProtocol.ProtocolHeader, NimbusProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
