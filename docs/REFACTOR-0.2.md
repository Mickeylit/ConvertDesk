# ConvertDesk 0.2 源码优化记录

本轮重点是把已完成的文档工作台整理为边界明确、可测试、依赖更少的产品构建。ConvertDesk 基于 [Filee](https://github.com/KnifeLemon/Filee) 的 MIT 代码开发，原始版权属于 Myeonggu Kim（KnifeLemon）和 Filee contributors；[LICENSE](../LICENSE) 与[第三方声明](../THIRD-PARTY-NOTICES.md) 保留。本报告描述衍生版本的改动，不主张整个项目完全原创，也不代表 Filee 官方发行版。

## 与 0.1 的区别

0.1 已完成独立名称和图标、无侧栏单窗口、顶部设置入口，以及“添加文件 → 选择格式 → 开始转换”的界面流程，并将公开输入/输出限制为 HTML、PDF、PPTX、DOCX、TXT。窗口内拖放无需辅助功能权限，Finder 的 Option 拖放仍是可选快捷方式。

0.2 沿用这套界面，主要改动发生在转换决策、服务生命周期、运行入口和编译依赖。不会把 0.1 的界面重设计重复计算成本轮新增能力。

## 转换决策与运行边界

- 将文档转换判断抽到 `Services/Documents/DocumentConversionPlanner.cs`：输入有效性、输出格式、混合批次可用性和依赖判断由独立服务处理，工作台 ViewModel 负责呈现与交互。
- 收紧实际入口：本地五格式文档才能进入任务；浏览器 URL、虚拟文件和网络拖放不会开启下载，也不会把无效批次的一部分悄悄送入转换。通用工具预设、额外输出格式和 ZIP 打包输出不属于文档产品入口。
- 主程序组合不再注册浏览器下载、文件夹监控和表格打印服务。文档发布模式也不注册上游更新及“新功能”展示服务，且排除相关宣传媒体资源。
- 生产导航仅保留工作台、通用、触发方式、外观、引擎和关于页面。关于页面使用独立只读信息；窗口和托盘的生产构造路径不再依赖更新服务。
- 退出先取消排队和运行任务，再在线程池等待队列排空，最长 3 秒；避免继续启动下一项，也避免 UI 退出等待死锁。转换服务的等待注册与释放使用同一生命周期锁，修复特定时序下无法结束的等待任务。
- 完善订阅与释放：托盘重复创建/销毁、服务销毁后拒收任务，以及保留的上游监控代码取消延迟操作和清理订阅，均有针对性验证。保留监控源码供上游测试使用，不等于生产模式启用了监控。

## 文档专用编译

`ConvertDeskDocumentsOnly=true` 是明确的产品编译开关。macOS 发布脚本自动传入该属性，使用独立 `artifacts/documents-build` 存放编译中间产物，避免覆盖默认全引擎配置的还原资产。默认不带该属性时，仍可编译上游全部引擎及其原有测试。

专用模式从产品程序集排除 CAD、3D、地理、邮箱、数据、电子书、表格、媒体、字幕、通用归档等无关转换器；所有源文件和许可证保留在仓库。`DocumentReaders` 同时收窄，保留五种公开格式以及 HWPX、Markdown 内部转换路线，避免仅隐藏菜单但仍编译所有解析器。

裁剪后不再直接依赖以下 9 个包：

| 移除的直接依赖 | 原用途 |
|---|---|
| ACadSharp | CAD 文件 |
| SharpGLTF.Core | 3D 模型 |
| MimeKitLite、MsgReader | 邮件、Outlook 消息 |
| YamlDotNet、Tomlyn | YAML / TOML 数据 |
| Unhwp | 上游 HWP 文本路线 |
| ExcelDataReader、ExcelNumberFormat | 表格读取与数字格式 |

随之不再进入引擎发布清单的传递依赖包括 Microsoft.IO.RecyclableMemoryStream、Microsoft.Maui.Graphics、OpenMcdf、RtfPipe、UTF.Unknown。

保留 ImageMagick、PDFium、PdfPig、SVG、字体及解压库，因为 DOCX/HTML 图片、PDF 页面和内部中间步骤仍会使用它们。保留少量旧预览与打印数据类型以维持源码兼容；FFmpeg 转换器本身不编入文档程序集，预览辅助代码在文档模式下不会发现或启动 FFmpeg。7-Zip、FFmpeg、calibre 二进制也不包含在当前分发包。

## 量化结果与口径

编译项和依赖数比较全引擎与文档两种配置。体积比较本轮裁剪前后的 **macOS arm64、Release、非自包含引擎发布目录**，不含 App UI、.NET 自包含运行时、字体资源、rhwp/Pandoc，也不含 DMG 压缩效果。

| 指标 | 裁剪前 / 全引擎 | 文档模式 |
|---|---:|---:|
| 编译 C# 源文件 | 262 | 140 |
| 直接 NuGet 依赖 | 21 | 12 |
| 依赖包总数（含传递依赖） | 37 | 23 |
| 引擎发布文件数 | 69 | 45 |
| 引擎发布目录 | 85,980,272 字节 | 74,538,179 字节 |
| `Filee.Engines.dll` | 2,802,176 字节 | 1,519,616 字节 |

引擎发布目录减少 **11,442,093 字节，约 11.44 MB / 13.3%**；引擎程序集减少约 **45.8%**。这些数字不表示完整应用或 DMG 同比例变小，最终整包大小以交付产物为准。

## 本轮验证

| 验证 | 结果 | 覆盖内容 |
|---|---|---|
| App 聚焦测试 | 44 / 44 通过 | 转换计划、工作台、输入边界、圆环入口、生产导航、托盘及窗口/服务生命周期 |
| 裁剪后的引擎测试 | 58 / 58 通过 | 50 项五格式转换与文本回归、6 项 TXT → PPTX 分页、2 项程序集/注册边界 |
| 默认全引擎配置测试 | 56 / 56 通过 | 完整默认测试工程编译成功，并执行同样的 50 项文档与 6 项分页测试 |
| App 文档模式 Release 构建 | 通过，0 错误 | 实际 App 在裁剪程序集下可编译 |
| 脚本与改动格式检查 | 通过 | `bash -n`、`git diff --check` |

三组测试存在样例复用，不应相加宣称为 158 个独立场景。58 项文档模式测试直接加载裁剪后的程序集，除了输出文件有效性和正文标记，还检查原文件 SHA 不变、无关转换器类型/程序集引用缺失，以及仅使用随包 rhwp/Pandoc 时的完整 20 个不同格式转换方向。

可复现的重点命令：

```bash
dotnet test tests/Filee.Documents.Tests/Filee.Documents.Tests.csproj -c Release \
  -p:ConvertDeskDocumentsOnly=true --artifacts-path artifacts/documents-tests

dotnet test tests/Filee.Engines.Tests/Filee.Engines.Tests.csproj -c Release \
  -p:ConvertDeskDocumentsOnly=false --artifacts-path artifacts/full-profile-tests \
  --filter 'FullyQualifiedName~ConvertDeskDocumentTests|FullyQualifiedName~PlainTextSlidePaginationTests'

dotnet build src/Filee.App/Filee.App.csproj -c Release \
  -p:ConvertDeskDocumentsOnly=true --artifacts-path artifacts/documents-app
```

## 功能与平台仍有的限制

基础转换样例通过不等于复杂排版完全保真。PDF → PPTX 仍是每页图片；HTML / DOCX / TXT → PPTX 会重排为可编辑内容；扫描 PDF 没有文字层时需要先进行 OCR，主工作台不会自动识别。复杂表格、公式、动画、网页 CSS 和缺失字体应逐份检查。

当前验证发生在 Apple Silicon Mac。Intel Mac 构建路径尚未实机验证；Windows 仍需后续打包和实机验证，本轮不宣称 Windows 成品已验收。详细使用边界见 [CONVERTDESK.md](CONVERTDESK.md)。
