using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Vintagestory.Server.Nimbus;

/// <summary>
/// Optional Nimbus network integration. Default <see cref="Enabled"/> is false. Stratum runs
/// stand-alone with zero registry dependency. Enable per-backend in stratum.json under "Network".
/// </summary>
internal sealed class NimbusBackendConfig
{
    /// <summary>Master switch. When false, every other Nimbus code path short-circuits to a no-op.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Stable backend id used by the registry. Should be unique across the network.</summary>
    public string ServerId { get; set; } = "";

    /// <summary>Human-friendly display name shown in /nimbus servers and hub UIs.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Public DNS/IP the vanilla client should use to reach this backend directly.</summary>
    public string PublicHost { get; set; } = "";

    /// <summary>Public TCP port. Default 42420 matches vanilla VS.</summary>
    public int PublicPort { get; set; } = 42420;

    /// <summary>Free-form routing tags. Examples: "civ", "event", "staff", "permadeath".</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Nimbus.Registry base URL, e.g. https://nimbus.example.com.</summary>
    public string RegistryUrl { get; set; } = "";

    /// <summary>HMAC shared secret. Must match a value in the registry's SharedSecret/AcceptedSecrets list.</summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>Seconds between heartbeats. Default 5s matches the registry's default sweep cadence.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// When true, this backend rejects player identification unless the registry has a fresh
    /// reservation for that PlayerUID + this ServerId. Use on event/staff/draining servers.
    /// Hub-style backends should keep this off so direct joins still work.
    /// </summary>
    public bool RequireReservationForJoin { get; set; } = false;

    /// <summary>
    /// If true, players with the controlserver privilege bypass the reservation requirement.
    /// Useful so staff can always log in to a draining/event backend without minting one.
    /// </summary>
    public bool StaffBypassReservation { get; set; } = true;

    /// <summary>Per-request HTTP timeout to the registry in seconds.</summary>
    public int RegistryHttpTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// IPv4/IPv6 CIDR ranges allowed to deliver a PROXY protocol v2 header on inbound TCP
    /// connections. Examples: "127.0.0.0/8", "10.0.0.0/8", "192.168.1.5/32", "::1/128".
    /// When empty, the backend ignores any PROXY header (treats every connection as direct).
    /// When non-empty, connections from listed peers MUST send a v2 header (the parsed source
    /// IP is forwarded into ConnectedClient for bans / logs / IsLocalConnection).
    /// </summary>
    public List<string> TrustedProxyCidrs { get; set; } = new();

    /// <summary>Read timeout for the PROXY v2 header in milliseconds.</summary>
    public int ProxyProtocolReadTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// When true, attempts to validate the registry shared secret at startup by issuing a
    /// signed heartbeat as part of preflight. Failure logs a warning but does not abort boot.
    /// </summary>
    public bool VerifyRegistryAtStartup { get; set; } = true;

    public void EnsurePopulated()
    {
        if (Tags == null) Tags = new List<string>();
        if (HeartbeatIntervalSeconds < 1) HeartbeatIntervalSeconds = 5;
        if (RegistryHttpTimeoutSeconds < 1) RegistryHttpTimeoutSeconds = 5;
        if (PublicPort <= 0 || PublicPort > 65535) PublicPort = 42420;
        if (TrustedProxyCidrs == null) TrustedProxyCidrs = new List<string>();
        if (ProxyProtocolReadTimeoutMs < 100) ProxyProtocolReadTimeoutMs = 2000;
        parsedCidrs = null;
    }

    // Cached parse of TrustedProxyCidrs. Rebuilt lazily on first use after EnsurePopulated.
    private List<(IPAddress network, int prefix, AddressFamily family)>? parsedCidrs;

    public bool IsTrustedProxyAddress(IPAddress addr)
    {
        if (addr == null) return false;
        if (TrustedProxyCidrs == null || TrustedProxyCidrs.Count == 0) return false;

        // Unwrap ::ffff:1.2.3.4 so "1.2.3.4/32" entries match dual-stack listeners.
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();

        if (parsedCidrs == null)
        {
            var list = new List<(IPAddress, int, AddressFamily)>(TrustedProxyCidrs.Count);
            foreach (var raw in TrustedProxyCidrs)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!TryParseCidr(raw.Trim(), out var net, out var prefix)) continue;
                list.Add((net, prefix, net.AddressFamily));
            }
            parsedCidrs = list;
        }

        foreach (var (net, prefix, fam) in parsedCidrs)
        {
            if (fam != addr.AddressFamily) continue;
            if (PrefixMatch(addr.GetAddressBytes(), net.GetAddressBytes(), prefix)) return true;
        }
        return false;
    }

    private static bool TryParseCidr(string s, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        int slash = s.IndexOf('/');
        string addrPart = slash >= 0 ? s.Substring(0, slash) : s;
        string maskPart = slash >= 0 ? s.Substring(slash + 1) : "";
        if (!IPAddress.TryParse(addrPart, out var ip)) return false;
        int defaultPrefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (slash < 0) { network = ip; prefix = defaultPrefix; return true; }
        if (!int.TryParse(maskPart, out prefix)) return false;
        if (prefix < 0 || prefix > defaultPrefix) return false;
        network = ip;
        return true;
    }

    private static bool PrefixMatch(byte[] a, byte[] b, int prefix)
    {
        if (a.Length != b.Length) return false;
        int fullBytes = prefix / 8;
        int remBits = prefix % 8;
        for (int i = 0; i < fullBytes; i++) if (a[i] != b[i]) return false;
        if (remBits == 0) return true;
        int mask = 0xFF << (8 - remBits) & 0xFF;
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }

    /// <summary>Returns a one-line summary suitable for /stratum status and /nimbus status.</summary>
    public string StatusSummary()
    {
        if (!Enabled) return "disabled";
        return $"enabled (id={ServerId}, host={PublicHost}:{PublicPort}, registry={(string.IsNullOrEmpty(RegistryUrl) ? "<unset>" : RegistryUrl)}, reservationRequired={RequireReservationForJoin})";
    }
}
