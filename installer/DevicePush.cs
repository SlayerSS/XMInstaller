using System.Text.Json.Nodes;

namespace XMInstaller;

static class DevicePush
{
    public static List<string> Run(NvrClient nvr, CfgStore source, bool cameras, bool titles, bool record, bool detect, bool network, bool users, IProgress<string> progress, IReadOnlySet<int>? channels = null, IReadOnlySet<string>? onlyUsers = null, IReadOnlySet<string>? netFields = null, IReadOnlyDictionary<int, int>? channelMap = null)
    {
        var package = source.Transfer();
        var lines = new List<string>();
        if (cameras)
            lines.Add(Step(progress, "камеры", () => PushCameras(nvr, package, channels, channelMap)));
        if (titles)
            lines.Add(Step(progress, "названия", () => PushTitles(nvr, package, channels, channelMap)));
        if (record)
            lines.Add(Step(progress, "запись", () => PushNamedList(nvr, "Record", package["records"] as JsonArray, channels, channelMap)));
        if (detect)
        {
            var part = package["detect"] as JsonObject;
            lines.Add(Step(progress, "движение", () => PushNamedList(nvr, "Detect.MotionDetect", part?["MotionDetect"] as JsonArray, channels, channelMap)));
            lines.Add(Step(progress, "потеря видео", () => PushNamedList(nvr, "Detect.LossDetect", part?["LossDetect"] as JsonArray, channels, channelMap)));
            lines.Add(Step(progress, "засвет", () => PushNamedList(nvr, "Detect.BlindDetect", part?["BlindDetect"] as JsonArray, channels, channelMap)));
        }
        if (network)
            lines.Add(Step(progress, "сеть", () => PushNetwork(nvr, package, netFields)));
        if (users)
            lines.AddRange(PushUsers(nvr, package, progress, onlyUsers));
        if (lines.Count == 0)
            lines.Add("Не отмечено, что переносить.");
        return lines;
    }

    public static string WriteUser(NvrClient nvr, UserRow user, string hash, IReadOnlyList<string> rights)
    {
        var packageUser = new JsonObject
        {
            ["name"] = user.Name,
            ["group"] = user.Group,
            ["memo"] = user.Memo,
            ["password"] = hash,
            ["reserved"] = user.Reserved,
            ["sharable"] = user.Sharable,
            ["authority"] = new JsonArray(rights.Select(item => (JsonNode)item).ToArray())
        };
        var groups = new JsonObject();
        foreach (var node in nvr.Groups())
        {
            if (node is JsonObject group && NvrClient.Str(group, "Name") is { Length: > 0 } name)
                groups[name] = group["AuthorityList"]?.DeepClone();
        }
        var known = nvr.Authority();
        var live = IndexUsers(nvr.Users());
        return ApplyUser(nvr, packageUser, new JsonObject(), groups, known.ToHashSet(StringComparer.Ordinal), live);
    }

    static string Step(IProgress<string> progress, string title, Func<string> action)
    {
        progress.Report(title);
        try
        {
            return title + ": " + action();
        }
        catch (Exception ex)
        {
            return title + ": " + ex.Message;
        }
    }

    static List<(int Source, int Target)> Pairs(int sourceCount, int liveCount, IReadOnlySet<int>? channels, IReadOnlyDictionary<int, int>? map)
    {
        var list = new List<(int, int)>();
        if (map != null)
        {
            foreach (var pair in map)
            {
                var source = pair.Key - 1;
                var target = pair.Value - 1;
                if (source >= 0 && source < sourceCount && target >= 0 && target < liveCount)
                    list.Add((source, target));
            }
            return list;
        }
        var count = Math.Min(sourceCount, liveCount);
        for (var i = 0; i < count; i++)
        {
            if (channels != null && !channels.Contains(i + 1))
                continue;
            list.Add((i, i));
        }
        return list;
    }

    static string PushCameras(NvrClient nvr, JsonObject package, IReadOnlySet<int>? channels, IReadOnlyDictionary<int, int>? map)
    {
        if (package["cameras"] is not JsonArray incoming)
            return "в файле нет камер";
        var live = nvr.TryConfig("NetWork.RemoteDeviceV3") as JsonArray ?? throw new NvrException("аппарат не отдал список камер");
        var pairs = Pairs(incoming.Count, live.Count, channels, map);
        var count = 0;
        foreach (var (source, target) in pairs)
        {
            var row = incoming[source] as JsonObject;
            var profiles = row?["profiles"] as JsonArray ?? [];
            var existing = (live[target] as JsonObject)?["Decoder"] as JsonArray;
            var decoders = new JsonArray();
            for (var p = 0; p < profiles.Count; p++)
            {
                var profile = profiles[p] as JsonObject;
                var keep = p < (existing?.Count ?? 0) ? existing?[p] as JsonObject : null;
                decoders.Add(new JsonObject
                {
                    ["Channel"] = IntOf(profile, "channel"),
                    ["ConfName"] = StrOf(profile, "name"),
                    ["DevType"] = keep == null ? "IPC" : First(NvrClient.Str(keep, "DevType"), "IPC"),
                    ["Enable"] = BoolOf(profile, "enable"),
                    ["IPAddress"] = StrOf(profile, "ip"),
                    ["Interval"] = keep == null ? 10 : NvrClient.Int(keep, "Interval"),
                    ["MacAddr"] = keep == null ? "" : NvrClient.Str(keep, "MacAddr"),
                    ["MainRtspUrl"] = "",
                    ["PassWord"] = StrOf(profile, "password"),
                    ["Port"] = IntOf(profile, "port") == 0 ? 34567 : IntOf(profile, "port"),
                    ["Protocol"] = First(StrOf(profile, "protocol"), "TCP"),
                    ["SerialNo"] = keep == null ? "" : NvrClient.Str(keep, "SerialNo"),
                    ["StreamType"] = First(StrOf(profile, "stream"), "MAIN"),
                    ["SubRtspUrl"] = "",
                    ["TransModel"] = keep?["TransModel"]?.DeepClone() ?? JsonValue.Create(0),
                    ["UserName"] = StrOf(profile, "user")
                });
            }
            var active = IntOf(row, "active");
            if (decoders.Count > 0)
                count++;
            live[target] = new JsonObject
            {
                ["ConnType"] = "SINGLE",
                ["Decoder"] = decoders.Count == 0 ? null : decoders,
                ["EnCheckTime"] = decoders.Count > 0,
                ["Enable"] = BoolOf(row, "enable"),
                ["SingleConnId"] = "0x" + Math.Max(0, active).ToString("X8"),
                ["SynchResolution"] = false,
                ["TourIntv"] = 10
            };
        }
        if (pairs.Count == 0)
            return "нет выбранных каналов";
        nvr.SetConfig("NetWork.RemoteDeviceV3", live);
        return DescribeMove("каналов", pairs) + ", с камерой " + count;
    }

    static string PushTitles(NvrClient nvr, JsonObject package, IReadOnlySet<int>? channels, IReadOnlyDictionary<int, int>? map)
    {
        if (package["cameras"] is not JsonArray incoming)
            return "в файле нет названий";
        var widgets = nvr.TryConfig("AVEnc.VideoWidget") as JsonArray ?? throw new NvrException("аппарат не отдал названия");
        var pairs = Pairs(incoming.Count, widgets.Count, channels, map);
        var count = 0;
        foreach (var (source, target) in pairs)
        {
            if (widgets[target] is not JsonObject widget)
                continue;
            var title = widget["ChannelTitle"] as JsonObject ?? new JsonObject();
            widget["ChannelTitle"] = title;
            title["Name"] = StrOf(incoming[source] as JsonObject, "title");
            count++;
        }
        if (count == 0)
            return "нет выбранных каналов";
        nvr.SetConfig("AVEnc.VideoWidget", widgets);
        return DescribeMove("названий", pairs);
    }

    static string PushNamedList(NvrClient nvr, string name, JsonArray? source, IReadOnlySet<int>? channels, IReadOnlyDictionary<int, int>? map)
    {
        if (source == null || source.Count == 0)
            return "в файле пусто";
        var live = nvr.TryConfig(name) as JsonArray ?? throw new NvrException("аппарат не отдал " + name);
        var pairs = Pairs(source.Count, live.Count, channels, map);
        var count = 0;
        foreach (var (from, target) in pairs)
        {
            if (live[target] is JsonObject left && source[from] is JsonObject right)
            {
                Overlay(left, right.DeepClone().AsObject());
                count++;
            }
        }
        if (count == 0)
            return "нет выбранных каналов";
        nvr.SetConfig(name, live);
        return DescribeMove("каналов", pairs);
    }

    static string DescribeMove(string word, List<(int Source, int Target)> pairs)
    {
        var moved = pairs.Where(pair => pair.Source != pair.Target).Select(pair => (pair.Source + 1) + "→" + (pair.Target + 1)).ToList();
        if (moved.Count == 0)
            return word + " " + pairs.Count;
        return word + " " + pairs.Count + " (" + string.Join(", ", moved) + ")";
    }

    static string PushNetwork(NvrClient nvr, JsonObject package, IReadOnlySet<string>? fields)
    {
        if (package["network"] is not JsonObject src)
            return "в файле нет сети";
        bool Take(string field) => fields == null || fields.Contains(field);
        var common = nvr.TryConfig("NetWork.NetCommon") as JsonObject ?? throw new NvrException("аппарат не отдал сеть");
        var mac = NvrClient.Str(common, "MAC");
        if (Take("ip") && StrOf(src, "ip").Length > 0)
            common["HostIP"] = IpHex(StrOf(src, "ip"));
        if (Take("mask") && StrOf(src, "mask").Length > 0)
            common["Submask"] = IpHex(StrOf(src, "mask"));
        if (Take("gateway") && StrOf(src, "gateway").Length > 0)
            common["GateWay"] = IpHex(StrOf(src, "gateway"));
        if (Take("tcp") && IntOf(src, "tcp") > 0)
            common["TCPPort"] = IntOf(src, "tcp");
        if (Take("udp") && IntOf(src, "udp") > 0)
            common["UDPPort"] = IntOf(src, "udp");
        if (Take("http") && IntOf(src, "http") > 0)
            common["HttpPort"] = IntOf(src, "http");
        if (mac.Length > 0)
            common["MAC"] = mac;
        nvr.SetConfig("NetWork.NetCommon", common);
        if (nvr.TryConfig("NetWork.NetDNS") is JsonObject dns && (Take("dns") || Take("dns2")))
        {
            if (Take("dns") && StrOf(src, "dns").Length > 0)
                dns["Address"] = IpHex(StrOf(src, "dns"));
            if (Take("dns2") && StrOf(src, "dns2").Length > 0)
                dns["SpareAddress"] = IpHex(StrOf(src, "dns2"));
            nvr.SetConfig("NetWork.NetDNS", dns);
        }
        if (Take("ntp") && nvr.TryConfig("NetWork.NetNTP") is JsonObject ntp)
        {
            ntp["Enable"] = BoolOf(src, "ntp");
            ntp["TimeZone"] = IntOf(src, "timezone");
            nvr.SetConfig("NetWork.NetNTP", ntp);
        }
        if (Take("dhcp") && nvr.TryConfig("NetWork.NetDHCP") is JsonArray dhcp)
        {
            foreach (var item in dhcp)
            {
                if (item is JsonObject row && NvrClient.Str(row, "Interface") == "eth0")
                    row["Enable"] = false;
            }
            nvr.SetConfig("NetWork.NetDHCP", dhcp);
        }
        return "записано, MAC оставлен " + mac;
    }

    static List<string> PushUsers(NvrClient nvr, JsonObject package, IProgress<string> progress, IReadOnlySet<string>? onlyUsers)
    {
        progress.Report("пользователи");
        var lines = new List<string>();
        try
        {
            var live = IndexUsers(nvr.Users());
            var groups = new JsonObject();
            foreach (var node in nvr.Groups())
            {
                if (node is JsonObject group && NvrClient.Str(group, "Name") is { Length: > 0 } name)
                    groups[name] = group["AuthorityList"]?.DeepClone();
            }
            var known = nvr.Authority().ToHashSet(StringComparer.Ordinal);
            var sourceGroups = package["groups"] as JsonObject ?? [];
            foreach (var node in package["users"] as JsonArray ?? [])
            {
                if (node is not JsonObject user)
                    continue;
                var name = StrOf(user, "name");
                if (onlyUsers != null && !onlyUsers.Contains(name))
                    continue;
                if (name.Length == 0 || name == nvr.Username)
                {
                    lines.Add(name + ": пропущен, это текущий вход");
                    continue;
                }
                lines.Add(name + ": " + ApplyUser(nvr, user, sourceGroups, groups, known, live));
            }
        }
        catch (Exception ex)
        {
            lines.Add("пользователи: " + ex.Message);
        }
        return lines;
    }

    static string ApplyUser(NvrClient nvr, JsonObject user, JsonObject sourceGroups, JsonObject targetGroups, HashSet<string> known, Dictionary<string, JsonObject> live)
    {
        var name = StrOf(user, "name");
        var rights = MapRights(user, sourceGroups, targetGroups, known);
        var hash = StrOf(user, "password");
        var group = First(StrOf(user, "group"), "user");
        var memo = StrOf(user, "memo");
        var sharable = BoolOf(user, "sharable");
        if (live.TryGetValue(name, out var current))
        {
            var modified = nvr.ModifyUser(name, group, rights, memo, sharable, NvrClient.Bool(current, "Reserved"));
            var text = "права " + NvrClient.Text(NvrClient.CodeOf(modified), "отказ");
            var currentHash = NvrClient.Str(current, "Password");
            if (hash.Length > 0 && hash != currentHash)
            {
                var pwd = nvr.ChangePassword(name, hash, currentHash);
                text += ", пароль " + NvrClient.Text(NvrClient.CodeOf(pwd), "отказ");
            }
            return text;
        }
        var added = nvr.AddUser(name, hash, group, rights, memo, sharable, BoolOf(user, "reserved"));
        if (NvrClient.CodeOf(added) is 100 or 150 or 515)
            return "создан, пароль " + (hash.Length == 0 ? "пустой" : "записан");
        var mod = nvr.ModifyUser(name, group, rights, memo, sharable, true);
        var line = "системная учётка, создание " + NvrClient.Text(NvrClient.CodeOf(added), "отказ") + ", права " + NvrClient.Text(NvrClient.CodeOf(mod), "отказ");
        if (hash.Length > 0)
        {
            var oldHash = name == "admin" ? Sofia.Hash("") : hash;
            var pwd = nvr.ChangePassword(name, hash, oldHash);
            line += ", пароль " + NvrClient.Text(NvrClient.CodeOf(pwd), "отказ");
        }
        return line;
    }

    static List<string> MapRights(JsonObject user, JsonObject sourceGroups, JsonObject targetGroups, HashSet<string> known)
    {
        var current = NvrClient.Strings(user["authority"]).ToHashSet(StringComparer.Ordinal);
        var sourceAdmin = NvrClient.Strings(sourceGroups["admin"]).ToHashSet(StringComparer.Ordinal);
        var sourceUser = NvrClient.Strings(sourceGroups["user"]).ToHashSet(StringComparer.Ordinal);
        if (sourceAdmin.Count > 0 && current.SetEquals(sourceAdmin) && targetGroups["admin"] is JsonArray admin)
            return NvrClient.Strings(admin);
        if (sourceUser.Count > 0 && current.SetEquals(sourceUser) && targetGroups["user"] is JsonArray plain)
            return NvrClient.Strings(plain);
        if (known.Count > 0)
            return current.Where(known.Contains).ToList();
        return current.ToList();
    }

    static Dictionary<string, JsonObject> IndexUsers(JsonArray users)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in users)
        {
            if (node is JsonObject user && NvrClient.Str(user, "Name") is { Length: > 0 } name)
                map[name] = user;
        }
        return map;
    }

    static void Overlay(JsonObject dst, JsonObject src)
    {
        foreach (var (key, value) in src)
        {
            if (value == null || dst[key] == null)
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
            else if (dst[key]!.GetType() == value.GetType())
                dst[key] = value.DeepClone();
        }
    }

    static string IpHex(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4 || !parts.All(part => byte.TryParse(part, out _)))
            return ip;
        var bytes = parts.Select(byte.Parse).ToArray();
        var value = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        return "0x" + value.ToString("X8");
    }

    static string StrOf(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";

    static int IntOf(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return 0;
    }

    static bool BoolOf(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    static string First(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
