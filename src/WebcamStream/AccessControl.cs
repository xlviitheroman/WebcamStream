using System.Net;
using System.Net.Sockets;

namespace WebcamStream;

sealed class AccessControl
{
    public delegate IPAddress[] NameResolver(string name);
    public delegate string? ReverseResolver(IPAddress address);

    readonly NameResolver _resolve;
    readonly ReverseResolver _reverse;
    readonly object _gate = new();
        Rules _rules = Rules.Open();
    Dictionary<string, IPAddress[]> _forward = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (DateTime At, string? Name)> _reverseCache = new();

    public AccessControl(NameResolver? resolve = null, ReverseResolver? reverse = null)
    {
        _resolve = resolve ?? DefaultResolve;
        _reverse = reverse ?? DefaultReverse;
    }

    public bool AllowAll { get { lock (_gate) return _rules.AllowAll; } }

    public void Replace(IEnumerable<string>? entries)
    {
        var rules = Rules.Parse(entries);
        Dictionary<string, IPAddress[]> forward = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in rules.Names)
        {
            try
            {
                forward[name] = _resolve(name) ?? Array.Empty<IPAddress>();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not resolve allow-list name {name}: {ex.Message}");
                forward[name] = Array.Empty<IPAddress>();
            }
        }

        lock (_gate)
        {
            _rules = rules;
            _forward = forward;
            _reverseCache.Clear();
        }
    }

    public bool Permits(IPAddress address)
    {
        address = Normalize(address);
        Rules rules;
        Dictionary<string, IPAddress[]> forward;
        lock (_gate)
        {
            rules = _rules;
            forward = _forward;
        }

        if (rules.AllowAll)
            return true;
        if (rules.Addresses.Contains(address))
            return true;
        foreach (var cidr in rules.Networks)
        {
            if (cidr.Contains(address))
                return true;
        }
        foreach (var name in rules.Names)
        {
            if (!forward.TryGetValue(name, out var ips))
                continue;
            foreach (var ip in ips)
            {
                if (Normalize(ip).Equals(address))
                    return true;
            }
        }
        if (rules.Suffixes.Count == 0)
            return false;

        var host = ReverseName(address);
        if (host == null)
            return false;
        host = host.TrimEnd('.');
        foreach (var suffix in rules.Suffixes)
        {
            if (HostMatchesSuffix(host, suffix))
                return true;
        }
        return false;
    }

    string? ReverseName(IPAddress address)
    {
        var key = address.ToString();
        lock (_gate)
        {
            if (_reverseCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
                return hit.Name;
        }
        string? name = null;
        try { name = _reverse(address); }
        catch { name = null; }
        lock (_gate)
            _reverseCache[key] = (DateTime.UtcNow, name);
        return name;
    }

    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address;
    }

    public static bool HostMatchesSuffix(string host, string suffix)
    {
        host = host.TrimEnd('.');
        suffix = suffix.TrimStart('.').TrimEnd('.');
        if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            return true;
        return host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
    }

    static IPAddress[] DefaultResolve(string name) => Dns.GetHostAddresses(name);

    static string? DefaultReverse(IPAddress address)
    {
        try
        {
            var entry = Dns.GetHostEntry(address);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    sealed class Rules
    {
        public bool AllowAll { get; private set; }
        public HashSet<IPAddress> Addresses { get; } = new();
        public List<Cidr> Networks { get; } = new();
        public List<string> Names { get; } = new();
        public List<string> Suffixes { get; } = new();

        public static Rules Open() => new() { AllowAll = true };

        public static Rules Parse(IEnumerable<string>? entries)
        {
            var list = (entries ?? Array.Empty<string>())
                .Select(e => e.Trim())
                .Where(e => e.Length > 0 && !e.StartsWith('#'))
                .ToList();
            if (list.Count == 0 || list.Any(e => e == "*"))
                return Open();

            var rules = new Rules();
            foreach (var entry in list)
            {
                if (entry.StartsWith('.'))
                {
                    rules.Suffixes.Add(entry);
                    continue;
                }
                var slash = entry.IndexOf('/');
                if (slash > 0)
                {
                    var addrText = entry[..slash];
                    var maskText = entry[(slash + 1)..];
                    if (!IPAddress.TryParse(addrText, out var network))
                    {
                        Log.Warn($"Ignoring allow-list entry '{entry}' because the address is not valid.");
                        continue;
                    }
                    int prefix;
                    try
                    {
                        if (int.TryParse(maskText, out prefix))
                        {
                            rules.Networks.Add(new Cidr(network, prefix));
                            continue;
                        }
                        if (IPAddress.TryParse(maskText, out var mask))
                        {
                            rules.Networks.Add(Cidr.FromMask(network, mask));
                            continue;
                        }
                    }
                    catch (FormatException ex)
                    {
                        Log.Warn($"Ignoring allow-list entry '{entry}': {ex.Message}");
                        continue;
                    }
                    Log.Warn($"Ignoring allow-list entry '{entry}' because the mask is not valid.");
                    continue;
                }
                if (IPAddress.TryParse(entry, out var ip))
                {
                    rules.Addresses.Add(Normalize(ip));
                    continue;
                }
                if (entry.Any(ch => char.IsWhiteSpace(ch) || ch == '/' || ch == '\\'))
                {
                    Log.Warn($"Ignoring allow-list entry '{entry}'.");
                    continue;
                }
                rules.Names.Add(entry);
            }
            return rules;
        }
    }

    readonly struct Cidr
    {
        readonly byte[] _network;
        readonly int _prefix;

        public Cidr(IPAddress network, int prefix)
        {
            network = Normalize(network);
            var bytes = network.GetAddressBytes();
            int max = bytes.Length * 8;
            if (prefix < 0 || prefix > max)
                throw new FormatException($"Prefix /{prefix} does not fit a {bytes.Length * 8}-bit address.");
            _prefix = prefix;
            _network = bytes;
            int full = prefix / 8;
            int rem = prefix % 8;
            for (int i = full + (rem == 0 ? 0 : 1); i < bytes.Length; i++)
                _network[i] = 0;
            if (rem != 0)
                _network[full] = (byte)(_network[full] & (0xFF << (8 - rem)));
        }

        public static Cidr FromMask(IPAddress network, IPAddress mask)
        {
            network = Normalize(network);
            mask = Normalize(mask);
            var maskBytes = mask.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (maskBytes.Length != netBytes.Length)
                throw new FormatException("The subnet mask does not match the address family.");
            int prefix = 0;
            bool zeroSeen = false;
            foreach (var b in maskBytes)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    bool on = (b & (1 << bit)) != 0;
                    if (on)
                    {
                        if (zeroSeen)
                            throw new FormatException("The subnet mask is not contiguous.");
                        prefix++;
                    }
                    else zeroSeen = true;
                }
            }
            return new Cidr(network, prefix);
        }

        public bool Contains(IPAddress address)
        {
            address = Normalize(address);
            var bytes = address.GetAddressBytes();
            if (bytes.Length != _network.Length)
                return false;
            int full = _prefix / 8;
            int rem = _prefix % 8;
            for (int i = 0; i < full; i++)
            {
                if (bytes[i] != _network[i])
                    return false;
            }
            if (rem == 0)
                return true;
            int m = (0xFF << (8 - rem)) & 0xFF;
            return (bytes[full] & m) == (_network[full] & m);
        }
    }
}
