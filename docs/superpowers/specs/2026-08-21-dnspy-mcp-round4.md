# dnSpy.MCP — Round 4: live/packed analysis, static-nav completeness, resources, robustness

Bối cảnh: e2e trên phần mềm thật phơi ra **một lỗ hổng cứng** — app bị pack/anti-debug (`PageMiner - HP Tools.exe`)
không parse tĩnh được dù code đã unpack trong bộ nhớ. Kèm theo: điều hướng tĩnh còn thiếu (chỉ có caller,
không có field/type ref hay cây kế thừa), không xem được embedded resource (nơi packer giấu payload), và vài
mép ergonomics (đường dẫn `/`, không có "run to method"). Round này khép chúng lại. **40 → 44 tool.**
(Phần **live/packed** `dump_module`/`mem_load` đã hiện thực + thử nhưng **HOÃN** sau e2e — xem cuối.)

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

**Live/packed:** đã hiện thực `dump_module`/`mem_load` nhưng **hoãn** (xem "Hoãn").

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
- `dump_module` / `mem_load` (đã hiện thực, **hoãn** — xem cuối): `DbgProcess.ReadMemory` + fix layout dnlib
  `PEImage`, và `DbgMetadataService.TryGetMetadata(module, ForceMemory)`.

## Fixture (dbgtest)

Thêm field `State` (ghi ở `SetState`, đọc ở `ReadState` — cặp read/write ở method khác nhau) và 1
`<EmbeddedResource>` (`dbgtest.embedded.txt`) + `ReadEmbedded()`. Cây kế thừa/type-use đã sẵn (Animal/Dog,
IGreeter, Node).

## Test & verify

- Build net48 + net10.0-windows sạch (0 warning).
- Tier 1 105/105.
- Tier 2 (lọc `StaticIntegrationTests`+`RunToIntegrationTests`, `run-integration.ps1 -Filter`): **40/40 pass** —
  field/type ref, hierarchy, resource, `dbg_run_to`, forward-slash. (Thêm tham số `-Filter` cho harness.)
- Tier 3 conformance: 44 tool.
- **Đã biết (không phải lỗi round này):** chạy *toàn bộ* Tier 2 thỉnh thoảng dính AV upstream của dnSpy
  (`0xC0000005/0xC0000374`, Locals-refresh đọc PE đã free) trong nhóm test attach — harness báo rõ "dnSpy no
  longer answering", các fail sau là hệ quả. Đây là bug upstream đã ghi nhận, không liên quan code round 4.

## Hoãn

- **live/packed (`dump_module` / `mem_load`)** — hiện thực xong, pass trên fixture, nhưng e2e trên module
  thật lớn bị làm rối (`653C0125.dll` của MilkMax, NETGuard) cho thấy hai đường đều **không bền**: (a)
  đọc ảnh in-memory rồi tự fix layout PE dao động (kích thước module dnSpy báo đổi giữa các lần break; ảnh
  convert lẫn ảnh raw đều không parse bằng dnlib); (b) `mem_load` nạp được nhưng document in-memory không
  resolve theo tên qua `ResolveModule`. Use-case thật (module *vừa packed vừa debug được*) lại không có
  trên mẫu sẵn có — PageMiner packed **+ anti-debug** (không debug được), 653C0125 của MilkMax không packed
  (load thẳng từ đĩa OK). ⇒ Gỡ khỏi round 4, để lại cho vòng sau đi thẳng theo cơ chế "Save Module" nội bộ
  của dnSpy (raw `ReadMemory` + fixup của `PEFilesSaver`), không tự cuộn tay.
- Ngoài phạm vi (như cũ): giải mã chuỗi/de4dot, heap inspection, export cả project.
