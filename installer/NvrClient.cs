using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XMInstaller;

sealed class NvrException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

sealed class NvrClient : IDisposable
{
    static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly HashSet<int> Ok = [100, 150, 515];

    public static readonly Dictionary<int, string> Codes = new()
    {
        [100] = "выполнено",
        [101] = "ошибка аппарата",
        [105] = "нет входа",
        [106] = "неверный логин или пароль",
        [107] = "нет права",
        [203] = "пароль не подошёл",
        [204] = "пользователя нет",
        [205] = "пользователь уже есть",
        [209] = "пользователь уже есть",
        [607] = "раздела нет на этой модели",
        [603] = "нужна перезагрузка"
    };

    readonly object _lock = new();
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    uint _session;
    uint _seq;
    Thread? _alive;
    volatile bool _closed;

    public string Host { get; }
    public int Port { get; }
    public string Username { get; }
    public JsonObject Login { get; private set; } = [];

    NvrClient(string host, int port, string username, TcpClient tcp)
    {
        Host = host;
        Port = port;
        Username = username;
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static NvrClient Connect(string host, int port, string username, string password, int timeoutMs = 8000)
    {
        var tcp = new TcpClient();
        try
        {
            var task = tcp.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs) || !tcp.Connected)
                throw new NvrException("нет ответа по порту " + port);
            tcp.ReceiveTimeout = 20000;
            tcp.SendTimeout = 8000;
            var client = new NvrClient(host, port, username, tcp);
            var reply = client.Request(1000, new JsonObject
            {
                ["EncryptType"] = "MD5",
                ["LoginType"] = "DVRIP-Web",
                ["PassWord"] = Sofia.Hash(password),
                ["UserName"] = username
            });
            var code = CodeOf(reply);
            if (!Ok.Contains(code))
                throw new NvrException(Text(code, "вход не выполнен"), code);
            var sid = Str(reply, "SessionID");
            if (sid.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                client._session = Convert.ToUInt32(sid, 16);
            client.Login = reply;
            var alive = Int(reply, "AliveInterval");
            var pause = Math.Max(5, (alive == 0 ? 20 : alive) - 2);
            client._alive = new Thread(() => client.KeepAlive(pause)) { IsBackground = true, Name = "nvr-alive" };
            client._alive.Start();
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { _stream.Dispose(); } catch { /* сокет уже закрыт */ }
        _tcp.Dispose();
    }

    public JsonObject Request(int code, JsonObject payload)
    {
        var body = Encoding.UTF8.GetBytes(Write(payload) + "\n\0");
        var header = new byte[20];
        header[0] = 255;
        lock (_lock)
        {
            BitConverter.GetBytes(_session).CopyTo(header, 4);
            BitConverter.GetBytes(_seq).CopyTo(header, 8);
            BitConverter.GetBytes((ushort)code).CopyTo(header, 14);
            BitConverter.GetBytes(body.Length).CopyTo(header, 16);
            _stream.Write(header, 0, header.Length);
            _stream.Write(body, 0, body.Length);
            _seq++;
            var raw = ReadExact(20);
            var session = BitConverter.ToUInt32(raw, 4);
            var length = BitConverter.ToInt32(raw, 16);
            if (session != 0)
                _session = session;
            if (length < 0 || length > 8_000_000)
                throw new NvrException("непонятный ответ аппарата");
            var data = length == 0 ? [] : ReadExact(length);
            if (data.Length >= 2 && data[^2] == 0x0A && data[^1] == 0x00)
                data = data[..^2];
            else
            {
                var end = data.Length;
                while (end > 0 && data[end - 1] == 0)
                    end--;
                if (end != data.Length)
                    data = data[..end];
            }
            if (data.Length == 0)
                return new JsonObject { ["Ret"] = 100 };
            var text = Encoding.UTF8.GetString(data);
            try
            {
                return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["Ret"] = 100 };
            }
            catch (JsonException)
            {
                return new JsonObject { ["Ret"] = 100, ["raw"] = text };
            }
        }
    }

    public JsonNode? TryConfig(string name)
    {
        var reply = Request(1042, new JsonObject { ["Name"] = name, ["SessionID"] = Sid() });
        var code = CodeOf(reply);
        if (code is 607 or 101)
            return null;
        if (!Ok.Contains(code))
            throw new NvrException(name + ": " + Text(code, "не прочитано"), code);
        return reply[name]?.DeepClone();
    }

    public void SetConfig(string name, JsonNode value)
    {
        var reply = Request(1040, new JsonObject
        {
            ["Name"] = name,
            ["SessionID"] = Sid(),
            [name] = value.DeepClone()
        });
        var code = CodeOf(reply);
        if (!Ok.Contains(code))
            throw new NvrException(name + ": " + Text(code, "не записано"), code);
    }

    public JsonObject SystemInfo()
    {
        var reply = Request(1020, new JsonObject { ["Name"] = "SystemInfo", ["SessionID"] = Sid() });
        Ensure(reply, "SystemInfo");
        return reply["SystemInfo"] as JsonObject ?? reply;
    }

    public JsonObject? SystemFunction()
    {
        var reply = Request(1360, new JsonObject { ["Name"] = "SystemFunction", ["SessionID"] = Sid() });
        if (!Ok.Contains(CodeOf(reply)))
            return null;
        return reply["SystemFunction"] as JsonObject;
    }

    public List<string> Authority()
    {
        var reply = Request(1470, new JsonObject { ["Name"] = "AuthorityList", ["SessionID"] = Sid() });
        Ensure(reply, "права");
        return Strings(reply["AuthorityList"]);
    }

    public JsonArray Groups()
    {
        var reply = Request(1474, new JsonObject { ["Name"] = "Groups", ["SessionID"] = Sid() });
        Ensure(reply, "группы");
        return reply["Groups"] as JsonArray ?? [];
    }

    public JsonArray Users()
    {
        var reply = Request(1472, new JsonObject { ["Name"] = "Users", ["SessionID"] = Sid() });
        Ensure(reply, "пользователи");
        return reply["Users"] as JsonArray ?? [];
    }

    public JsonObject AddUser(string name, string passwordHash, string group, IReadOnlyList<string> rights, string memo, bool sharable, bool reserved)
    {
        return Request(1482, new JsonObject
        {
            ["Name"] = "User",
            ["SessionID"] = Sid(),
            ["User"] = UserBody(name, passwordHash, group, rights, memo, sharable, reserved)
        });
    }

    public JsonObject ModifyUser(string name, string group, IReadOnlyList<string> rights, string memo, bool sharable, bool reserved)
    {
        return Request(1484, new JsonObject
        {
            ["SessionID"] = Sid(),
            ["UserName"] = name,
            ["User"] = UserBody(name, "", group, rights, memo, sharable, reserved)
        });
    }

    public JsonObject ChangePassword(string name, string newHash, string oldHash)
    {
        return Request(1488, new JsonObject
        {
            ["EncryptType"] = "MD5",
            ["NewPassWord"] = newHash,
            ["PassWord"] = oldHash ?? "",
            ["SessionID"] = Sid(),
            ["UserName"] = name
        });
    }

    public JsonObject DeleteUser(string name)
    {
        return Request(1486, new JsonObject { ["Name"] = name, ["SessionID"] = Sid() });
    }

    public JsonArray Logs(string begin, string end)
    {
        var reply = Request(1442, new JsonObject
        {
            ["Name"] = "OPLogQuery",
            ["SessionID"] = Sid(),
            ["OPLogQuery"] = new JsonObject
            {
                ["BeginTime"] = begin,
                ["EndTime"] = end,
                ["Type"] = "LogAll"
            }
        });
        return reply["OPLogQuery"] as JsonArray ?? [];
    }

    public static string Text(int code, string fallback) => Codes.TryGetValue(code, out var text) ? text : fallback + " (" + code + ")";

    public static int CodeOf(JsonObject reply) => Int(reply, "Ret");

    void Ensure(JsonObject reply, string action)
    {
        var code = CodeOf(reply);
        if (!Ok.Contains(code))
            throw new NvrException(action + ": " + Text(code, "отказ"), code);
    }

    void KeepAlive(int pauseSeconds)
    {
        while (!_closed)
        {
            Thread.Sleep(TimeSpan.FromSeconds(pauseSeconds));
            if (_closed)
                return;
            try
            {
                Request(1006, new JsonObject { ["Name"] = "KeepAlive", ["SessionID"] = Sid() });
            }
            catch
            {
                return;
            }
        }
    }

    string Sid() => "0x" + _session.ToString("X8");

    byte[] ReadExact(int size)
    {
        var buffer = new byte[size];
        var got = 0;
        while (got < size)
        {
            var read = _stream.Read(buffer, got, size - got);
            if (read == 0)
                throw new NvrException("аппарат закрыл соединение");
            got += read;
        }
        return buffer;
    }

    static JsonObject UserBody(string name, string password, string group, IReadOnlyList<string> rights, string memo, bool sharable, bool reserved)
    {
        var list = new JsonArray();
        foreach (var right in rights)
            list.Add(right);
        return new JsonObject
        {
            ["AuthorityList"] = list,
            ["Group"] = group,
            ["Memo"] = memo ?? "",
            ["Name"] = name,
            ["Password"] = password ?? "",
            ["Reserved"] = reserved,
            ["Sharable"] = sharable
        };
    }

    static string Write(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonValue value:
                builder.Append(value.ToJsonString(JsonOpts));
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    Write(builder, array[i]);
                }
                builder.Append(']');
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                        builder.Append(", ");
                    first = false;
                    builder.Append(JsonValue.Create(key)!.ToJsonString(JsonOpts));
                    builder.Append(": ");
                    Write(builder, value);
                }
                builder.Append('}');
                break;
        }
    }

    public static string Str(JsonObject? obj, string key)
    {
        if (obj == null || obj[key] is not JsonValue value)
            return "";
        if (value.TryGetValue<string>(out var text))
            return text ?? "";
        return value.ToString();
    }

    public static int Int(JsonObject? obj, string key)
    {
        if (obj == null || obj[key] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out number))
            return number;
        return 0;
    }

    public static bool Bool(JsonObject? obj, string key)
    {
        if (obj == null || obj[key] is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        return false;
    }

    public static List<string> Strings(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray array)
            return list;
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text != null)
                list.Add(text);
        }
        return list;
    }
}
