# dnSpy.MCP — Kế hoạch mở rộng integration test, tập trung vào debug

Ngày: 2026-08-19
Bối cảnh: Tier 2 hiện có 33 test và đều xanh, nhưng phủ theo *bề rộng công cụ* chứ chưa theo *chiều
sâu debug*. Tài liệu này liệt kê đúng những gì chưa được kiểm chứng và cách bổ sung.

## Đang thiếu gì (đối chiếu từng test hiện có)

Nhóm A — **những công cụ chưa từng được chạy thật**, chỉ mới kiểm tra phần vỏ:

| Công cụ | Hiện trạng |
|---|---|
| `dbg_step` `into` / `out` | Chỉ `over` được test. Vào/ra khỏi hàm chưa từng chạy. |
| `dbg_attach` | Chỉ test `dbg_list_attachable`. **Chưa attach thật lần nào.** |
| `dbg_write_memory` | Chưa test. Chỉ đọc. Đây là công cụ phá hủy, cần cả đường thành công lẫn thất bại. |
| `exc_break` | Chỉ khẳng định "bật được", chưa chứng minh nó **thật sự dừng** khi có exception. |
| `dbg_set_thread` | Chưa test. |
| `dbg_restart` | Chưa test. |
| `dbg_variables` `returns` / `exceptions` | Chỉ `statics` được test. |
| SSE `notifications/paused` | Có ở Tier 1 với `Broadcast` giả; chưa từng nhận sự kiện từ breakpoint thật. |

Nhóm B — **những tham số chưa từng khác giá trị mặc định**:

| Tham số | Hiện trạng |
|---|---|
| `frame_index` | Mọi test dùng frame 0. Đọc biến ở frame **gọi** chưa từng chạy. |
| `thread_id` | Mọi test dùng thread mặc định. Debuggee chỉ có một thread nên chưa phân biệt được. |
| `max_frames`, `max_children` | Giới hạn chưa bị chạm tới. |

Nhóm C — **hành vi chưa được chứng minh, mới chỉ kiểm tra trạng thái**:

- `bp_toggle` kiểm tra cờ `enabled` đổi, nhưng **không chứng minh breakpoint tắt thì không dừng**.
- Điều kiện breakpoint chỉ test trường hợp đúng; chưa test điều kiện sai thì **không** dừng.
- `dbg_expand` chỉ test trên mảng phẳng; chưa đi sâu vào object lồng nhau.
- Chưa test format giá trị của chuỗi và collection — chỉ toàn số nguyên.

## Fixture phải mở rộng trước

Phần lớn khoảng trống trên **không thể test với fixture hiện tại**: nó chỉ có một thread, không có
chuỗi lời gọi, và `Node` được khai báo nhưng chưa bao giờ khởi tạo (mã chết).

| Thêm gì | Phục vụ |
|---|---|
| Chuỗi `Level1 → Level2 → Level3`, mỗi mức có biến riêng | step into/out, `frame_index` > 0 |
| Thread phụ đặt tên, gọi method **riêng** | `thread_id`, `dbg_set_thread`, `dbg_threads` |
| Đồ thị `Node` lồng 3 tầng, khởi tạo thật | `dbg_expand` chiều sâu |
| Biến chuỗi + mảng + collection trong một frame | format giá trị ngoài kiểu số |
| Ném exception định kỳ trong vòng lặp | `exc_break` dừng thật, `kind=exceptions` |
| Vùng nhớ ghi được (mảng byte tĩnh) | `dbg_write_memory` vòng tròn ghi–đọc |

Ràng buộc: thread phụ **không được** gọi `Program.Add`. Các test hiện có dừng ở `Add` và giả định
đó là thread chính; một thread thứ hai cùng gọi `Add` sẽ khiến chúng dừng nhầm chỗ và hỏng ngẫu nhiên.

Giá trị phải tất định. Chuỗi lời gọi chọn sao cho khi `seed == 7` thì `Level3` nhận `16` và trả `116`,
frame gọi có `two == 8`, `mid == 16`, frame trên nữa có `seed == 7`, `one == 8` — mọi assertion đều
là hằng số kiểm chứng được bằng tay.

## Các lớp test mới

| Lớp | Nội dung |
|---|---|
| `SteppingIntegrationTests` | `into` vào đúng callee; `out` về đúng caller; `over` không vào trong; step tại call site; chuỗi step nhiều bước giữ đúng thread |
| `FrameAndThreadIntegrationTests` | locals/eval ở `frame_index` 1 và 2; `thread_id` trỏ đúng thread phụ; `dbg_set_thread` đổi ngữ cảnh mặc định; `max_frames` cắt đúng |
| `AttachIntegrationTests` | chạy fixture độc lập rồi `dbg_attach` theo pid và theo tên; break; đọc callstack; dừng phiên mà **không** giết tiến trình ngoài |
| `ExceptionIntegrationTests` | `exc_break` dừng thật khi ném; `kind=exceptions` mô tả được exception; tắt đi thì không dừng nữa |
| `MemoryWriteIntegrationTests` | ghi rồi đọc lại khớp; địa chỉ không ghi được báo lỗi "not confirmed"; hex nhiều định dạng |
| `NotificationIntegrationTests` | mở SSE, cho trúng breakpoint thật, nhận `notifications/paused` đúng pid/threadId |
| Bổ sung vào lớp sẵn có | breakpoint tắt thì không dừng; điều kiện sai thì không dừng; `dbg_expand` object lồng; `dbg_restart` |

Ước lượng: fixture ~90 dòng, khoảng 34 test mới, tổng Tier 2 lên ~67.

## Rủi ro cần xử lý

- **Test "không dừng" luôn tốn thời gian chờ.** Phải có ngưỡng rõ ràng (chờ N giây rồi khẳng định
  vẫn đang chạy) chứ không chờ vô hạn.
- **`dbg_attach` để lại tiến trình mồi sống.** Teardown phải giết tiến trình do test tự khởi động,
  nếu không nó giữ file lock và phá các lần chạy sau.
- **Thread phụ làm nhiễu test cũ** nếu nó chạm vào cùng method. Đã ràng buộc ở trên.
- **`returns` có thể trả rỗng** tùy dnSpy. Khẳng định "không lỗi" và nếu có dữ liệu thì kiểm hình
  dạng — không bịa kỳ vọng mà engine không cam kết.

## Thứ tự làm

1. Mở rộng fixture (chặn mọi việc còn lại).
2. Viết song song các lớp test mới trên các file rời nhau.
3. Verify tuần tự: chỉ một dnSpy, một debug engine đơn luồng.
