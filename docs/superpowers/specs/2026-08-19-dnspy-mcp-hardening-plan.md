# dnSpy.MCP — Kế hoạch xử lý 3 vấn đề còn lại

Ngày: 2026-08-19
Bối cảnh: sau khi Tier 1/2/3 xanh (125 check). Extension dùng được, nhưng còn ba điểm đã nêu khi
bàn giao. Tài liệu này là kế hoạch xử lý, chưa triển khai.

| # | Vấn đề | Bản chất | Mức |
|---|---|---|---|
| P1 | Không xác thực mặc định | Bảo mật | Cao |
| P2 | Suite test dùng chung profile với người dùng | An toàn dữ liệu | Trung bình |
| P3 | dnSpy crash khi hủy process đang pause; `autos` trả `NYI` | Ổn định / UX | Trung bình |

Ba việc độc lập nhau, nhưng **P1 và P2 dùng chung một hạ tầng nhỏ** (xem P2) nên làm liền mạch sẽ rẻ hơn.

---

## P1 — Bật xác thực theo mặc định

### Hiện trạng

`DNSPY_MCP_TOKEN` là tùy chọn. Không đặt → `IsAuthorized` trả `true` cho mọi request. Endpoint chỉ
nghe loopback và chặn `Origin`/`Host` lạ, nên trang web không lái được; nhưng **mọi tiến trình local
chạy dưới tài khoản người dùng đều toàn quyền** — mà bộ tool có `dbg_start` (chạy exe bất kỳ),
`dbg_write_memory` (vá bộ nhớ tiến trình khác) và `dbg_eval` (chạy code trong debuggee). Trên thực
tế đây là thực thi mã tùy ý cục bộ.

Rào cản khiến "bật mặc định" chưa làm ngay: token phải **ổn định giữa các lần khởi động**, nếu không
người dùng phải cấu hình lại MCP client mỗi lần mở dnSpy.

### Thiết kế đề xuất

Sinh một lần, lưu lại, mặc định bắt buộc:

1. Khi `McpServer.Start()` và không có `DNSPY_MCP_TOKEN`:
   - Đọc token từ file `mcp-token.txt` **đặt cạnh file settings đang dùng**
     (`Path.GetDirectoryName(AppDirectories.SettingsFilename)`).
   - Không có thì sinh 32 byte ngẫu nhiên (`RandomNumberGenerator`), base64url, ghi ra file đó.
2. Thứ tự ưu tiên: `DNSPY_MCP_TOKEN` (env) → file → sinh mới.
3. Lối thoát có chủ đích: `DNSPY_MCP_NO_AUTH=1` tắt hẳn xác thực, kèm cảnh báo vào log như hiện nay.

**Vì sao đặt cạnh file settings, không phải `%APPDATA%` cứng:** để nó tự động đi theo
`--settings-file`. Một phiên test cô lập sẽ có token riêng, không đụng token thật — cùng cơ chế
giải quyết P2.

**Về quyền file:** không thêm ACL thủ công (sẽ phải kéo `System.IO.FileSystem.AccessControl` cho
net10). Cả `%APPDATA%` lẫn `%TEMP%` của người dùng đã là thư mục riêng theo ACL mặc định của
Windows — cùng mức bảo vệ với chính `dnSpy.xml`. Nếu người dùng tự trỏ `--settings-file` vào thư mục
dùng chung thì ghi cảnh báo vào log.

**Khả năng khám phá.** Đây là chỗ dễ làm người dùng bực nhất, nên xử lý riêng: khi thiếu/sai
`Authorization`, thân phản hồi 401 ghi rõ đường dẫn file token. Bạn gặp thông báo đúng lúc cần nó.
Không làm lộ gì thêm — kẻ tấn công cục bộ đọc được file đó thì đã thắng từ trước.

```
HTTP 401
unauthorized — token is at C:\Users\<you>\AppData\Roaming\dnSpy\mcp-token.txt
send it as: Authorization: Bearer <token>
```

### Đánh đổi phải chấp nhận

- **Phá vỡ cấu hình cũ.** Ai đã `claude mcp add` không kèm header sẽ hỏng sau khi nâng cấp. Giảm đau
  bằng: `DNSPY_MCP_NO_AUTH=1`, thông báo 401 tự chỉ đường, và cập nhật README/USAGE.
- **Lệnh kết nối dài hơn:**
  `claude mcp add --transport http dnspy http://127.0.0.1:27115/mcp --header "Authorization: Bearer <token>"`

### Kiểm thử (Tier 1, không cần dnSpy)

- Không env, không file → sinh token, file được tạo, request không token bị 401
- Chạy lần hai → **dùng lại đúng token cũ** (đây là điểm mấu chốt của thiết kế)
- `DNSPY_MCP_TOKEN` có giá trị → env thắng file
- `DNSPY_MCP_NO_AUTH=1` → cho qua, và log có cảnh báo
- Thân 401 chứa đường dẫn file token
- Token sinh ra đủ entropy và khác nhau giữa hai thư mục settings khác nhau

Ước lượng: ~120 dòng code, ~8 test.

---

## P2 — Không để suite test đụng profile thật

### Hiện trạng

`run-integration.ps1` đã truyền `--settings-file <temp>`, nên đường chuẩn là an toàn. Nhưng biện pháp
này **nằm ở người gọi**: chạy `dotnet test tests/dnSpy.MCP.IntegrationTests` trực tiếp trong lúc dnSpy
thường đang mở là suite sẽ `bp_remove all=true` lên breakpoint thật. Trong phiên vừa rồi việc này đã
xảy ra; dữ liệu còn nguyên chỉ vì dnSpy bị kill cứng nên không kịp ghi settings — **may, không phải
thiết kế**.

### Thiết kế đề xuất

Chuyển từ "người gọi phải nhớ" sang "suite tự từ chối":

1. **Thêm tool `dnspy_info`** (read-only): trả về cổng, phiên bản dnSpy, file settings đang dùng,
   trạng thái xác thực, số tool. Đây không phải tool chỉ để phục vụ test — nó trả lời câu hỏi chính
   đáng "tôi đang nói chuyện với dnSpy nào, cấu hình ra sao", và cũng là chỗ báo trạng thái token của P1.
2. **Chốt an toàn trong suite:** trong fixture khởi tạo, gọi `dnspy_info`; nếu đường dẫn settings
   **không** nằm dưới thư mục temp thì fail ngay với thông báo rõ ràng, trước khi chạm vào breakpoint.

```
Refusing to run: dnSpy is using C:\Users\you\AppData\Roaming\dnSpy\dnSpy.xml.
The suite clears all breakpoints. Launch via tests/run-integration.ps1, which
passes --settings-file pointing at a temp file.
```

3. **Chốt phụ:** `run-integration.ps1` từ chối chạy nếu đã có `dnSpy.exe` đang mở, để không vô tình
   thao tác lên phiên làm việc thật của người dùng.

### Vì sao không chọn cách khác

- *Bắt suite tự sao lưu rồi khôi phục `dnSpy.xml`*: mong manh (dnSpy ghi lúc thoát, không xác định
  thời điểm), và vẫn hỏng nếu test crash giữa chừng.
- *Bỏ hẳn `bp_remove all=true`*: mất tính cô lập giữa các test, đổi lấy một rủi ro khác.

### Kiểm thử

- Tier 1: `dnspy_info` trả đủ trường, có annotation `readOnlyHint`
- Tier 2: chốt an toàn cho qua khi settings nằm trong temp
- Thủ công: trỏ vào profile thật → suite phải fail **trước khi** breakpoint bị xóa (kiểm chứng bằng
  cách so `dnSpy.xml` trước/sau)

Ước lượng: ~90 dòng, ~5 test.

---

## P3 — Crash khi hủy process đang pause, và `autos`

Hai việc rời nhau, gộp vào đây vì cùng là chất lượng vận hành.

### P3a — Access violation khi stop lúc đang pause

**Đã biết chắc:** AV `0xC0000005`, `dnlib` đọc PE image từ bộ nhớ debuggee đã giải phóng, kích hoạt
từ `ValueNodesVM.RecreateRootChildren_UI` — cửa sổ Locals của chính dnSpy tự refresh khi call stack
đổi. Trong stack không có frame nào của `dnSpy.MCP`. Đây là lỗi của dnSpy, nhưng agent điều khiển
nhanh thì đua trúng nó.

**Chưa biết chắc, và phải làm rõ trước khi sửa:** trong suite tôi đã đổi **hai** thứ cùng lúc —
continue-trước-khi-stop, *và* chờ lắng 750 ms. Sau đó hết crash. Tôi **không biết cái nào thực sự có
tác dụng**. Sửa hành vi của `dbg_stop` dựa trên phỏng đoán là sai phương pháp.

**Bước 1 — bisect (bắt buộc làm trước).** Dựng kịch bản lặp start → pause tại breakpoint → stop,
chạy N=30 vòng cho mỗi biến thể:

| Biến thể | Mục đích |
|---|---|
| stop thẳng, không chờ | tái hiện crash, xác nhận có tái hiện được |
| chỉ chờ 750 ms rồi stop | độ trễ có đủ không |
| chỉ continue rồi stop | thứ tự có đủ không |
| continue + chờ | hiện trạng |

Tỉ lệ crash của từng biến thể sẽ quyết định bước 2. Nếu không tái hiện nổi biến thể đầu thì dừng —
không sửa cái không đo được.

**Bước 2 — sửa theo kết quả.** Nếu continue-trước-stop là yếu tố quyết định, đưa nó vào chính
`dbg_stop` thay vì để mỗi client tự lo:

```
dbg_stop:
  nếu có process đang paused và force != true:
      RunAll(); đợi ngắn cho tới khi IsRunning == true
  StopDebuggingAll()
```

Kèm tham số `force` (mặc định `false`) để ai cần dừng ngay tại chỗ vẫn làm được.

**Đánh đổi phải nói rõ trong mô tả tool:** cho chạy tiếp trước khi hủy nghĩa là debuggee thực thi
thêm một quãng ngắn. Có trường hợp người ta pause chính là để ngăn điều đó. Vì vậy phải là hành vi
mặc định *có thể tắt*, và mô tả tool phải nói thẳng, không giấu.

**Bước 3 — báo ngược lên dnSpyEx.** Đã có stack đầy đủ và cách tái hiện; đây mới là chỗ sửa gốc.
Kèm link issue vào comment trong code.

### P3b — `dbg_variables kind=autos`

dnSpy chưa implement provider này; nó trả `[{"name":"Error","value":"NYI"}]`. Agent gọi vào, nhận
một mảng trông như dữ liệu hợp lệ, rồi suy luận sai.

Đề xuất:
- Mô tả tool nói rõ `autos` chưa được engine .NET hỗ trợ.
- Nếu provider trả đúng dấu hiệu NYI, đổi thành lỗi có hướng đi tiếp:
  `"autos is not implemented by dnSpy's .NET engine; use dbg_locals instead"`.
- Giữ nguyên `returns`/`statics`/`exceptions`.

Nhận diện dựa vào chuỗi `"NYI"` là hơi mong manh, nên bọc trong một hàm nhỏ có chú thích, và test
Tier 2 đã có sẵn ca "báo động khi dnSpy implement xong" để phát hiện lúc điều kiện thay đổi.

### Kiểm thử

- Bisect: script riêng, kết quả ghi vào spec này (không phải test thường trực — nó cố ý làm sập app)
- Tier 2: `dbg_stop` khi đang pause chạy sạch qua N vòng lặp; `force=true` vẫn dừng được
- Tier 2: `autos` trả thông báo có hướng dẫn thay vì `NYI`

Ước lượng: bisect ~80 dòng; sửa ~60 dòng, ~4 test.

---

## Thứ tự đề xuất

1. **P2 trước** — nó tạo `dnspy_info`, thứ mà P1 cần để báo trạng thái xác thực. Và nó chặn nguy cơ
   mất dữ liệu, thứ duy nhất trong ba vấn đề có thể gây thiệt hại không hồi phục.
2. **P1** — dùng lại `dnspy_info`; là vấn đề nghiêm trọng nhất về bảo mật.
3. **P3a bisect** — đo trước, chưa sửa.
4. **P3a sửa + P3b** — theo kết quả đo.

Tổng ước lượng: ~350 dòng code, ~17 test, cộng một lần bisect.

## Ghi chú

- P1 và P2 hội tụ vào cùng một ý: **cấu hình phải đi theo file settings**, nhờ đó `--settings-file`
  cô lập được cả token lẫn breakpoint chỉ bằng một cơ chế.
- P3a là vấn đề duy nhất mà bản sửa tốt nhất nằm ngoài repo này. Phần làm được ở đây chỉ là giảm xác
  suất trúng; nói đúng như vậy trong tài liệu, đừng hứa là đã hết.
- **Giả định đã kiểm chứng.** `AppDirectories.SetSettingsFilename(args.SettingsFilename)` nằm trong
  constructor của `App` (`App.xaml.cs:109`), chạy đồng bộ và xong từ lâu trước khi MEF compose xong
  rồi `[ExportAutoLoaded] ServerLoader` gọi `host.Start()`. Nên extension đọc
  `AppDirectories.SettingsFilename` tại thời điểm `Start()` chắc chắn thấy giá trị từ dòng lệnh.
  Kiểm chứng thực nghiệm: chạy với `--settings-file <temp>` thì `bp_list` rỗng, tức breakpoint thật
  của người dùng không được nạp.

- **Phát hiện phụ ở thượng nguồn (không chặn kế hoạch này).** Ngay phía trên dòng đó,
  `App.xaml.cs:104` đã kick MEF sang thread nền bằng `Task.Run(() => InitializeMEF(...))`, mà
  `InitializeMEF` lại đọc settings. Tức có một cuộc đua giữa việc đọc settings và việc gán tên file
  settings. Thực tế lệnh gán thắng vì `Task.Run` còn phải xếp lịch, nhưng thứ tự này không được bảo
  đảm bởi cấu trúc. Đáng báo lên dnSpyEx cùng với P3a.
