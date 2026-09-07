# AxamlTPreviewer

**中文** | [English](#english)

基于开源 Avalonia Visual Studio 扩展的社区衍生版，为 Avalonia / AXAML 开发提供实时预览、智能补全等能力。

> [!NOTE]
> 官方上游 AvaloniaVS 已归档，其新版商业能力见 [Avalonia Accelerate](https://avaloniaui.net/accelerate)。本仓库在旧开源版本基础上继续社区定制维护，**不是** Avalonia 官方扩展。

**本仓库：** https://github.com/xiu-xiang/AvaloniaVS  
**许可证：** MIT（见 [license.md](license.md)）

### 来源与致谢

本项目 **fork** 自 [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS)（其对官方 [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) 旧开源版本的定制分支，含样式类补全等增强），并在此基础上重命名与继续开发为 **AxamlTPreviewer**。

| 层级 | 仓库 |
|------|------|
| 官方原版（已归档） | [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) |
| Fork 源（直接上游） | [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) |
| 本仓库 | [xiu-xiang/AvaloniaVS](https://github.com/xiu-xiang/AvaloniaVS) |

感谢 Avalonia 原作者与贡献者，以及 [kongdetuo](https://github.com/kongdetuo) 的定制工作与开源贡献。本仓库在 **MIT** 许可下修改与再分发，并保留上游版权与许可声明；文中提及的第三方名称与商标归其各自权利人所有。

---

## 功能特性

### 基础能力（继承自开源 AvaloniaVS / 上游定制）

- **AXAML 实时预览**（Previewer / Designer）：编辑即预览
- **智能代码补全**（IntelliSense）：元素、属性、绑定表达式等
- **样式类补全**（上游特色能力）
  - 在 `Classes=""` 内补全类名（多类名、基类/接口/全局候选）
  - 支持 `Classes.class="{Binding}"`，点号后补全并生成绑定
  - 自动过滤伪类（`:pseudo`）
- 绑定表达式补全（`{Binding ...}`）
- AXAML 文件图标与代码片段（Snippets）
- 捆绑 **Avalonia.Templates 12.1.0** 项目模板（默认 `Avalonia` 包版本 **12.1.0**）
- **添加新项**支持：窗体 Window、UserControl、TemplatedControl、资源字典、Styles（AXAML；VS2026 包已接入）

### 本仓库新增 / 增强

以下能力为本仓库在上游基础上继续开发的增强项，便于在 AXAML 编辑体验上贴近日常 C# 工作流：

- **已废弃成员提示**
  - 补全列表中标识带 `[Obsolete]` 的属性/事件（例如 `TextBox.Watermark`），并展示替代说明
  - 对文档中已写属性名显示警告波浪线与悬停提示（风格接近 CS0618）
- **转到定义**
  - 支持 AXAML 中类型、属性、附加属性、事件名以及事件处理方法的 F12 / Ctrl+点击导航
- **补全体验改进**
  - 属性引号内补全、`{x:Static}` 成员过滤等交互优化
- **多宿主支持**
  - 同时提供 Visual Studio **2022** 与 **2026** 扩展包

> 说明：预览器协议、补全引擎核心等仍建立在开源 AvaloniaVS / CompletionEngine 之上；本仓库的改动以增强编辑器体验与 Avalonia 12.1 适配为主。

## 系统要求

按 Visual Studio 版本选择对应扩展包：

| Visual Studio | 宿主项目 | 安装目标 |
|---------------|----------|----------|
| **2022**（17.x） | `AvaloniaVS.VS2022` | `[17.0, 18.0)` |
| **2026**（18.x） | `AvaloniaVS.VS2026` | `[18.0,)` |

- 需安装 .NET 开发工具与 C# 语言服务
- 支持 amd64 / arm64

## 构建与调试

1. 使用对应版本的 Visual Studio 打开 `AvaloniaVS.sln`
2. 安装「Visual Studio 扩展开发」工作负载
3. 将 **AvaloniaVS.VS2022** 或 **AvaloniaVS.VS2026** 设为启动项目并运行（F5）
4. 将打开 [VS Experimental Instance](https://docs.microsoft.com/en-us/visualstudio/extensibility/the-experimental-instance)，可在其中加载 Avalonia 解决方案进行调试

产物示例：

- `AvaloniaVS.VS2022\bin\<Config>\AxamlTPreviewer.vsix`
- `AvaloniaVS.VS2026\bin\<Config>\AxamlTPreviewer.vsix`


```powershell
dotnet restore AvaloniaVS.sln
dotnet test tests\CompletionEngineTests\CompletionEngineTests.csproj
```

> 若问题出在 Avalonia Previewer 宿主进程本身，请参考 [Debugging the Previewer](https://docs.avaloniaui.net/docs/guides/implementation-guides/debugging-the-previewer)。

## 更多文档

- [项目开发说明](docs/项目开发说明.md)（架构、模块、二次开发）
- [VS2022 商店介绍 overview.md](AvaloniaVS.VS2022/overview.md)
- [VS2026 商店介绍 overview.md](AvaloniaVS.VS2026/overview.md)

---

<a id="english"></a>

# AxamlTPreviewer

[中文](#axamltpreviewer) | **English**

A community **fork** of the open-source Avalonia Visual Studio extension, providing live preview, IntelliSense, and related tooling for Avalonia / AXAML development on Visual Studio **2022** and **2026**.

> [!NOTE]
> Upstream AvaloniaVS is archived. Official newer tooling is available via [Avalonia Accelerate](https://avaloniaui.net/accelerate). This repository continues community customization based on the older open-source extension and is **not** an official Avalonia product.

**This repository:** https://github.com/xiu-xiang/AvaloniaVS  
**License:** MIT (see [license.md](license.md))

### Provenance & Credits

This project is a **fork** of [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) (a customization of the archived official [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS), including enhancements such as style class completion), and is further developed here as **AxamlTPreviewer**.

| Layer | Repository |
|------|------------|
| Official original (archived) | [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) |
| Fork source (direct upstream) | [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) |
| This repository | [xiu-xiang/AvaloniaVS](https://github.com/xiu-xiang/AvaloniaVS) |

Thanks to the Avalonia authors and contributors, and to [kongdetuo](https://github.com/kongdetuo) for upstream customization. Modifications and redistribution follow the **MIT** license while retaining upstream copyright and license notices. Third-party names and trademarks belong to their respective owners.

---

## Features

### Baseline (from open-source AvaloniaVS / upstream customization)

- **AXAML live previewer** (Previewer / Designer)
- **IntelliSense** for elements, properties, bindings, and more
- **Style class completion** (upstream highlight feature)
  - Complete class names inside `Classes=""` (multi-token, base/interface/global candidates)
  - Support `Classes.class="{Binding}"` completion after the dot
  - Filter out pseudo-classes (`:pseudo`)
- Binding expression completion (`{Binding ...}`)
- AXAML file icons and code snippets
- Bundled **Avalonia.Templates 12.1.0** project templates (default `Avalonia` package version **12.1.0**)
- **Add New Item** templates: Window, UserControl, TemplatedControl, ResourceDictionary, Styles (AXAML; wired in the VS2026 package)

### Added / enhanced in this repository

- **Obsolete member hints**
  - Mark `[Obsolete]` properties/events in completion (e.g. `TextBox.Watermark`) with replacement messages
  - Warning squiggles and hover tooltips for obsolete attributes already present in AXAML
- **Go to Definition**
  - F12 / Ctrl+Click for types, properties, attached members, events, and event handlers in AXAML
- **Completion UX improvements**
  - Better in-quotes completion and `{x:Static}` member filtering
- **Multi-host packages**
  - Separate VSIX packages for Visual Studio **2022** and **2026**

## Requirements

Install the package that matches your Visual Studio version:

| Visual Studio | Host project | Installation target |
|---------------|--------------|---------------------|
| **2022** (17.x) | `AvaloniaVS.VS2022` | `[17.0, 18.0)` |
| **2026** (18.x) | `AvaloniaVS.VS2026` | `[18.0,)` |

- .NET development tools and C# language services required
- amd64 / arm64 supported

## Build & Debug

1. Open `AvaloniaVS.sln` in the matching Visual Studio version
2. Install the **Visual Studio extension development** workload
3. Set **AvaloniaVS.VS2022** or **AvaloniaVS.VS2026** as the startup project and press F5
4. An [Experimental Instance](https://docs.microsoft.com/en-us/visualstudio/extensibility/the-experimental-instance) launches; open an Avalonia solution there to debug

Output examples:

- `AvaloniaVS.VS2022\bin\<Config>\AxamlTPreviewer.vsix`
- `AvaloniaVS.VS2026\bin\<Config>\AxamlTPreviewer.vsix`


```powershell
dotnet restore AvaloniaVS.sln
dotnet test tests\CompletionEngineTests\CompletionEngineTests.csproj
```

> If the issue is inside the Avalonia Previewer host process itself, see [Debugging the Previewer](https://docs.avaloniaui.net/docs/guides/implementation-guides/debugging-the-previewer).

## Further reading

- [Development guide (Chinese)](docs/项目开发说明.md)
- [VS2022 Marketplace overview](AvaloniaVS.VS2022/overview.md)
- [VS2026 Marketplace overview](AvaloniaVS.VS2026/overview.md)
