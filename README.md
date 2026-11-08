# Choyeon Tools

Windows 硬件与系统配置查看器（WinUI 3 / .NET 10 桌面应用）。界面显示名 **Choyeon Tools**，作者 Choyeon，MIT 许可。

## 下载

[Releases](../../releases/latest) 里有两个安装包：

- `Choyeon Tools.exe`（约 72 MB）—— 自包含单文件，双击即用，不需要装 .NET 运行时。推荐。
- `choyeon-tools.msix`（约 48 MB）—— 带包身份的 MSIX，有开始菜单项和磁贴。它用自签名开发证书签名，直接双击装不上，需要先信任证书：

  ```powershell
  # 以管理员身份执行，证书在同一个 Release 里
  Add-AppxPackage -CertificatePath .\choyeon-tools_cert.cer -PackagePath .\choyeon-tools.msix
  ```

  单独导入证书的话，把 `.cer` 装进 **本地计算机** 的 `受信任的人` 和 `受信任的根证书颁发机构`，或直接在 设置 → 系统 → 开发者选项 里开启侧载。

## 目录结构

```
ChoyeonTools/                 应用本体
  Models/                     页面数据模型（InfoCategory / InfoGroup / InfoField / Reading）
  Providers/                  各分类页的采集与拼装（CPU、内存、GPU、硬盘、声卡、网络、USB、系统、显示器）
  Services/                   WMI、原始 SMBIOS、Win32 P/Invoke、传感器、驱动、实时采样、名称库、格式化
  ViewModels/                 总览与卡片状态、导出
  Controls/ Resources/        UsageGraph 曲线控件、设计令牌与卡片样式
  Assets/                     图标、磁贴、Assets/Hardware/*.json 硬件 ID 名称库
  Package.appxmanifest        MSIX 清单（显示名、图标、能力声明）
tools/                        仓库自带的构建与验证脚本，不参与应用编译
  vendor-data/*.ids           PCI/USB/PNP/JEP106 上游公开数据快照
  build-*-catalog.ps1         把 .ids 生成 ChoyeonTools/Assets/Hardware/*.json
  uia-*.ps1 verify-*.ps1      用 UIA 逐页取词、截图，验收界面渲染结果
  capture-window.ps1          按窗口截图
.artifacts/                   验证脚本的转储与截图输出（已 gitignore，不入库）
```

## 构建与运行

打包（MSIX，带包身份）运行，必须在 `ChoyeonTools` 目录里执行——`dotnet build` 不会刷新 AppX 布局，只有 `winapp run` 会重新部署：

```
cd ChoyeonTools
dotnet build -c Debug -p:PublishTrimmed=false
winapp run --detach -c Debug -p:PublishTrimmed=false
```

松散发布（走 `Properties/PublishProfiles/win-x64.pubxml`）：

```
dotnet publish -c Release -r win-x64
```

自包含单文件 exe（约 72 MB，单个文件）。注意两点硬要求：`-p:AssemblyName` 决定 exe 名字，**发布后再改 exe 文件名会启动崩溃**；`PublishTrimmed` 必须为 false，WinUI/XAML/LibreHardwareMonitor/WMI 都依赖反射：

```
dotnet publish -c Release -r win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
  -p:WindowsAppSdkUndockedRegFreeWinRTInitialize=true -p:AssemblyName="Choyeon Tools" --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o ../publish/win-x64-singlefile
```

MSIX 安装包用 `winapp package` 生成（`dotnet publish -p:WindowsPackageType=MSIX` 只发布松散文件，不会产出包）：

```
winapp package ChoyeonTools.csproj -c Release --arch x64 --generate-cert --publisher "CN=AppPublisher" --output ../dist/choyeon-tools.msix
```

`dist/` 连同里面的 `*_cert.pfx` 已在 `.gitignore` 里；对外分发只放导出的公钥 `.cer`，私钥不入库。

## 数据来源

- 静态规格：WMI（`root\cimv2`、`root\wmi`、`root\microsoft\windows\storage`）+ 原始 SMBIOS 表（`GetSystemFirmwareTable('RSMB')`）。内存条厂商只写在 SMBIOS Type 17 偏移 2Ch 的数字 ID 里，WMI 与 LibreHardwareMonitor 都拿不到，因此单独读原始表补齐。CPU 加速频率同理：`Win32_Processor.MaxClockSpeed` 只是基础频率，最大加速频率取 Type 4 偏移 14h 的 Max Speed。
- 实时读数：LibreHardwareMonitorLib（温度、风扇、功耗），提权后才能读到全部传感器。
- ID 名称库：`Assets/Hardware/*.json`，由 `tools/build-*-catalog.ps1` 从 `tools/vendor-data/*.ids`（PCI ID Repository、hwdata、usb.ids、JEDEC JEP106）生成。每个 JSON 都带 `source`/`attribution`/`version` 字段，随数据分发许可声明。未使用任何需要破解或解密的加密数据库。

## 验证

改完界面或采集逻辑后跑逐页转储，确认每一页都有内容而不是占位说明：

```
powershell -File tools/uia-pages.ps1      # 输出 .artifacts/uia-pages.txt
powershell -File tools/verify-elevated.ps1  # 提权会话里部署 + 取词 + 截图
```
