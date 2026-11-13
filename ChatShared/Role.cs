namespace ChatShared;

/// <summary>
/// Vai trò của người dùng. Giá trị số càng lớn thì cấp càng cao,
/// nên có thể so sánh trực tiếp: Role.Admin > Role.Moderator.
/// </summary>
public enum Role
{
    User = 0,
    Moderator = 1,
    Admin = 2
}

public static class RoleExtensions
{
    /// <summary>Tên vai trò khi truyền trên đường mạng và lưu trong MySQL: user, moderator, admin.</summary>
    public static string ToWire(this Role role) => role.ToString().ToLowerInvariant();

    /// <summary>Đọc tên vai trò (không phân biệt hoa thường). Sai tên thì trả về false.</summary>
    public static bool TryParseRole(string? text, out Role role)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "user":
                role = Role.User;
                return true;
            case "moderator":
                role = Role.Moderator;
                return true;
            case "admin":
                role = Role.Admin;
                return true;
            default:
                role = Role.User;
                return false;
        }
    }
}
