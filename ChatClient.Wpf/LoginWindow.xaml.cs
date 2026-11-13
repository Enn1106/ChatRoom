using System.Windows;
using System.Windows.Media;
using ChatShared;

namespace ChatClientWpf;

public partial class LoginWindow : Window
{
    private ChatConnection? _conn;
    private string _pendingUser = "";

    public LoginWindow()
    {
        InitializeComponent();
    }

    /// <summary>Hiện một thông báo ở cuối cửa sổ (dùng khi bị đuổi, mất kết nối, đăng xuất).</summary>
    public void ShowMessage(string text) => SetStatus(text, isError: true);

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? Palette.Danger : Palette.Muted;
    }

    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        RegisterButton.IsEnabled = !busy;
    }

    /// <summary>Kết nối tới server nếu chưa có kết nối. Trả về false nếu thất bại.</summary>
    private async Task<bool> EnsureConnectedAsync()
    {
        if (_conn != null) return true;

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            SetStatus("Cổng không hợp lệ", isError: true);
            return false;
        }

        var conn = new ChatConnection();
        conn.MessageReceived += OnMessage;
        conn.Disconnected += OnDisconnected;
        try
        {
            SetStatus("Đang kết nối...");
            await conn.ConnectAsync(HostBox.Text.Trim(), port);
        }
        catch (Exception ex)
        {
            conn.Dispose();
            SetStatus($"Không kết nối được: {ex.Message}", isError: true);
            return false;
        }

        _conn = conn;
        return true;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
        => await SubmitAsync(Cmd.Login);

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        => await SubmitAsync(Cmd.Register);

    private async Task SubmitAsync(string command)
    {
        var user = UserBox.Text.Trim();
        if (user.Length == 0 || PassBox.Password.Length == 0)
        {
            SetStatus("Hãy nhập tên đăng nhập và mật khẩu", isError: true);
            return;
        }

        SetBusy(true);
        if (!await EnsureConnectedAsync())
        {
            SetBusy(false);
            return;
        }

        _pendingUser = user;
        try
        {
            await _conn!.SendAsync(command, user, PassBox.Password);
        }
        catch (Exception ex)
        {
            SetStatus($"Gửi lỗi: {ex.Message}", isError: true);
            SetBusy(false);
        }
    }

    // Hai hàm dưới được gọi từ LUỒNG NỀN của ChatConnection, nên mọi thao tác giao diện phải qua Dispatcher.
    private void OnMessage(string cmd, string[] args)
    {
        Dispatcher.Invoke(() =>
        {
            if (cmd == Cmd.Ok && args.Length >= 1)
            {
                if (args[0] == Cmd.Login)
                {
                    var role = args.Length >= 2 && RoleExtensions.TryParseRole(args[1], out var r) ? r : Role.User;
                    OpenMainWindow(role);
                }
                else if (args[0] == Cmd.Register)
                {
                    SetStatus("Đăng ký thành công, hãy nhấn Đăng nhập.");
                    SetBusy(false);
                }
            }
            else if (cmd == Cmd.Err && args.Length >= 2)
            {
                SetStatus(args[1], isError: true);
                SetBusy(false);
            }
        });
    }

    private void OnDisconnected(string reason)
    {
        Dispatcher.Invoke(() =>
        {
            _conn = null;
            SetStatus($"Mất kết nối: {reason}", isError: true);
            SetBusy(false);
        });
    }

    private void OpenMainWindow(Role role)
    {
        // Chuyển kết nối sang MainWindow. Hàm này chạy trong Dispatcher.Invoke (đồng bộ) nên vòng nhận tin
        // bị chặn cho tới khi MainWindow đã đăng ký xong sự kiện, các tin ROOMS/USERS gửi ngay sau
        // OK|LOGIN không bị mất.
        var conn = _conn!;
        conn.MessageReceived -= OnMessage;
        conn.Disconnected -= OnDisconnected;
        _conn = null;

        var main = new MainWindow(conn, _pendingUser, role);
        Application.Current.MainWindow = main;
        main.Show();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _conn?.Dispose(); // đóng kết nối nếu người dùng tắt cửa sổ khi chưa đăng nhập
        base.OnClosed(e);
    }
}
