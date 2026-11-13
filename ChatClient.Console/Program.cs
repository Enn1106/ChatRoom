using ChatShared;

// Client dòng lệnh dùng để TEST server: gõ tay lệnh theo giao thức, ví dụ
//   REGISTER|an|matkhau
//   LOGIN|an|matkhau
//   MSG|General|Xin chào
//   HISTORY|General|20
// Gõ /quit để thoát. Có thể chạy nhiều cửa sổ để giả lập nhiều client.

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.Write("Địa chỉ server [127.0.0.1]: ");
var host = Console.ReadLine();
if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

Console.Write("Cổng [5000]: ");
var portText = Console.ReadLine();
var port = int.TryParse(portText, out var p) ? p : 5000;

using var conn = new ChatConnection();
var done = new TaskCompletionSource();

conn.MessageReceived += (cmd, args) =>
{
    Console.ForegroundColor = cmd == Cmd.Err ? ConsoleColor.Red : ConsoleColor.Cyan;
    Console.WriteLine($"< {cmd}|{string.Join("|", args)}");
    Console.ResetColor();
    if (cmd == Cmd.Kicked) Console.WriteLine("Bạn đã bị kick. Gõ /quit để thoát.");
};
conn.Disconnected += reason =>
{
    Console.WriteLine($"Mất kết nối: {reason}");
    done.TrySetResult();
};

try
{
    await conn.ConnectAsync(host, port);
}
catch (Exception ex)
{
    Console.WriteLine($"Không kết nối được tới {host}:{port}: {ex.Message}");
    return;
}

Console.WriteLine("Đã kết nối. Gõ lệnh (ví dụ LOGIN|user|pass), /quit để thoát.");

// Đọc bàn phím trong task riêng để không chặn việc nhận tin.
_ = Task.Run(async () =>
{
    while (true)
    {
        var line = Console.ReadLine();
        if (line == null || line.Trim() == "/quit")
        {
            done.TrySetResult();
            return;
        }
        if (line.Length == 0) continue;
        try
        {
            await conn.SendRawAsync(line);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gửi lỗi: {ex.Message}");
        }
    }
});

await done.Task;
