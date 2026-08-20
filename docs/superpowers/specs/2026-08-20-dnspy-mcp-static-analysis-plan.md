# dnSpy.MCP — Mở rộng phân tích tĩnh: search, IL, find_references

Ngày: 2026-08-20
Bối cảnh: nhóm static (`list_types`/`list_methods`/`decompile`) đã có (36 tool). Ba bổ sung này hoàn
thiện năng lực "đọc & khám phá" cho agent RE. Tất cả **tĩnh, read-only, không cần debug session**, và
nằm trong `StaticTools.cs` dưới cùng `metadataLock` (dnlib nạp metadata lazy → không an toàn đồng thời).

## 1. `decompile` thêm tham số `format` (csharp | il)

**Vì sao:** đọc IL cạnh C# là nhu cầu RE cốt lõi — thấy chính xác opcode, offset, tránh việc decompiler
"làm đẹp" che mất hành vi thật (đặc biệt với code obfuscated).

**Thiết kế:** `decompile(..., format="csharp"|"il")`, mặc định `csharp`. `il` → lấy decompiler qua
`decompilerService.Find(DecompilerConstants.LANGUAGE_IL)`; nếu không có, báo lỗi rõ. Dùng lại toàn bộ
đường decompile hiện có (cùng `IDecompiler.Decompile(member, output, ctx)`). Không thêm tool.

## 2. Tool mới `search` — tìm theo tên và theo string literal trong một module

**Vì sao:** đây là thao tác RE đầu tiên với một binary lạ — "chuỗi `youtu.be` / URL / thông báo lỗi này
xuất hiện ở method nào?" và "có member nào tên khớp pattern?". Trong e2e, chính string sweep đã lộ ra
Rickroll — `search` đưa năng lực đó vào tay agent, có kèm vị trí (method + token).

**Thiết kế:** `search(module, query, kind="all"|"names"|"strings", max?)`.
- `names`: khớp wildcard `*`/`?` (case-insensitive) trên FullName của type/method/field.
- `strings`: quét body mỗi method tìm instruction `Ldstr` có operand **chứa** `query` (substring,
  case-insensitive) → trả method + giá trị string.
- Trả mảng hit hợp nhất: `{kind: "type"|"method"|"field"|"string", name, token, value?}`. Cap `max`.

**API:** dnlib — duyệt `mod.GetTypes()` → `.Methods/.Fields`; body: `m.HasBody`,
`instr.OpCode.Code == Code.Ldstr`, `instr.Operand as string`.

## 3. Tool mới `find_references` — ai gọi method X

**Vì sao:** "used-by" là tính năng đắt giá nhất của Analyzer trong dnSpy cho việc lần theo luồng. Với
agent: dựng call graph ngược mà không cần chạy chương trình.

**Thiết kế:** `find_references(module, method | token, scope="module"|"open")`.
- Xác định method đích (theo tên đầy đủ — mọi overload — hoặc theo token).
- Quét mọi method trong phạm vi (`module` = chỉ module đích; `open` = tất cả document đang mở qua
  `documentService.GetDocuments()`), tìm instruction có `Operand is IMethod` khớp đích.
- Trả caller: `{caller, token, callee, ilOffset}`.

**Khớp chuẩn (theo `MethodUsedByNode`):** prefilter theo `mr.Name`, rồi `mr.ResolveMethodDef()` và so
`MDToken.Raw` + `Module.Location` với đích — chính xác, xử lý được cả MemberRef xuyên module. Các opcode
tính là "gọi": `Call`, `Callvirt`, `Newobj`, `Ldftn`, `Ldvirtftn`.

## Fixture (đã đủ, không cần sửa)

- **IL:** decompile `Add` format=il → chứa `ldarg`/`add`/`ret`.
- **search strings:** `"hello"` → `Inspect`; `"pong"` → `Outer.Inner.Ping`.
- **search names:** `"*.Level*"` → Level1/2/3.
- **find_references:** đích `DbgTest.Program.Level2` → caller `Level1`; đích `Add` → caller `Main`.

## Kiểm thử & rủi ro

- Tier 2 (`StaticIntegrationTests`): IL format; search names/strings; find_references với caller tất
  định; token từ `find_references` ghép được với `decompile`; error path (đích không tồn tại).
- Rủi ro: `find_references` scope=open có thể chậm nếu nhiều document lớn → cap số method quét + trả
  `scannedMethods`; string search cần bỏ qua method không body. Cả hai đều dưới `metadataLock` nên
  serial, không đua với engine.
- Không đụng code debug-engine; token nhất quán với debugger như nhóm static hiện có.

## Thứ tự

1. `format=il` (nhỏ nhất, xác nhận đường IL). 2. `search`. 3. `find_references`. 4. Test + docs (38
   tool) + verify Tier 1/2/3 + CI + push.
