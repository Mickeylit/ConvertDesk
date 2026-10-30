# ConvertDesk

一个专注于 **HTML、PDF、PPTX、DOCX、TXT** 的本地文档转换工具。当前版本优先交付 macOS，使用独立名称、图标和简洁的单窗口工作台。

ConvertDesk 基于 [Filee](https://github.com/KnifeLemon/Filee) 的 MIT 开源代码开发，是独立衍生版本，并非 Filee 官方发行版。原项目版权和许可证保留在 [LICENSE](LICENSE)；第三方组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 使用

1. 打开 `ConvertDesk.app`。如果使用 DMG，将应用拖入“应用程序”后打开。
2. 将本地文档拖入窗口，或点击“选择文件”。可以一次添加多个文件。
3. 选择 HTML、PDF、PPTX、DOCX 或 TXT，再点击“开始转换”。选择格式本身不会立即转换。
4. 在窗口查看进度和错误，完成后点击“打开文件夹”。结果默认保存在原文件旁；保留原文件，重名结果自动编号。

主界面没有侧栏。顶部“设置”菜单可打开通用设置、转换引擎、触发方式、外观和关于页面；通用设置可修改默认输出位置。

**窗口内拖放和文件选择无需辅助功能权限，也无需重启电脑。** Finder 中按住 Option 拖动唤出圆环是可选快捷方式，需要开启对应触发方式并授予辅助功能权限；它不是主工作台的使用前提。

本地构建默认使用 ad-hoc 签名，尚未经过 Apple 公证。首次打开如果被 macOS 阻止，可在“系统设置 → 隐私与安全”中允许打开。

## 能力与边界

- 产品入口只接受五种格式，HTML 同时接受 `.html` 和 `.htm`；Word 格式为 `.docx`。
- 五种格式之间的 20 个不同格式转换方向已完成基础样例测试，包括仅使用随包引擎的情况。相同格式不重复转换。
- **PDF → PPTX**：每页生成一张图片幻灯片，保持页面外观；页内文字不可直接编辑。
- **HTML / DOCX / TXT → PPTX**：按内容结构生成可编辑幻灯片，会重新排版，不保证与原文件逐页一致。
- **扫描 PDF**：没有文字层时，普通转换无法可靠提取文字；主工作台不会自动执行 OCR。需要先使用 OCR 工具将扫描件变成带文字层的 PDF，再导入转换。
- 复杂表格、公式、图表、动画、字体和网页 CSS 不保证完全还原。TXT 只保留文本；转换完成后应检查正式用途的文件。

详细说明见 [docs/CONVERTDESK.md](docs/CONVERTDESK.md)。

## 平台状态

| 平台 | 当前状态 |
|---|---|
| macOS Apple Silicon（arm64） | 本机已完成构建、应用启动和基础转换测试，是本次主要交付平台 |
| macOS Intel（x64） | 构建脚本支持选择该目标，尚未在 Intel Mac 实机验证 |
| Windows | 保留跨平台代码与引擎获取路径，后续仍需打包和 Windows 实机验证；本次不宣称已完成 Windows 版本验收 |

## 在 macOS 构建

需要 .NET 10 SDK、Python 3 和 macOS 命令行工具。应用按自包含方式发布，使用者不需要另外安装 .NET。

在仓库根目录执行：

```bash
# 构建并运行当前 Mac 架构版本
./script/build_and_run.sh

# 只构建应用和 DMG
./script/build_and_run.sh --build-only --dmg

# 为 Intel Mac 构建（不代表已完成 Intel 实机验证）
./script/build_and_run.sh --build-only --runtime osx-x64 --output artifacts/macos-x64
```

默认产物位于 `artifacts/macos/`。如果 `dotnet` 不在 PATH，可以通过 `CONVERTDESK_DOTNET=/完整路径/dotnet` 指定 SDK。

脚本以 `ConvertDeskDocumentsOnly=true` 发布文档专用程序集；默认开发配置仍保留上游全引擎，源文件和许可证不删除。专用构建排除 CAD、3D、地理、邮箱、数据、电子书、表格与媒体转换器及其不再需要的直接依赖，保留文档/PDF内部使用的图像、SVG、字体与解压组件。

脚本发布应用和命令行入口，打包 rhwp **0.8.7**、Pandoc **3.12**，并验证下载文件的 SHA-256。首次构建需联网获取依赖；常规文档转换在本机执行。构建时可用 `CONVERTDESK_SIGNING_IDENTITY` 指定自己的签名身份，公证仍需另行完成。

运行产品重点测试（保留的上游测试包含本产品已经移除的功能，不作为产品验收入口）：

```bash
./script/test_documents.sh
```

`ConvertDeskDocumentsOnly=true` 是发布脚本使用的文档构建模式：不编译 CAD、3D、邮箱、地理信息、表格读写等无关转换器，并排除对应依赖和原版发布/赞助媒体。完整上游源码和版权仍保留，便于查阅与审计；交付包不等于全功能 Filee。

本轮架构变更和验证记录见 [docs/REFACTOR-0.2.md](docs/REFACTOR-0.2.md)。

## 来源与许可证

原始 Filee 由 **Myeonggu Kim（KnifeLemon）和 Filee contributors** 开发。ConvertDesk 保留其 MIT 许可和署名，新增界面、产品范围限制与文档转换流程；没有使用 Filee 的官方更新通道或官方签名身份。

rhwp 与 Pandoc 作为独立程序调用，各自保留其许可证。对应文本和源码链接见 [licenses](licenses) 与 [第三方声明](THIRD-PARTY-NOTICES.md)。Pandoc 使用 GPL-2.0-or-later，不因主程序采用 MIT 而改变。

原版说明保留在 [README.upstream.md](README.upstream.md) 和 [README.zh-CN.upstream.md](README.zh-CN.upstream.md)。其他未更新的语言说明与通用架构文档可能描述上游完整功能，不代表 ConvertDesk 当前提供这些功能。
