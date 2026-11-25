namespace XMInstaller;

static class Sofia
{
    const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    static readonly Dictionary<string, string> Known = Build();

    public static string Hash(string password)
    {
        var md5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = Alphabet[(md5[i * 2] + md5[i * 2 + 1]) % 62];
        return new string(chars);
    }

    public static string Hint(string hash) => Known.TryGetValue(hash, out var text) ? text : "";

    public static string Show(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return "пустой пароль";
        var hint = Hint(hash);
        return string.IsNullOrEmpty(hint) ? "неизвестен" : hint;
    }

    static Dictionary<string, string> Build()
    {
        string[] words =
        [
            "", "tluafed", "admin", "123456", "12345", "888888", "666666", "000000", "111111",
            "1234", "12345678", "123456789", "password", "admin123", "88888888", "54321",
            "123123", "112233", "654321", "abc123", "admin1", "pass", "pass123", "qwerty",
            "1q2w3e", "1qaz2wsx", "camera", "ipc123", "tlJwpbo6"
        ];
        var map = new Dictionary<string, string>();
        foreach (var word in words)
        {
            var hash = Hash(word);
            map.TryAdd(hash, word.Length == 0 ? "пустой пароль" : word);
        }
        return map;
    }
}
