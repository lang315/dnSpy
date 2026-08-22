# dnSpy.MCP — Round 5 (DRAFT): Deobfuscation, Flow, Live/packed, Robustness

Phạm vi do người dùng chốt: **1 + 2 + 3** (Deobfuscation & Flow + hoàn thiện live/packed + Robustness/Protocol).
Đây là round lớn, nhiều phiên. Giữ kỷ luật: plan → 3-tier test → CI → e2e observe-only. **44 → ~50 tool.**
Ranh giới **observe-only** nguyên vẹn: mọi tool là phân tích/đọc; KHÔNG có patch/save-modified/bypass.

Repo (git): `...\scratchpad\dnspy-mcp` nhánh `feat/mcp-debugger-server`. Base: `d2d32c1e` (44 tool).

## Thứ tự thực thi (rủi ro thấp → cao)

### Phase 1 — Call-graph / xref theo độ sâu  (tĩnh, rủi ro thấp — làm trước)
- **`call_graph`**: từ 1 method, đi `callers` (dùng lại logic `MethodCallers` của `find_references`) hoặc
  `callees` (quét body lấy operand `IMethod` của call/callvirt/newobj), tới `depth` N; `direction`, `depth`,
  `max_nodes`. Trả cạnh (from→to token) + node để agent dựng đồ thị.
- Fixture: chuỗi `Main→Warmup→…` đã có. Test Tier 2 (không cần session).
- Đường đã chắc — chỉ tái dùng helper sẵn.

### Phase 2 — Structured content (outputSchema)  (protocol, rủi ro thấp-vừa)
- Thêm `outputSchema` + `structuredContent` (MCP 2025-06-18) cho các read-tool giá trị cao trước:
  `list_types`, `list_methods`, `decompile`, `find_references`, `dbg_locals`, `dbg_callstack`.
- Giữ nguyên `content` text (tương thích ngược). Cập nhật Tier 1 (kiểm well-formed schema) + Tier 3 client.
- **Spike XONG (~4 edit, non-breaking):**
  - `Tools\ToolDef.cs`: thêm trailing ctor param `JObject? outputSchema = null` + prop `OutputSchema` (mọi
    call-site cũ compile nguyên). `Schema.Object/...` dùng lại **y nguyên** để dựng outputSchema.
  - `Server\McpServer.cs` `ListTools` (~:322): `if (t.OutputSchema is not null) tool["outputSchema"] = ...`.
  - `Server\McpServer.cs` `CallTool` (~:346): sau `Handler(args)`, **try-parse** text → `JObject` (gate bằng
    OutputSchema có mặt), truyền vào `ToolContent`. `ToolContent` (~:421) thêm param `JObject? structured`
    → emit `structuredContent`. `content` text + `isError` giữ nguyên ⇒ tương thích ngược.
  - **First cut (0 sửa handler):** các tool đã trả JSON object top-level — `list_types`, `list_methods`,
    `find_references`, `dbg_callstack` (+ peers trả object: `search`, `type_hierarchy`, `find_implementations`,
    `list_resources`, `dbg_threads`, `dbg_modules`, `call_graph`). Chỉ cần thêm `outputSchema:` khi đăng ký.
  - **Hoãn có chủ đích:** `decompile` (text thô) + `dbg_locals` (array top-level) — cần bọc `{...}`, sẽ phá
    contract text hiện có ⇒ để lần sau (projector riêng), KHÔNG blind-parse.
  - Tier 1: `ProtocolTests` đếm động (không có số cứng) — thêm 1 stub tool có outputSchema + test anh em.
    Tier 3: `content` text còn nguyên nên assertion cũ pass; thêm check outputSchema well-formed.

### Phase 3 — Giải mã chuỗi động  (dùng debugger sẵn có, rủi ro vừa)
- **`decrypt_strings`**: cho `decryptor` (token/tên) — hoặc heuristic tìm ứng viên (static, trả `string`,
  nhận `int`/`string`) — với mỗi call-site (qua `find_references`) đọc hằng số đối số từ IL rồi **func-eval**
  decryptor để lấy plaintext. Trả bảng `{callsite, arg, plaintext}` (cap số lượng + timeout).
- Tận dụng `dbg_eval`/func-eval đã chạy tốt. Cần session paused + decryptor đã nạp.
- Rủi ro: func-eval có side-effect (decryptor thường thuần); có cờ `dry_run`/`max_calls`.
- Fixture: thêm decryptor XOR/base64 + vài call-site vào `dbgtest`; Tier 2 so plaintext kỳ vọng.

### Phase 4 — Tracepoint (log & continue)  (API mới, rủi ro vừa)
- Breakpoint chế độ "trace": khi trúng thì eval message rồi **chạy tiếp** (không dừng). Thu message vào sink,
  đẩy qua SSE + lấy bằng **`trace_log`**.
- **`bp_add_trace`** (hoặc mở rộng `bp_add` với `trace_message`): `{expr}` nội suy trong message.
- **Spike XONG (verdict):** mode có ở contract, sink thì không.
  - `bp_add_trace`: **contract-level, gần như trivial** — set `DbgCodeBreakpointSettings.Trace =
    new DbgCodeBreakpointTrace(message, @continue: true)` trong `MakeSettings` (BreakpointTools.cs). dnSpy tự
    resume + tự format `{expr}` (đủ macro `$CALLER/$FUNCTION/...`). Người dùng thấy ở Debug Output.
    (`dnSpy.Contracts.Debugger\Breakpoints\Code\DbgCodeBreakpointSettings.cs:317`)
  - `trace_log`: **phải tự thu** — string đã-format chỉ tới `ITracepointMessageListener` **internal** (không
    ref được). Subscribe event contract **`DbgManager.MessageBoundBreakpoint`** (qua `DbgAccess.DbgManager`),
    với bound-bp có `Breakpoint.Trace is { Continue: true }` thì **tự format** message (dùng eval service sẵn
    có: `DbgLanguageService.GetCurrentLanguage → ExpressionEvaluator.Evaluate → Formatter.FormatValue`) rồi
    đẩy vào ring-buffer cho `trace_log` trả. **KHÔNG set `e.Pause`** (monotonic-OR → quan sát không nhiễu).
    v1: hỗ trợ text + `{expr}`, bỏ qua `$macro`.
- Fixture: bp trace tại 1 method trong vòng lặp; Tier 2 xác nhận thu ≥N dòng mà process không dừng hẳn.

### Phase 5 — Live/packed dump LÀM ĐÚNG + fixture packed  (rủi ro cao — làm sau cùng, cẩn thận)
Lý do vòng 4 hoãn: tự cuộn PE fixup mong manh; `DbgModule.Size` dao động; in-memory `ModuleDef` không phải
`ModuleDefMD` thuần.
- **Spike XONG — đã tìm ra ĐÚNG nguyên nhân + cách sửa:**
  - **Bug gốc:** vòng 4 (và cả `PEFilesSaver` của dnSpy) đều tin `DbgModule.Size`. Trên module obfuscated
    lớn, `Size < SizeOfImage` → section cao đọc quá vùng hợp lệ → `new PEImage` throw → fallback dump raw →
    không parse. **`DbgMetadataService`'s `ModuleDefMD` cũng chỉ có `module.Size` byte nền → dính y hệt, đừng dùng.**
  - **Fix:** lấy size thật từ **bảng section đã parse**, KHÔNG tin `Size` cũng KHÔNG tin `OptionalHeader.SizeOfImage`
    (packer làm hỏng field này). dnSpy có sẵn pattern: `PortableExecutableHelper.GetImageSize`
    (`dnSpy.Debugger.DotNet.Mono\Impl\PortableExecutableHelper.cs:59`):
    ```
    read 0x2000 byte header @ base; check MZ (0x5A4D);
    using pe = new PEImage(hdr, null, ImageLayout.Memory, verify:true);
    align = oh.SectionAlignment; len = AlignUp(oh.SizeOfHeaders, align);
    foreach s in ImageSectionHeaders: len = Max(len, AlignUp(s.VirtualAddress + Max(s.VirtualSize, s.SizeOfRawData), align));
    → imageSize = len   (fallback DbgModule.Size nếu len vô lý)
    ```
  - Đọc đúng `imageSize` byte (`ReadMemory` đọc trang chưa map = 0, không throw), rồi memory→file bằng thuật
    toán `PEFilesSaver.WritePEFile` (`dnSpy.Debugger\ToolWindows\Modules\PEFilesSaver.cs:110` — **public static
    nhưng ở assembly UI, KHÔNG ref được → replicate ~15 LOC**): copy `[0,SizeOfHeaders)`; mỗi section
    `Array.Copy(raw, VirtualAddress, dst, PointerToRawData, SizeOfRawData)`.
  - dnlib 4.5 API (verified): `dnlib.PE.PEImage(byte[], string?, ImageLayout, bool verify)` (IDisposable),
    `.ImageNTHeaders.OptionalHeader.{SizeOfHeaders,SectionAlignment,FileAlignment}`, `.ImageSectionHeaders[].{VirtualAddress,VirtualSize,PointerToRawData,SizeOfRawData}`.
  - **Chế độ phụ (tùy chọn) "normalize":** `ModuleDefMD.Load(new PEImage(bufferĐúngKíchThước, Memory)).Write(path)`
    — PE sạch khi header gốc hỏng; nhưng có thể rớt native/mixed-mode. v1 làm raw-rebuild trước, normalize để sau.
  - **Hạn chế còn lại (ghi rõ trên tool):** section `SizeOfRawData==0` (virtual-only) copy rỗng; method body
    JIT-decrypt lười chưa materialize sẽ thiếu; header bị anti-tamper phá → `verify:true` throw (retry `verify:false`
    hoặc raw fallback).
- **Fixture packed** (mảnh còn thiếu để validate): assembly stub `Assembly.Load(<embedded-encrypted-bytes>)`
  → module thật chỉ tồn tại trong RAM; dump ra rồi so với bản gốc plaintext đã biết. Cho Tier 2 kiểm được
  provenance (RAM≠đĩa), điều fixture cũ không làm được.
- Chỉ bật lại `dump_module`/`mem_load` khi Tier 2 fixture-packed xanh.

#### KẾT QUẢ Phase 5 (đã ship — chỉ `dump_module`, 48→49; `mem_load` bỏ vì thừa)
E2e thật trên `653C0125.dll` (NETGuard) lộ ra **3 lớp** phải xử lý, và tool nay xử lý cả 3:
1. **Size sai** → tính `imageSize` từ **section table** (`max(AlignUp(VA+max(VSize,RawSize),SA))`), không tin
   `DbgModule.Size`. E2e: imageSize=9224192 = 2× file trên đĩa (4688384). ✓
2. **Compact làm mất metadata** (section virtual-grown, `VSize>RawSize`) → chuyển sang **UNMAP**: giữ nguyên
   ảnh RAM đầy đủ + viết lại section table identity-map (`PointerToRawData=VA`, `SizeOfRawData=AlignUp(VSize,SA)`,
   `FileAlignment=SectionAlignment`) ⇒ file-offset==RVA. ✓
3. **Anti-dump: COR20 directory bị zero** (NETGuard zero `DataDirectory[14]` → dnlib báo ".NET data directory
   RVA is 0") → **`ReconstructCor20Directory`**: quét `BSJB` (metadata root) + CLI header (`cb==0x48`,
   `MetaData.RVA==BSJB`), ghi lại `DataDirectory[14]={cliRva,72}`. E2e: `cor20Reconstructed:true`, **dnlib LOAD
   được dump** (trước đây "could not load module"). ✓
- **Hạn chế THẬT (đã ghi trên tool + README):** NETGuard **virtualize metadata** — type tables KHÔNG nằm trong
  ảnh PE đã map (0 types dù dump ở entry hay sau khi UI init). Dump load được nhưng rỗng; đây là **bản chất
  protection**, không phải bug dump. 653C0125 phân tích được từ **file đĩa** (161 types). Khôi phục metadata bị
  virtualize = ngoài phạm vi (thuộc nhóm de4dot/unpack đã hoãn).
- **Validate:** (a) Tier 2 fixture own-module dump (MZ + `list_types` thấy `DbgTest.Program` + `decompile`);
  (b) Tier 1 unit test cho `ReconstructCor20Directory` (synthetic PE, COR20 zero → khôi phục); (c) **e2e thật**
  653C0125 (dump load được trong dnlib). Bỏ "fixture packed `Assembly.Load`" — e2e thật mạnh hơn.

### Xuyên suốt — Robustness (từ option 3)
- **Teardown AV** (upstream, Locals-refresh đọc PE đã free dưới churn): không sửa được dnSpy, nhưng (a)
  serial hoá dump/read nặng để tránh refresh đồng thời, (b) try/guard quanh read rủi ro, (c) trình tự reset
  rõ ràng trước stop. Giảm thiểu + ghi rõ.
- **Tier 2 trong CI**: khó thật (cần desktop Windows + debuggee). Điều tra runner self-hosted/desktop; **có
  thể vẫn phải manual** — sẽ báo trung thực thay vì hứa.

## Tool trajectory
`call_graph`, `decrypt_strings`, `bp_add_trace`, `trace_log`, và bật lại `dump_module`/`mem_load` (nếu Phase 5
đạt) → 44 → ~50.

## Verify (mỗi phase)
Build net48 + net10 sạch · Tier 1 · Tier 2 lọc theo class mới (`run-integration.ps1 -Filter`) · Tier 3 conformance
(cập nhật số tool) · CI 4 build + Tier 1 xanh · e2e observe-only (`--settings-file` tạm, `dnSpy.xml` nguyên vẹn).
Commit theo phase.

## Điểm bất định (nói trước, không giấu)
- Phase 4 tracepoint: phụ thuộc dnSpy có expose trace ở tầng contract không.
- Phase 5 live/packed: đã thất bại 1 vòng; spike phải xong & fixture-packed phải xanh trước khi ship.
- Tier 2 CI: có thể bất khả thi headless.

## Ngoài phạm vi (vẫn hoãn): heap inspection, assembly diff, batch decompile, de4dot tĩnh, export cả project,
mọi tool sửa/lưu assembly (ngoài ranh giới observe-only).
