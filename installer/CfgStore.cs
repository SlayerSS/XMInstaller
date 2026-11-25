using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XMInstaller;

sealed class Profile
{
    public string Ip { get; set; } = "";
    public string Protocol { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Stream { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enable { get; set; }
    public int Channel { get; set; }
}

sealed class CameraRow
{
    public int Channel { get; set; }
    public bool Enable { get; set; }
    public string Title { get; set; } = "";
    public int Active { get; set; }
    public string Ip { get; set; } = "";
    public string Protocol { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public int ProfilesCount { get; set; }
    public List<Profile> Profiles { get; set; } = [];
}

sealed class UserRow
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Memo { get; set; } = "";
    public string Password { get; set; } = "";
    public string PasswordV2 { get; set; } = "";
    public bool Reserved { get; set; }
    public bool Sharable { get; set; }
    public List<string> Authority { get; set; } = [];
    public string Shown => Sofia.Show(Password);
}

sealed class NetInfo
{
    public string Ip { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Dns { get; set; } = "";
    public string Dns2 { get; set; } = "";
    public string Mac { get; set; } = "";
    public int Http { get; set; }
    public int Tcp { get; set; }
    public int Udp { get; set; }
    public bool Ntp { get; set; }
    public int TimeZone { get; set; }
}

sealed class RecordRow
{
    public int Channel { get; set; }
    public string Mode { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Period { get; set; } = "";
}

sealed class MotionRow
{
    public int Channel { get; set; }
    public bool Motion { get; set; }
    public int Level { get; set; }
    public bool Loss { get; set; }
    public bool Blind { get; set; }
}

sealed class LogRow
{
    public string Time { get; set; } = "";
    public string Who { get; set; } = "";
    public string Event { get; set; } = "";
    public string Data { get; set; } = "";
    public string Source { get; set; } = "";
}

sealed class Summary
{
    public string File { get; set; } = "";
    public string Generation { get; set; } = "";
    public string Version { get; set; } = "";
    public string Product { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public int Channels { get; set; }
    public int Cameras { get; set; }
    public string Users { get; set; } = "";
    public string Runtime { get; set; } = "";
}

sealed class CfgStore
{
    static readonly Regex LogLine = new(@"^(\d+-\d+-\d+ \d+:\d+:\d+)\[([^\]]+)\],\[([^\]]*)\],\[(.*)\]$", RegexOptions.Compiled);

    public string Name { get; }
    public Dictionary<string, byte[]> Blobs { get; }
    public Dictionary<string, JsonNode> Json { get; } = new(StringComparer.Ordinal);
    public List<UserRow> Users { get; } = [];
    public Dictionary<string, List<string>> Groups { get; } = [];

    CfgStore(string name, Dictionary<string, byte[]> blobs)
    {
        Name = name;
        Blobs = blobs;
        foreach (var (path, raw) in blobs)
        {
            if (!path.StartsWith("Json/", StringComparison.Ordinal) && path is not ("Account1" or "Account2"))
                continue;
            var node = Parse(raw);
            if (node != null)
                Json[path] = node;
        }
        ReadUsers();
    }

    public static CfgStore Open(string path)
    {
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            using var input = entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            blobs[entry.FullName.Replace('\\', '/')] = memory.ToArray();
        }
        return new CfgStore(Path.GetFileName(path), blobs);
    }

    public static CfgStore FromBytes(string name, byte[] data)
    {
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            using var input = entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            blobs[entry.FullName.Replace('\\', '/')] = memory.ToArray();
        }
        return new CfgStore(name, blobs);
    }

    void ReadUsers()
    {
        var account = Object("Account1") ?? Object("Account2");
        if (account == null)
            return;
        foreach (var group in account["Groups"] as JsonArray ?? [])
        {
            var obj = group as JsonObject;
            var name = S(obj, "Name");
            if (name.Length == 0)
                continue;
            Groups[name] = Strings(obj?["AuthorityList"]);
        }
        foreach (var item in account["Users"] as JsonArray ?? [])
        {
            var obj = item as JsonObject;
            if (obj == null)
                continue;
            Users.Add(new UserRow
            {
                Name = S(obj, "Name"),
                Group = S(obj, "Group"),
                Memo = S(obj, "Memo"),
                Password = S(obj, "Password"),
                PasswordV2 = S(obj, "PasswordV2"),
                Reserved = B(obj, "Reserved"),
                Sharable = B(obj, "Sharable"),
                Authority = Strings(obj["AuthorityList"])
            });
        }
    }

    public Summary Describe()
    {
        var version = Text("Version.ini");
        var shortVersion = version.Split('\n', '\r').FirstOrDefault(line => line.StartsWith("Version:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? version.Trim();
        var dhcp = Text("dhcp.cfg").Trim();
        var account = Text("Account1");
        var kind = account.Contains("PasswordV2", StringComparison.Ordinal) || shortVersion.StartsWith("V4.03", StringComparison.Ordinal) || dhcp.StartsWith("DhcpEnable", StringComparison.Ordinal)
            ? "новый"
            : shortVersion.StartsWith("V4.02", StringComparison.Ordinal) || dhcp is "0" or "1"
                ? "старый"
                : "неизвестный";
        var net = Network();
        var cameras = Cameras();
        return new Summary
        {
            File = Name,
            Generation = kind,
            Version = shortVersion,
            Product = Text("ProductType.ini").Trim(),
            Serial = Text("SerialNumber").Trim(),
            Ip = net.Ip,
            Mac = net.Mac,
            Channels = cameras.Count,
            Cameras = cameras.Count(row => row.Ip.Length > 0),
            Users = string.Join(", ", Users.Select(user => user.Name))
        };
    }

    public List<CameraRow> Cameras()
    {
        var remote = Object("Json/NetWork")?["RemoteDeviceV3"] as JsonArray ?? [];
        var widgets = Object("Json/AVEnc")?["VideoWidget"] as JsonArray ?? [];
        var rows = new List<CameraRow>();
        for (var i = 0; i < remote.Count; i++)
        {
            var slot = remote[i] as JsonObject;
            var title = "";
            if (i < widgets.Count)
                title = S(widgets[i]?["ChannelTitle"] as JsonObject, "Name");
            rows.Add(ReadCamera(i + 1, slot, title));
        }
        return rows;
    }

    public static CameraRow ReadCamera(int channel, JsonObject? slot, string title)
    {
        var decoders = slot?["Decoder"] as JsonArray ?? [];
        var profiles = new List<Profile>();
        foreach (var node in decoders)
        {
            var dec = node as JsonObject;
            if (dec == null)
                continue;
            profiles.Add(new Profile
            {
                Ip = S(dec, "IPAddress"),
                Protocol = S(dec, "Protocol"),
                Port = I(dec, "Port"),
                User = S(dec, "UserName"),
                Password = S(dec, "PassWord"),
                Stream = S(dec, "StreamType"),
                Name = S(dec, "ConfName"),
                Enable = B(dec, "Enable"),
                Channel = I(dec, "Channel")
            });
        }
        var active = 0;
        if (int.TryParse(S(slot, "SingleConnId").Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var id))
            active = id - 1;
        var chosen = active >= 0 && active < profiles.Count ? profiles[active] : profiles.FirstOrDefault();
        return new CameraRow
        {
            Channel = channel,
            Enable = B(slot, "Enable"),
            Title = title,
            Active = profiles.Count == 0 ? 0 : (chosen == null ? 1 : profiles.IndexOf(chosen) + 1),
            Ip = chosen?.Ip ?? "",
            Protocol = chosen?.Protocol ?? "",
            Port = chosen?.Port ?? 0,
            User = chosen?.User ?? "",
            Password = chosen?.Password ?? "",
            ProfilesCount = profiles.Count,
            Profiles = profiles
        };
    }

    public NetInfo Network()
    {
        var root = Object("Json/NetWork");
        var common = root?["NetCommon"] as JsonObject;
        var dns = root?["NetDNS"] as JsonObject;
        var ntp = root?["NetNTP"] as JsonObject;
        return new NetInfo
        {
            Ip = IpText(S(common, "HostIP")),
            Mask = IpText(S(common, "Submask")),
            Gateway = IpText(S(common, "GateWay")),
            Dns = IpText(S(dns, "Address")),
            Dns2 = IpText(S(dns, "SpareAddress")),
            Mac = S(common, "MAC"),
            Http = I(common, "HttpPort"),
            Tcp = I(common, "TCPPort"),
            Udp = I(common, "UDPPort"),
            Ntp = B(ntp, "Enable"),
            TimeZone = I(ntp, "TimeZone")
        };
    }

    public List<RecordRow> Records()
    {
        var rows = new List<RecordRow>();
        var list = Json.GetValueOrDefault("Json/Record") as JsonArray ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i] as JsonObject;
            var mask = item?["Mask"]?[0]?[0]?.ToString() ?? "";
            var period = item?["TimeSection"]?[0]?[0]?.ToString() ?? "";
            rows.Add(new RecordRow { Channel = i + 1, Mode = S(item, "RecordMode"), Mask = mask, Period = period });
        }
        return rows;
    }

    public List<MotionRow> Motion()
    {
        var detect = Object("Json/Detect");
        var motion = detect?["MotionDetect"] as JsonArray ?? [];
        var loss = detect?["LossDetect"] as JsonArray ?? [];
        var blind = detect?["BlindDetect"] as JsonArray ?? [];
        var count = Math.Max(motion.Count, Math.Max(loss.Count, blind.Count));
        var rows = new List<MotionRow>();
        for (var i = 0; i < count; i++)
        {
            var m = i < motion.Count ? motion[i] as JsonObject : null;
            var l = i < loss.Count ? loss[i] as JsonObject : null;
            var b = i < blind.Count ? blind[i] as JsonObject : null;
            rows.Add(new MotionRow
            {
                Channel = i + 1,
                Motion = B(m, "Enable"),
                Level = I(m, "Level"),
                Loss = B(l, "Enable"),
                Blind = B(b, "Enable")
            });
        }
        return rows;
    }

    public List<LogRow> Logs()
    {
        var rows = new List<LogRow>();
        foreach (var path in Blobs.Keys.Where(path => path.StartsWith("Log", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).Order())
        {
            foreach (var line in Text(path).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;
                var match = LogLine.Match(trimmed);
                rows.Add(match.Success
                    ? new LogRow { Time = match.Groups[1].Value, Who = match.Groups[2].Value, Event = match.Groups[3].Value, Data = match.Groups[4].Value, Source = path }
                    : new LogRow { Data = trimmed, Source = path });
            }
        }
        return rows;
    }

    public IEnumerable<string> Sections() => Blobs.Keys.Order();

    public string SectionText(string path)
    {
        if (!Blobs.TryGetValue(path, out var raw))
            return "";
        var node = Parse(raw);
        var text = node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? Encoding.UTF8.GetString(raw);
        return text.Length > 120000 ? text[..120000] + "\n\n… обрезано" : text;
    }

    public JsonObject Transfer()
    {
        var package = new JsonObject
        {
            ["format"] = "xm-transfer",
            ["version"] = 1,
            ["exportedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["users"] = new JsonArray(Users.Select(UserJson).ToArray()),
            ["groups"] = new JsonObject()
        };
        var groups = package["groups"]!.AsObject();
        foreach (var (name, rights) in Groups)
            groups[name] = new JsonArray(rights.Select(item => (JsonNode)item).ToArray());
        var cameras = new JsonArray();
        foreach (var camera in Cameras())
            cameras.Add(CameraJson(camera));
        package["cameras"] = cameras;
        var net = Network();
        package["network"] = new JsonObject
        {
            ["ip"] = net.Ip, ["mask"] = net.Mask, ["gateway"] = net.Gateway,
            ["dns"] = net.Dns, ["dns2"] = net.Dns2, ["http"] = net.Http,
            ["tcp"] = net.Tcp, ["udp"] = net.Udp, ["ntp"] = net.Ntp, ["timezone"] = net.TimeZone
        };
        if (Json.TryGetValue("Json/Record", out var record))
            package["records"] = record.DeepClone();
        var detect = Object("Json/Detect");
        if (detect != null)
        {
            var part = new JsonObject();
            foreach (var key in new[] { "MotionDetect", "LossDetect", "BlindDetect" })
            {
                if (detect[key] is JsonArray list)
                    part[key] = list.DeepClone();
            }
            package["detect"] = part;
        }
        return package;
    }

    public static byte[] Apply(CfgStore target, JsonObject package, bool cameras, bool titles, bool record, bool detect, bool network)
    {
        var blobs = new Dictionary<string, byte[]>(target.Blobs, StringComparer.Ordinal);
        var net = target.Object("Json/NetWork")?.DeepClone().AsObject() ?? new JsonObject();
        if (cameras && package["cameras"] is JsonArray incoming && net["RemoteDeviceV3"] is JsonArray remote)
        {
            for (var i = 0; i < incoming.Count && i < remote.Count; i++)
            {
                var row = incoming[i] as JsonObject;
                var profiles = row?["profiles"] as JsonArray ?? [];
                var decoders = new JsonArray();
                foreach (var node in profiles)
                {
                    var profile = node as JsonObject;
                    decoders.Add(new JsonObject
                    {
                        ["Channel"] = I(profile, "channel"),
                        ["ConfName"] = S(profile, "name"),
                        ["DevType"] = "IPC",
                        ["Enable"] = B(profile, "enable"),
                        ["IPAddress"] = S(profile, "ip"),
                        ["Interval"] = 10,
                        ["MacAddr"] = "",
                        ["MainRtspUrl"] = "",
                        ["PassWord"] = S(profile, "password"),
                        ["Port"] = I(profile, "port") == 0 ? 34567 : I(profile, "port"),
                        ["Protocol"] = S(profile, "protocol").Length == 0 ? "TCP" : S(profile, "protocol"),
                        ["SerialNo"] = "",
                        ["StreamType"] = S(profile, "stream").Length == 0 ? "MAIN" : S(profile, "stream"),
                        ["SubRtspUrl"] = "",
                        ["TransModel"] = 0,
                        ["UserName"] = S(profile, "user")
                    });
                }
                remote[i] = new JsonObject
                {
                    ["ConnType"] = "SINGLE",
                    ["Decoder"] = decoders.Count == 0 ? null : decoders,
                    ["EnCheckTime"] = decoders.Count > 0,
                    ["Enable"] = B(row, "enable"),
                    ["SingleConnId"] = "0x" + Math.Max(0, I(row, "active")).ToString("X8"),
                    ["SynchResolution"] = false,
                    ["TourIntv"] = 10
                };
            }
        }
        if (network && package["network"] is JsonObject src)
        {
            var common = net["NetCommon"] as JsonObject ?? new JsonObject();
            net["NetCommon"] = common;
            var mac = S(common, "MAC");
            if (S(src, "ip").Length > 0) common["HostIP"] = IpHex(S(src, "ip"));
            if (S(src, "mask").Length > 0) common["Submask"] = IpHex(S(src, "mask"));
            if (S(src, "gateway").Length > 0) common["GateWay"] = IpHex(S(src, "gateway"));
            if (I(src, "http") > 0) common["HttpPort"] = I(src, "http");
            if (I(src, "tcp") > 0) common["TCPPort"] = I(src, "tcp");
            if (I(src, "udp") > 0) common["UDPPort"] = I(src, "udp");
            if (mac.Length > 0) common["MAC"] = mac;
            var dns = net["NetDNS"] as JsonObject ?? new JsonObject();
            net["NetDNS"] = dns;
            if (S(src, "dns").Length > 0) dns["Address"] = IpHex(S(src, "dns"));
            if (S(src, "dns2").Length > 0) dns["SpareAddress"] = IpHex(S(src, "dns2"));
            if (net["NetNTP"] is JsonObject ntp)
            {
                ntp["Enable"] = B(src, "ntp");
                ntp["TimeZone"] = I(src, "timezone");
            }
            if (net["NetDHCP"] is JsonArray dhcp)
            {
                foreach (var item in dhcp)
                    if (item is JsonObject row && S(row, "Interface") == "eth0")
                        row["Enable"] = false;
            }
            if (net["ForceNetDHCP"] is JsonObject force)
                force["Enable"] = false;
            blobs["network"] = Encoding.ASCII.GetBytes($"HOSTIP = {S(src, "ip")}\nSUBMASK = {S(src, "mask")}\nGATEWAYIP = {S(src, "gateway")}\n");
            var resolv = Encoding.ASCII.GetBytes($"nameserver {First(S(src, "dns"), "8.8.8.8")}\nnameserver {First(S(src, "dns2"), "8.8.8.8")}\n");
            foreach (var path in new[] { "resolv.conf", "resolv_eth0.conf", "eth0.conf" })
                if (blobs.ContainsKey(path))
                    blobs[path] = resolv;
            if (blobs.TryGetValue("dhcp.cfg", out var dhcpBytes))
                blobs["dhcp.cfg"] = Encoding.ASCII.GetString(dhcpBytes).StartsWith("Dhcp", StringComparison.Ordinal) ? "DhcpEnable:0"u8.ToArray() : "0"u8.ToArray();
        }
        blobs["Json/NetWork"] = Gzip(net);
        if (blobs.ContainsKey("Json/NetWork.second"))
            blobs["Json/NetWork.second"] = blobs["Json/NetWork"];

        if (titles && package["cameras"] is JsonArray titled)
        {
            var av = target.Object("Json/AVEnc")?.DeepClone().AsObject();
            if (av?["VideoWidget"] is JsonArray widgets)
            {
                for (var i = 0; i < titled.Count && i < widgets.Count; i++)
                {
                    var name = S(titled[i] as JsonObject, "title");
                    if (widgets[i] is JsonObject widget)
                    {
                        var title = widget["ChannelTitle"] as JsonObject ?? new JsonObject();
                        widget["ChannelTitle"] = title;
                        title["Name"] = name;
                    }
                }
                blobs["Json/AVEnc"] = Gzip(av);
                if (blobs.ContainsKey("Json/AVEnc.second"))
                    blobs["Json/AVEnc.second"] = blobs["Json/AVEnc"];
            }
        }

        if (record && package["records"] is JsonArray records && records.Count > 0)
        {
            var count = (net["RemoteDeviceV3"] as JsonArray)?.Count ?? records.Count;
            var copy = new JsonArray();
            for (var i = 0; i < count; i++)
                copy.Add((i < records.Count ? records[i] : records[^1])!.DeepClone());
            var packed = Gzip(copy);
            blobs["Json/Record"] = packed;
            blobs["Json/Record.second"] = packed;
        }

        if (detect && package["detect"] is JsonObject srcDetect && target.Object("Json/Detect") is JsonObject dstDetect)
        {
            var copy = dstDetect.DeepClone().AsObject();
            foreach (var key in new[] { "MotionDetect", "LossDetect", "BlindDetect" })
            {
                if (copy[key] is not JsonArray dst || srcDetect[key] is not JsonArray srcList)
                    continue;
                for (var i = 0; i < srcList.Count && i < dst.Count; i++)
                    if (dst[i] is JsonObject left && srcList[i] is JsonObject right)
                        Overlay(left, right);
            }
            var packed = Gzip(copy);
            blobs["Json/Detect"] = packed;
            if (blobs.ContainsKey("Json/Detect.second"))
                blobs["Json/Detect.second"] = packed;
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var path in blobs.Keys.Order())
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(blobs[path]);
            }
        }
        return output.ToArray();
    }

    static void Overlay(JsonObject dst, JsonObject src)
    {
        foreach (var (key, value) in src)
        {
            if (value == null || !dst.ContainsKey(key))
                continue;
            if (dst[key] is JsonObject child && value is JsonObject other)
                Overlay(child, other);
            else if (dst[key] is JsonArray left && value is JsonArray right)
            {
                if (left.Count == right.Count && left.Count > 0 && left[0] is JsonObject && right[0] is JsonObject)
                {
                    for (var i = 0; i < left.Count; i++)
                        if (left[i] is JsonObject a && right[i] is JsonObject b)
                            Overlay(a, b);
                }
                else if (left.Count == right.Count)
                    dst[key] = right.DeepClone();
                else
                {
                    for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
                    {
                        if (left[i] is JsonObject a && right[i] is JsonObject b)
                            Overlay(a, b);
                        else
                            left[i] = right[i]?.DeepClone();
                    }
                }
            }
            else if (dst[key]?.GetType() == value.GetType())
                dst[key] = value.DeepClone();
        }
    }

    static JsonObject UserJson(UserRow user) => new()
    {
        ["name"] = user.Name,
        ["group"] = user.Group,
        ["memo"] = user.Memo,
        ["password"] = user.Password,
        ["reserved"] = user.Reserved,
        ["sharable"] = user.Sharable,
        ["authority"] = new JsonArray(user.Authority.Select(item => (JsonNode)item).ToArray())
    };

    static JsonObject CameraJson(CameraRow camera)
    {
        var profiles = new JsonArray();
        foreach (var profile in camera.Profiles)
        {
            profiles.Add(new JsonObject
            {
                ["ip"] = profile.Ip, ["protocol"] = profile.Protocol, ["port"] = profile.Port,
                ["user"] = profile.User, ["password"] = profile.Password, ["stream"] = profile.Stream,
                ["name"] = profile.Name, ["enable"] = profile.Enable, ["channel"] = profile.Channel
            });
        }
        return new JsonObject
        {
            ["channel"] = camera.Channel, ["enable"] = camera.Enable, ["title"] = camera.Title,
            ["active"] = camera.Active, ["profiles"] = profiles
        };
    }

    JsonObject? Object(string path) => Json.GetValueOrDefault(path) as JsonObject;

    string Text(string path) => Blobs.TryGetValue(path, out var raw) ? Encoding.UTF8.GetString(raw) : "";

    static JsonNode? Parse(byte[] raw)
    {
        try
        {
            if (raw.Length >= 2 && raw[0] == 0x1f && raw[1] == 0x8b)
            {
                using var input = new MemoryStream(raw);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                raw = output.ToArray();
            }
            return JsonNode.Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    static byte[] Gzip(JsonNode node)
    {
        var text = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (!text.EndsWith('\n'))
            text += "\n";
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes);
        }
        return output.ToArray();
    }

    public static string IpText(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var number))
            return $"{number & 255}.{(number >> 8) & 255}.{(number >> 16) & 255}.{(number >> 24) & 255}";
        return value;
    }

    static string IpHex(string ip)
    {
        var parts = ip.Split('.').Select(byte.Parse).ToArray();
        uint number = parts[0] | ((uint)parts[1] << 8) | ((uint)parts[2] << 16) | ((uint)parts[3] << 24);
        return $"0x{number:X8}";
    }

    static string S(JsonObject? obj, string key) => obj?[key]?.ToString() ?? "";
    static int I(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : int.TryParse(obj?[key]?.ToString(), out number) ? number : 0;
    static bool B(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    static List<string> Strings(JsonNode? node) => (node as JsonArray)?.Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToList() ?? [];
    static string First(string value, string fallback) => value.Length == 0 ? fallback : value;
}
