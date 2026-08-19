# dnSpyEx — hai vấn đề gặp khi làm dnSpy.MCP (gửi upstream)

Ngày: 2026-08-19
Cả hai **không** thuộc extension MCP — chúng ở phần lõi dnSpy, phát hiện khi lái debugger cường độ
cao từ một AI agent. Đây là bản thảo issue để dán lên https://github.com/dnSpyEx/dnSpy/issues.
Có repro và stack đầy đủ.

---

## Issue 1 — AccessViolation khi terminate một process đang paused (Locals window refresh đọc PE image đã giải phóng)

### Tóm tắt
dnSpy có thể sập với `System.AccessViolationException` (`0xC0000005`), thỉnh thoảng kèm heap
corruption (`0xC0000374`), khi một process đang bị debug được **terminate trong lúc đang paused**, nếu
cửa sổ Locals đang hiển thị. Không có UI/user code trong đường crash ngoài phần refresh của chính dnSpy.

### Môi trường
- dnSpyEx, `DnSpyAssemblyVersion` 6.6.0.0, net10.0-windows, Windows 10 x64.
- Debug engine: CorDebug (.NET Framework/.NET target).

### Repro
1. Mở một app .NET để debug, đặt breakpoint ở một method chạy nhiều lần.
2. Để cửa sổ **Locals** hiển thị.
3. Lặp nhanh: start → chờ trúng breakpoint (paused) → **Stop Debugging (terminate)** ngay khi đang
   paused → start lại. Lặp vài chục lần dưới điều khiển tự động (không có thao tác tay chờ giữa các bước).
4. Sau một số vòng, dnSpy AV.

Điều khiển bằng tay khó tái hiện vì con người thao tác chậm; lái tự động (ví dụ qua MCP) thì tái hiện được.

### Stack (rút gọn)
```
System.AccessViolationException: Attempted to read or write protected memory.
   at dnlib.IO.UnalignedNativeMemoryDataStream.ReadUInt16(UInt32)
   at dnlib.PE.PEImage..ctor(IntPtr, UInt32, ImageLayout, Boolean)
   at ...Metadata.Impl.MD.DmdEcma335MetadataReader.Create(DmdModuleImpl, IntPtr, UInt32, Boolean)
   at ...Metadata.Impl.DmdLazyMetadataReader.InitializeMetadataReader()
   at ...Metadata.Impl.DmdAppDomainImpl.GetAssemblyCore(String, IDmdAssemblyName)
   at dnSpy.Documents.AssemblyResolver...Resolve(IAssembly, ModuleDef)
   at dnlib.DotNet.TypeRef.Resolve()
   at ICSharpCode.Decompiler.Ast.AstBuilder.CreateMethod(MethodDef)
   at ...DbgMethodDebugInfoProviderImpl.GetMethodDebugInfo(...)
   at ...DbgEngineLanguageImpl.InitializeContext(...)
   at dnSpy.Debugger.Evaluation.UI.ValueNodesProviderImpl.GetNodes(...)
   at dnSpy.Debugger.Evaluation.ViewModel.Impl.ValueNodesVM.RecreateRootChildren_UI()
   at ...ValueNodesProviderImpl.<DbgCallStackService_FramesChanged>b__31_0()
   ... WPF Dispatcher ...
   at dnSpy.MainApp.StartUpClass.Main()
```

### Phân tích
`ValueNodesVM.RecreateRootChildren_UI` chạy khi call stack đổi. Đường này decompile method đang paused,
và để làm vậy nó **đọc PE image của debuggee ngay từ bộ nhớ tiến trình đó** (`PEImage..ctor(IntPtr, ...)`).
Khi process bị terminate, vùng nhớ đó được giải phóng; nếu refresh đua với việc terminate, nó đọc bộ nhớ
đã giải phóng → AV. Việc đọc từ `IntPtr` bộ nhớ tiến trình ngoài vốn không an toàn nếu tiến trình có thể
biến mất giữa chừng.

### Đề xuất
- Bọc đường đọc PE-from-memory (hoặc toàn bộ `RecreateRootChildren_UI` refresh) sao cho chịu được việc
  process đang biến mất — kiểm tra process còn sống / bắt AV ở ranh giới native read thay vì để nó nổi lên.
- Hoặc: hủy/không lên lịch refresh Locals khi runtime đang ở trạng thái terminating.

### Giảm nhẹ phía chúng tôi
Extension MCP không sửa được (crash ở UI dnSpy). Suite test của chúng tôi phát hiện dnSpy mất kết nối và
báo đúng "dnSpy crashed" thay vì để thành hàng loạt lỗi rời rạc.

---

## Issue 2 — Race lúc khởi động: settings được nạp trên background thread trước khi `--settings-file` được áp

### Tóm tắt
`App` constructor kick MEF (đọc settings) sang một background thread **một dòng trước** khi
`AppDirectories.SetSettingsFilename(args.SettingsFilename)` được gọi. Thứ tự đúng chỉ nhờ `Task.Run`
phải xếp lịch nên chậm hơn, không phải nhờ cấu trúc bảo đảm.

### Vị trí
`dnSpy/dnSpy/MainApp/App.xaml.cs`, trong `App(bool readSettings, Stopwatch startupStopwatch)`:

```csharp
// PERF: Init MEF on a BG thread...
initializeMEFTask = Task.Run(() => InitializeMEF(readSettings, useCache: readSettings));   // đọc settings
...
AppDirectories.SetSettingsFilename(args.SettingsFilename);                                   // đặt path
```

`InitializeMEF` (chạy nền) cuối cùng gọi tới việc đọc settings; `SetSettingsFilename` chạy ở dòng sau.
Nếu background thread chạy tới phần đọc settings trước khi dòng `SetSettingsFilename` kịp thực thi, nó sẽ
đọc settings từ path **mặc định** thay vì path truyền qua `--settings-file`.

### Vì sao đáng quan tâm
`--settings-file` là cách chuẩn để cô lập một dnSpy dùng-một-lần (test, automation) khỏi profile thật của
người dùng. Nếu race thắng sai chiều, dnSpy đọc/ghi nhầm profile thật. Thực tế hiện chưa thấy thua, nhưng
đây là bảo đảm ngầm dựa vào timing.

### Đề xuất
Gọi `AppDirectories.SetSettingsFilename(args.SettingsFilename)` **trước** khi kick `initializeMEFTask`,
để path settings được cố định trước khi bất kỳ ai đọc nó.

### Ghi chú
Đã kiểm chứng thực nghiệm rằng thứ tự hiện tại *thường* đúng (chạy với `--settings-file <temp>` thì
`bp_list` rỗng, tức profile thật không bị nạp). Đây là đề xuất làm cho đúng-về-cấu-trúc, không phải báo
lỗi đang xảy ra.
