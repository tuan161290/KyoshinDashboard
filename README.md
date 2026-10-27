# Kyoshin Dashboard

Giám sát 48 máy trong xưởng qua PLC Mitsubishi FX. Mỗi máy có đèn tháp Xanh/Vàng/Đỏ và 3 tín hiệu lỗi: Báo thoát Misfeed PS-101T, Báo rác Die height PS-642/464, Sai bước. Dashboard xem được trên mọi máy trong mạng LAN của xưởng.

## Thành phần

| Thư mục | Vai trò |
|---|---|
| `KyoshinDashboard/KyoshinDashboard` | Web dashboard (ASP.NET Core 10, SignalR, SQLite), chạy thành service **KyoshinServer** ở cổng 5000 |
| `KyoshinDashboard/KyoshinAgent` | Đọc PLC qua RS-485 (Computer Link, đọc thẳng đầu vào X), gửi về Server; chạy thành service **KyoshinAgent** |
| `Kyoshin.Plc` | Thư viện dùng chung: giao thức FX, bảng địa chỉ I/O, bộ lọc tín hiệu ổn định (ON/OFF 15 s) |
| `KyoshinDashboard/deploy/windows` | Script cài đặt, chạy thử và cập nhật trên máy Windows ở xưởng |

Cả hai chương trình được đóng gói self-contained, nên máy ở xưởng không cần cài .NET.

## Phát triển

Cần .NET SDK 10.

```
dotnet run --project KyoshinDashboard/KyoshinDashboard      # web, Development: khóa API "dev-key"
dotnet run --project KyoshinDashboard/KyoshinAgent          # agent, Development: PLC giả lập (mock)
```

Muốn tạo gói cài đặt trên máy mình để thử, chạy `KyoshinDashboard\publish.cmd [thư_mục] [phiên_bản]`. Mặc định gói nằm ở `D:\Deploy\Kyoshin`, phiên bản `0.0.0-local`.

## Phát hành bản mới

1. Commit các thay đổi.
2. Chạy `KyoshinDashboard\release.cmd 1.2.0`. Lệnh này đẩy code lên GitHub, gắn tag `v1.2.0` rồi đẩy tag.
3. GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) build gói `Kyoshin-win-x64.zip` cùng file `.sha256`, rồi tạo bản phát hành trong [Releases](../../releases). Mất khoảng 3–5 phút.

Số phiên bản theo dạng `chính.phụ.sửa`. Mỗi lần phát hành phải dùng số lớn hơn bản trước. Số phiên bản hiện ở chân thanh bên của dashboard.

## Cài lần đầu ở xưởng

1. Tải `Kyoshin-win-x64.zip` từ [bản phát hành mới nhất](../../releases/latest), giải nén.
2. Double-click `run-console.cmd`, chọn cổng COM của bộ USB-RS485, kiểm tra dashboard và log PLC.
3. Chạy được rồi thì chuột phải `install-service.cmd`, chọn *Run as administrator*.
   - Script cài 2 service vào `C:\Kyoshin` và mở cổng web cho mạng LAN.
   - Script cũng chép `update.cmd` vào `C:\Kyoshin`.

## Cập nhật ở xưởng

Double-click `C:\Kyoshin\update.cmd`. Script tự xin quyền Administrator.

| Lệnh | Tác dụng |
|---|---|
| `update.cmd` | Tải bản mới nhất từ GitHub và cài (cần Internet) |
| `update.cmd check` | Chỉ xem có bản mới không |
| `update.cmd force` | Cài lại bản mới nhất, kể cả khi đang dùng bản đó |
| `update.cmd <file.zip>` | Cài từ file zip chép qua USB, không cần Internet. Kéo-thả file zip vào `update.cmd` cũng được |
| `update.cmd <file.zip> force` | Cài cả khi gói cũ hơn bản đang chạy (quay về bản cũ) |

Các bước script thực hiện:

1. Kiểm tra SHA256 của gói.
2. Dừng 2 service.
3. Sao lưu chương trình, CSDL và cấu hình vào `C:\Kyoshin\backup\` (giữ 3 bản gần nhất).
4. Chép file mới.
5. Khởi động lại và kiểm tra web có trả lời không. Nếu bản mới không chạy được, script **tự trả lại bản cũ**.

Mỗi lần cập nhật dừng hệ thống khoảng 15–30 giây. Trong lúc đó, dữ liệu gửi về Server vẫn nằm an toàn trong hàng đợi của Agent.

Những thứ **không bao giờ bị thay đổi** khi cập nhật:
- `appsettings.json` (cấu hình đang dùng)
- `Kyoshin.db` (dữ liệu sản xuất)
- `agent.db` (hàng đợi)

Cấu hình mặc định của bản mới được chép thành `appsettings.default.json` để so sánh khi có mục cấu hình mới. Ứng dụng chỉ đọc `appsettings.json`.

Nhật ký cập nhật nằm ở `C:\Kyoshin\update.log`.

## Cấu hình chính

| File | Mục | Ý nghĩa |
|---|---|---|
| `KyoshinAgent\appsettings.json` | `Plc:ComPort`, `Plc:UseMock` | Cổng COM; `true` = dữ liệu giả lập |
| | `Plc:OnConfirmSeconds`, `Plc:OffConfirmSeconds` | Tín hiệu phải giữ nguyên bao lâu mới được xác nhận (mặc định 15 s) |
| `KyoshinServer\appsettings.json` | `Kestrel:Endpoints:Http:Url` | Cổng web (mặc định 5000) |
| | `DataSettings:RetentionDays` | Số ngày giữ log chi tiết; `0` = giữ hết (mặc định) |
| | `Errors:OnlyWhenGreenOrYellow` | Chỉ ghi lỗi khi đèn Xanh/Vàng (mặc định `true`, như bản cũ) |

Khóa API giữa Agent và Server được `install-service.cmd` tự tạo, không cần sửa tay.

## Dữ liệu

Dữ liệu sản xuất nằm ở `C:\Kyoshin\KyoshinServer\Kyoshin.db` (SQLite), khoảng 0,3–0,4 MB mỗi ngày cho 48 máy. Muốn bắt đầu CSDL mới:
1. Dừng service KyoshinServer.
2. Chuyển `Kyoshin.db` cùng `Kyoshin.db-wal` và `Kyoshin.db-shm` (nếu có) đi chỗ khác để lưu.
3. Chạy lại service. Server tự tạo CSDL mới.
