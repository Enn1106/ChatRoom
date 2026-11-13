using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ChatShared;

namespace ChatClientWpf;

/// <summary>
/// Cửa sổ chat chính. Mọi tin từ server đến trên LUỒNG NỀN (ChatConnection), nên OnMessage
/// chuyển vào luồng giao diện bằng Dispatcher.Invoke trước khi đụng tới control hoặc collection.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ChatConnection _conn;
    private readonly string _me;
    private readonly Role _role;

    private readonly ObservableCollection<ChatLine> _messages = new();
    private readonly ObservableCollection<UserItem> _users = new();
    private readonly ObservableCollection<string> _rooms = new();

    private string? _currentRoom = ServerConfigDefaults.DefaultRoom; // server tự cho vào General khi đăng nhập
    private bool _suppressRoomChange;
    private bool _leaving;
    private int _historyCount;

    public MainWindow(ChatConnection conn, string username, Role role)
    {
        InitializeComponent();

        _conn = conn;
        _me = username;
        _role = role;

        Title = $"Chat Room - {_me} ({_role.ToWire()})";
        MessageList.ItemsSource = _messages;
        UserList.ItemsSource = _users;
        RoomList.ItemsSource = _rooms;
        UpdateHeader();
        ApplyRolePermissions();

        // Đăng ký nhận tin NGAY trong constructor (xem ghi chú ở LoginWindow.OpenMainWindow).
        _conn.MessageReceived += OnMessage;
        _conn.Disconnected += OnDisconnected;

        // Đã ở trong General (server tự vào), xin lịch sử.
        _ = SendSafeAsync(Cmd.History, _currentRoom!, "50");
    }

    /// <summary>Ẩn các chức năng quản trị mà vai trò hiện tại không có. Đây chỉ là cho gọn giao diện,
    /// server mới là nơi kiểm tra quyền thật sự.</summary>
    private void ApplyRolePermissions()
    {
        var isMod = _role >= Role.Moderator;
        var isAdmin = _role >= Role.Admin;

        CreateRoomButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        DeleteRoomButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        if (!isMod)
        {
            UserList.ContextMenu = null;
            return;
        }

        var adminOnly = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        AdminSeparator.Visibility = adminOnly;
        BanItem.Visibility = adminOnly;
        UnbanItem.Visibility = adminOnly;
        PromoteItem.Visibility = adminOnly;
        DemoteItem.Visibility = adminOnly;
    }

    // ---------- Nhận tin từ server ----------

    private void OnMessage(string cmd, string[] a)
    {
        Dispatcher.Invoke(() => HandleMessage(cmd, a));
    }

    private void OnDisconnected(string reason)
    {
        Dispatcher.Invoke(() => ReturnToLogin($"Mất kết nối: {reason}"));
    }

    private void HandleMessage(string cmd, string[] a)
    {
        switch (cmd)
        {
            case Cmd.Msg when a.Length >= 4:
                // MSG|room|user|giờ|text
                if (IsCurrent(a[0]))
                    AddLine(new ChatLine(IsMe(a[1]) ? LineKind.Mine : LineKind.Other, a[3], a[1], a[2]));
                break;

            case Cmd.Hist when a.Length >= 4:
                // HIST|room|giờ|user|text
                if (IsCurrent(a[0]))
                {
                    _historyCount++;
                    AddLine(new ChatLine(IsMe(a[2]) ? LineKind.Mine : LineKind.Other, a[3], a[2], a[1]));
                }
                break;

            case Cmd.HistEnd:
                if (_historyCount > 0)
                    AddLine(new ChatLine(LineKind.System, "Đã tải lịch sử"));
                _historyCount = 0;
                break;

            case Cmd.Sys when a.Length >= 2:
                if (IsCurrent(a[0])) AddLine(new ChatLine(LineKind.System, a[1]));
                break;

            case Cmd.Notice when a.Length >= 1:
                AddLine(new ChatLine(LineKind.System, a[0]));
                break;

            case Cmd.Rooms when a.Length >= 1:
                UpdateRooms(a[0]);
                break;

            case Cmd.Users when a.Length >= 2:
                if (IsCurrent(a[0])) UpdateUsers(a[1]);
                break;

            case Cmd.Left when a.Length >= 1:
                // Phòng đang ở bị xóa: quay về General.
                if (IsCurrent(a[0]))
                {
                    AddLine(new ChatLine(LineKind.System, a.Length >= 2 ? a[1] : "Bạn đã rời phòng"));
                    _currentRoom = null; // đã bị server đưa ra khỏi phòng nên không cần gửi LEAVE
                    SelectRoom(ServerConfigDefaults.DefaultRoom);
                }
                break;

            case Cmd.Err when a.Length >= 2:
                AddLine(new ChatLine(LineKind.Error, $"Lỗi {a[0]}: {a[1]}"));
                break;

            case Cmd.Ok when a.Length >= 1:
                // Chỉ báo kết quả cho các lệnh quản trị.
                if (a[0] is Cmd.Kick or Cmd.Mute or Cmd.Unmute or Cmd.Ban or Cmd.Unban
                    or Cmd.SetRole or Cmd.CreateRoom or Cmd.DelRoom)
                {
                    var target = a.Length >= 2 ? a[1] : "";
                    AddLine(new ChatLine(LineKind.System, $"Đã thực hiện {a[0]} {target}".Trim()));
                }
                break;

            case Cmd.Kicked:
                ReturnToLogin($"Bạn đã bị đuổi: {(a.Length >= 1 ? a[0] : "")}");
                break;
        }
    }

    private bool IsCurrent(string room) => _currentRoom != null && room.Equals(_currentRoom, StringComparison.OrdinalIgnoreCase);

    private bool IsMe(string user) => user.Equals(_me, StringComparison.OrdinalIgnoreCase);

    private void AddLine(ChatLine line)
    {
        _messages.Add(line);
        // Cuộn xuống cuối sau khi giao diện đã đo xong dòng mới.
        _ = Dispatcher.InvokeAsync(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void UpdateRooms(string csv)
    {
        var names = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        _suppressRoomChange = true; // đổi danh sách không được tính là người dùng chọn phòng
        try
        {
            _rooms.Clear();
            foreach (var n in names) _rooms.Add(n);
            if (_currentRoom != null) RoomList.SelectedItem = _rooms.FirstOrDefault(r => IsCurrent(r));
        }
        finally
        {
            _suppressRoomChange = false;
        }
    }

    private void UpdateUsers(string csv)
    {
        _users.Clear();
        foreach (var item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':');
            RoleExtensions.TryParseRole(parts.Length > 1 ? parts[1] : "user", out var role);
            _users.Add(new UserItem(parts[0], role));
        }
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        RoomTitle.Text = _currentRoom == null ? "" : "# " + _currentRoom;
        RoomSubtitle.Text = $"{_users.Count} người đang online trong phòng";
        OnlineHeader.Text = $"ĐANG ONLINE TRONG PHÒNG ({_users.Count})";
    }

    private void SelectRoom(string room)
    {
        // Gán SelectedItem sẽ kích hoạt RoomList_SelectionChanged, nơi gửi JOIN.
        RoomList.SelectedItem = _rooms.FirstOrDefault(r => r.Equals(room, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Gửi lệnh lên server ----------

    private async Task SendSafeAsync(string command, params string[] args)
    {
        try
        {
            await _conn.SendAsync(command, args);
        }
        catch (Exception ex)
        {
            AddLine(new ChatLine(LineKind.Error, $"Gửi lỗi: {ex.Message}"));
        }
    }

    private async void RoomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRoomChange) return;
        if (RoomList.SelectedItem is not string room) return;
        if (IsCurrent(room)) return;

        var old = _currentRoom;
        _currentRoom = room;
        _messages.Clear();
        _users.Clear();
        _historyCount = 0;
        UpdateHeader();

        // Chuyển phòng = rời phòng cũ, vào phòng mới, xin lịch sử. Server tự gửi lại danh sách người online.
        if (old != null) await SendSafeAsync(Cmd.Leave, old);
        await SendSafeAsync(Cmd.Join, room);
        await SendSafeAsync(Cmd.History, room, "50");
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendInputAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendInputAsync();
        }
    }

    private async Task SendInputAsync()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _currentRoom == null) return;
        InputBox.Clear();
        await SendSafeAsync(Cmd.Msg, _currentRoom, text);
        // Không tự hiện tin của mình: server phát lại cho cả phòng (kể cả người gửi).
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await SendSafeAsync(Cmd.Logout);
        ReturnToLogin("Đã đăng xuất.");
    }

    // ---------- Quản trị ----------

    private string? SelectedUserName() => (UserList.SelectedItem as UserItem)?.Name;

    private async void Kick_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUserName() is { } user) await SendSafeAsync(Cmd.Kick, user, "Vi phạm nội quy");
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUserName() is { } user) await SendSafeAsync(Cmd.Mute, user, "5");
    }

    private async void Unmute_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUserName() is { } user) await SendSafeAsync(Cmd.Unmute, user);
    }

    private async void Ban_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUserName() is not { } user) return;
        var ok = MessageBox.Show(this, $"Khóa vĩnh viễn tài khoản {user}?", "Ban tài khoản",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok == MessageBoxResult.Yes) await SendSafeAsync(Cmd.Ban, user);
    }

    private async void Unban_Click(object sender, RoutedEventArgs e)
    {
        // Người bị ban không còn trong danh sách online nên phải gõ tên.
        var name = InputDialog.Ask(this, "Mở khóa tài khoản", "Nhập tên tài khoản cần mở khóa:", SelectedUserName() ?? "");
        if (name != null) await SendSafeAsync(Cmd.Unban, name);
    }

    private async void Promote_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUserName() is { } user) await SendSafeAsync(Cmd.SetRole, user, "moderator");
    }

    private async void Demote_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUserName() is { } user) await SendSafeAsync(Cmd.SetRole, user, "user");
    }

    private async void CreateRoomButton_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "Tạo phòng", "Tên phòng (chữ, số, gạch dưới, tối đa 32 ký tự):");
        if (name != null) await SendSafeAsync(Cmd.CreateRoom, name);
    }

    private async void DeleteRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRoom == null) return;
        var ok = MessageBox.Show(this, $"Xóa phòng {_currentRoom} cùng toàn bộ tin nhắn?", "Xóa phòng",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok == MessageBoxResult.Yes) await SendSafeAsync(Cmd.DelRoom, _currentRoom);
    }

    /// <summary>Chuột phải cũng chọn luôn dòng được bấm, để menu biết đang thao tác với ai.</summary>
    private void UserList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node != null && node is not ListBoxItem)
        {
            // Run trong TextBlock không phải Visual nên phải đi theo cây logic.
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        if (node is ListBoxItem item) item.IsSelected = true;
    }

    // ---------- Thoát ----------

    private void ReturnToLogin(string message)
    {
        if (_leaving) return;
        _leaving = true;

        _conn.MessageReceived -= OnMessage;
        _conn.Disconnected -= OnDisconnected;
        _conn.Dispose();

        var login = new LoginWindow();
        login.ShowMessage(message);
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_leaving) _conn.Dispose(); // người dùng tắt cửa sổ bằng nút X
        base.OnClosed(e);
    }
}

/// <summary>Tên phòng mặc định phía client (trùng với ServerConfig.DefaultRoom của server).</summary>
internal static class ServerConfigDefaults
{
    public const string DefaultRoom = "General";
}
