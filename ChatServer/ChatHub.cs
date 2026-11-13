using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ChatShared;

namespace ChatServer;

/// <summary>Một phòng chat: tên và tập thành viên đang online.</summary>
public sealed class Room
{
    public Room(string name) => Name = name;

    public string Name { get; }

    /// <summary>Khóa là tên người dùng (không phân biệt hoa thường).</summary>
    public ConcurrentDictionary<string, ClientSession> Members { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Server chat: nhận kết nối bằng TcpListener, mỗi client chạy trong một task riêng (async/await),
/// quản lý phòng, người online và quét heartbeat.
/// </summary>
public sealed class ChatHub
{
    private readonly ConcurrentDictionary<string, ClientSession> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ClientSession, byte> _connections = new();

    public ChatHub(ServerConfig config, Database db)
    {
        Config = config;
        Db = db;
    }

    public ServerConfig Config { get; }
    public Database Db { get; }

    // ---------- Vòng đời ----------

    public async Task RunAsync(CancellationToken ct)
    {
        await Db.EnsureSeedAsync();
        foreach (var name in await Db.GetRoomNamesAsync())
            _rooms.TryAdd(name, new Room(name));
        Log.Info($"Đã nạp {_rooms.Count} phòng từ database.");

        var listener = new TcpListener(IPAddress.Any, Config.Port);
        listener.Start();
        Log.Info($"Server đang lắng nghe ở cổng {Config.Port}. Nhấn Ctrl+C để dừng.");

        _ = HeartbeatLoopAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                tcp.NoDelay = true;
                var session = new ClientSession(this, tcp);
                _connections.TryAdd(session, 0);
                // Mỗi client một task; không await để tiếp tục nhận kết nối mới.
                _ = Task.Run(session.RunAsync, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
            foreach (var s in _connections.Keys) s.Close();
            Log.Info("Server đã dừng.");
        }
    }

    /// <summary>Quét định kỳ và ngắt client im lặng quá lâu (heartbeat).</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Config.HeartbeatCheckSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var limit = TimeSpan.FromSeconds(Config.HeartbeatTimeoutSeconds);
                foreach (var s in _connections.Keys)
                {
                    if (s.IdleTime > limit)
                    {
                        Log.Warn($"Ngắt {s.Describe()}: quá {Config.HeartbeatTimeoutSeconds} giây không có tín hiệu.");
                        s.Close();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void RemoveConnection(ClientSession session) => _connections.TryRemove(session, out _);

    // ---------- Người online ----------

    /// <summary>Trả về false nếu tài khoản này đã đăng nhập ở nơi khác.</summary>
    public bool TryAddOnline(ClientSession session) => _online.TryAdd(session.Username!, session);

    public void RemoveOnline(ClientSession session)
    {
        // Chỉ xóa nếu đúng phiên này đang giữ tên, tránh xóa nhầm phiên khác.
        if (session.Username != null)
            _online.TryRemove(new KeyValuePair<string, ClientSession>(session.Username, session));
    }

    public ClientSession? FindOnline(string username)
        => _online.TryGetValue(username, out var s) ? s : null;

    // ---------- Phòng ----------

    public Room? GetRoom(string name) => _rooms.TryGetValue(name, out var r) ? r : null;

    public string[] RoomNames() => _rooms.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryAddRoom(string name) => _rooms.TryAdd(name, new Room(name));

    /// <summary>Xóa phòng khỏi bộ nhớ và trả về các thành viên đang ở trong phòng đó.</summary>
    public List<ClientSession> RemoveRoom(string name)
    {
        if (!_rooms.TryRemove(name, out var room)) return new List<ClientSession>();
        var members = room.Members.Values.ToList();
        room.Members.Clear();
        foreach (var m in members) m.JoinedRooms.TryRemove(room.Name, out _);
        return members;
    }

    public async Task JoinRoomAsync(ClientSession session, Room room)
    {
        if (!room.Members.TryAdd(session.Username!, session)) return;
        session.JoinedRooms.TryAdd(room.Name, 0);
        await BroadcastAsync(room, Protocol.Build(Cmd.Sys, room.Name, $"{session.Username} vừa vào phòng"));
        await BroadcastUsersAsync(room);
    }

    public async Task LeaveRoomAsync(ClientSession session, Room room, string verb = "vừa rời")
    {
        if (!room.Members.TryRemove(session.Username!, out _)) return;
        session.JoinedRooms.TryRemove(room.Name, out _);
        await BroadcastAsync(room, Protocol.Build(Cmd.Sys, room.Name, $"{session.Username} {verb} phòng"));
        await BroadcastUsersAsync(room);
    }

    /// <summary>Gửi một thông báo SYS vào mọi phòng mà người này đang ở.</summary>
    public async Task AnnounceInRoomsOfAsync(ClientSession session, string text)
    {
        foreach (var roomName in session.JoinedRooms.Keys)
        {
            var room = GetRoom(roomName);
            if (room != null) await BroadcastAsync(room, Protocol.Build(Cmd.Sys, room.Name, text));
        }
    }

    // ---------- Phát tin ----------

    /// <summary>Gửi một dòng cho mọi thành viên của phòng.</summary>
    public Task BroadcastAsync(Room room, string line)
        => Task.WhenAll(room.Members.Values.Select(m => m.SendLineAsync(line)));

    /// <summary>Gửi một dòng cho mọi người đang đăng nhập.</summary>
    public Task BroadcastAllAsync(string line)
        => Task.WhenAll(_online.Values.Select(m => m.SendLineAsync(line)));

    public Task BroadcastRoomListAsync()
        => BroadcastAllAsync(Protocol.Build(Cmd.Rooms, string.Join(",", RoomNames())));

    public Task BroadcastUsersAsync(Room room)
        => BroadcastAsync(room, BuildUsersLine(room));

    public static string BuildUsersLine(Room room)
    {
        var list = string.Join(",",
            room.Members.Values
                .OrderBy(m => m.Username, StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{m.Username}:{m.Role.ToWire()}"));
        return Protocol.Build(Cmd.Users, room.Name, list);
    }
}
