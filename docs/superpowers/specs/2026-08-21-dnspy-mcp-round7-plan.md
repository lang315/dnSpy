# dnSpy.MCP — Round 7 PLAN: heap inspection (ClrMD) + de4dot (consent-gated)

Tổng hợp từ 4 spike độc lập (2 heap: ClrMD vs engine/COM; 2 de4dot: CAN vs SHOULD/boundary).
Đây là **kế hoạch**, chưa code. Base: `271f809a` (51 tool).

## A. HEAP INSPECTION — ĐỀ XUẤT: LÀM (ClrMD, chỉ trong extension) ✅

Round 6 kết luận "không khả thi" — nhưng đó là ở **tầng contract**. ClrMD lật lại verdict đó:

**Câu hỏi sống-còn (coexist ClrMD + CorDebug) đã có đáp án DỨT KHOÁT = CÓ, vì dnSpy ĐÃ làm đúng thế này rồi.**
- `Extensions\dnSpy.Debugger\dnSpy.Debugger.DotNet.CorDebug\DAC\ClrDacProvider.cs:46`:
  `DataTarget.AttachToProcess(pid, 0, AttachFlag.Passive)` — dnSpy attach ClrMD passive vào chính tiến trình
  CorDebug đang debug (đã paused), giữ `ClrRuntime` cả phiên (để đọc threads + resolve symbol).
- **Passive KHÔNG dùng debugger API** (chỉ `ReadProcessMemory` + nạp DAC in-process) → không đụng ICorDebug
  attach độc quyền. DAC read-only, nhiều consumer coexist được (SOS, ClrMD). Doc ClrMD nói thẳng dùng Passive
  "khi có debugger khác đang paused target" — đúng kịch bản dnSpy.
- Spike đã **thực nghiệm** (throwaway allocator riêng, net10, 2500 object): cả ClrMD **4.0** lẫn **1.1 pin sẵn của
  dnSpy** đều `CanWalkHeap=True`, đọc được int/string/**array** field. (Đã kill, không sót orphan.)
- **Bất ngờ có lợi:** ClrMD **1.1 cũ (pin sẵn)** đọc heap .NET 10 hiện đại OK (tự nạp DAC của target;
  `ISOSDacInterface` ABI ổn định) → **KHÔNG cần nâng ClrMD**. netstandard2.0 → phủ cả net48 + net10-windows.
  MIT, đã bundle sẵn (`DnSpyCommon.props:52`, `MSDiagRuntimeVersion=1.1.142101`).

**Đường MVP (chỉ sửa extension, 0 đụng engine):**
- `dnSpy.MCP.csproj` thêm `PackageReference Microsoft.Diagnostics.Runtime $(MSDiagRuntimeVersion)` (đúng version đã
  ở bin → không thêm DLL, không đụng độ). Tự tạo `DataTarget` passive từ `DbgProcess.Id` **khi `State==Paused`**.
- **Tools (+3 → 54):**
  - `heap_stats` — histogram theo type: count + tổng size mỗi type (loop group-by; ClrMD 1.1 không có sẵn stat).
  - `heap_find` — mọi instance của 1 type: address, size, tóm tắt field. Filter theo `ClrType.Name`.
  - `heap_object` — soi 1 object theo address: field/string/array element.
- **Luật cứng:** CHỈ Passive (1.1) / `suspend:false` (2.0+), KHÔNG bao giờ Invasive/NonInvasive (chúng dùng
  debugger API → đánh nhau với CorDebug); không để ClrMD tự suspend; chỉ đọc khi `State==Paused` (heap tĩnh);
  `ClrRuntime.Flush()` mỗi lần stop (như `ClrDacImpl` đã làm); assert `DbgProcess.Bitness` khớp (x86 dnSpy ↔ x86
  target — luật cũ, không thêm mới).
- **Gate #1 (prototype trước tiên):** dựng `DataTarget` passive **bên trong dnSpy** khi đang paused ở breakpoint,
  xác nhận `EnumerateObjectAddresses()` cho heap nhất quán và KHÔNG làm hỏng stop/continue của CorDebug. (Gần như
  chắc chắn OK vì ClrDac của dnSpy đã làm — nhưng đây là mắt xích duy nhất chưa test trực tiếp trong-dnSpy.)
- **Perf gate:** walk heap tiến trình lớn = nhiều giây, vượt timeout 10s của `DbgAccess` → cho heap tool **budget
  timeout riêng**, cap/stream kết quả, đừng marshal cả walk qua đường 10s.
- **Phủ:** .NET Framework + .NET/CoreCLR (đều CorDebug + DAC). **KHÔNG** Mono/Unity (ghi rõ hạn chế).
- **Fallback** (nếu vì lý do gì không muốn ClrMD trong MCP): engine-wrap `ICorDebugProcess5.EnumerateHeap`
  (~250–400 LOC, 6–8 file; contract seam `DbgCorDebugInternalRuntime` ĐÃ được reference; state=Paused là đủ +
  check `areGCStructuresValid`). Object model nghèo hơn (field cần round-trip `GetObject` mỗi object). Blast
  radius lớn hơn (đụng engine core). Chỉ dùng nếu ClrMD-trong-MCP trục trặc.
- **Ranh giới:** đọc heap = đọc state (như `dbg_read_memory`/`dbg_locals`) → **observe-only OK, không lo**.
- **Test:** fixture cấp phát object đã biết → Tier 2 heap_stats/find/object assert; Tier 3 đếm; Gate #1 là e2e đầu.

## B. DE4DOT — ĐỀ XUẤT: CONSENT-GATED, nghiêng về HOÃN ⚠️

**CAN (kỹ thuật) = sạch:** fork **`GDATAAdvancedAnalytics/de4dotEx`** (GPL-3.0, **dnlib 4.5.0 trùng dnSpy**,
net48+net8.0, push 2026-08, có sẵn cả `de4dot.mcp`). Interop **out-of-process file-in/file-out** (né hẳn đụng độ
type-identity dnlib + cô lập việc chạy code target). Submodule thứ 8 + host console mỏng (kiểu `dnSpy.Console`)
hoặc gọi CLI dựng sẵn. Headless: `ObfuscatedFile(Options{Filename,NewFilename})` → `Load(CreateDeobfuscators())`
→ `DeobfuscateBegin/Deobfuscate/End` → `Save()`. **~1 tuần, MEDIUM.**

**SHOULD (giá trị) = YẾU trên target thật, chỉ generic:**
- **Trên 653C0125.dll ≈ vô dụng:** NETGuard KHÔNG có trong 20 obfuscator de4dot hỗ trợ; **virtualize metadata
  vô hiệu hoá mọi static-deob** (dnlib không đọc nổi tables → de4dot không có gì để rewrite). Round 5 đã chứng minh.
- Giá trị = **generic** (Reactor/Eazfuscator/SmartAssembly/Dotfuscator/ConfuserEx-lite…), *nếu* sau này phân tích
  .NET obfuscate kiểu thường ngoài 2 app này.
- **Chồng `decrypt_strings` chỉ ở phần string**; de4dot thêm control-flow/proxy/dọn cả assembly. `decrypt_strings`
  vẫn hơn ở chỗ: chạy trong session đã kiểm soát, obfuscator-agnostic, in-bounds hiển nhiên.

**RANH GIỚI = gần/qua vạch — cần bạn duyệt rõ ràng (khác mọi thứ đã ship):**
- Danh sách NOT-OK của bạn ghi thẳng "**rename**" và "defeat anti-debug" — mà pipeline mặc định de4dot **rename
  symbol** + **gỡ anti-tamper/anti-debug**. Và mode string động **CHẠY code của target** trong host de4dot.
- Mặt in-bounds: là **bản copy tĩnh chết** để đọc (như dump_module+COR20), không re-inject, không bypass license
  lúc chạy. Mặt out-of-bounds: "kỹ thuật" rename/gỡ-protection đúng thứ ranh giới cảnh giác, và chạy code target.
- **Nếu duyệt:** rename **OFF mặc định** (`--dont-rename`); string mode `static` mặc định, `dynamic` opt-in + cô
  lập out-of-process (bitness khớp); disclose mọi transform mỗi lần; output chỉ là input read-only cho static
  tools, KHÔNG re-inject; cập nhật spec/MEMORY boundary. Đây là quyết định của bạn, không phải add âm thầm.

**Đề xuất:** HOÃN de4dot trừ khi bạn dự định phân tích .NET obfuscate-kiểu-thường ngoài MilkMax/PageMiner. Nó
không cứu được 653C0125. Nếu muốn, làm round riêng có consent-gate + boundary update.

## Kết luận
- **Heap inspection: LÀM ngay** (green, in-bounds, giá trị cao, coexist đã chứng minh). → 54 tool.
- **de4dot: chờ quyết định boundary của bạn.** Kỹ thuật sẵn sàng nhưng giá trị generic + gần vạch → mặc định hoãn.

## CẬP NHẬT (thực thi): ClrMD 1.1 MVP CHẾT — pivot sang helper 4.x out-of-process
Impl MVP dùng ClrMD **1.1 pin sẵn** thất bại ở Tier 2: **1.1 không walk được heap .NET 6+ (GC "regions")**.
Đo tận nơi trên CÙNG 1 tiến trình net8 đang paused:
- ClrMD **1.1** (bản dnSpy bundle, in-proc + standalone): 459/127 object, 5–6 type toàn system, **0 Widget**.
- ClrMD **4.0**: **1686 object, 179 type, đủ 5 DbgTest.Widget** + List<Widget>/Widget[]/Greeter/Dog/Node.
⇒ Dứt khoát là **version** (1.1 quá cũ cho regions-GC), không phải double-attach. Coexist thì OK (attach chạy,
trả JSON, không crash CorDebug).
**Vấn đề:** không reference thẳng 4.x trong extension — bin chỉ chứa 1 `Microsoft.Diagnostics.Runtime.dll`, mà
engine (`ClrDac`: threads+symbol) build theo 1.1. 3 đường: (A) **helper 4.x out-of-process** (đã CHỨNG MINH bằng
probe; engine 0 đụng → 0 rủi ro threads/symbol; +helper exe theo bitness), (B) nâng ClrMD global + port ClrDac
(rủi ro symbol dnSpy), (C) engine-wrap ICorDebugProcess5 (không dep nhưng ~300 LOC COM, chưa chứng minh trên net8).
**Người dùng chọn "làm, mình tự chọn đường" → chọn (A)** (proven + an toàn nhất cho dnSpy hiện có). Bitness helper
= bitness dnSpy = bitness target (luật cũ). Ưu tiên gói nhẹ (framework-dependent/embedded).
