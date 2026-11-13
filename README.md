# Chat Room nhiều người (C# .NET 8 / WPF / SQL Server)

Bản code demo của nhóm. **Chưa được biên dịch thử trên máy tạo ra nó** (môi trường tạo code không có .NET SDK),
nên lần build đầu tiên trên Visual Studio có thể báo vài lỗi nhỏ: xem mục "Nếu build lỗi" ở cuối.

## Cần cài

- Windows 10/11, **Visual Studio 2022 (bản 17.8 trở lên)** với workload ".NET desktop development"
  (đã gồm .NET 8 SDK và WPF).
- **SQL Server Express** (hoặc LocalDB đi kèm Visual Studio) và **SSMS** để chạy script. Chỉ máy chạy server cần cài.

## Chạy lần đầu (khoảng 10 phút)

1. Mở `ChatRoom.sln` bằng Visual Studio. Lần đầu nó tự tải gói NuGet (Microsoft.Data.SqlClient, BCrypt.Net-Next).
2. Mở **SSMS**, kết nối vào SQL Server, mở file `schema.sql`, nhấn Execute. Sẽ có database `chatroom` với 3 bảng.
3. Mở `ChatServer/ServerConfig.cs`, sửa `ConnectionString` cho đúng tên instance của máy bạn
   (tên hiện ở ô "Server name" khi mở SSMS). Hoặc đặt biến môi trường `CHAT_DB`.
4. Chạy **ChatServer** (chuột phải project, Set as Startup Project, F5). Thấy dòng
   `Server đang lắng nghe ở cổng 5000` là đạt. Lần đầu server tự tạo tài khoản `admin` / `admin123` và phòng `General`.
5. Chạy **ChatClient.Wpf** (nhiều cửa sổ cũng được: chuột phải project, Debug, Start New Instance).
   Đăng nhập `admin` / `admin123`, hoặc bấm Đăng ký để tạo tài khoản thường.
6. Test nhanh không cần giao diện: chạy **ChatClient.Console**, gõ `LOGIN|admin|admin123`, rồi `MSG|General|xin chao`.

Đổi mật khẩu admin trước khi demo: hiện chưa có lệnh đổi mật khẩu, nên NA có thể thêm lệnh `PASSWD` (xem mục "Việc tiếp theo").

## Demo nhiều máy trong LAN

- Máy chạy server: mở cổng 5000 trên Windows Firewall (Inbound rule, TCP 5000) và xem IP bằng `ipconfig`.
- Trên máy client, ô "Địa chỉ server" nhập IP đó (ví dụ `192.168.1.10`).

## Kịch bản demo gợi ý (5 phút)

1. Mở 3 client: `admin`, `NA`, `Q` (đăng ký NA và Q trước). Cả 3 vào General, thấy nhau trong danh sách online.
2. Chat qua lại; tạo phòng `Lap_trinh_mang` bằng admin (nút +); client khác chuyển phòng.
3. Admin chuột phải `NA` trong danh sách, chọn "Đặt làm moderator". NA mute `Q` 5 phút, Q gửi tin sẽ báo lỗi 201.
4. Tắt đột ngột một client (kill process): sau tối đa 45 giây phòng báo họ đã rời (heartbeat).
5. Đóng và mở lại client: lịch sử tin nhắn hiện lại (đọc từ SQL Server).

## Cấu trúc

| Thư mục | Vai trò | Người phụ trách |
| --- | --- | --- |
| `ChatShared` | Giao thức, vai trò, kết nối client dùng chung | An |
| `ChatServer` | Server console: listener, phòng, heartbeat, xử lý lệnh, SQL | An (SQL: NA) |
| `ChatClient.Console` | Client dòng lệnh để test | NA, L |
| `ChatClient.Wpf` | Giao diện WPF | Q |
| `schema.sql` | Tạo database SQL Server | NA |

Mỗi thành viên chỉ cần sửa trong thư mục của mình, giao thức nằm ở `ChatShared/Protocol.cs`
(đổi giao thức là đổi cả hai phía, hãy báo An trước).

## Việc tiếp theo (gợi ý cho từng người)

- **Q (WPF)**: thay giao diện mặc định bằng MaterialDesignInThemes hoặc ModernWpf (NuGet), thêm avatar chữ cái đầu,
  đếm tin chưa đọc theo phòng, đối chiếu với bản mockup.
- **NA (CSDL, kiểm thử)**: viết kịch bản test theo bảng quyền trong tài liệu, thêm lệnh đổi mật khẩu, sao lưu database.
- **L (tài liệu)**: báo cáo, so sánh Zalo/Discord/IRC; chụp ảnh demo từ kịch bản trên.
- **An**: ghép, kiểm tra mọi lệnh có kiểm tra quyền ở server, thêm log ra file nếu cần.

## Nếu build lỗi

- `The type or namespace name 'Microsoft.Data.SqlClient'...`: NuGet chưa khôi phục. Chuột phải solution, "Restore NuGet Packages".
- Lỗi kết nối SQL (`A network-related or instance-specific error`): sai tên instance trong `ConnectionString`.
  Thử `(localdb)\MSSQLLocalDB` nếu dùng LocalDB.
- Lỗi `Login failed`: tài khoản Windows chưa có quyền; mở SSMS, Security, Logins, thêm tài khoản của bạn với vai trò `db_owner` trên `chatroom`.
- Mọi lỗi biên dịch khác: gửi nguyên văn thông báo lỗi (cả số dòng) để sửa.
