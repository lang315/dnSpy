# dnSpy.MCP — Kế hoạch integration testing

Ngày: 2026-08-19

## Cập nhật: đã triển khai và chạy thật (2026-08-19)

Kế hoạch đã thực hiện xong. **Tier 1: 71/71 xanh. Tier 2: 33/33 xanh** trên Windows 10 x64 với
dnSpy build đầy đủ (net10.0-windows) và app mồi `dbgtest`.

Kiểm chứng test có tác dụng: khôi phục `Server/McpServer.cs` về bản trước khi sửa review làm **đúng
ba** test Tier 1 đỏ — chặn body chunked, cảnh báo thiếu token, và long-poll starvation. Hai fix còn
lại (so sánh token hằng-thời-gian, strip tiền tố hex) là thuộc tính về timing/robustness mà test
chức năng không phân biệt được; ghi nhận thẳng thay vì giả vờ đã phủ.

**Chạy thật phát hiện thêm 2 defect của extension, đã sửa:**

1. **Func-eval bị chặn ở 1 giây.** `InspectionTools` gọi `language.CreateContext(...)` mà không
   truyền `funcEvalTimeout`, nên rơi vào `DbgLanguage.DefaultFuncEvalTimeout = 1s`. Mọi biểu thức
   gọi method/property chậm hơn 1s trả "Evaluation timed out". Đây cũng là lý do fix timeout
   dispatcher 60s (#3 của review) vô nghĩa với func-eval — cap của dnSpy bắn trước. Sửa: truyền 30s.
2. **Số được format theo hệ hex.** `DbgValueFormatterOptions.None` nghĩa là theo mặc định của dnSpy,
   mà mặc định là hexadecimal — `a + b` trả `0x00000008`. Tệ hơn, định dạng phụ thuộc một toggle UI
   mà agent không nhìn thấy. Sửa: truyền `DbgValueFormatterOptions.Decimal` để output ổn định.

**Hai cạm bẫy vận hành, đã xử lý trong harness:**

- **dnSpy crash khi hủy process đang pause.** AV `0xC0000005` trong `dnlib` đọc PE image từ bộ nhớ
  debuggee đã giải phóng, kích hoạt từ `ValueNodesVM.RecreateRootChildren_UI` — cửa sổ Locals của
  chính dnSpy tự refresh khi call stack đổi. Không có frame nào của `dnSpy.MCP` trong stack, nhưng
  agent điều khiển nhanh thì đua được với nó. `Dbg.Reset()` nay continue trước rồi mới stop.
- **Suite xóa breakpoint thật của người dùng.** dnSpy lưu breakpoint vào `%APPDATA%\dnSpy\dnSpy.xml`
  khi thoát sạch; suite gọi `bp_remove all=true` giữa các test. Bắt buộc chạy dnSpy với
  `--settings-file <temp>`; `run-integration.ps1` đã làm vậy.

Thực tế khác kế hoạch: test đặt ở `tests/` gốc repo, không phải `Extensions/dnSpy.MCP/tests/` — lồng
project vào trong extension khiến glob `**/*.cs` của nó nuốt luôn file test vào assembly shipping.

Bối cảnh: sau commit "Fix review findings in dnSpy.MCP" (7 lỗi từ review). Roadmap mục 19 còn treo:
smoke test đang ở scratchpad, chưa có gì trong repo bảo vệ regression.

## Mục tiêu

1. **Biến verify thủ công thành tự động.** Việc verify runtime trên Windows x64 (roadmap 2026-08-15)
   đã chứng minh luồng debug chạy thật, nhưng làm bằng tay — không lặp lại được, không chặn regression.
2. **Khóa 7 fix vừa làm.** Mỗi fix phải có ít nhất một test đỏ-trên-code-cũ.
3. **Tách phần chạy được trong CI** khỏi phần bắt buộc cần dnSpy GUI + debug engine thật.

## Ràng buộc phải thiết kế quanh nó

| Ràng buộc | Hệ quả |
|---|---|
| MCP server sống **bên trong** `dnSpy.exe` (WPF) | Không thể test in-process kiểu thường; phải hoặc dựng `McpServer` riêng, hoặc chạy dnSpy thật |
| dnSpy cần **phiên desktop tương tác** | Session 0 isolation: verify x64 trước đây phải chạy qua scheduled task interactive. CI headless sẽ fail |
| Debug engine là **CorDebug native** | Kết quả khác nhau giữa x64 và ARM64 (roadmap: `dbg_start` launch + func-eval fail trên ARM64) |
| dnSpy **lưu breakpoint vào settings** | State bleed giữa các lần chạy — phải reset đầu và cuối mỗi run |
| Dispatcher debugger là **một luồng duy nhất** | Test song song sẽ tự chặn nhau; phải tuần tự hóa trong mỗi phiên |

## Kiến trúc 3 tầng

Chia theo "cần gì để chạy", vì đó là thứ quyết định test nào vào được CI.

```
Tier 1  protocol/security     không cần dnSpy   → CI mọi PR      (~10s)
Tier 2  debug engine thật     cần dnSpy + GUI   → gate thủ công  (~5-10ph)
Tier 3  MCP client thật       cần client ngoài  → trước release
```

---

## Tier 1 — Test protocol & security (không cần dnSpy)

**Ý tưởng cốt lõi:** `McpServer`, `JsonRpc`, `ToolDef`, `JsonUtils` là code thuần — không chạm WPF,
không chạm `DbgManager`. Có thể `new McpServer(port, stubTools, log, token)` ngay trong test process
và bắn HTTP vào nó. Đây là phần bảo vệ được bằng CI, và nó phủ 4/7 fix.

**Vị trí:** `Extensions/dnSpy.MCP/tests/dnSpy.MCP.Tests/` — xUnit, target `net10.0-windows`,
chỉ `ProjectReference` tới `dnSpy.MCP.csproj`. Cần thêm `InternalsVisibleTo` vào `dnSpy.MCP.csproj`
(thay đổi nhỏ, không ảnh hưởng runtime).

**Fixture:** vài `ToolDef` giả — một readOnly, một destructive, một ném exception, một trả JSON,
một sleep để test long-poll. Port cấp phát động (bind port 0 rồi lấy số) để chạy song song được.

### Ca kiểm thử

**Protocol**
- `initialize` → có `protocolVersion`, `capabilities.tools`, `serverInfo.name == "dnSpy"`
- Echo version: gửi `2025-06-18` → nhận lại đúng; gửi `1999-01-01` → fallback `2024-11-05`
- `ping` → `{}`
- `tools/list` → đủ tool, mỗi tool có `name`/`description`/`inputSchema`; `readOnlyHint` và
  `destructiveHint` gắn đúng chỗ
- Notification (không `id`) → HTTP 202, body rỗng
- Method lạ → JSON-RPC `-32601`; JSON hỏng → HTTP 400 + `-32700`; tool lạ → `-32602`
- **Tool ném exception → `result.isError == true`, KHÔNG phải JSON-RPC error** (đúng ngữ nghĩa MCP —
  dễ làm sai khi refactor)

**Security / hardening**
- Có header `Origin` → 403 *(chống DNS-rebinding)*
- `Host` không phải loopback → 403
- `GET /` → 200 liveness; path lạ → 404; `PUT` → 405
- **[fix #2]** Không token → log chứa cảnh báo `DNSPY_MCP_TOKEN is not set`
- **[fix #5]** Có token: thiếu `Authorization` → 401; sai token → 401; **token sai độ dài** → 401
  (ca này chính là thứ vòng lặp cũ xử lý sai); đúng token → 200
- **[fix #6]** Body chunked (`Transfer-Encoding: chunked`, không `Content-Length`) vượt 4 MB → 413.
  *Code cũ đọc vô hạn — test này đỏ trên bản cũ.*
- `Content-Length` vượt 4 MB → 413

**JsonUtils**
- **[fix #7]** `"0xDEAD"`, `"DE AD"`, `"0x90 0x90"`, `"DE-AD"`, `"de,ad"` parse ra đúng byte;
  độ dài lẻ → lỗi; ký tự không hex → lỗi
- `ParseULong` hex/dec, tràn số → lỗi rõ ràng
- `Clamp` biên

**SSE (không cần debugger)**
- Mở `GET /mcp` với `Accept: text/event-stream` → 200 và giữ mở
- Gọi `Broadcast(...)` trực tiếp → client nhận `event: message` + JSON đúng
- Heartbeat `: ping` tới trong ~16s
- Client ngắt giữa chừng → server prune, `Broadcast` sau đó không ném, server vẫn phục vụ request khác

**[fix #4] Concurrency**
- Mở N=8 request tới stub tool "sleep 30s", rồi gọi `tools/list` → phải trả về **< 2s**.
  Chạy kèm `ThreadPool.SetMinThreads(2,2)` để ép lộ starvation: bản cũ (`QueueUserWorkItem`) sẽ treo,
  bản mới (thread riêng) thì không.

---

## Tier 2 — Integration với debug engine thật

Cần `dnSpy.exe` chạy trên desktop tương tác + app mồi.

### App mồi (`tests/fixture/dbgtest`)

Kế thừa fixture đã dùng khi verify x64, bổ sung cho các ca mới. Giá trị **tất định**, không random:

| Thành phần | Dùng cho |
|---|---|
| `DbgTest.Program.Add(int,int)` | breakpoint theo tên/token, locals `a`/`b`, func-eval |
| `Add(int,int,int)` overload | "đặt bp trên mọi overload" |
| `DbgTest.Outer.Inner.Ping()` (nested) | fallback `.` → `+` khi resolve type |
| `DbgTest.Slow.SlowProperty` (sleep ~15s) | **[fix #3]** timeout eval |
| static field + array + object lồng | `dbg_variables statics`, `dbg_expand` |
| ném + bắt `InvalidOperationException` | `exc_break` |
| vòng lặp có bộ đếm | `hit_count`, `condition` |

Build **cả `net48` lẫn `net8.0`** (kèm apphost `.exe` + `.dll` + `.runtimeconfig.json`) để phủ heuristic
`IsDotNetCore` và cú swap apphost→dll trong `dbg_start`. Có PDB để test `bp_add_line`.

### Ca kiểm thử

**Phiên debug**
- `dbg_status` lúc rảnh → `isDebugging false`
- `dbg_start` (.dll net8) → chạy; `dbg_stop` → dừng; `dbg_restart`
- **[regression]** `dbg_start` bằng apphost `.exe` → tự chuyển sang `.dll`, và **breakpoint đặt TRƯỚC
  khi start vẫn bind** (`boundCount ≥ 1`) — đây là bug đã sửa ở commit trước, phải giữ
- **[regression]** `dbg_list_attachable` có filter `name` → không AV, khớp đúng
- `dbg_attach` theo pid và theo name; `dbg_break`/`dbg_continue`; `dbg_set_thread`

**Breakpoint**
- **[fix #1 — ca quan trọng nhất]** `bp_add` với **tên module trần** (`dbgtest.dll`) →
  `boundCount ≥ 1` và tiến trình **dừng thật** tại đó.
  *Trên code cũ `ModuleId.Create("dbgtest.dll")` resolve theo thư mục dnSpy nên không bao giờ bind —
  test này là đỏ/xanh rõ ràng.*
- `bp_add` với full path → cùng kết quả
- `bp_add` module không tồn tại → lỗi rõ ràng (không im lặng không-bind)
- `bp_add_method` `DbgTest.Program.Add` → token `0x06000001`, cả 2 overload
- `bp_add_method` type lồng qua tên có dấu `.`
- `bp_add_line` (cần PDB) → bind đúng dòng
- `condition` → chỉ dừng khi đúng; `hit_count` → dừng ở lần thứ N
- `bp_list`/`bp_toggle`/`bp_remove(id)`/`bp_remove(all)`; đặt trùng → báo "already exists"
- `mbp_*` → dừng khi module nạp; `exc_break` → dừng khi ném exception

**Inspection khi đang dừng**
- `dbg_wait_for_break` → trả topFrame JSON khi bp hit
- `dbg_callstack` → format đúng `dbgtest.dll!int Program.Add(int a, int b)`
- `dbg_locals` → `a`/`b` đúng giá trị tất định
- `dbg_eval`: `a+b`, `a*10+b`, `System.Math.Max(a,b)` (func-eval)
- `dbg_expand` object + array → children đúng
- `dbg_variables`: `returns`/`statics`/`exceptions` OK — **`autos` là known-xfail** (dnSpy trả `NYI`)
- `dbg_set_variable` → verify bằng eval lại
- **[regression]** `dbg_set_next_statement` → IP dời thật (bug `IDbgDotNetCodeLocation` đã sửa)
- `dbg_step` into/over/out → trả frame mới
- Gọi khi chưa dừng → thông báo lỗi rõ ràng

**Memory**
- `dbg_read_memory` tại địa chỉ module đã biết → hex khác 0
- `dbg_write_memory` → verify read-back; ghi vào vùng không ghi được → lỗi "write not confirmed"
- `size > 65536` → lỗi

**SSE với sự kiện thật**
- Mở stream, cho hit breakpoint → nhận `notifications/paused` có `pid` + `threadId` đúng

**[fix #3] Timeout eval**
- Eval `SlowProperty` (~15s) → **thành công**. Code cũ (timeout mặc định 10s) ném `TimeoutException` →
  test đỏ trên bản cũ.
- Eval vượt 60s → trả lỗi timeout sạch sẽ, **server vẫn phục vụ request sau đó**
- `dbg_step` với `timeout_ms` nhỏ → "step started but did not complete before timeout"

### Orchestration (`tests/run-integration.ps1`)

Đây là phần dễ làm ẩu nhất; các điểm bắt buộc:

1. **Port động** qua `DNSPY_MCP_PORT` → chạy lại/song song không đụng nhau
2. **Settings sạch**: dnSpy lưu breakpoint — đầu run gọi `bp_remove all=true` + `mbp_remove all=true`,
   cuối run lặp lại. (Tốt hơn: chạy dnSpy với thư mục user-data tạm nếu hỗ trợ.)
3. **Chờ sẵn sàng**: poll `GET /mcp` tới khi 200 (hoặc 401 nếu có token — 401 cũng chứng minh server sống),
   tối đa 60s; fail thì in luôn `%TEMP%\dnSpy.MCP.log`
4. **Teardown chắc tay**: kill `dnSpy.exe` **và** tiến trình mồi/`dotnet.exe` con — debuggee kẹt sẽ giữ
   file lock làm hỏng run kế tiếp
5. **Timeout mỗi test** để debugger treo không treo cả CI
6. **Thu log** `dnSpy.MCP.log` theo từng run, đính kèm khi fail

### Chính sách với hạn chế đã biết

Đánh dấu **skip có lý do**, và **báo động khi bất ngờ pass** (biết được lúc dnSpy sửa xong):

- `dbg_variables autos` → `NYI` (hạn chế dnSpy, không phải bug MCP)
- Trên ARM64: skip `dbg_start` (launch) và toàn bộ nhóm func-eval; chỉ chạy đường attach

---

## Tier 3 — Conformance với MCP client thật

Bắt được lỗi mà test tự viết bỏ sót (client thật gửi header/`notifications/initialized` khác mình tưởng).

- **MCP Inspector**: `npx @modelcontextprotocol/inspector --cli http://127.0.0.1:PORT/mcp --method tools/list`
  → parse sạch, hiện đủ tool và schema
- **Claude Code**: `claude mcp add --transport http dnspy ...` rồi chạy một kịch bản có sẵn
  ("đặt breakpoint ở `Program.Add`, chạy, đọc `a` và `b`"). Smoke thôi — không assert chặt vì
  agent không tất định. Mục đích: bắt lỗi mô tả tool khó hiểu / schema sai.

---

## CI

```yaml
# Tier 1: mọi PR, windows-latest
- dotnet build Extensions/dnSpy.MCP/dnSpy.MCP.csproj -c Release
- dotnet test  Extensions/dnSpy.MCP/tests/dnSpy.MCP.Tests -c Release
```

Tier 2 **không đưa vào CI công khai ngay**: cần desktop tương tác, và kinh nghiệm x64 trước đây phải
dùng scheduled task. Đề xuất: chạy thủ công trước mỗi lần merge vào branch chính, ghi kết quả vào
roadmap như các lần verify trước. Nếu sau này muốn tự động: thử `windows-latest` trước (nhiều project WPF
chạy được), fallback là self-hosted runner.

---

## Thứ tự làm

1. **Tier 1** trước — rẻ, vào CI được ngay, khóa fix #2/#4/#5/#6/#7. Cần thêm `InternalsVisibleTo`.
2. **App mồi + orchestration** — hạ tầng cho mọi thứ còn lại.
3. **Tier 2 nhóm breakpoint + inspection** — khóa fix #1 và #3, hai fix chỉ lộ ra khi chạy thật.
4. **Tier 2 phần còn lại** (memory, SSE, exception, module bp).
5. **Tier 3** khi chuẩn bị phát hành.

## Ước lượng

| Hạng mục | Quy mô |
|---|---|
| Tier 1 (project + ~35 ca) | ~600 dòng |
| App mồi `dbgtest` (2 TFM) | ~150 dòng |
| Orchestration PowerShell | ~200 dòng |
| Tier 2 (~45 ca) | ~800 dòng |

## Ghi chú thiết kế

- **Không mock `DbgManager`.** Giá trị của Tier 2 nằm ở chỗ chạy CorDebug thật; mock lại chỉ test
  chính cái mock. Phần thuần đã tách sang Tier 1 rồi.
- **Mỗi fix một test đỏ-trên-code-cũ.** Test không phân biệt được bản cũ/mới thì không bảo vệ được gì.
  Nên verify bằng cách `git stash` fix rồi chạy lại.
- **Test tuần tự trong mỗi phiên dnSpy.** Dispatcher debugger đơn luồng; song song hóa chỉ tạo flaky.
  Muốn nhanh thì chạy nhiều **phiên dnSpy** khác port, không phải nhiều test cùng phiên.
