# dnSpy.MCP — Round 4: live/packed analysis, static-nav completeness, resources, robustness

Bối cảnh: e2e trên phần mềm thật phơi ra **một lỗ hổng cứng** — app bị pack/anti-debug (`PageMiner - HP Tools.exe`)
không parse tĩnh được dù code đã unpack trong bộ nhớ. Kèm theo: điều hướng tĩnh còn thiếu (chỉ có caller,
không có field/type ref hay cây kế thừa), không xem được embedded resource (nơi packer giấu payload), và vài
mép ergonomics (đường dẫn `/`, không có "run to method"). Round này khép chúng lại. **40 → 46 tool.**

## Tools

**Phân tích tĩnh (không cần phiên debug):**
- `find_references` **mở rộng** sang field & type: method → caller; field → đọc/ghi (`access`); type → method
  dùng nó (khởi tạo/cast/gọi/local/catch). `token` tự nhận loại (Method/Field/Type). Đường method giữ
  nguyên hành vi cũ.
- `type_hierarchy` — base type + interface, và/hoặc type dẫn xuất + implementer (`direction`).
- `list_resources` / `extract_resource` — liệt kê manifest resource và trích 1 embedded resource (inline
  hoặc ghi ra `save_path`).

**Robustness/điều khiển:**
- `dbg_run_to` — chạy tiếp tới 1 method rồi dừng (đặt bp tạm, continue, chờ, gỡ).
- `ResolveModule` nhận đường dẫn `/` (chuẩn hoá về sep OS; fallback so tên file).

**Live/packed (cần tiến trình paused):**
- `dump_module` — dump ảnh in-memory của 1 module đã nạp ra đĩa (dạng unpack), rồi phân tích file bằng tool tĩnh.
- `mem_load` — nạp module từ bộ nhớ vào dnSpy, phân tích theo tên.

## Thiết kế (đều soi gương chính dnSpy)

- Field ref: mô phỏng `FieldAccessNode` — opcode `Ld/Stfld`, `Ld/Stsfld`, `Ld*flda`, `Ldtoken`; operand
  `IField.ResolveFieldDef()`; matcher `SameField`/`FieldKey` song song `Same`/`MethodKey`.
- Type use: mô phỏng `TypeUsedByNode` — operand `ITypeDefOrRef`/`IField`/`IMethod` (declaring type) + locals +
  catch; so bằng `new SigComparer().Equals(target, t.GetScopeType())`.
- Hierarchy: `TypesHierarchyHelpers.IsBaseType`/`GetTypeAndBaseTypes` (public, đã reference); derived quét
  `mod.GetTypes()` + kiểm interface.
- Resource: dnlib `ModuleDef.Resources` + `EmbeddedResource.CreateReader().ToArray()` (dnlib 4.5, **không**
  có `.Data`/`.GetResourceData()`).
- `dbg_run_to`: tín hiệu "đã tới" = **hit-count của bp mục tiêu tăng** — né được đua RunAll-bất-đồng-bộ (chờ
  `!IsRunning` ngay sau RunAll có thể trả về trên pause cũ).
- `dump_module`: `DbgProcess.ReadMemory(Address, .., Size)` (vượt cap 64 KB của MemoryTools) + fix layout
  memory→file bằng dnlib `PEImage` (sao `PEFilesSaver.WritePEFile`); không reference assembly UI.
- `mem_load`: `DbgMetadataService.TryGetMetadata(module, ForceMemory)` → `ModuleDef` (contract đã reference).
  **Rủi ro UI-dispatcher đã prototype: gọi từ dbg dispatcher chạy tốt, không cần UI dispatcher.**

## Fixture (dbgtest)

Thêm field `State` (ghi ở `SetState`, đọc ở `ReadState` — cặp read/write ở method khác nhau) và 1
`<EmbeddedResource>` (`dbgtest.embedded.txt`) + `ReadEmbedded()`. Cây kế thừa/type-use đã sẵn (Animal/Dog,
IGreeter, Node).

## Test & verify

- Build net48 + net10.0-windows sạch (0 warning).
- Tier 1 105/105.
- Tier 2 (lọc theo các class mới, `run-integration.ps1 -Filter`): **43/43 pass** — field/type ref, hierarchy,
  resource, `dbg_run_to`, `dump_module`, `mem_load`, forward-slash. (Thêm tham số `-Filter` cho harness.)
- Tier 3 conformance: 46 tool.
- **Đã biết (không phải lỗi round này):** chạy *toàn bộ* Tier 2 thỉnh thoảng dính AV upstream của dnSpy
  (`0xC0000005/0xC0000374`, Locals-refresh đọc PE đã free) trong nhóm test attach — harness báo rõ "dnSpy no
  longer answering", các fail sau là hệ quả. Đây là bug upstream đã ghi nhận, không liên quan code round 4.

## Hoãn (ngoài phạm vi): giải mã chuỗi/de4dot, heap inspection, export cả project.
