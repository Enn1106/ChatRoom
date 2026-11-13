namespace ChatServer;

/// <summary>
/// Cấu hình server. Sửa giá trị mặc định ở đây, hoặc đặt biến môi trường:
///   CHAT_PORT  cổng lắng nghe (mặc định 5000)
///   CHAT_DB    chuỗi kết nối SQL Server
/// </summary>
public sealed class ServerConfig
{
    public int Port { get; init; } = 5000;

    /// <summary>
    /// Mặc định: SQL Server Express cài trên máy, đăng nhập bằng tài khoản Windows.
    /// Tên instance xem trong SSMS khi kết nối. Một số dạng hay gặp:
    ///   localhost\SQLEXPRESS            (SQL Server Express)
    ///   (localdb)\MSSQLLocalDB          (LocalDB đi kèm Visual Studio)
    ///   localhost                       (bản đầy đủ, instance mặc định)
    /// Dùng tài khoản sa thay cho Windows: bỏ Integrated Security, thêm User Id=sa;Password=...;
    /// </summary>
    public string ConnectionString { get; init; } =
        @"Server=DESKTOP-KBVODGI\SQLEXPRESS;Database=chatroom;Integrated Security=True;TrustServerCertificate=True;";

    /// <summary>Quét client chết mỗi bao nhiêu giây.</summary>
    public int HeartbeatCheckSeconds { get; init; } = 5;

    /// <summary>Quá bấy nhiêu giây không nhận được dòng nào từ client thì ngắt kết nối.</summary>
    public int HeartbeatTimeoutSeconds { get; init; } = 45;

    public const string DefaultRoom = "General";
    public const string DefaultAdminUser = "admin";
    public const string DefaultAdminPassword = "admin123";

    public static ServerConfig Load()
    {
        var defaults = new ServerConfig();
        var port = defaults.Port;
        if (int.TryParse(Environment.GetEnvironmentVariable("CHAT_PORT"), out var p) && p is > 0 and < 65536)
            port = p;

        var cs = Environment.GetEnvironmentVariable("CHAT_DB");
        return new ServerConfig
        {
            Port = port,
            ConnectionString = string.IsNullOrWhiteSpace(cs) ? defaults.ConnectionString : cs
        };
    }
}
