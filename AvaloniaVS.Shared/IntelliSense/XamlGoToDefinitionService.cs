using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Ide.CompletionEngine;
using AvaloniaVS.Models;
using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// AXAML「转到定义」解析与导航（F12 / Ctrl+点击共用）。
    /// </summary>
    internal sealed class XamlGoToDefinitionService
    {
        private readonly CompletionEngine _engine;

        public XamlGoToDefinitionService(CompletionEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// 解析指定位置的导航目标；成功返回 true。
        /// </summary>
        public bool TryResolve(ITextBuffer buffer, int position, out XamlNavigationTarget target)
        {
            target = null;

            if (!buffer.Properties.TryGetProperty(typeof(XamlBufferMetadata), out XamlBufferMetadata metadata)
                || metadata?.CompletionMetadata == null)
            {
                return false;
            }

            if (position < 0 || position > buffer.CurrentSnapshot.Length)
            {
                return false;
            }

            var snapshot = buffer.CurrentSnapshot;
            var text = snapshot.GetText();
            var assemblyName = buffer.Properties.TryGetProperty("AssemblyName", out string asm) ? asm : null;
            _engine.Helper.SetMetadata(metadata.CompletionMetadata, text, assemblyName);

            var parser = XmlParser.Parse(text.AsMemory(), 0, position);

            // 1) 事件处理：Click="Button_Click" 引号内的方法名
            if (TryResolveEventHandler(text, position, parser, out target))
            {
                return true;
            }

            // 2) 属性 / 附加属性 / 事件名（必须落在属性名标识符上，避免点在值上误跳）
            if (TryResolveAttributeName(text, position, parser, out target))
            {
                return true;
            }

            // 3) 元素类型名
            if (TryResolveElementType(text, position, parser, out target))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 异步执行导航（可从 F12 或 Ctrl+点击调用）。
        /// </summary>
        public void Navigate(XamlNavigationTarget target)
        {
            if (target == null)
            {
                return;
            }

            if (target.TargetKind == XamlNavigationTarget.Kind.EventHandler)
            {
                NavigateToEventHandlerAsync(target.XamlClassName, target.HandlerMethodName).FireAndForget();
            }
            else
            {
                NavigateToSymbolAsync(target.TypeFullName, target.MemberName).FireAndForget();
            }
        }

        private bool TryResolveEventHandler(string text, int position, XmlParser parser, out XamlNavigationTarget target)
        {
            target = null;

            if (parser.State != XmlParser.ParserState.AttributeValue)
            {
                return false;
            }

            var tagName = parser.ParseCurrentTagName() ?? parser.TagName;
            var attributeName = GetFullAttributeName(text, parser);
            if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(attributeName))
            {
                return false;
            }

            var type = _engine.Helper.LookupType(tagName);
            if (type?.Events?.Any(e => e.Name == attributeName) != true)
            {
                return false;
            }

            if (!TryGetAttributeValueSpan(text, parser, out var valueStart, out var valueLength))
            {
                return false;
            }

            // 光标必须落在属性值标识符上（与 WPF 一致：点方法名才跳）
            if (position < valueStart || position > valueStart + valueLength)
            {
                return false;
            }

            var handler = text.Substring(valueStart, valueLength).Trim();
            if (!IsValidIdentifier(handler))
            {
                return false;
            }

            var xamlClass = TryGetXamlClassName(text);
            if (string.IsNullOrEmpty(xamlClass))
            {
                return false;
            }

            // 下划线高亮用完整标识符范围
            GetIdentifierSpan(text, Math.Min(position, text.Length - 1), out var idStart, out var idLength);

            target = new XamlNavigationTarget
            {
                TargetKind = XamlNavigationTarget.Kind.EventHandler,
                XamlClassName = xamlClass,
                HandlerMethodName = handler,
                SpanStart = idLength > 0 ? idStart : valueStart,
                SpanLength = idLength > 0 ? idLength : valueLength,
            };
            return true;
        }

        private bool TryResolveAttributeName(string text, int position, XmlParser parser, out XamlNavigationTarget target)
        {
            target = null;

            // 属性值内不按属性名跳转（事件已单独处理）
            if (parser.State == XmlParser.ParserState.AttributeValue)
            {
                return false;
            }

            if (parser.State is not (XmlParser.ParserState.StartAttribute
                or XmlParser.ParserState.BeforeAttributeValue
                or XmlParser.ParserState.AfterAttributeValue
                or XmlParser.ParserState.InsideElement))
            {
                return false;
            }

            // InsideElement 时可能仍在标签名上
            if (parser.State == XmlParser.ParserState.InsideElement
                && IsPositionOnTagName(text, position, parser))
            {
                return false;
            }

            if (!TryGetAttributeNameSpan(text, position, parser, out var nameStart, out var nameLength))
            {
                return false;
            }

            // 必须点在属性名上，而不是 = 或空白
            if (position < nameStart || position > nameStart + nameLength)
            {
                return false;
            }

            var attributeName = text.Substring(nameStart, nameLength);
            var tagName = parser.ParseCurrentTagName() ?? parser.TagName;
            if (string.IsNullOrEmpty(attributeName))
            {
                return false;
            }

            if (attributeName.Contains("."))
            {
                var parts = attributeName.Split(new[] { '.' }, 2);
                if (parts.Length == 2)
                {
                    var prop = _engine.Helper.LookupProperty(parts[0], parts[1]);
                    var declaring = prop?.DeclaringType?.FullName;
                    if (string.IsNullOrEmpty(declaring))
                    {
                        var owner = _engine.Helper.LookupType(parts[0]);
                        declaring = owner?.FullName;
                    }

                    if (!string.IsNullOrEmpty(declaring))
                    {
                        target = MemberTarget(declaring, parts[1], nameStart, nameLength);
                        return true;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(tagName))
            {
                var prop = _engine.Helper.LookupProperty(tagName, attributeName);
                if (prop != null)
                {
                    var declaring = prop.DeclaringType?.FullName
                                    ?? _engine.Helper.LookupType(tagName)?.FullName;
                    if (!string.IsNullOrEmpty(declaring))
                    {
                        target = MemberTarget(declaring, attributeName, nameStart, nameLength);
                        return true;
                    }
                }

                var ownerType = _engine.Helper.LookupType(tagName);
                var evt = ownerType?.Events?.FirstOrDefault(e => e.Name == attributeName);
                if (evt != null)
                {
                    var declaring = evt.DeclaringType?.FullName ?? ownerType.FullName;
                    if (!string.IsNullOrEmpty(declaring))
                    {
                        target = MemberTarget(declaring, attributeName, nameStart, nameLength);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryResolveElementType(string text, int position, XmlParser parser, out XamlNavigationTarget target)
        {
            target = null;

            if (parser.State is not (XmlParser.ParserState.StartElement
                or XmlParser.ParserState.InsideElement))
            {
                return false;
            }

            if (parser.State == XmlParser.ParserState.InsideElement
                && !IsPositionOnTagName(text, position, parser))
            {
                return false;
            }

            var tagName = parser.ParseCurrentTagName();
            if (string.IsNullOrEmpty(tagName))
            {
                return false;
            }

            // 去掉命名空间前缀：local:MyControl → MyControl（LookupType 需要带前缀的完整标签）
            var type = _engine.Helper.LookupType(tagName);
            if (type?.FullName is not { Length: > 0 } fullName)
            {
                return false;
            }

            GetTagNameSpan(text, parser, out var start, out var length);
            if (length <= 0 || position < start || position > start + length)
            {
                // 允许光标紧贴标签名末尾
                if (length <= 0)
                {
                    GetIdentifierSpan(text, Math.Min(position, Math.Max(0, text.Length - 1)), out start, out length);
                }
                else if (position < start || position > start + length)
                {
                    return false;
                }
            }

            target = new XamlNavigationTarget
            {
                TargetKind = XamlNavigationTarget.Kind.Type,
                TypeFullName = fullName,
                SpanStart = start,
                SpanLength = length,
            };
            return true;
        }

        private static XamlNavigationTarget MemberTarget(string typeFullName, string memberName, int spanStart, int spanLength)
            => new XamlNavigationTarget
            {
                TargetKind = XamlNavigationTarget.Kind.Member,
                TypeFullName = typeFullName,
                MemberName = memberName,
                SpanStart = spanStart,
                SpanLength = spanLength,
            };

        /// <summary>
        /// 取完整属性名（避免光标在中间时 XmlParser.AttributeName 被截断）。
        /// </summary>
        private static string GetFullAttributeName(string text, XmlParser parser)
        {
            if (parser.State == XmlParser.ParserState.StartAttribute
                && parser.CurrentValueStart is int start)
            {
                return ReadXmlName(text, start);
            }

            // 已越过属性名时 AttributeName 为完整值
            return parser.AttributeName ?? string.Empty;
        }

        private static bool TryGetAttributeNameSpan(string text, int position, XmlParser parser, out int start, out int length)
        {
            start = 0;
            length = 0;

            if (parser.State == XmlParser.ParserState.StartAttribute
                && parser.CurrentValueStart is int attrStart)
            {
                var name = ReadXmlName(text, attrStart);
                if (name.Length == 0)
                {
                    return false;
                }

                start = attrStart;
                length = name.Length;
                return true;
            }

            var attrName = parser.AttributeName;
            if (string.IsNullOrEmpty(attrName))
            {
                // 回退：从光标处扩展标识符
                if (!GetIdentifierSpan(text, position, out start, out length) || length == 0)
                {
                    return false;
                }

                return true;
            }

            // BeforeAttributeValue / AfterAttributeValue：向前搜索属性名=
            var searchFrom = Math.Min(position, text.Length - 1);
            for (var i = searchFrom; i >= 0; i--)
            {
                if (i + attrName.Length <= text.Length
                    && string.CompareOrdinal(text, i, attrName, 0, attrName.Length) == 0)
                {
                    var after = i + attrName.Length;
                    // 后面应是空白或 =
                    if (after < text.Length && (char.IsWhiteSpace(text[after]) || text[after] == '='))
                    {
                        start = i;
                        length = attrName.Length;
                        return true;
                    }
                }

                if (text[i] == '<' || text[i] == '>')
                {
                    break;
                }
            }

            return GetIdentifierSpan(text, position, out start, out length) && length > 0;
        }

        private static bool TryGetAttributeValueSpan(string text, XmlParser parser, out int start, out int length)
        {
            start = 0;
            length = 0;

            if (parser.CurrentValueStart is not int valueStart || valueStart < 0 || valueStart >= text.Length)
            {
                return false;
            }

            var end = valueStart;
            while (end < text.Length && text[end] != '"' && text[end] != '\'')
            {
                end++;
            }

            start = valueStart;
            length = end - valueStart;
            return length >= 0;
        }

        private static void GetTagNameSpan(string text, XmlParser parser, out int start, out int length)
        {
            start = 0;
            length = 0;

            var tagStart = parser.ContainingTagStart;
            if (tagStart < 0 || tagStart >= text.Length || text[tagStart] != '<')
            {
                return;
            }

            var i = tagStart + 1;
            if (i < text.Length && text[i] == '/')
            {
                i++;
            }

            start = i;
            while (i < text.Length && IsXmlNameChar(text[i]))
            {
                i++;
            }

            length = i - start;
        }

        private static bool IsPositionOnTagName(string text, int position, XmlParser parser)
        {
            GetTagNameSpan(text, parser, out var start, out var length);
            return length > 0 && position >= start && position <= start + length;
        }

        private static string ReadXmlName(string text, int start)
        {
            if (start < 0 || start >= text.Length)
            {
                return string.Empty;
            }

            var i = start;
            while (i < text.Length && IsXmlNameChar(text[i]))
            {
                i++;
            }

            return text.Substring(start, i - start);
        }

        private static bool GetIdentifierSpan(string text, int position, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (string.IsNullOrEmpty(text) || position < 0 || position >= text.Length)
            {
                if (position == text?.Length && position > 0)
                {
                    position = position - 1;
                }
                else
                {
                    return false;
                }
            }

            if (!IsXmlNameChar(text[position]) && position > 0 && IsXmlNameChar(text[position - 1]))
            {
                position--;
            }

            if (!IsXmlNameChar(text[position]))
            {
                return false;
            }

            start = position;
            while (start > 0 && IsXmlNameChar(text[start - 1]))
            {
                start--;
            }

            var end = position + 1;
            while (end < text.Length && IsXmlNameChar(text[end]))
            {
                end++;
            }

            length = end - start;
            return length > 0;
        }

        private static bool IsXmlNameChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':';

        private static string TryGetXamlClassName(string xml)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                xml,
                @"\bx:Class\s*=\s*""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
            {
                return false;
            }

            for (var i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private async Task NavigateToEventHandlerAsync(string xamlClassName, string handlerMethodName)
        {
            try
            {
                var workspace = GetWorkspace();
                if (workspace == null)
                {
                    return;
                }

                IMethodSymbol method = null;
                RoslynProject project = null;

                foreach (var candidate in workspace.CurrentSolution.Projects)
                {
                    if (candidate.Language != LanguageNames.CSharp)
                    {
                        continue;
                    }

                    var compilation = await candidate.GetCompilationAsync().ConfigureAwait(false);
                    if (compilation == null)
                    {
                        continue;
                    }

                    var typeSymbol = compilation.GetTypeByMetadataName(xamlClassName)
                                     ?? FindTypeBySimpleName(compilation, xamlClassName);
                    if (typeSymbol == null)
                    {
                        continue;
                    }

                    method = typeSymbol.GetMembers(handlerMethodName)
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary);

                    if (method != null)
                    {
                        project = candidate;
                        break;
                    }
                }

                if (method == null || project == null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetStatus($"未找到事件处理方法: {xamlClassName}.{handlerMethodName}");
                    return;
                }

                // 走 VS/Roslyn 导航（源码或反编译，与 WPF 一致）
                await NavigateWithVisualStudioAsync(workspace, method, project).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus($"转到事件处理失败: {ex.Message}");
            }
        }

        private async Task NavigateToSymbolAsync(string typeFullName, string memberName)
        {
            try
            {
                var workspace = GetWorkspace();
                if (workspace == null)
                {
                    return;
                }

                ISymbol symbol = null;
                RoslynProject project = null;

                foreach (var candidate in workspace.CurrentSolution.Projects)
                {
                    if (candidate.Language != LanguageNames.CSharp)
                    {
                        continue;
                    }

                    var compilation = await candidate.GetCompilationAsync().ConfigureAwait(false);
                    if (compilation == null)
                    {
                        continue;
                    }

                    var typeSymbol = compilation.GetTypeByMetadataName(typeFullName)
                                     ?? FindTypeBySimpleName(compilation, typeFullName);
                    if (typeSymbol == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(memberName))
                    {
                        symbol = typeSymbol;
                        project = candidate;
                        break;
                    }

                    // CLR 属性 / 事件 → Avalonia *Property 字段 → 其它同名成员；并沿基类查找
                    symbol = FindMember(typeSymbol, memberName);
                    if (symbol == null)
                    {
                        for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
                        {
                            symbol = FindMember(baseType, memberName);
                            if (symbol != null)
                            {
                                break;
                            }
                        }
                    }

                    // 找不到成员时至少跳到类型（反编译类页面）
                    symbol ??= typeSymbol;
                    project = candidate;
                    break;
                }

                if (symbol == null || project == null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetStatus($"未能解析类型: {typeFullName}");
                    return;
                }

                // 走 VS/Roslyn 导航：有源码则开源文件，否则开反编译视图（勿用对象浏览器）
                await NavigateWithVisualStudioAsync(workspace, symbol, project).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus($"转到定义失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过 VisualStudioWorkspace 转到定义，行为与 C#/WPF XAML 的 F12 一致（含反编译）。
        /// </summary>
        private static async Task NavigateWithVisualStudioAsync(
            VisualStudioWorkspace workspace,
            ISymbol symbol,
            RoslynProject project)
        {
            // 优先解析到解决方案内源码定义（若有）
            var preferred = await SymbolFinder.FindSourceDefinitionAsync(
                symbol,
                workspace.CurrentSolution,
                CancellationToken.None).ConfigureAwait(false) ?? symbol;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // TryGoToDefinition：元数据符号会打开「[反编译]」文档，与 WPF 一致
            if (await workspace.TryGoToDefinitionAsync(preferred, project, CancellationToken.None).ConfigureAwait(true))
            {
                return;
            }

            if (!ReferenceEquals(preferred, symbol)
                && await workspace.TryGoToDefinitionAsync(symbol, project, CancellationToken.None).ConfigureAwait(true))
            {
                return;
            }

            // 少数情况下 Roslyn 导航失败时，再尝试直接打开源码位置
            if (TryNavigateToSourceLocations(preferred) || TryNavigateToSourceLocations(symbol))
            {
                return;
            }

            SetStatus($"无法打开定义: {symbol.ToDisplayString()}");
        }

        private static ISymbol FindMember(INamedTypeSymbol typeSymbol, string memberName)
            => typeSymbol.GetMembers(memberName).FirstOrDefault(m => m is IPropertySymbol or IEventSymbol)
               ?? typeSymbol.GetMembers(memberName + "Property").OfType<IFieldSymbol>().FirstOrDefault()
               ?? typeSymbol.GetMembers(memberName + "Event").OfType<IFieldSymbol>().FirstOrDefault()
               ?? typeSymbol.GetMembers(memberName).FirstOrDefault();

        private static VisualStudioWorkspace GetWorkspace()
        {
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<VisualStudioWorkspace>();
        }

        private static INamedTypeSymbol FindTypeBySimpleName(Compilation compilation, string fullName)
        {
            var simple = fullName.Contains('.') ? fullName.Substring(fullName.LastIndexOf('.') + 1) : fullName;
            return compilation.GetSymbolsWithName(simple, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.ToDisplayString() == fullName || t.Name == simple);
        }

        private static bool TryNavigateToSourceLocations(ISymbol symbol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (var location in symbol.Locations)
            {
                if (TryOpenSourceLocation(location))
                {
                    return true;
                }
            }

            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var tree = reference.SyntaxTree;
                if (tree?.FilePath == null)
                {
                    continue;
                }

                var lineSpan = tree.GetLineSpan(reference.Span);
                if (OpenFileAt(tree.FilePath, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryOpenSourceLocation(Location location)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!location.IsInSource || location.SourceTree?.FilePath == null)
            {
                return false;
            }

            var lineSpan = location.GetLineSpan();
            return OpenFileAt(
                location.SourceTree.FilePath,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character);
        }

        private static bool OpenFileAt(string filePath, int line, int column)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte == null)
                {
                    return false;
                }

                var window = dte.ItemOperations.OpenFile(filePath);
                window?.Activate();

                if (dte.ActiveDocument?.Selection is TextSelection selection)
                {
                    selection.MoveToLineAndOffset(line + 1, Math.Max(1, column + 1), false);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SetStatus(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte != null)
                {
                    dte.StatusBar.Text = message;
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// AXAML 转到定义的解析结果。
    /// </summary>
    internal sealed class XamlNavigationTarget
    {
        public enum Kind
        {
            Type,
            Member,
            EventHandler,
        }

        public Kind TargetKind { get; set; }
        public string TypeFullName { get; set; }
        public string MemberName { get; set; }
        public string XamlClassName { get; set; }
        public string HandlerMethodName { get; set; }
        public int SpanStart { get; set; }
        public int SpanLength { get; set; }
    }
}
