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
- **Bitness phải khớp với target.** CorDebug debug in-process theo bitness: dnSpy x64 chỉ debug được target **x64**, dnSpy x86 chỉ debug được target **x86**. Rất nhiều app .NET Framework là **x86 (32-bit)** — muốn debug chúng, dùng bản dnSpy x86 (xem dưới). Không chắc bitness của target thì `dnspy_info` trên bản đang chạy + `dbg_status` sau khi launch (trường `bitness`) sẽ cho biết.

Build bản x86 khi cần debug target 32-bit:

```powershell
./build.ps1 -buildtfm net-x86 -NoMsbuild
# → dnSpy\dnSpy\bin\Release\net10.0-windows\win-x86\publish\dnSpy.exe (self-contained, kèm extension MCP)
```

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
# → 401 kèm đường dẫn file token. Nhận được 401 tức server đã sống;
#   thêm header token thì ra "dnSpy MCP server".
```

---

## 5. Kết nối MCP client

### Claude Code

```sh
$token = Get-Content "$env:APPDATA\dnSpy\mcp-token.txt"
claude mcp add --transport http dnspy http://127.0.0.1:27115/mcp `
  --header "Authorization: Bearer $token"
```

### File cấu hình MCP (chung)

```json
{
  "mcpServers": {
    "dnspy": { "type": "http", "url": "http://127.0.0.1:27115/mcp" }
  }
}
```

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

### Endpoint & phân tích tĩnh (không cần phiên debug)

Đọc và khám phá assembly ngay trên đĩa (hoặc đã mở trong dnSpy) mà không cần chạy nó — điểm khởi đầu khi reverse một assembly lạ.

| Tool | Tham số | Mô tả |
|---|---|---|
| `dnspy_info` | — | Đây là dnSpy nào: phiên bản, cổng, số tool, xác thực có bật không + nguồn token, file settings đang dùng. |
| `list_types` | `module` (bắt buộc), `filter`, `max` | Liệt kê type trong module (tên đầy đủ, token, kind); `filter` là pattern `*`/`?`. |
| `list_methods` | `module`, `type` (bắt buộc) | Liệt kê method của 1 type kèm token + chữ ký — đưa token cho `bp_add`/`decompile`. |
| `decompile` | `module` (bắt buộc), `method`\|`type`\|`token`, `format` | Dịch ngược 1 method (mọi overload)/type/token → C#; `format="il"` để xem IL (opcode + offset, hữu ích với code bị obfuscate). |
| `search` | `module`, `query` (bắt buộc), `kind`, `max` | Tìm tên member (`*`/`?`) và/hoặc chuỗi literal trong thân method, kèm vị trí + token. |
| `find_references` | `module` (bắt buộc), `method`\|`token`, `scope`, `max` | Các method **gọi** method đích (call graph ngược); `scope="open"` để quét mọi assembly đang mở. |
| `find_implementations` | `module` (bắt buộc), `method`\|`token`, `scope`, `max` | Các method **override/hiện thực** một method ảo/abstract/interface (chiều xuôi của cây kế thừa) — bổ sung cho `find_references`. |
| `extract_iocs` | `module` (bắt buộc), `categories`, `max` | Trích IOC bằng đọc tĩnh: URL, IP, khóa registry, đường dẫn file, email trong chuỗi literal + import P/Invoke, mỗi cái gắn với method chứa nó. Phục vụ triage / báo cáo phân tích mã độc. `categories` lọc theo `url,ip,registry,path,email,pinvoke,base64` (mặc định tất cả trừ `base64`). |

### Điều khiển phiên

| Tool | Tham số | Mô tả |
|---|---|---|
| `dbg_status` | — | Trạng thái phiên: đang debug? đang chạy/paused? danh sách tiến trình. |
| `dbg_start` | `path` (bắt buộc), `args`, `working_dir`, `break_at_entry`, `runtime` | Khởi động 1 file .exe/.dll để debug. Tự nhận .NET (Core/5+) hay .NET Framework; file self-contained thì truyền `runtime` = `net` hoặc `netfx`. |
| `dbg_list_attachable` | `name` | Liệt kê tiến trình .NET có thể attach. |
| `dbg_attach` | `pid` hoặc `name` | Attach vào tiến trình .NET đang chạy (name cho phép ký tự đại diện `*` `?`). |
| `dbg_set_thread` | `thread_id` | Đặt thread hiện tại làm ngữ cảnh mặc định cho callstack/locals/eval. |
| `dbg_break` | — | Tạm dừng (pause) tất cả tiến trình. |
| `dbg_continue` | — | Chạy tiếp tất cả. |
| `dbg_stop` | — | Kết thúc debug (terminate). |
| `dbg_restart` | — | Khởi động lại phiên hiện tại. |
| `dbg_step` | `kind` = `into`\|`over`\|`out` (bắt buộc), `timeout_ms` | Step luồng đang paused và chờ hoàn thành, trả về frame trên cùng. |
| `dbg_wait_for_break` | `timeout_ms` | Chặn cho tới khi có tiến trình paused (trúng breakpoint / step xong / break), hoặc timeout. |

### Breakpoint

Đặt được cả trước khi tiến trình chạy (sẽ bind khi module được nạp).

| Tool | Tham số | Mô tả |
|---|---|---|
| `bp_add` | `module`, `token` (bắt buộc), `il_offset`, `condition`, `hit_count`, `enabled` | Breakpoint theo metadata token. `token`/`il_offset` nhận hex (`0x06000001`) hoặc thập phân. `hit_count` = chỉ dừng sau N lần trúng. |
| `bp_add_method` | `module`, `method` (bắt buộc), `condition`, `enabled` | Breakpoint theo tên method đầy đủ (`MyApp.Program.Main`) — không cần token. Đặt cho mọi overload. Module là đường dẫn hoặc đã mở trong dnSpy. |
| `bp_add_line` | `module`, `method`, `line` (bắt buộc), `enabled` | Breakpoint theo dòng nguồn trong method (cần PDB). |
| `bp_list` | — | Liệt kê breakpoint: id, vị trí, bật/tắt, số lần trúng, số bound. |
| `bp_remove` | `id` hoặc `all` | Xóa 1 breakpoint theo id, hoặc tất cả. |
| `bp_toggle` | `id`, `enabled` | Bật/tắt breakpoint. |
| `mbp_add` | `module_name` (pattern, bắt buộc), `enabled` | Module-load breakpoint: dừng khi module khớp pattern được nạp. |
| `mbp_list` / `mbp_remove` | — / `id`\|`all` | Liệt kê / xóa module breakpoint. |
| `exc_break` | `exception` (tên đầy đủ hoặc `all`), `enabled` | Bật/tắt dừng khi ném exception CLR (first-chance). |

### Kiểm tra (yêu cầu tiến trình đang paused)

| Tool | Tham số | Mô tả |
|---|---|---|
| `dbg_threads` | — | Liệt kê thread (id, managedId, tên, kind, state). |
| `dbg_modules` | — | Liệt kê module đã nạp (tên, đường dẫn, địa chỉ, kích thước, dynamic/in-memory). |
| `dbg_callstack` | `thread_id`, `max_frames` | Call stack của thread paused (mặc định thread hiện tại, tối đa 200 frame, giới hạn 1000). |
| `dbg_locals` | `frame_index`, `thread_id` | Biến local + tham số của 1 frame (mặc định frame 0 = trên cùng). |
| `dbg_variables` | `kind` = `returns`\|`statics`\|`exceptions`, `frame_index`, `thread_id` | Liệt kê nhóm biến khác. `autos` chưa được engine .NET của dnSpy hiện thực — tool báo lỗi rõ thay vì trả về giá trị giả. |
| `dbg_eval` | `expression` (bắt buộc), `frame_index`, `thread_id` | Đánh giá biểu thức C#/VB trong ngữ cảnh 1 frame. |
| `dbg_expand` | `expression` (bắt buộc), `frame_index`, `max_children`, `thread_id` | Liệt kê thành viên con (field/phần tử) của 1 biểu thức — drill vào object/mảng. |
| `dbg_set_variable` | `target`, `value` (bắt buộc), `frame_index`, `thread_id` | Gán giá trị mới cho biến/biểu thức. |
| `dbg_set_next_statement` | `il_offset` (bắt buộc), `thread_id` | Dời con trỏ thực thi tới IL offset khác trong cùng method. |
| `dbg_read_memory` | `address`, `size` (bắt buộc), `pid` | Đọc byte thô từ memory tiến trình, trả về hex (tối đa 65536 byte). |
| `dbg_write_memory` | `address`, `bytes` (bắt buộc), `pid` | Ghi byte thô (hex) vào memory tiến trình. |

Tool chỉ đọc mang annotation `readOnlyHint`; tool thay đổi tiến trình (`dbg_start`, `dbg_write_memory`, `dbg_set_variable`, …) mang `destructiveHint` để client cảnh báo.

### Nhận sự kiện realtime (SSE)

Client có thể mở stream `GET /mcp` với header `Accept: text/event-stream`. Server đẩy notification JSON-RPC `notifications/paused` mỗi khi tiến trình dừng (trúng breakpoint / step xong / break), giúp agent phản ứng tức thì mà không cần poll `dbg_wait_for_break`.

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

- **Token bắt buộc, bật sẵn** — lần chạy đầu tiên server tự sinh token và lưu vào `mcp-token.txt` **đặt cạnh file settings của dnSpy**. Token giữ nguyên qua các lần khởi động nên bạn chỉ cấu hình client một lần. Mọi request phải có `Authorization: Bearer <token>`.
- **Chỉ loopback** — bind `127.0.0.1`, không máy từ xa nào truy cập trực tiếp được.
- **Chống CSRF / DNS-rebinding** — request mang header `Origin` của trình duyệt, hoặc header `Host` không phải loopback, đều bị từ chối (403). Ngăn một trang web độc bạn vô tình mở điều khiển được debugger. Chỉ phục vụ đường dẫn `/mcp`, body giới hạn 4 MB.

Lấy token:

```powershell
Get-Content "$env:APPDATA\dnSpy\mcp-token.txt"
```

Quên token thì không cần đi tra tài liệu — phản hồi `401` tự ghi rõ đường dẫn file token.

Muốn tự đặt token thay vì dùng token sinh tự động:

```powershell
$env:DNSPY_MCP_TOKEN = "chuoi-bi-mat-cua-ban"
.\dnSpy.exe
```

Tắt hẳn xác thực (chỉ khi bạn hiểu rõ hệ quả — mọi tiến trình chạy dưới user của bạn sẽ toàn quyền điều khiển debugger):

```powershell
$env:DNSPY_MCP_NO_AUTH = "1"
```

Token nằm cạnh file settings chứ không ở đường dẫn cố định, nên nó đi theo `--settings-file`: một dnSpy chạy tạm sẽ có token riêng và không đọc được token thật.

Gọi `dnspy_info` bất cứ lúc nào để biết mình đang nối tới dnSpy nào, xác thực có bật không và nó đang dùng file settings nào.

---

## 9. Dùng từ macOS / Linux

Server bắt buộc Windows, nhưng MCP client chạy ở đâu cũng được. Nếu dnSpy chạy trong máy ảo Windows (Parallels/UTM/VMware) hoặc máy Windows từ xa.

### Chạy trong Parallels Desktop (build & test runtime)

1. **Chia sẻ mã nguồn vào VM.** Parallels tự mount thư mục Mac: trong VM Windows, repo nằm tại `\\Mac\Home\GolandProjects\github.com\lang315\dnSpy` (hoặc bật *Share Mac → Home folder*). Có thể build ngay từ đó, hoặc copy repo vào ổ đĩa VM cho nhanh.
2. **Cài .NET 10 SDK** trong VM (https://dot.net) nếu chưa có.
3. **Init submodule + build dnSpy** (PowerShell trong VM):
   ```powershell
   cd C:\dnSpy            # hoặc \\Mac\Home\...\dnSpy
   git submodule update --init --recursive
   .\build.ps1 net
   ```
   Extension `dnSpy.MCP.x.dll` được đóng gói cùng vào `dnSpy\dnSpy\bin\Release\net10.0-windows\`.
4. **Chạy dnSpy** từ thư mục build đó. Kiểm tra server:
   ```powershell
   curl.exe http://127.0.0.1:27115/mcp     # → "dnSpy MCP server"
   ```
5. **Verify tool** (trong VM): mở 1 .NET exe bằng dnSpy, rồi:
   ```powershell
   curl.exe http://127.0.0.1:27115/mcp -H "content-type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"dbg_status\",\"arguments\":{}}}"
   ```

### Kết nối MCP client trên macOS (host)

Server bind loopback trong VM. Forward cổng ra host qua SSH (bật OpenSSH Server trong Windows) hoặc dùng IP của VM nếu Parallels ở chế độ *Shared/Bridged* — nhưng khi đó Host header không còn là loopback nên guard sẽ chặn. Cách sạch nhất là **tunnel về loopback**:

```sh
# trên macOS host — tunnel cổng 27115 của VM về loopback máy Mac
ssh -L 27115:127.0.0.1:27115 user@<địa-chỉ-VM-Windows>
```

Rồi trỏ MCP client tới `http://127.0.0.1:27115/mcp`. Guard Host cho qua vì Host = loopback. (Nếu bật `DNSPY_MCP_TOKEN`, thêm header `Authorization: Bearer <token>`.)

> **Wine/CrossOver:** dnSpy có `WineFixes.cs` nên giao diện chạy được trên Wine, nhưng debug engine CorDebug cần API Windows thật — Wine không cung cấp đủ, nên tính năng debug không đáng tin. Khuyến nghị dùng máy ảo Windows thật.

> **Lưu ý Windows ARM64 (Parallels trên Apple Silicon):** đã kiểm thực tế — server, đầy đủ tool, `bp_add_method` (bind + hit), `dbg_attach`, break/wait, threads/modules/callstack chạy đúng. Nhưng `dbg_start` (CorDebug **launch**) không tạo được process, và `dbg_locals`/`dbg_eval`/`dbg_set_variable` trả "Internal debugger error" — đây là giới hạn func-eval/launch của CorDebug trên ARM64, không phải lỗi extension. Trên ARM64 hãy **attach** vào tiến trình đang chạy thay vì launch. Để dùng đầy đủ (launch + eval), chạy Windows **x64** (RID chính thức của dnSpy là win-x86/win-x64).
>
> Khi cài SDK vào thư mục riêng (vd `C:\dotnet10`), đặt `DOTNET_ROOT` trỏ tới đó để dnSpy.exe (apphost) tìm được .NET Desktop runtime; và dành cổng cho HttpListener nếu chạy non-admin: `netsh http add urlacl url=http://127.0.0.1:27115/ user=Everyone`.

> **Windows x64 — đã kiểm thực tế đầy đủ (2026-08-15):** trên Windows 10 x64, **toàn bộ luồng debug lõi chạy thật** — `dbg_start` (launch), `dbg_attach`, `bp_add_method` bind+hit, `dbg_step`, `dbg_callstack`, `dbg_locals`, `dbg_eval` (kể cả gọi method: `System.Math.Max(a,b)`), `dbg_set_variable`, `dbg_set_next_statement`. Các "Internal debugger error" thấy ở ARM64 **không xuất hiện** trên x64. Vài lưu ý dùng:
> - `dbg_start` nhận cả `.exe` (apphost) lẫn `.dll`; nếu truyền apphost `.exe` có `.dll`+`.runtimeconfig.json` cạnh bên, tool tự debug thẳng `.dll` để breakpoint đặt trước khi launch bind được.
> - Debuggee phải cùng bitness với dnSpy (dnSpy x64 ⇒ target x64).
> - `dbg_variables kind=autos` báo lỗi "not implemented" (provider autos của dnSpy là stub) — dùng `dbg_locals` hoặc `kind=statics/returns/exceptions`.

---

## 10. Khắc phục sự cố

| Triệu chứng | Nguyên nhân / cách xử lý |
|---|---|
| `curl` báo connection refused | Server chưa chạy: kiểm tra `dnSpy.MCP.x.dll` có trong `bin` cạnh `dnSpy.exe`; kiểm tra cổng qua `DNSPY_MCP_PORT`. dnSpy đã nạp xong chưa. |
| Server không khởi động, log `failed to start MCP server` | Cổng đang bị chiếm. Đặt `DNSPY_MCP_PORT` sang cổng khác rồi mở lại dnSpy. |
| Tool trả về `not debugging` / `no paused thread; break first` | Tool kiểm tra yêu cầu tiến trình đang paused. Gọi `dbg_break` hoặc chờ trúng breakpoint trước. |
| `403 forbidden` | Client gửi header `Origin` hoặc `Host` không phải loopback. Dùng MCP client trực tiếp (không qua trình duyệt); nếu tunnel, đảm bảo trỏ tới `127.0.0.1`. |
| `401 unauthorized` | Client chưa gửi `Authorization: Bearer`, hoặc gửi sai token. Chính thân phản hồi 401 ghi đường dẫn file token — đọc file đó rồi thêm header vào cấu hình client. |
| `dbg_start` chọn sai runtime | File self-contained không có `.runtimeconfig.json`: truyền `runtime` = `net` hoặc `netfx` tường minh. |
| Log của server ở đâu | Ghi qua `Debug.WriteLine` (tiền tố `[dnSpy.MCP]`) — xem bằng DebugView hoặc khi chạy dnSpy dưới debugger. |
