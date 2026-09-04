# AxamlTPreviewer

**中文** | [English](#english)

基于 Avalonia Visual Studio 扩展的深度定制版，为 Avalonia / AXAML 开发提供实时预览、智能补全等能力。

> [!NOTE]
> 官方上游 AvaloniaVS 已归档，其新版能力见 [Avalonia Accelerate](https://avaloniaui.net/accelerate)。本仓库在旧开源基础上继续定制维护。

**本仓库：** https://github.com/xiu-xiang/AvaloniaVS  
**许可证：** MIT（见 [license.md](license.md)）

### 来源与致谢

本项目 **fork** 自 [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS)（其对官方 [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) 旧开源版本的定制分支，含样式类补全等增强），并在此基础上重命名与继续开发为 **AxamlTPreviewer**。

| 层级 | 仓库 |
|------|------|
| 官方原版（已归档） | [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) |
| Fork 源（直接上游） | [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) |
| 本仓库 | [xiu-xiang/AvaloniaVS](https://github.com/xiu-xiang/AvaloniaVS) |

感谢 [kongdetuo](https://github.com/kongdetuo) 的定制工作与开源贡献。

---

## 功能特性

- **AXAML 实时预览**（Previewer / Designer）：编辑即预览
- **智能代码补全**（IntelliSense）：元素、属性、绑定表达式等
- **样式类补全**（特色）
  - 在 `Classes=""` 内补全类名（多类名、基类/接口/全局候选）
  - 支持 `Classes.class="{Binding}"`，点号后补全并生成绑定
  - 自动过滤伪类（`:pseudo`）
- 绑定表达式补全（`{Binding ...}`）
- AXAML 文件图标与代码片段（Snippets）
- 捆绑 **Avalonia.Templates 12.1.0** 项目模板（默认 `Avalonia` 包版本 **12.1.0**）
- **添加新项**支持：窗体 Window、UserControl、TemplatedControl、资源字典、Styles（AXAML）

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
- [商店介绍 overview.md](AvaloniaVS.VS2022/overview.md)

---

<a id="english"></a>

# AxamlTPreviewer

[中文](#axamltpreviewer) | **English**

A customized **Visual Studio 2022** extension for Avalonia / AXAML development, providing live preview, IntelliSense, and related tooling.

> [!NOTE]
> Upstream AvaloniaVS is archived. Official newer tooling is available via [Avalonia Accelerate](https://avaloniaui.net/accelerate). This repository continues customization based on the older open-source extension.

**This repository:** https://github.com/xiu-xiang/AvaloniaVS  
**License:** MIT (see [license.md](license.md))

### Provenance & Credits

This project is a **fork** of [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) (a customization of the archived official [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS), including enhancements such as style class completion), and is further developed here as **AxamlTPreviewer**.

| Layer | Repository |
|------|------------|
| Official original (archived) | [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) |
| Fork source (direct upstream) | [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) |
| This repository | [xiu-xiang/AvaloniaVS](https://github.com/xiu-xiang/AvaloniaVS) |

Thanks to [kongdetuo](https://github.com/kongdetuo) for the customization work and open-source contribution.

---

## Features

- **AXAML live previewer** (Previewer / Designer)
- **IntelliSense** for elements, properties, bindings, and more
- **Style class completion** (highlight feature)
  - Complete class names inside `Classes=""` (multi-token, base/interface/global candidates)
  - Support `Classes.class="{Binding}"` completion after the dot
  - Filter out pseudo-classes (`:pseudo`)
- Binding expression completion (`{Binding ...}`)
- AXAML file icons and code snippets
- Bundled **Avalonia.Templates 12.1.0** project templates (default `Avalonia` package version **12.1.0**)
- **Add New Item** templates: Window, UserControl, TemplatedControl, ResourceDictionary, Styles (AXAML)

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
- [Marketplace overview](AvaloniaVS.VS2022/overview.md)
