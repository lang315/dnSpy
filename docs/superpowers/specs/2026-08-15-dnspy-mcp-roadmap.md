# dnSpy.MCP — Gap analysis & roadmap

Ngày: 2026-08-15
Đối chiếu: extension hiện tại vs. API debugger dnSpy + đặc tả MCP.

## Trạng thái release (cập nhật 2026-08-19)

Extension đã **sẵn sàng release**. Vài dòng cũ bên dưới nay đã lỗi thời; trạng thái cuối:

- **36 tool** — thêm `dnspy_info` và nhóm **phân tích tĩnh** `decompile`/`list_types`/`list_methods`
  (đọc & khám phá assembly không cần debug session). Xác thực **bật mặc định** (token sinh tự động,
  lưu cạnh file settings).
- **Đã có test project trong repo** (bác bỏ mục 19): Tier 1 (105 test, chạy CI qua `mcp-tests.yml`),
  Tier 2 (82 pass + 1 skip có lý do, cần desktop, chạy qua `run-integration.ps1`), Tier 3 conformance.
- **Đã verify runtime thật trên Windows x64 và x86**, và **e2e trên app thật** (MilkMax x86 obfuscated,
  PageMiner x64) — kể cả build bản dnSpy x86 để debug target 32-bit.
- Các mục P2 còn treo là **quyết định có chủ đích**, không phải thiếu sót: structured content (16),
  log Output window (17), UI trạng thái server (18) — trả JSON-text + log file vẫn đủ cho agent.
- Mục 10 (hit-count) **đã làm** — `bp_add` có tham số `hit_count`.
- Hạn chế còn lại là của **dnSpy/CorDebug**, không phải extension, và đều đã ghi rõ: `dbg_variables autos`
  chưa được engine hỗ trợ (tool báo lỗi có hướng dẫn); expand `List<T>` qua DebuggerTypeProxy không ổn
  định (dùng `dbg_eval`); điểm dừng exception nằm ở native transition frame (stack chập chờn); dnSpy có
  thể AV khi hủy process đang pause dưới cường độ cao (đã báo upstream, suite phát hiện và báo đúng).

---

## Cập nhật: đã triển khai (2026-08-15)

Extension đã mở rộng từ 18 → **32 tool**, build sạch net48 + net10.0-windows, smoke test pass 20 assertion (gồm SSE + echo-version + Host/Origin/token guard).

- **P0#2 xong** — `bp_add_method` (đặt bp theo tên method đầy đủ, mọi overload; qua `IDsDocumentService` + dnlib + `DbgDotNetCodeLocationFactory`).
- **P0#3 xong** — `bp_add_line` (đặt bp theo dòng nguồn qua PDB sequence points).
- **P1 xong hết** — `dbg_read_memory`/`dbg_write_memory` (4), `dbg_expand` (5), `dbg_set_variable` (6), `dbg_variables` autos/returns/statics/exceptions (7), `dbg_set_next_statement` (8), `exc_break` (9), `bp_add` hit-count (10), `mbp_*` module breakpoints (11), `dbg_set_thread` (12), `dbg_list_attachable` (13).
- **P2 phần lớn xong** — SSE `notifications/paused` push (14), tool annotations readOnly/destructive (15), echo protocolVersion (20).
- **P2 chưa làm (quyết định có chủ đích):** structured content/outputSchema (16 — trả JSON-text vẫn đủ), log vào Output window (17 — giữ `Debug.WriteLine`, tránh phụ thuộc UI-thread), UI trạng thái server (18), test project trong repo (19 — repo không có convention test; smoke test giữ ở scratchpad làm evidence).
- **P0#1 verified runtime** (Parallels Windows 11 ARM64 VM, dnSpy chạy thật):
  - ✅ Server + 32 tool + protocol (initialize/tools/list/tools/call) + args + annotations + echo version.
  - ✅ `bp_add_method` resolve tên→token đúng (`Program.Add` → 0x06000001), **bind + hit** thật (boundCount=1, hitCount=1).
  - ✅ `dbg_attach` → `dbg_break` → `dbg_wait_for_break` → `dbg_threads` / `dbg_modules` / `dbg_callstack` (formatter đúng: `dbgtest.dll!int Program.Add(int a, int b)`).
  - ⚠️ `dbg_locals` / `dbg_eval` / `dbg_set_variable` trả "Internal debugger error" — **func-eval của CorDebug trên ARM64**, không phải lỗi MCP (context tạo được, callstack/format chạy; chỉ func-eval fail). Kỳ vọng chạy đúng trên x64.
  - ❌ `dbg_start` (CorDebug **launch**) không spawn process trên ARM64 (engine nhận, isDebugging=true, nhưng process không tạo). Attach là đường thay thế trên ARM64. Kỳ vọng chạy đúng trên x64.
  - Kết luận: code MCP đúng; hạn chế còn lại là CorDebug native trên Windows ARM64. Nên verify lại trên Windows x64 (nền tảng chính dnSpy hỗ trợ: RID win-x86/win-x64).

## Cập nhật: verified runtime trên Windows x64 (2026-08-15)

Chạy thật trên host Windows 10 22H2 **AMD64** (build `build.ps1 net -NoMsbuild`, dnSpy chạy qua scheduled task interactive, MCP server 32 tool). Xác nhận cả 2 giả thuyết ARM64 và sửa 3 bug mới.

- ✅ **Các hạn chế ARM64 là ARM64-specific** (không phải bug extension):
  - `dbg_start` LAUNCH: spawn process thật (dotnet.exe, bitness 64, Paused).
  - `dbg_locals`/`dbg_eval`/`dbg_set_variable`: chạy đúng — locals ra giá trị thật (a=14,b=1,...); **func-eval gọi method** chạy: `a+b`=15, `a*10+b`=141, `System.Math.Max(a,b)`=14; `set_variable a=777` verify được.
  - Attach path đầy đủ: attach → bp_add_method bind (token 0x06000001, boundCount=1) + hit (hitCount 1→2) → callstack `Program.Add`←`Main` → locals/eval/set_variable.
- 🔧 **3 bug mới (x64, không phải ARM-specific) — đã sửa & retest xanh** (commit "Fix three debug tools found by Windows x64 runtime verification"):
  1. `dbg_set_next_statement` báo "current frame has no .NET code location": frame đã JIT trả `DbgDotNetNativeCodeLocation` (không kế thừa `DbgDotNetCodeLocation`). Sửa: match interface `IDbgDotNetCodeLocation`. Verify: IP dời 0x5→0x0.
  2. `dbg_start` bằng apphost `.exe`: apphost re-exec .NET host làm rớt breakpoint đặt trước khi launch (boundCount=0). Sửa: nếu có `.dll`+`.runtimeconfig.json` cạnh bên thì debug thẳng `.dll`. Verify: bp đặt trước dbg_start nay bind+hit khi launch.
  3. `dbg_list_attachable`/`dbg_attach` theo tên: enumeration của dnSpy AV ("Invalid access to memory location") khi có name filter. Sửa: lấy list không filter (an toàn) rồi tự match name/pid. Verify: list+attach `dbgtest*` OK.
- ⚠️ `dbg_variables kind=autos` trả `[{"name":"Error","value":"NYI"}]` — provider autos của dnSpy chưa implement (returns/statics/exceptions dùng cùng khuôn vẫn OK). Hạn chế dnSpy, không phải bug MCP.
- Kết luận: **toàn bộ luồng debug lõi (launch/attach/breakpoint theo tên/step/callstack/locals/eval/func-eval/set_variable/set_next_statement) chạy thật trên Windows x64.**

---

Trạng thái hiện tại: build sạch net48 + net10.0-windows, protocol/security smoke test pass, **chưa test runtime trên Windows**.

---

## P0 — Cần trước khi coi là "dùng thật được"

| # | Hạng mục | Vì sao |
|---|---|---|
| 1 | **Verify runtime trên Windows** | Toàn bộ luồng marshaling (`DbgAccess`), start/attach/step/eval mới chỉ verify ở mức compile + protocol. Phải chạy dnSpy thật, gọi từng tool, sửa lỗi runtime. Đây là gap lớn nhất. |
| 2 | **Resolve method theo tên → token** | `bp_add` hiện chỉ nhận metadata token. Agent gần như không biết token; nó biết "namespace.Class.Method". Cần tool `bp_add_method` nhận tên (dùng dnlib/`IDsDocumentService` + decompiler đã có trong dnSpy) để tra token. Không có cái này, breakpoint rất khó dùng. |
| 3 | **Breakpoint theo source line** | Cùng lý do: agent suy nghĩ theo dòng lệnh, không theo IL offset. Cần map (module, method, source line) → IL offset qua `DbgMethodDebugInfo`. |

## P1 — Tính năng debug còn thiếu (API dnSpy đã có sẵn)

| # | Tool đề xuất | API dnSpy | Ghi chú |
|---|---|---|---|
| 4 | `dbg_read_memory` / `dbg_write_memory` | `DbgProcess.ReadMemory/WriteMemory` | Rất hữu ích cho reverse engineering (đọc byte array đã giải mã, patch runtime). |
| 5 | `dbg_locals` **mở rộng con** | `DbgValueNode.GetChildren/HasChildren` | Hiện chỉ 1 cấp; object lồng nhau không xem sâu được. Thêm tham số `expression`/`path` hoặc `depth`. |
| 6 | `dbg_set_variable` | `DbgValueNode.Assign` / `ExpressionEvaluator.Assign` | Sửa giá trị biến khi đang debug. |
| 7 | `dbg_autos` / `dbg_return_values` / `dbg_static_fields` | `DbgLanguage.AutosProvider / ReturnValuesProvider / StaticFieldsProvider` | Hiện chỉ expose `LocalsProvider`. Cùng khuôn, thêm dễ. |
| 8 | `dbg_set_next_statement` | `DbgThread.SetIP` / `CanSetIP` | Nhảy con trỏ thực thi. |
| 9 | `exc_config` (break-on-exception) | `dnSpy.Contracts.Debugger/Exceptions/*` | Bật/tắt break khi ném exception (1st chance). dnSpy quảng cáo tính năng này. |
| 10 | `bp_add` hỗ trợ **hit-count trigger** | `DbgCodeBreakpointSettings.HitCount` | Hiện chỉ đọc hit count; chưa cho đặt điều kiện "dừng sau N lần". |
| 11 | `mbp_*` module breakpoints | `DbgModuleBreakpointsService` | Break khi module được nạp. |
| 12 | Chọn thread/frame hiện tại | `DbgManager.CurrentThread.Current` (settable), `DbgCallStackService.ActiveFrameIndex` | Cho agent set ngữ cảnh mặc định thay vì truyền `thread_id`/`frame_index` mỗi lần. |
| 13 | `dbg_list_attachable` | `AttachableProcessesService` (đã import) | Liệt kê tiến trình attach được để agent chọn; hiện `dbg_attach` tự lấy phần tử [0]. |

## P2 — Chất lượng MCP / vận hành

| # | Hạng mục | Ghi chú |
|---|---|---|
| 14 | **Server → client notifications (SSE)** | Hiện agent phải poll `dbg_wait_for_break`. MCP hỗ trợ notification qua SSE stream; push sự kiện "breakpoint hit / step complete" realtime. Đăng ký `DbgManager.MessageBoundBreakpoint/ProcessPaused`. Nâng cấp lớn nhất về UX. |
| 15 | **Tool annotations** | Gắn `readOnlyHint`/`destructiveHint` vào định nghĩa tool (dbg_start, dbg_write_memory là destructive) để client cảnh báo người dùng. |
| 16 | **Structured content / outputSchema** | Đang trả JSON-trong-text. MCP mới có `structuredContent` + `outputSchema` — client parse trực tiếp. |
| 17 | **Log vào Output window của dnSpy** | Hiện chỉ `Debug.WriteLine`. Dùng `IOutputService` (marshal UI thread) để user thấy server chạy / request lỗi ngay trong dnSpy. |
| 18 | **UI trạng thái server** | Không có chỗ nào trong dnSpy hiển thị "server đang chạy ở cổng X" hay nút bật/tắt. Menu/tool window nhỏ. |
| 19 | **Test project cho protocol layer** | Repo không có test project; smoke test đang ở scratchpad (không commit). Nếu muốn CI bảo vệ regression, thêm test project cho `McpServer`/`JsonRpc`/`DbgAccess` (phần thuần, không cần WPF). |
| 20 | **Echo protocolVersion của client** | Hiện hardcode `2024-11-05`. Nên echo version client gửi trong `initialize` để tương thích rộng hơn. |

---

## Đề xuất thứ tự làm

1. **P0** trước: verify Windows (1) → resolve method/line → token (2, 3). Không có 2/3 thì breakpoint gần như không dùng được với agent.
2. **P1** theo giá trị RE: memory r/w (4), expand children (5), set variable (6), autos/return (7).
3. **P2** khi cần trải nghiệm agent mượt: SSE notifications (14) là nâng cấp đáng giá nhất, rồi annotations (15).

Mọi thứ P1 đều dùng API đã khảo sát trong session này (xem spec thiết kế cùng thư mục) và theo đúng khuôn `DbgAccess.Invoke` + `ToolDef` hiện có — chi phí thêm mỗi tool thấp.
