using System.Net.Sockets;
using System.Text;

namespace ChatShared;

/// <summary>
/// Kết nối phía client dùng chung cho client console và client WPF:
/// kết nối TCP, gửi lệnh, nhận tin trong một task nền và tự gửi PING định kỳ.
///
/// Lưu ý: sự kiện MessageReceived và Disconnected được gọi từ LUỒNG NỀN.
/// Client WPF phải chuyển sang luồng giao diện bằng Dispatcher trước khi sửa control.
/// </summary>
public sealed class ChatConnection : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _disposed;

    /// <summary>Khoảng cách giữa hai lần gửi PING (server ngắt nếu 45 giây không nhận được gì).</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Nhận được một thông điệp từ server: (lệnh viết hoa, danh sách trường).</summary>
    public event Action<string, string[]>? MessageReceived;

    /// <summary>Kết nối bị đóng không do client chủ động (tham số là lý do).</summary>
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        var stream = tcp.GetStream();
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };

        _ = ReceiveLoopAsync(_cts.Token);
        _ = PingLoopAsync(_cts.Token);
    }

    /// <summary>Gửi một lệnh, các trường tự được thoát ký tự đặc biệt.</summary>
    public Task SendAsync(string command, params string[] args)
        => SendRawAsync(Protocol.Build(command, args));

    /// <summary>Gửi nguyên một dòng (dùng cho client console để gõ tay lệnh).</summary>
    public async Task SendRawAsync(string line)
    {
        if (_writer == null || Volatile.Read(ref _disposed) == 1)
            throw new InvalidOperationException("Chưa kết nối tới server.");

        // Nhiều task có thể gửi cùng lúc (người dùng gõ tin và PING định kỳ),
        // nên phải khóa để hai dòng không bị ghi chồng lên nhau.
        await _sendLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(line);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "Server đã đóng kết nối.";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line == null) break; // server đóng kết nối
                if (line.Length == 0) continue;

                var (command, args) = Protocol.Parse(line);
                try
                {
                    MessageReceived?.Invoke(command, args);
                }
                catch
                {
                    // Lỗi trong xử lý của giao diện không được làm chết vòng nhận tin.
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        if (Volatile.Read(ref _disposed) == 1) return; // chính client đã đóng

        Disconnected?.Invoke(reason);
        Dispose();
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(PingInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await SendAsync(Cmd.Ping);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Nếu gửi PING lỗi thì vòng nhận tin cũng sẽ phát hiện và báo Disconnected.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        try
        {
            _tcp?.Close();
        }
        catch
        {
        }
    }
}
