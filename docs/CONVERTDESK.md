# ConvertDesk 0.2 使用与实现范围

ConvertDesk 是基于 [KnifeLemon/Filee](https://github.com/KnifeLemon/Filee) 的独立 MIT 衍生版本，当前优先交付 macOS。本页记录实际功能边界，完整使用和构建入口见 [README](../README.md)。

## 工作台

主界面采用无侧栏布局，顶部保留“设置”入口。操作顺序是“拖入或选择文件 → 选择目标格式 → 开始转换”。文件先进入待转换列表，可移除、清空或继续添加；混合批次中如有不可转换项目，会明确提示，不会悄悄跳过。

窗口内拖放不需要辅助功能权限。Finder 的 Option 拖放快捷菜单是可选功能，需要单独启用和授权；普通转换无需重启电脑。主工作台只接收本地文件，网页链接应先保存为本地文档。

结果默认位于原文件旁，也可在通用设置中选择输出位置。源文件保留，结果遇到重名会自动编号。转换状态、错误和打开输出目录的入口在主窗口中展示。

## 五种格式

| 输入 | 可选择的其他输出 | 主要限制 |
|---|---|---|
| HTML（`.html` / `.htm`） | PDF、PPTX、DOCX、TXT | 读取文档内容，不等同于完整浏览器渲染；复杂 CSS、脚本和交互不保证保留 |
| PDF | HTML、PPTX、DOCX、TXT | 可编辑输出依赖文字与阅读顺序提取；扫描件另需文字识别 |
| PPTX | HTML、PDF、DOCX、TXT | 动画、转场和演讲交互不会保留，复杂图表和字体可能变化 |
| DOCX | HTML、PDF、PPTX、TXT | 复杂表格、公式、浮动对象和分页不保证与 Word 完全一致 |
| TXT | HTML、PDF、PPTX、DOCX | 只包含文本，目标格式中的样式是重新生成的 |

输入和输出范围在 UI、CLI 和任务队列中均受限制。源代码仍包含上游通用引擎和内部中间格式，这不表示产品入口支持额外文件类型。同一格式不重复转换。

**PPTX 的两种生成方式：** PDF 转 PPTX 时，将每页渲染为一张图片幻灯片，适合展示页面外观，页内文字不可编辑；HTML、DOCX、TXT 转 PPTX 时，按内容结构生成可编辑幻灯片，内容会重排，不承诺页数或布局与原文件一致。

**扫描 PDF 与 OCR：** 已存在可识别文字层的 PDF 可以提取文本。只有扫描图片、没有文字层的 PDF，普通 TXT 导出会提示没有可提取文字，其他文档输出可能保留为图片；主工作台不会自动进行 OCR。可先使用 OCR 工具将扫描件变成带文字层的 PDF，再导入工作台。保留的底层 OCR 功能依赖可用识别引擎：macOS 可用 Apple Vision 支持的语言，其他情况需要另行配置 Tesseract 及相应语言数据。当前工作台未提供 OCR 开关，包内也不包含 Tesseract；扫描件识别不属于本次已验证的完整能力。

## 引擎与隐私

应用随包包含 rhwp 0.8.7 和 Pandoc 3.12。基础五格式转换不要求安装 Microsoft Office 或 LibreOffice。额外配置的 LibreOffice 等引擎可能参与部分转换，但不是本次基础样例验证的必要条件。

7-Zip、FFmpeg、calibre 不包含在 ConvertDesk 当前打包清单中。更完整的第三方库来源与历史上游范围见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

文档转换在本机完成。构建和下载可选引擎需要网络；主工作台不提供浏览器链接下载流程。此衍生版关闭上游 Filee 的自动更新入口，以免将独立应用更新为原版 Filee。

## 文档专用构建

macOS 发布脚本显式传入 `ConvertDeskDocumentsOnly=true`，并使用独立 `artifacts/documents-build` 目录。默认不带该属性的开发与测试仍编译上游全引擎，所有源文件与历史许可证保留。

专用模式从程序集移除 CAD、3D、地理、邮箱、数据、电子书、表格、媒体、字幕、通用归档等转换器，文档读取入口仅保留五种公开格式及 HWPX / Markdown 内部路线。依赖随用途裁剪；ImageMagick、PDFium、SVG、字体和引擎解压辅助代码仍被文档图像/PDF流程使用，不能按目录名称直接删去。为保持旧视图源码兼容，少量预览/打印数据类型保留；文档模式不发现或启动 FFmpeg。

同一 macOS arm64 引擎发布口径的对比（不含 App UI、.NET 自包含运行时和 rhwp/Pandoc）：

| 项目 | 全引擎 | 文档模式 |
|---|---:|---:|
| 编译 C# 源文件 | 262 | 140 |
| 直接 NuGet 依赖 | 21 | 12 |
| 依赖包总数（含传递依赖） | 37 | 23 |
| 引擎发布目录 | 85,980,272 字节 | 74,538,179 字节 |
| 引擎程序集 | 2,802,176 字节 | 1,519,616 字节 |

这次移除的直接依赖为 ACadSharp、SharpGLTF.Core、MimeKitLite、MsgReader、YamlDotNet、Tomlyn、Unhwp、ExcelDataReader、ExcelNumberFormat。发布目录减少约 11.44 MB（13.3%）；完整应用的体积还取决于运行时、字体和随包外部引擎，不应把该比例当作整个应用的缩减比例。

`tests/Filee.Documents.Tests` 直接验证裁剪后的程序集，复用五格式转换与长文本分页样例，并检查无关转换器/程序集引用确实缺失。普通 `Filee.Engines.Tests` 继续用于全引擎配置。

## 验证与平台

在当前 Apple Silicon Mac 上，已完成五种格式 20 个不同格式转换方向的基础样例验证，另有仅使用打包引擎的同范围验证。检查内容包括输出格式有效性、正文标记保留及源文件未被修改；这些检查不等于所有复杂文档的排版验收。

macOS arm64 是当前主要交付平台。脚本支持 `--runtime osx-x64`，但 Intel Mac 尚未实机验证。Windows 保留跨平台实现基础，后续需要完成分发包、系统集成和 Windows 实机验证，不应把源码可编译等同于 Windows 成品已验收。

## 署名与分发

原始代码版权属于 Myeonggu Kim（KnifeLemon）和 Filee contributors；[MIT LICENSE](../LICENSE) 保持原文。ConvertDesk 的新名称、界面和功能范围是此衍生版本的改动，不表示抹去上游来源。

打包的独立引擎分别使用 MIT（rhwp）和 GPL-2.0-or-later（Pandoc）。[第三方声明](../THIRD-PARTY-NOTICES.md) 列出固定版本、源码位置和随包许可证。重新分发时须保留这些声明，并履行相应组件的源码提供义务。
