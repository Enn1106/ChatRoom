using System.Text;

namespace ChatShared;

/// <summary>Tên các lệnh. Client gửi lên một số lệnh, server gửi xuống một số khác (xem tài liệu mục 5).</summary>
public static class Cmd
{
    // Client -> Server
    public const string Register = "REGISTER";
    public const string Login = "LOGIN";
    public const string Logout = "LOGOUT";
    public const string Ping = "PING";
    public const string Msg = "MSG";
    public const string Join = "JOIN";
    public const string Leave = "LEAVE";
    public const string Rooms = "ROOMS";
    public const string Users = "USERS";
    public const string History = "HISTORY";
    public const string CreateRoom = "CREATEROOM";
    public const string DelRoom = "DELROOM";
    public const string Kick = "KICK";
    public const string Mute = "MUTE";
    public const string Unmute = "UNMUTE";
    public const string Ban = "BAN";
    public const string Unban = "UNBAN";
    public const string SetRole = "SETROLE";

    // Server -> Client (MSG, ROOMS, USERS dùng chung tên với lệnh phía trên)
    public const string Ok = "OK";
    public const string Err = "ERR";
    public const string Sys = "SYS";
    public const string Pong = "PONG";
    public const string Hist = "HIST";
    public const string HistEnd = "HISTEND";
    public const string Left = "LEFT";
    public const string Notice = "NOTICE";
    public const string Kicked = "KICKED";
}

/// <summary>Mã lỗi trong thông điệp ERR|mã|mô tả.</summary>
public static class ErrorCodes
{
    public const int BadFormat = 100;
    public const int NotLoggedIn = 101;
    public const int BadCredentials = 102;
    public const int UserExists = 103;
    public const int AlreadyOnline = 104;
    public const int Banned = 105;
    public const int NoPermission = 200;
    public const int Muted = 201;
    public const int RoomNotFound = 300;
    public const int RoomExists = 301;
    public const int NotInRoom = 302;
    public const int UserNotFound = 400;
    public const int ServerError = 500;
}

/// <summary>
/// Giao thức text theo dòng: LỆNH|trường1|trường2|...
/// Ký tự đặc biệt trong trường được thoát: \ thành \\, | thành \p, xuống dòng thành \n, CR thành \r.
/// </summary>
public static class Protocol
{
    public const char Separator = '|';

    /// <summary>Độ dài tối đa của một dòng; server bỏ qua dòng dài hơn.</summary>
    public const int MaxLineLength = 4096;

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '|': sb.Append("\\p"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i++;
                switch (text[i])
                {
                    case 'p': sb.Append('|'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(text[i]); break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Ghép lệnh và các trường thành một dòng (đã thoát ký tự đặc biệt, chưa có ký tự xuống dòng).</summary>
    public static string Build(string command, params string[] fields)
    {
        var sb = new StringBuilder(command);
        foreach (var field in fields)
        {
            sb.Append(Separator);
            sb.Append(Escape(field));
        }
        return sb.ToString();
    }

    /// <summary>Tách một dòng thành lệnh (viết hoa) và danh sách trường (đã bỏ thoát ký tự).</summary>
    public static (string Command, string[] Args) Parse(string line)
    {
        var parts = line.Split(Separator);
        var args = new string[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
        {
            args[i - 1] = Unescape(parts[i]);
        }
        return (parts[0].Trim().ToUpperInvariant(), args);
    }
}
