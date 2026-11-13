-- Chạy file này MỘT LẦN bằng SQL Server Management Studio (SSMS) hoặc Visual Studio (SQL Server Object Explorer),
-- hoặc dòng lệnh:  sqlcmd -S localhost\SQLEXPRESS -E -i schema.sql
-- Tài khoản admin (admin / admin123) và phòng General được SERVER tự tạo ở lần chạy đầu tiên.

IF DB_ID(N'chatroom') IS NULL
    CREATE DATABASE chatroom;
GO

USE chatroom;
GO

IF OBJECT_ID(N'dbo.users', N'U') IS NULL
CREATE TABLE dbo.users (
    id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    username      NVARCHAR(32)  NOT NULL,
    password_hash VARCHAR(100)  NOT NULL,
    [role]        VARCHAR(10)   NOT NULL
                  CONSTRAINT df_users_role DEFAULT 'user'
                  CONSTRAINT ck_users_role CHECK ([role] IN ('user','moderator','admin')),
    is_banned     BIT           NOT NULL CONSTRAINT df_users_banned DEFAULT 0,
    muted_until   DATETIME2     NULL,       -- giờ UTC; NULL hoặc đã qua = không bị mute
    created_at    DATETIME2     NOT NULL,   -- giờ UTC
    CONSTRAINT uq_users_username UNIQUE (username)
);
GO

IF OBJECT_ID(N'dbo.rooms', N'U') IS NULL
CREATE TABLE dbo.rooms (
    id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name        NVARCHAR(32) NOT NULL,
    created_by  INT          NULL,
    created_at  DATETIME2    NOT NULL,      -- giờ UTC
    CONSTRAINT uq_rooms_name UNIQUE (name),
    CONSTRAINT fk_rooms_user FOREIGN KEY (created_by)
        REFERENCES dbo.users(id) ON DELETE SET NULL
);
GO

-- Lưu ý: khóa ngoại user_id để NO ACTION (không CASCADE) vì SQL Server không cho
-- hai đường CASCADE từ users tới messages (báo lỗi "multiple cascade paths").
IF OBJECT_ID(N'dbo.messages', N'U') IS NULL
CREATE TABLE dbo.messages (
    id       BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    room_id  INT           NOT NULL,
    user_id  INT           NOT NULL,
    content  NVARCHAR(MAX) NOT NULL,
    sent_at  DATETIME2     NOT NULL,        -- giờ UTC
    CONSTRAINT fk_messages_room FOREIGN KEY (room_id)
        REFERENCES dbo.rooms(id) ON DELETE CASCADE,
    CONSTRAINT fk_messages_user FOREIGN KEY (user_id)
        REFERENCES dbo.users(id)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_messages_room_time')
    CREATE INDEX idx_messages_room_time ON dbo.messages (room_id, sent_at);
GO
