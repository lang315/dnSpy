# dnSpy.MCP — find_implementations + extract_iocs

Bối cảnh: nhóm static đã có `list_types`/`list_methods`/`decompile`/`search`/`find_references` (38 tool).
Hai bổ sung này khép vòng RE tĩnh một assembly .NET: chiều **xuôi** của cây kế thừa (ai override/hiện
thực method X) và **trích IOC** cho triage/báo cáo mã độc. Sau round này: **40 tool**.

## 1. `find_implementations` — ai override/hiện thực method X

Bổ sung chiều ngược của `find_references`: cho một method ảo/abstract/interface, tìm mọi method
override hoặc hiện thực nó.

**Thiết kế:** `find_implementations(module, method | token, scope="module"|"open", max=200)`.
Trả `implementations[]` gồm `{type, method, token, kind, implements}`, kèm `targetKind`
(`interface`/`virtual`/`abstract`/`non-virtual`) và `scannedTypes`.

**Thuật toán — soi gương chính analyzer của dnSpy** (không tự chế), dùng
`dnSpy.Contracts.Decompiler.TypesHierarchyHelpers` (public, đã reference sẵn):
- Interface method → mô phỏng `InterfaceMethodImplementedByNode`: `.override`/MethodImpl tường minh
  thắng trước (kind `explicit`); còn lại khớp ngầm qua ngữ cảnh interface (`GetInterfaceContext`) +
  `MatchInterfaceMethod` (generic-aware) → kind `interface`.
- Virtual/abstract class method → mô phỏng `MethodOverridesNode` ("Overridden By"):
  `IsBaseType` + `IsBaseMethod`; phân biệt override thật vs che (`hides = !IsVirtual ^ IsNewSlot`) →
  kind `override` hoặc `hides`; cộng thêm `.override` tường minh → `explicit`.

Vì tái dùng helper đã kiểm nghiệm của dnSpy nên xử lý đúng generic, newslot/reuseslot, và
interface kế thừa — những chỗ SigComparer tay dễ sai.

## 2. `extract_iocs` — chỉ báo tĩnh cho triage

**Thiết kế:** `extract_iocs(module, categories?, max=500)`. Quét **string literal** (mọi `ldstr`) +
khai báo **P/Invoke** (`ImplMap`), mỗi hit gắn method + token, dedup theo `(category,value)` kèm
`occurrences`.

Category: `url, ip, registry, path, email, pinvoke, base64`. Mặc định tất cả **trừ `base64`**
(base64 khớp mọi chuỗi dài → nhiễu, nên opt-in). Regex cố tình bảo thủ: IPv4 kiểm octet ≤255,
path neo theo ổ đĩa/UNC, registry neo theo `HK*`, để version string hay token thường không bị báo nhầm.
P/Invoke value = `dll!entryPoint` từ `ImplMap.Module` + `ImplMap.Name`.

## 3. Fixture (tests/fixture/dbgtest)

- `IGreeter` + `EnglishGreeter`/`FrenchGreeter` (implicit impl) và `Animal.Speak` virtual +
  `Dog` override — cho `find_implementations`.
- `Program.Indicators()` chứa URL/IP/registry/path/email **tổng hợp** (không phải hạ tầng thật) và
  `[DllImport("kernel32.dll", EntryPoint="GetTickCount")]` — cho `extract_iocs`.
- `Warmup()` chạm tới chúng một lần để chắc chắn compiler phát ra; không đụng vào Add/Level/Inspect/
  Boom/WorkerTick nên các assertion cũ giữ nguyên.

## 4. Test (Tier 2, StaticIntegrationTests)

- `find_implementations`: 2 impl của `IGreeter.Greet` (kind interface); `Dog` override `Animal.Speak`;
  tìm theo token; error path thiếu đích.
- `extract_iocs`: mỗi category có đúng value + method + token; `base64` không nằm trong sweep mặc định;
  lọc `categories=email` chỉ ra email; category lạ báo lỗi.

## 5. Sửa harness (run-integration.ps1)

- **Bug scalar/array:** `Resolve-DnSpy` làm `$candidates = @(...) | Where-Object {...}` rồi
  `$candidates[0]`. Khi chỉ 1 ứng viên tồn tại, pipeline trả **chuỗi vô hướng**, `[0]` lấy **ký tự đầu**
  (`"C"`) → `Start-Process C` báo "cannot find the file". Bọc `@()` quanh pipeline để luôn là mảng.
- **TRX logger:** suite không chạy CI (cần desktop tương tác) và stderr native dễ bị PowerShell bọc làm
  mất summary — ghi `integration.trx` để có bản ghi bền vững.

## Kết quả

net48 + net10.0-windows build sạch (0 warning). Tier 1 105/105. Tier 2 xác minh trên dnSpy cô lập
(`--settings-file` tạm). Dữ liệu người dùng (`%APPDATA%\dnSpy\dnSpy.xml`, breakpoint CheckLicense) giữ nguyên.
