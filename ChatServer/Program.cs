using ChatServer;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = ServerConfig.Load();
var db = new Database(config.ConnectionString);
var server = new ChatHub(config, db);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // để server dừng êm thay vì bị tắt đột ngột
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Log.Error($"Server không chạy được: {ex.Message}");
    Log.Error("Kiểm tra: SQL Server đã chạy chưa, đã chạy schema.sql chưa, và chuỗi kết nối trong ServerConfig.cs (hoặc biến môi trường CHAT_DB) có đúng tên instance không.");
    Environment.ExitCode = 1;
}
