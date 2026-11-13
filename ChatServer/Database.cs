using ChatShared;
using Microsoft.Data.SqlClient;

namespace ChatServer;

public sealed record UserRecord(
    int Id,
    string Username,
    string PasswordHash,
    Role Role,
    bool IsBanned,
    DateTime? MutedUntilUtc);

public sealed record HistoryLine(DateTime SentAtLocal, string Username, string Content);

/// <summary>
/// Toàn bộ câu lệnh SQL của server nằm ở đây (SQL Server, thư viện Microsoft.Data.SqlClient).
/// Luôn dùng tham số (@name), KHÔNG nối chuỗi vào câu SQL để tránh SQL injection.
/// Mọi mốc thời gian trong database là giờ UTC.
/// </summary>
public sealed class Database
{
    // Mã lỗi SQL Server khi vi phạm ràng buộc duy nhất (UNIQUE / PRIMARY KEY).
    private const int UniqueViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly string _connectionString;

    public Database(string connectionString) => _connectionString = connectionString;

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static bool IsDuplicate(SqlException ex)
        => ex.Number == UniqueViolation || ex.Number == UniqueIndexViolation;

    // ---------- Khởi tạo ----------

    /// <summary>Tạo tài khoản admin mặc định và phòng General nếu chưa có.</summary>
    public async Task EnsureSeedAsync()
    {
        await using var conn = await OpenAsync();

        long adminCount;
        await using (var cmd = new SqlCommand("SELECT COUNT(*) FROM users WHERE [role] = 'admin'", conn))
        {
            adminCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        if (adminCount == 0)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(ServerConfig.DefaultAdminPassword);
            await using var cmd = new SqlCommand(
                "IF NOT EXISTS (SELECT 1 FROM users WHERE username = @u) " +
                "INSERT INTO users (username, password_hash, [role], created_at) VALUES (@u, @h, 'admin', @t)", conn);
            cmd.Parameters.AddWithValue("@u", ServerConfig.DefaultAdminUser);
            cmd.Parameters.AddWithValue("@h", hash);
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync();
            Log.Warn($"Đã tạo tài khoản admin mặc định: {ServerConfig.DefaultAdminUser} / " +
                     $"{ServerConfig.DefaultAdminPassword}. Hãy đổi mật khẩu trước khi demo.");
        }

        await using (var cmd = new SqlCommand(
            "IF NOT EXISTS (SELECT 1 FROM rooms WHERE name = @n) " +
            "INSERT INTO rooms (name, created_by, created_at) VALUES (@n, NULL, @t)", conn))
        {
            cmd.Parameters.AddWithValue("@n", ServerConfig.DefaultRoom);
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---------- Người dùng ----------

    /// <summary>Trả về false nếu tên đăng nhập đã tồn tại.</summary>
    public async Task<bool> CreateUserAsync(string username, string passwordHash)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "INSERT INTO users (username, password_hash, [role], created_at) VALUES (@u, @h, 'user', @t)", conn);
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@h", passwordHash);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return false;
        }
    }

    public async Task<UserRecord?> GetUserAsync(string username)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT id, username, password_hash, [role], is_banned, muted_until FROM users WHERE username = @u", conn);
        cmd.Parameters.AddWithValue("@u", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        // SqlDataReader chỉ đọc theo số thứ tự cột, đúng theo thứ tự trong câu SELECT ở trên.
        RoleExtensions.TryParseRole(reader.GetString(3), out var role);
        DateTime? muted = reader.IsDBNull(5)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc);

        return new UserRecord(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            role,
            reader.GetBoolean(4),
            muted);
    }

    public Task SetRoleAsync(int userId, Role role)
        => ExecuteAsync("UPDATE users SET [role] = @v WHERE id = @id", ("@v", role.ToWire()), ("@id", userId));

    public Task SetBannedAsync(int userId, bool banned)
        => ExecuteAsync("UPDATE users SET is_banned = @v WHERE id = @id", ("@v", banned), ("@id", userId));

    /// <summary>Truyền null để bỏ mute.</summary>
    public Task SetMutedUntilAsync(int userId, DateTime? untilUtc)
        => ExecuteAsync("UPDATE users SET muted_until = @v WHERE id = @id",
            ("@v", (object?)untilUtc ?? DBNull.Value), ("@id", userId));

    // ---------- Phòng ----------

    public async Task<List<string>> GetRoomNamesAsync()
    {
        var result = new List<string>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT name FROM rooms ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Trả về false nếu phòng đã tồn tại.</summary>
    public async Task<bool> CreateRoomAsync(string name, int createdByUserId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "INSERT INTO rooms (name, created_by, created_at) VALUES (@n, @u, @t)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@u", createdByUserId);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (SqlException ex) when (IsDuplicate(ex))
        {
            return false;
        }
    }

    /// <summary>Xóa phòng; tin nhắn của phòng bị xóa theo (ON DELETE CASCADE).</summary>
    public Task DeleteRoomAsync(string name)
        => ExecuteAsync("DELETE FROM rooms WHERE name = @n", ("@n", name));

    // ---------- Tin nhắn ----------

    public Task AddMessageAsync(string roomName, int userId, string content)
        => ExecuteAsync(
            "INSERT INTO messages (room_id, user_id, content, sent_at) " +
            "SELECT id, @u, @c, @t FROM rooms WHERE name = @r",
            ("@u", userId), ("@c", content), ("@t", DateTime.UtcNow), ("@r", roomName));

    /// <summary>Lấy tối đa count tin gần nhất của phòng, xếp từ cũ đến mới.</summary>
    public async Task<List<HistoryLine>> GetHistoryAsync(string roomName, int count)
    {
        var result = new List<HistoryLine>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP (@n) m.sent_at, u.username, m.content FROM messages m " +
            "JOIN rooms r ON r.id = m.room_id " +
            "JOIN users u ON u.id = m.user_id " +
            "WHERE r.name = @r ORDER BY m.id DESC", conn);
        cmd.Parameters.AddWithValue("@r", roomName);
        cmd.Parameters.AddWithValue("@n", count);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var utc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
            result.Add(new HistoryLine(utc.ToLocalTime(), reader.GetString(1), reader.GetString(2)));
        }
        result.Reverse();
        return result;
    }

    // ---------- Tiện ích ----------

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
