# Hướng dẫn sử dụng dnSpy.MCP

Tài liệu này hướng dẫn build, cài đặt và dùng extension `dnSpy.MCP` để điều khiển trình gỡ lỗi (debugger) của dnSpy từ một AI agent (Claude Code, hoặc bất kỳ MCP client nào).

- [1. Tổng quan](#1-tổng-quan)
- [2. Yêu cầu](#2-yêu-cầu)
- [3. Build extension](#3-build-extension)
- [4. Kích hoạt trong dnSpy](#4-kích-hoạt-trong-dnspy)
- [5. Kết nối MCP client](#5-kết-nối-mcp-client)
- [6. Danh sách tool](#6-danh-sách-tool)
- [7. Quy trình debug điển hình](#7-quy-trình-debug-điển-hình)
- [8. Bảo mật](#8-bảo-mật)
- [9. Dùng từ macOS / Linux](#9-dùng-từ-macos--linux)
- [10. Khắc phục sự cố](#10-khắc-phục-sự-cố)

---

## 1. Tổng quan

`dnSpy.MCP.x.dll` là một extension chạy bên trong tiến trình dnSpy. Khi dnSpy khởi động, extension mở một MCP server HTTP trên `127.0.0.1:27115`. Agent kết nối tới server này và gọi các "tool" để:

- Khởi động / attach phiên debug
- Đặt, liệt kê, bật/tắt, xóa breakpoint
- Break / continue / stop / restart / step
- Đọc thread, module, call stack, biến local, và đánh giá biểu thức (expression)

```
┌──────────────┐   HTTP JSON-RPC 2.0    ┌────────────────────────────┐
│ Claude Code  │ ─────────────────────► │ dnSpy.exe                  │
│ (MCP client) │  127.0.0.1:27115/mcp   │  dnSpy.MCP.x.dll           │
└──────────────┘ ◄───────────────────── │   → DbgManager, breakpoints│
                                         │     call stack, evaluation │
                                         └────────────────────────────┘
```

Toàn bộ truy cập vào debugger được marshal về đúng luồng (thread) của debug engine dnSpy, nên gọi tool an toàn về mặt luồng.

---

## 2. Yêu cầu

- **Windows** — dnSpy là ứng dụng WPF, debug engine dùng CorDebug (API của Windows). Server chỉ chạy trên Windows. (Client MCP thì chạy ở đâu cũng được — xem [mục 9](#9-dùng-từ-macos--linux).)
- **.NET 10 SDK** để build (repo nhắm `net48` và `net10.0-windows`).
- dnSpy đã build đầy đủ (xem `README.md` gốc của repo và `CLAUDE.md`).

---

## 3. Build extension

Extension nằm trong solution `dnSpy.sln` và build cùng dnSpy. Để build riêng extension:

```powershell
# nhớ init submodule của repo trước (bắt buộc cho toàn repo)
git submodule update --init --recursive

# build extension — ra thẳng thư mục bin của dnSpy
dotnet build Extensions\dnSpy.MCP\dnSpy.MCP.csproj -c Release -f net10.0-windows
```

Output: `dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.MCP.x.dll` (đặt cạnh `dnSpy.exe`). Đuôi `.x.dll` là điều kiện để dnSpy nạp extension.

Khi build cả dnSpy bằng `build.ps1`, extension được đóng gói sẵn — không cần bước riêng.

---

## 4. Kích hoạt trong dnSpy

Không cần thao tác gì thêm: chỉ cần `dnSpy.MCP.x.dll` nằm trong thư mục `bin` (hoặc `Extensions\`) cạnh `dnSpy.exe`. Server tự khởi động khi dnSpy nạp xong và tự dừng khi thoát.

Đổi cổng (nếu 27115 bị chiếm) — đặt biến môi trường **trước khi** mở dnSpy:

```powershell
$env:DNSPY_MCP_PORT = "31000"
.\dnSpy.exe
```

Kiểm tra server sống:

```powershell
curl.exe http://127.0.0.1:27115/mcp
# → "dnSpy MCP server"
```

---

## 5. Kết nối MCP client

### Claude Code

```sh
claude mcp add --transport http dnspy http://127.0.0.1:27115/mcp
```

### File cấu hình MCP (chung)

```json
{
  "mcpServers": {
    "dnspy": { "type": "http", "url": "http://127.0.0.1:27115/mcp" }
  }
}
```

### Nếu bật token (xem [mục 8](#8-bảo-mật))

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "http",
      "url": "http://127.0.0.1:27115/mcp",
      "headers": { "Authorization": "Bearer <token-của-bạn>" }
    }
  }
}
```

Kiểm tra thủ công bằng curl:

```sh
curl -s http://127.0.0.1:27115/mcp -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

---

## 6. Danh sách tool

Kết quả trả về là JSON dạng text. Lỗi trả về dưới dạng MCP tool error (`isError: true`) kèm thông báo.

### Điều khiển phiên

| Tool | Tham số | Mô tả |
|---|---|---|
| `dbg_status` | — | Trạng thái phiên: đang debug? đang chạy/paused? danh sách tiến trình. |
| `dbg_start` | `path` (bắt buộc), `args`, `working_dir`, `break_at_entry`, `runtime` | Khởi động 1 file .exe/.dll để debug. Tự nhận .NET (Core/5+) hay .NET Framework; file self-contained thì truyền `runtime` = `net` hoặc `netfx`. |
| `dbg_attach` | `pid` hoặc `name` | Attach vào tiến trình .NET đang chạy (name cho phép ký tự đại diện `*` `?`). |
| `dbg_break` | — | Tạm dừng (pause) tất cả tiến trình. |
| `dbg_continue` | — | Chạy tiếp tất cả. |
| `dbg_stop` | — | Kết thúc debug (terminate). |
| `dbg_restart` | — | Khởi động lại phiên hiện tại. |
| `dbg_step` | `kind` = `into`\|`over`\|`out` (bắt buộc), `timeout_ms` | Step luồng đang paused và chờ hoàn thành, trả về frame trên cùng. |
| `dbg_wait_for_break` | `timeout_ms` | Chặn cho tới khi có tiến trình paused (trúng breakpoint / step xong / break), hoặc timeout. |

### Breakpoint (theo IL offset)

Breakpoint được xác định bằng **tên module + metadata token của method + IL offset**. Đặt được cả trước khi tiến trình chạy (sẽ bind khi module được nạp).

| Tool | Tham số | Mô tả |
|---|---|---|
| `bp_add` | `module` (bắt buộc), `token` (bắt buộc), `il_offset`, `condition`, `enabled` | Thêm breakpoint. `token`/`il_offset` chấp nhận hex (`0x06000001`) hoặc thập phân. `condition` là biểu thức C#/VB. |
| `bp_list` | — | Liệt kê breakpoint: id, vị trí, bật/tắt, số lần trúng, số bound. |
| `bp_remove` | `id` hoặc `all` | Xóa 1 breakpoint theo id, hoặc tất cả. |
| `bp_toggle` | `id`, `enabled` | Bật/tắt breakpoint. |

### Kiểm tra (yêu cầu tiến trình đang paused)

| Tool | Tham số | Mô tả |
|---|---|---|
| `dbg_threads` | — | Liệt kê thread (id, managedId, tên, kind, state). |
| `dbg_modules` | — | Liệt kê module đã nạp (tên, đường dẫn, địa chỉ, kích thước, dynamic/in-memory). |
| `dbg_callstack` | `thread_id`, `max_frames` | Call stack của thread paused (mặc định thread hiện tại, tối đa 200 frame, giới hạn 1000). |
| `dbg_locals` | `frame_index`, `thread_id` | Biến local + tham số của 1 frame (mặc định frame 0 = trên cùng). |
| `dbg_eval` | `expression` (bắt buộc), `frame_index`, `thread_id` | Đánh giá biểu thức C#/VB trong ngữ cảnh 1 frame. |

---

## 7. Quy trình debug điển hình

Ví dụ: đặt breakpoint tại một method, chạy tới đó, xem biến, đánh giá biểu thức, step.

Trong dnSpy, mở assembly cần debug, tìm method mục tiêu, xem **metadata token** của nó (ví dụ trong cửa sổ hex/metadata, hoặc tab thông tin method). Giả sử module `MyApp.dll`, token `0x06000042`.

```jsonc
// 1. Đặt breakpoint đầu method
tools/call bp_add { "module": "MyApp.dll", "token": "0x06000042", "il_offset": 0 }

// 2. Khởi động, dừng ở entry point
tools/call dbg_start { "path": "C:\\path\\MyApp.exe", "break_at_entry": true }

// 3. Chạy tiếp, chờ trúng breakpoint
tools/call dbg_continue {}
tools/call dbg_wait_for_break { "timeout_ms": 30000 }
//    → trả về thread + frame trên cùng khi paused

// 4. Xem ngữ cảnh
tools/call dbg_callstack {}
tools/call dbg_locals { "frame_index": 0 }

// 5. Đánh giá biểu thức
tools/call dbg_eval { "expression": "this.count + 1" }

// 6. Step qua vài dòng
tools/call dbg_step { "kind": "over" }
tools/call dbg_step { "kind": "into" }

// 7. Chạy tiếp / kết thúc
tools/call dbg_continue {}
tools/call dbg_stop {}
```

Với agent (Claude Code), bạn chỉ cần mô tả bằng ngôn ngữ tự nhiên, ví dụ: *"Đặt breakpoint tại token 0x06000042 trong MyApp.dll, chạy MyApp.exe, khi dừng thì cho tôi xem call stack và giá trị biến local"* — agent tự gọi các tool tương ứng.

---

## 8. Bảo mật

Các tool này **thực thi mã** (khởi động tiến trình, đánh giá biểu thức), nên endpoint được bảo vệ:

- **Chỉ loopback** — bind `127.0.0.1`, không máy từ xa nào truy cập trực tiếp được.
- **Chống CSRF / DNS-rebinding** — request mang header `Origin` của trình duyệt, hoặc header `Host` không phải loopback, đều bị từ chối (403). Ngăn một trang web độc bạn vô tình mở điều khiển được debugger. Chỉ phục vụ đường dẫn `/mcp`, body giới hạn 4 MB.
- **Token tùy chọn** — đặt `DNSPY_MCP_TOKEN` trước khi mở dnSpy để bắt buộc mọi request phải có `Authorization: Bearer <token>`. Dùng khi muốn chặn cả tiến trình cục bộ khác trên máy.

```powershell
$env:DNSPY_MCP_TOKEN = "chuoi-bi-mat-ngau-nhien"
.\dnSpy.exe
```

Lưu ý: cơ chế trên **không** cô lập bạn khỏi phần mềm khác chạy dưới cùng user của bạn (trừ khi bật token). Hãy coi `dbg_start` / `dbg_eval` là công cụ thực thi mã và chỉ kết nối MCP client mà bạn tin tưởng.

---

## 9. Dùng từ macOS / Linux

Server bắt buộc Windows, nhưng MCP client chạy ở đâu cũng được. Nếu dnSpy chạy trong máy ảo Windows (Parallels/UTM/VMware) hoặc máy Windows từ xa:

Vì server chỉ bind loopback, cần forward cổng về máy của bạn:

```sh
# tunnel cổng 27115 từ máy Windows về máy local qua SSH
ssh -L 27115:127.0.0.1:27115 user@windows-host
```

Sau đó trỏ MCP client tới `http://127.0.0.1:27115/mcp` như bình thường. Guard Host vẫn cho qua vì Host = loopback.

> **Wine/CrossOver:** dnSpy có `WineFixes.cs` nên giao diện chạy được trên Wine, nhưng debug engine CorDebug cần API Windows thật — Wine không cung cấp đủ, nên tính năng debug không đáng tin. Khuyến nghị dùng máy ảo Windows thật.

---

## 10. Khắc phục sự cố

| Triệu chứng | Nguyên nhân / cách xử lý |
|---|---|
| `curl` báo connection refused | Server chưa chạy: kiểm tra `dnSpy.MCP.x.dll` có trong `bin` cạnh `dnSpy.exe`; kiểm tra cổng qua `DNSPY_MCP_PORT`. dnSpy đã nạp xong chưa. |
| Server không khởi động, log `failed to start MCP server` | Cổng đang bị chiếm. Đặt `DNSPY_MCP_PORT` sang cổng khác rồi mở lại dnSpy. |
| Tool trả về `not debugging` / `no paused thread; break first` | Tool kiểm tra yêu cầu tiến trình đang paused. Gọi `dbg_break` hoặc chờ trúng breakpoint trước. |
| `403 forbidden` | Client gửi header `Origin` hoặc `Host` không phải loopback. Dùng MCP client trực tiếp (không qua trình duyệt); nếu tunnel, đảm bảo trỏ tới `127.0.0.1`. |
| `401 unauthorized` | Đã bật `DNSPY_MCP_TOKEN` nhưng client chưa gửi `Authorization: Bearer`. Thêm header token vào cấu hình client. |
| `dbg_start` chọn sai runtime | File self-contained không có `.runtimeconfig.json`: truyền `runtime` = `net` hoặc `netfx` tường minh. |
| Log của server ở đâu | Ghi qua `Debug.WriteLine` (tiền tố `[dnSpy.MCP]`) — xem bằng DebugView hoặc khi chạy dnSpy dưới debugger. |
