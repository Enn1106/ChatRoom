using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using ChatShared;

namespace ChatServer;

/// <summary>
/// Một kết nối client trên server. RunAsync chạy trong task riêng: đọc từng dòng lệnh
/// bằng ReadLineAsync (không chặn luồng) rồi xử lý.
/// </summary>
public sealed class ClientSession
{
    private readonly ChatHub _hub;
    private readonly Database _db;
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _endPoint;
    private long _lastSeenTicks = Environment.TickCount64;
    private int _closed;

    public ClientSession(ChatHub hub, TcpClient tcp)
    {
        _hub = hub;
        _db = hub.Db;
        _tcp = tcp;
        _endPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        var stream = tcp.GetStream();
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };
    }

    public int UserId { get; private set; }
    public string? Username { get; private set; }
    public Role Role { get; set; }
    public DateTime? MutedUntilUtc { get; set; }
    public ConcurrentDictionary<string, byte> JoinedRooms { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoggedIn => Username != null;
    public TimeSpan IdleTime => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastSeenTicks));
    public string Describe() => Username != null ? $"{Username} ({_endPoint})" : _endPoint;

    // ---------- Vòng đọc lệnh ----------

    public async Task RunAsync()
    {
        Log.Info($"Kết nối mới: {_endPoint}");
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null) break; // client đóng kết nối

                Interlocked.Exchange(ref _lastSeenTicks, Environment.TickCount64);
                if (line.Length == 0) continue;
                if (line.Length > Protocol.MaxLineLength)
                {
                    await SendErrorAsync(ErrorCodes.BadFormat, "Dòng lệnh quá dài");
                    continue;
                }

                try
                {
                    await HandleLineAsync(line);
                }
                catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
                {
                    Log.Error($"Lỗi khi xử lý lệnh của {Describe()}: {ex.Message}");
                    await SendErrorAsync(ErrorCodes.ServerError, "Lỗi server, thử lại sau");
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        Close();
        _hub.RemoveConnection(this);

        if (Username != null)
        {
            foreach (var roomName in JoinedRooms.Keys.ToList())
            {
                var room = _hub.GetRoom(roomName);
                if (room != null) await _hub.LeaveRoomAsync(this, room, "vừa rời");
            }
            _hub.RemoveOnline(this);
        }
        Log.Info($"Đã ngắt: {Describe()}");
    }

    /// <summary>Đóng socket (an toàn khi gọi nhiều lần). Vòng đọc sẽ thoát và tự dọn dẹp.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try
        {
            _tcp.Close();
        }
        catch
        {
        }
    }

    // ---------- Gửi ----------

    /// <summary>Gửi một dòng. Lỗi mạng thì đóng kết nối, không ném ngoại lệ.</summary>
    public async Task SendLineAsync(string line)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        // Nhiều task (phát tin từ client khác, heartbeat...) có thể gửi cùng lúc tới socket này, nên phải khóa.
        await _sendLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(line);
        }
        catch
        {
            Close();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task SendAsync(string command, params string[] args) => SendLineAsync(Protocol.Build(command, args));

    private Task SendOkAsync(string command, string info = "") => SendAsync(Cmd.Ok, command, info);

    private Task SendErrorAsync(int code, string message) => SendAsync(Cmd.Err, code.ToString(), message);

    // ---------- Xử lý lệnh ----------

    private async Task HandleLineAsync(string line)
    {
        var (cmd, a) = Protocol.Parse(line);

        if (cmd == Cmd.Ping)
        {
            await SendAsync(Cmd.Pong);
            return;
        }

        if (!IsLoggedIn)
        {
            switch (cmd)
            {
                case Cmd.Register: await HandleRegisterAsync(a); break;
                case Cmd.Login: await HandleLoginAsync(a); break;
                default: await SendErrorAsync(ErrorCodes.NotLoggedIn, "Hãy đăng nhập trước"); break;
            }
            return;
        }

        switch (cmd)
        {
            case Cmd.Logout:
                await SendOkAsync(Cmd.Logout);
                Close();
                break;
            case Cmd.Msg: await HandleMessageAsync(a); break;
            case Cmd.Join: await HandleJoinAsync(a); break;
            case Cmd.Leave: await HandleLeaveAsync(a); break;
            case Cmd.Rooms: await SendAsync(Cmd.Rooms, string.Join(",", _hub.RoomNames())); break;
            case Cmd.Users: await HandleUsersAsync(a); break;
            case Cmd.History: await HandleHistoryAsync(a); break;
            case Cmd.CreateRoom: await HandleCreateRoomAsync(a); break;
            case Cmd.DelRoom: await HandleDeleteRoomAsync(a); break;
            case Cmd.Kick: await HandleKickAsync(a); break;
            case Cmd.Mute: await HandleMuteAsync(a, mute: true); break;
            case Cmd.Unmute: await HandleMuteAsync(a, mute: false); break;
            case Cmd.Ban: await HandleBanAsync(a, ban: true); break;
            case Cmd.Unban: await HandleBanAsync(a, ban: false); break;
            case Cmd.SetRole: await HandleSetRoleAsync(a); break;
            default: await SendErrorAsync(ErrorCodes.BadFormat, $"Lệnh không hợp lệ: {cmd}"); break;
        }
    }

    // --- Đăng ký / đăng nhập ---

    private async Task HandleRegisterAsync(string[] a)
    {
        if (a.Length < 2) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: REGISTER|user|pass"); return; }
        var name = a[0].Trim();
        var pass = a[1];

        if (!IsValidName(name, 3))
        {
            await SendErrorAsync(ErrorCodes.BadFormat, "Tên đăng nhập 3-32 ký tự, chỉ gồm chữ, số, gạch dưới");
            return;
        }
        if (pass.Length < 4)
        {
            await SendErrorAsync(ErrorCodes.BadFormat, "Mật khẩu tối thiểu 4 ký tự");
            return;
        }

        // BCrypt cố tình chạy chậm (tốn CPU), nên đưa sang thread pool để không chặn việc khác.
        var hash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(pass));
        if (await _db.CreateUserAsync(name, hash))
        {
            Log.Info($"Đăng ký tài khoản mới: {name}");
            await SendOkAsync(Cmd.Register);
        }
        else
        {
            await SendErrorAsync(ErrorCodes.UserExists, "Tên đăng nhập đã tồn tại");
        }
    }

    private async Task HandleLoginAsync(string[] a)
    {
        if (a.Length < 2) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: LOGIN|user|pass"); return; }

        var user = await _db.GetUserAsync(a[0].Trim());
        var pass = a[1];
        if (user == null || !await Task.Run(() => BCrypt.Net.BCrypt.Verify(pass, user.PasswordHash)))
        {
            await SendErrorAsync(ErrorCodes.BadCredentials, "Sai tên đăng nhập hoặc mật khẩu");
            return;
        }
        if (user.IsBanned)
        {
            await SendErrorAsync(ErrorCodes.Banned, "Tài khoản đã bị khóa");
            return;
        }

        UserId = user.Id;
        Username = user.Username;
        Role = user.Role;
        MutedUntilUtc = user.MutedUntilUtc;

        if (!_hub.TryAddOnline(this))
        {
            Username = null; // trả về trạng thái chưa đăng nhập
            await SendErrorAsync(ErrorCodes.AlreadyOnline, "Tài khoản đang đăng nhập ở nơi khác");
            return;
        }

        Log.Info($"{Username} đăng nhập (vai trò {Role.ToWire()}) từ {_endPoint}");
        await SendOkAsync(Cmd.Login, Role.ToWire());
        await SendAsync(Cmd.Rooms, string.Join(",", _hub.RoomNames()));

        var general = _hub.GetRoom(ServerConfig.DefaultRoom);
        if (general != null) await _hub.JoinRoomAsync(this, general);
    }

    // --- Chat, phòng ---

    private async Task HandleMessageAsync(string[] a)
    {
        if (a.Length < 2) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: MSG|room|text"); return; }
        var room = _hub.GetRoom(a[0]);
        if (room == null) { await SendErrorAsync(ErrorCodes.RoomNotFound, "Phòng không tồn tại"); return; }
        if (!room.Members.ContainsKey(Username!)) { await SendErrorAsync(ErrorCodes.NotInRoom, "Bạn chưa ở trong phòng này"); return; }

        if (MutedUntilUtc is { } until && until > DateTime.UtcNow)
        {
            var left = (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
            await SendErrorAsync(ErrorCodes.Muted, $"Bạn đang bị mute, còn khoảng {left} phút");
            return;
        }

        var text = a[1].Trim();
        if (text.Length == 0) return;
        if (text.Length > 1000) { await SendErrorAsync(ErrorCodes.BadFormat, "Tin nhắn tối đa 1000 ký tự"); return; }

        await _db.AddMessageAsync(room.Name, UserId, text);
        await _hub.BroadcastAsync(room, Protocol.Build(Cmd.Msg, room.Name, Username!, DateTime.Now.ToString("HH:mm:ss"), text));
    }

    private async Task HandleJoinAsync(string[] a)
    {
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: JOIN|room"); return; }
        var room = _hub.GetRoom(a[0]);
        if (room == null) { await SendErrorAsync(ErrorCodes.RoomNotFound, "Phòng không tồn tại"); return; }
        await SendOkAsync(Cmd.Join, room.Name);
        await _hub.JoinRoomAsync(this, room);
    }

    private async Task HandleLeaveAsync(string[] a)
    {
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: LEAVE|room"); return; }
        var room = _hub.GetRoom(a[0]);
        if (room == null) { await SendErrorAsync(ErrorCodes.RoomNotFound, "Phòng không tồn tại"); return; }
        if (!room.Members.ContainsKey(Username!)) { await SendErrorAsync(ErrorCodes.NotInRoom, "Bạn chưa ở trong phòng này"); return; }
        await SendOkAsync(Cmd.Leave, room.Name);
        await _hub.LeaveRoomAsync(this, room);
    }

    private async Task HandleUsersAsync(string[] a)
    {
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: USERS|room"); return; }
        var room = _hub.GetRoom(a[0]);
        if (room == null) { await SendErrorAsync(ErrorCodes.RoomNotFound, "Phòng không tồn tại"); return; }
        await SendLineAsync(ChatHub.BuildUsersLine(room));
    }

    private async Task HandleHistoryAsync(string[] a)
    {
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: HISTORY|room|n"); return; }
        var room = _hub.GetRoom(a[0]);
        if (room == null) { await SendErrorAsync(ErrorCodes.RoomNotFound, "Phòng không tồn tại"); return; }
        if (!room.Members.ContainsKey(Username!)) { await SendErrorAsync(ErrorCodes.NotInRoom, "Bạn chưa ở trong phòng này"); return; }

        var count = 50;
        if (a.Length >= 2 && int.TryParse(a[1], out var n)) count = Math.Clamp(n, 1, 200);

        foreach (var h in await _db.GetHistoryAsync(room.Name, count))
            await SendAsync(Cmd.Hist, room.Name, h.SentAtLocal.ToString("dd/MM HH:mm:ss"), h.Username, h.Content);
        await SendAsync(Cmd.HistEnd, room.Name);
    }

    private async Task HandleCreateRoomAsync(string[] a)
    {
        if (!await RequireRoleAsync(Role.Admin)) return;
        if (a.Length < 1 || !IsValidName(a[0].Trim(), 1))
        {
            await SendErrorAsync(ErrorCodes.BadFormat, "Tên phòng 1-32 ký tự, chỉ gồm chữ, số, gạch dưới");
            return;
        }

        var name = a[0].Trim();
        if (_hub.GetRoom(name) != null || !await _db.CreateRoomAsync(name, UserId))
        {
            await SendErrorAsync(ErrorCodes.RoomExists, "Phòng đã tồn tại");
            return;
        }

        _hub.TryAddRoom(name);
        Log.Info($"{Username} tạo phòng {name}");
        await SendOkAsync(Cmd.CreateRoom, name);
        await _hub.BroadcastRoomListAsync();
    }

    private async Task HandleDeleteRoomAsync(string[] a)
    {
        if (!await RequireRoleAsync(Role.Admin)) return;
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: DELROOM|room"); return; }

        var room = _hub.GetRoom(a[0]);
        if (room == null) { await SendErrorAsync(ErrorCodes.RoomNotFound, "Phòng không tồn tại"); return; }
        if (room.Name.Equals(ServerConfig.DefaultRoom, StringComparison.OrdinalIgnoreCase))
        {
            await SendErrorAsync(ErrorCodes.NoPermission, "Không thể xóa phòng mặc định");
            return;
        }

        await _db.DeleteRoomAsync(room.Name);
        var members = _hub.RemoveRoom(room.Name);
        Log.Info($"{Username} xóa phòng {room.Name}");

        foreach (var m in members)
            await m.SendLineAsync(Protocol.Build(Cmd.Left, room.Name, "Phòng đã bị xóa"));
        await SendOkAsync(Cmd.DelRoom, room.Name);
        await _hub.BroadcastRoomListAsync();
    }

    // --- Quản trị ---

    private async Task<bool> RequireRoleAsync(Role min)
    {
        if (Role >= min) return true;
        await SendErrorAsync(ErrorCodes.NoPermission, "Bạn không đủ quyền");
        return false;
    }

    /// <summary>
    /// Lấy người bị xử lý từ database (nguồn đúng về vai trò, kể cả khi họ offline) và kiểm tra:
    /// không tự xử mình, không xử người có cấp bằng hoặc cao hơn mình.
    /// </summary>
    private async Task<UserRecord?> GetManageableTargetAsync(string username)
    {
        var target = await _db.GetUserAsync(username.Trim());
        if (target == null)
        {
            await SendErrorAsync(ErrorCodes.UserNotFound, "Không tìm thấy người dùng");
            return null;
        }
        if (target.Id == UserId || target.Role >= Role)
        {
            await SendErrorAsync(ErrorCodes.NoPermission, "Không thể xử lý người có cấp bằng hoặc cao hơn bạn");
            return null;
        }
        return target;
    }

    private async Task HandleKickAsync(string[] a)
    {
        if (!await RequireRoleAsync(Role.Moderator)) return;
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: KICK|user|reason"); return; }

        var target = await GetManageableTargetAsync(a[0]);
        if (target == null) return;

        var session = _hub.FindOnline(target.Username);
        if (session == null) { await SendErrorAsync(ErrorCodes.UserNotFound, "Người này đang không online"); return; }

        var reason = a.Length >= 2 && a[1].Trim().Length > 0 ? a[1].Trim() : "Vi phạm nội quy";
        Log.Info($"{Username} kick {target.Username}: {reason}");
        await _hub.AnnounceInRoomsOfAsync(session, $"{target.Username} bị {Username} đuổi khỏi server");
        await session.SendLineAsync(Protocol.Build(Cmd.Kicked, reason));
        session.Close();
        await SendOkAsync(Cmd.Kick, target.Username);
    }

    private async Task HandleMuteAsync(string[] a, bool mute)
    {
        if (!await RequireRoleAsync(Role.Moderator)) return;
        if (a.Length < 1 || (mute && a.Length < 2))
        {
            await SendErrorAsync(ErrorCodes.BadFormat, mute ? "Dùng: MUTE|user|minutes" : "Dùng: UNMUTE|user");
            return;
        }

        var minutes = 0;
        if (mute && (!int.TryParse(a[1], out minutes) || minutes is < 1 or > 1440))
        {
            await SendErrorAsync(ErrorCodes.BadFormat, "Số phút phải từ 1 đến 1440");
            return;
        }

        var target = await GetManageableTargetAsync(a[0]);
        if (target == null) return;

        DateTime? until = mute ? DateTime.UtcNow.AddMinutes(minutes) : null;
        await _db.SetMutedUntilAsync(target.Id, until);

        var session = _hub.FindOnline(target.Username);
        if (session != null)
        {
            session.MutedUntilUtc = until;
            await session.SendAsync(Cmd.Notice, mute
                ? $"Bạn bị {Username} mute {minutes} phút"
                : $"{Username} đã mở mute cho bạn");
        }
        Log.Info(mute ? $"{Username} mute {target.Username} {minutes} phút" : $"{Username} unmute {target.Username}");
        await SendOkAsync(mute ? Cmd.Mute : Cmd.Unmute, target.Username);
    }

    private async Task HandleBanAsync(string[] a, bool ban)
    {
        if (!await RequireRoleAsync(Role.Admin)) return;
        if (a.Length < 1) { await SendErrorAsync(ErrorCodes.BadFormat, ban ? "Dùng: BAN|user" : "Dùng: UNBAN|user"); return; }

        var target = await GetManageableTargetAsync(a[0]);
        if (target == null) return;

        await _db.SetBannedAsync(target.Id, ban);
        Log.Info(ban ? $"{Username} ban {target.Username}" : $"{Username} unban {target.Username}");

        if (ban)
        {
            var session = _hub.FindOnline(target.Username);
            if (session != null)
            {
                await _hub.AnnounceInRoomsOfAsync(session, $"{target.Username} bị khóa tài khoản");
                await session.SendLineAsync(Protocol.Build(Cmd.Kicked, "Tài khoản của bạn đã bị khóa"));
                session.Close();
            }
        }
        await SendOkAsync(ban ? Cmd.Ban : Cmd.Unban, target.Username);
    }

    private async Task HandleSetRoleAsync(string[] a)
    {
        if (!await RequireRoleAsync(Role.Admin)) return;
        if (a.Length < 2 || !RoleExtensions.TryParseRole(a[1], out var newRole))
        {
            await SendErrorAsync(ErrorCodes.BadFormat, "Dùng: SETROLE|user|user hoặc moderator hoặc admin");
            return;
        }

        var target = await _db.GetUserAsync(a[0].Trim());
        if (target == null) { await SendErrorAsync(ErrorCodes.UserNotFound, "Không tìm thấy người dùng"); return; }
        if (target.Id == UserId) { await SendErrorAsync(ErrorCodes.NoPermission, "Không thể tự đổi vai trò của mình"); return; }

        await _db.SetRoleAsync(target.Id, newRole);
        Log.Info($"{Username} đặt vai trò của {target.Username} thành {newRole.ToWire()}");

        var session = _hub.FindOnline(target.Username);
        if (session != null)
        {
            session.Role = newRole;
            await session.SendAsync(Cmd.Notice, $"Vai trò của bạn giờ là {newRole.ToWire()}");
            foreach (var roomName in session.JoinedRooms.Keys)
            {
                var room = _hub.GetRoom(roomName);
                if (room != null) await _hub.BroadcastUsersAsync(room);
            }
        }
        await SendOkAsync(Cmd.SetRole, target.Username);
    }

    // ---------- Tiện ích ----------

    /// <summary>Tên hợp lệ: từ minLength đến 32 ký tự, chỉ chữ cái, chữ số và gạch dưới.</summary>
    private static bool IsValidName(string name, int minLength)
        => name.Length >= minLength && name.Length <= 32 && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
