using System.Text;
using System.Text.Json.Nodes;

namespace XMInstaller;

static class Catalog
{
    public static readonly (string Group, string Name, string Title)[] Items =
    [
        ("Устройство", "General.General", "Общие"),
        ("Устройство", "General.Location", "Язык и видео"),
        ("Устройство", "General.AutoMaintain", "Автоперезагрузка"),
        ("Устройство", "Ability", "Возможности"),
        ("Сеть", "NetWork.NetCommon", "IP и порты"),
        ("Сеть", "NetWork.NetDNS", "DNS"),
        ("Сеть", "NetWork.NetNTP", "Время NTP"),
        ("Сеть", "NetWork.NetDHCP", "DHCP"),
        ("Сеть", "NetWork.NetIPFilter", "Фильтр IP"),
        ("Сервисы", "NetWork.NetEmail", "Почта"),
        ("Сервисы", "NetWork.NetFTP", "FTP"),
        ("Сервисы", "NetWork.NetDDNS", "DDNS"),
        ("Сервисы", "NetWork.NetPPPoE", "PPPoE"),
        ("Сервисы", "NetWork.Upnp", "UPnP"),
        ("Сервисы", "NetWork.RTSP", "RTSP"),
        ("Сервисы", "NetWork.Nat", "Облако / NAT"),
        ("Сервисы", "NetWork.Wifi", "Wi-Fi"),
        ("Сервисы", "NetWork.NetARSP", "ARSP"),
        ("Камеры", "NetWork.RemoteDeviceV3", "Подключения камер"),
        ("Камеры", "NetWork.DigTimeSyn", "Синхронизация времени камер"),
        ("Камеры", "AVEnc.VideoWidget", "Названия каналов"),
        ("Запись", "Record", "Расписание"),
        ("Запись", "Storage.StoragePosition", "Диск"),
        ("Охрана", "Detect.MotionDetect", "Движение"),
        ("Охрана", "Detect.LossDetect", "Потеря видео"),
        ("Охрана", "Detect.BlindDetect", "Засвет"),
        ("Экран", "fVideo.GUISet", "Интерфейс"),
        ("Экран", "fVideo.Tour", "Обход каналов"),
        ("Прочее", "Uart.Comm", "Порты"),
        ("Прочее", "Guide", "Мастер первого запуска")
    ];
}

sealed class DeviceSnapshot
{
    public Dictionary<string, JsonNode> Configs { get; } = new(StringComparer.Ordinal);
    public List<string> Missing { get; } = [];
    public JsonObject Info { get; set; } = [];
    public JsonObject? Function { get; set; }
    public List<CameraRow> Cameras { get; set; } = [];
    public List<UserRow> Users { get; set; } = [];
    public Dictionary<string, List<string>> Groups { get; } = [];
    public List<string> Authority { get; set; } = [];
    public NetInfo Network { get; set; } = new();
    public List<RecordRow> Records { get; set; } = [];
    public List<MotionRow> Motion { get; set; } = [];
    public List<LogRow> Logs { get; set; } = [];

    public static DeviceSnapshot Load(NvrClient nvr, IProgress<string> progress)
    {
        var shot = new DeviceSnapshot();
        progress.Report("сведения об аппарате");
        shot.Info = nvr.SystemInfo();
        shot.Function = nvr.SystemFunction();
        foreach (var item in Catalog.Items)
        {
            progress.Report(item.Title);
            try
            {
                var node = nvr.TryConfig(item.Name);
                if (node == null)
                    shot.Missing.Add(item.Name);
                else
                    shot.Configs[item.Name] = node;
            }
            catch (Exception ex)
            {
                shot.Missing.Add(item.Name + " — " + ex.Message);
            }
        }
        progress.Report("пользователи");
        shot.ReadUsers(nvr);
        progress.Report("журнал");
        shot.ReadLogs(nvr);
        shot.ReadCameras();
        shot.ReadNetwork();
        shot.ReadRecords();
        shot.ReadMotion();
        return shot;
    }

    void ReadUsers(NvrClient nvr)
    {
        try
        {
            Authority = nvr.Authority();
        }
        catch (Exception ex)
        {
            Missing.Add("права — " + ex.Message);
        }
        try
        {
            foreach (var node in nvr.Groups())
            {
                if (node is not JsonObject group)
                    continue;
                var name = NvrClient.Str(group, "Name");
                if (name.Length > 0)
                    Groups[name] = NvrClient.Strings(group["AuthorityList"]);
            }
        }
        catch (Exception ex)
        {
            Missing.Add("группы — " + ex.Message);
        }
        try
        {
            foreach (var node in nvr.Users())
            {
                if (node is not JsonObject user)
                    continue;
                Users.Add(new UserRow
                {
                    Name = NvrClient.Str(user, "Name"),
                    Group = NvrClient.Str(user, "Group"),
                    Memo = NvrClient.Str(user, "Memo"),
                    Password = NvrClient.Str(user, "Password"),
                    PasswordV2 = NvrClient.Str(user, "PasswordV2"),
                    Reserved = NvrClient.Bool(user, "Reserved"),
                    Sharable = NvrClient.Bool(user, "Sharable"),
                    Authority = NvrClient.Strings(user["AuthorityList"])
                });
            }
        }
        catch (Exception ex)
        {
            Missing.Add("пользователи — " + ex.Message);
        }
    }

    void ReadLogs(NvrClient nvr)
    {
        try
        {
            var end = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var node in nvr.Logs("2020-01-01 00:00:00", end))
            {
                if (node is not JsonObject row)
                    continue;
                Logs.Add(new LogRow
                {
                    Time = NvrClient.Str(row, "Time"),
                    Who = NvrClient.Str(row, "User"),
                    Event = NvrClient.Str(row, "Type"),
                    Data = NvrClient.Str(row, "Data"),
                    Source = "регистратор"
                });
            }
        }
        catch (Exception ex)
        {
            Missing.Add("журнал — " + ex.Message);
        }
    }

    void ReadCameras()
    {
        var remote = Configs.GetValueOrDefault("NetWork.RemoteDeviceV3") as JsonArray ?? [];
        var widgets = Configs.GetValueOrDefault("AVEnc.VideoWidget") as JsonArray ?? [];
        var rows = new List<CameraRow>();
        for (var i = 0; i < remote.Count; i++)
        {
            var title = "";
            if (i < widgets.Count && widgets[i]?["ChannelTitle"] is JsonObject channel)
                title = NvrClient.Str(channel, "Name");
            rows.Add(CfgStore.ReadCamera(i + 1, remote[i] as JsonObject, title));
        }
        Cameras = rows;
    }

    void ReadNetwork()
    {
        var common = Configs.GetValueOrDefault("NetWork.NetCommon") as JsonObject;
        var dns = Configs.GetValueOrDefault("NetWork.NetDNS") as JsonObject;
        var ntp = Configs.GetValueOrDefault("NetWork.NetNTP") as JsonObject;
        Network = new NetInfo
        {
            Ip = CfgStore.IpText(NvrClient.Str(common, "HostIP")),
            Mask = CfgStore.IpText(NvrClient.Str(common, "Submask")),
            Gateway = CfgStore.IpText(NvrClient.Str(common, "GateWay")),
            Dns = CfgStore.IpText(NvrClient.Str(dns, "Address")),
            Dns2 = CfgStore.IpText(NvrClient.Str(dns, "SpareAddress")),
            Mac = NvrClient.Str(common, "MAC"),
            Http = NvrClient.Int(common, "HttpPort"),
            Tcp = NvrClient.Int(common, "TCPPort"),
            Udp = NvrClient.Int(common, "UDPPort"),
            Ntp = NvrClient.Bool(ntp, "Enable"),
            TimeZone = NvrClient.Int(ntp, "TimeZone")
        };
    }

    void ReadRecords()
    {
        var list = Configs.GetValueOrDefault("Record") as JsonArray ?? [];
        var rows = new List<RecordRow>();
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i] as JsonObject;
            rows.Add(new RecordRow
            {
                Channel = i + 1,
                Mode = NvrClient.Str(item, "RecordMode"),
                Mask = Plain(item?["Mask"]?[0]?[0]),
                Period = Plain(item?["TimeSection"]?[0]?[0])
            });
        }
        Records = rows;
    }

    void ReadMotion()
    {
        var motion = Configs.GetValueOrDefault("Detect.MotionDetect") as JsonArray ?? [];
        var loss = Configs.GetValueOrDefault("Detect.LossDetect") as JsonArray ?? [];
        var blind = Configs.GetValueOrDefault("Detect.BlindDetect") as JsonArray ?? [];
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
                Motion = NvrClient.Bool(m, "Enable"),
                Level = NvrClient.Int(m, "Level"),
                Loss = NvrClient.Bool(l, "Enable"),
                Blind = NvrClient.Bool(b, "Enable")
            });
        }
        Motion = rows;
    }

    public string Overview(string host)
    {
        var builder = new StringBuilder();
        builder.AppendLine(host);
        builder.AppendLine("Модель: " + NvrClient.Str(Info, "HardWare"));
        builder.AppendLine("Прошивка: " + NvrClient.Str(Info, "SoftWareVersion"));
        builder.AppendLine("Сборка: " + NvrClient.Str(Info, "BuildTime"));
        builder.AppendLine("Серийный: " + NvrClient.Str(Info, "SerialNo"));
        builder.AppendLine("Каналов: " + NvrClient.Str(Info, "DigChannel"));
        builder.AppendLine("Тревожных входов: " + NvrClient.Str(Info, "AlarmInChannel") + ", выходов: " + NvrClient.Str(Info, "AlarmOutChannel"));
        var runtime = NvrClient.Str(Info, "DeviceRunTime");
        if (runtime.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(runtime[2..], System.Globalization.NumberStyles.HexNumber, null, out var seconds))
            builder.AppendLine("Работает: " + TimeSpan.FromSeconds(seconds).ToString(@"d\.hh\:mm\:ss"));
        var net = Network;
        builder.AppendLine();
        builder.AppendLine("IP " + net.Ip + "  маска " + net.Mask + "  шлюз " + net.Gateway);
        builder.AppendLine("DNS " + net.Dns + " / " + net.Dns2 + "  MAC " + net.Mac);
        builder.AppendLine("TCP " + net.Tcp + "  UDP " + net.Udp + "  HTTP " + net.Http);
        builder.AppendLine("NTP " + (net.Ntp ? "включён, пояс " + net.TimeZone : "выключен"));
        builder.AppendLine("Камер с IP: " + Cameras.Count(row => row.Ip.Length > 0) + " из " + Cameras.Count);
        builder.AppendLine("Пользователи, которых аппарат отдаёт: " + (Users.Count == 0 ? "нет" : string.Join(", ", Users.Select(user => user.Name))));
        builder.AppendLine("Системные admin и default в этом списке не приходят, но при переносе их можно обновить отдельно.");
        if (Missing.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Не прочитано:");
            foreach (var item in Missing)
                builder.AppendLine("  " + item);
        }
        return builder.ToString();
    }

    public static string Outline(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node, 0);
        var text = builder.ToString();
        return text.Length > 20000 ? text[..20000] + "\r\n… дальше обрезано, раздел слишком большой" : text;
    }

    static void Write(StringBuilder builder, JsonNode? node, int depth)
    {
        if (builder.Length > 20000)
            return;
        var pad = new string(' ', depth * 2);
        switch (node)
        {
            case null:
                builder.Append(pad).AppendLine("пусто");
                break;
            case JsonValue value:
                builder.AppendLine(Plain(value));
                break;
            case JsonArray array:
                builder.AppendLine(array.Count + " записей");
                var limit = Math.Min(array.Count, 40);
                for (var i = 0; i < limit; i++)
                {
                    if (array[i] is JsonObject obj)
                    {
                        builder.Append(pad).Append(i + 1).Append(". ");
                        builder.AppendLine(Scalars(obj));
                    }
                    else
                    {
                        builder.Append(pad).Append(i + 1).Append(". ").AppendLine(Plain(array[i]));
                    }
                }
                if (array.Count > limit)
                    builder.AppendLine(pad + "… ещё " + (array.Count - limit));
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is JsonValue or null)
                        builder.Append(pad).Append(key).Append(": ").AppendLine(Plain(value));
                    else if (value is JsonArray list)
                        builder.Append(pad).Append(key).Append(": список, ").Append(list.Count).AppendLine(" шт.");
                    else if (value is JsonObject child && depth < 2)
                    {
                        builder.Append(pad).Append(key).AppendLine(":");
                        Write(builder, child, depth + 1);
                    }
                    else
                        builder.Append(pad).Append(key).AppendLine(": {…}");
                }
                break;
        }
    }

    static string Scalars(JsonObject obj)
    {
        var parts = new List<string>();
        foreach (var (key, value) in obj)
        {
            if (value is JsonValue)
                parts.Add(key + "=" + Plain(value));
            else if (value is JsonArray list)
                parts.Add(key + "[" + list.Count + "]");
        }
        var text = string.Join(", ", parts);
        return text.Length > 360 ? text[..360] + "…" : text;
    }

    static string Plain(JsonNode? node)
    {
        if (node == null)
            return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return CfgStore.IpText(text ?? "");
        var raw = node.ToString();
        return raw.Length > 180 ? raw[..180] + "…" : raw;
    }
}
