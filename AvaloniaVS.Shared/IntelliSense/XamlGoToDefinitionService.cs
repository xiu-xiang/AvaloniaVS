using System;
using System.Linq;
using System.Text.RegularExpressions;
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
using Microsoft.VisualStudio.Shell.Interop;
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
            var currentFilePath = TryGetBufferFilePath(buffer);

            // 1) 事件处理：Click="Button_Click" 引号内的方法名
            if (TryResolveEventHandler(text, position, parser, out target))
            {
                return true;
            }

            // 2) 标记扩展：{Binding} / {x:Static} / {StaticResource} / {DynamicResource}
            if (TryResolveMarkupExtension(text, position, parser, currentFilePath, out target))
            {
                return true;
            }

            // 3) 样式类：Classes="accent" / Classes.accent
            if (TryResolveStyleClass(text, position, parser, currentFilePath, out target))
            {
                return true;
            }

            // 4) 属性 / 附加属性 / 事件名（必须落在属性名标识符上，避免点在值上误跳）
            if (TryResolveAttributeName(text, position, parser, out target))
            {
                return true;
            }

            // 5) 元素类型名
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

            switch (target.TargetKind)
            {
                case XamlNavigationTarget.Kind.EventHandler:
                    NavigateToEventHandlerAsync(target.XamlClassName, target.HandlerMethodName).FireAndForget();
                    break;
                case XamlNavigationTarget.Kind.StyleClass:
                    NavigateToStyleClassAsync(target.MemberName, target.PreferredFilePath).FireAndForget();
                    break;
                case XamlNavigationTarget.Kind.ResourceKey:
                    NavigateToResourceKeyAsync(target.MemberName, target.PreferredFilePath).FireAndForget();
                    break;
                default:
                    NavigateToSymbolAsync(target.TypeFullName, target.MemberName).FireAndForget();
                    break;
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

        /// <summary>
        /// 解析属性值内的标记扩展（Binding / x:Static / StaticResource / DynamicResource）。
        /// </summary>
        private bool TryResolveMarkupExtension(
            string text,
            int position,
            XmlParser parser,
            string currentFilePath,
            out XamlNavigationTarget target)
        {
            target = null;

            if (parser.State != XmlParser.ParserState.AttributeValue)
            {
                return false;
            }

            if (!TryGetAttributeValueSpan(text, parser, out var valueStart, out var valueLength))
            {
                return false;
            }

            if (position < valueStart || position > valueStart + valueLength)
            {
                return false;
            }

            // 定位标记扩展起始 '{'
            var braceStart = -1;
            var valueEnd = valueStart + valueLength;
            for (var i = valueStart; i < valueEnd; i++)
            {
                if (text[i] == '{')
                {
                    braceStart = i;
                    break;
                }
            }

            if (braceStart < 0 || position < braceStart)
            {
                return false;
            }

            var markupText = text.Substring(braceStart, valueEnd - braceStart);
            if (!TryGetMarkupExtensionName(markupText, out var extensionName))
            {
                return false;
            }

            if (IsBindingExtension(extensionName))
            {
                return TryResolveBindingPath(braceStart, markupText, position, parser, out target);
            }

            if (IsStaticExtension(extensionName))
            {
                return TryResolveStaticMember(text, braceStart, markupText, position, out target);
            }

            if (IsResourceExtension(extensionName))
            {
                return TryResolveResourceKey(braceStart, markupText, position, currentFilePath, out target);
            }

            return false;
        }

        /// <summary>
        /// Classes="accent danger" 属性值，或 Classes.accent 属性名中的样式类。
        /// </summary>
        private bool TryResolveStyleClass(
            string text,
            int position,
            XmlParser parser,
            string currentFilePath,
            out XamlNavigationTarget target)
        {
            target = null;

            // Classes="foo bar"：属性值中的类名片段
            if (parser.State == XmlParser.ParserState.AttributeValue)
            {
                var attrName = GetFullAttributeName(text, parser);
                if (!string.Equals(attrName, "Classes", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!TryGetAttributeValueSpan(text, parser, out var valueStart, out var valueLength))
                {
                    return false;
                }

                if (position < valueStart || position > valueStart + valueLength)
                {
                    return false;
                }

                if (!TryGetStyleClassTokenAt(text, valueStart, valueLength, position, out var tokenStart, out var tokenLength))
                {
                    return false;
                }

                var valueClassName = text.Substring(tokenStart, tokenLength);
                target = StyleClassTarget(valueClassName, currentFilePath, tokenStart, tokenLength);
                return true;
            }

            // Classes.accent=：属性名中 '.' 后的类名
            if (parser.State is not (XmlParser.ParserState.StartAttribute
                or XmlParser.ParserState.BeforeAttributeValue
                or XmlParser.ParserState.AfterAttributeValue
                or XmlParser.ParserState.InsideElement))
            {
                return false;
            }

            if (parser.State == XmlParser.ParserState.InsideElement
                && IsPositionOnTagName(text, position, parser))
            {
                return false;
            }

            if (!TryGetAttributeNameSpan(text, position, parser, out var nameStart, out var nameLength))
            {
                return false;
            }

            if (position < nameStart || position > nameStart + nameLength)
            {
                return false;
            }

            var attributeName = text.Substring(nameStart, nameLength);
            if (!attributeName.StartsWith("Classes.", StringComparison.Ordinal)
                || attributeName.Length <= "Classes.".Length)
            {
                return false;
            }

            var classNameStart = nameStart + "Classes.".Length;
            var attrClassName = attributeName.Substring("Classes.".Length);
            if (position < classNameStart || position > classNameStart + attrClassName.Length)
            {
                return false;
            }

            target = StyleClassTarget(attrClassName, currentFilePath, classNameStart, attrClassName.Length);
            return true;
        }

        /// <summary>
        /// {StaticResource Key} / {DynamicResource Key} → 搜索 x:Key 定义。
        /// </summary>
        private static bool TryResolveResourceKey(
            int markupAbsStart,
            string markupText,
            int position,
            string currentFilePath,
            out XamlNavigationTarget target)
        {
            target = null;

            if (!TryExtractResourceKey(markupText, out var key, out var keyStartInMarkup))
            {
                return false;
            }

            var keyAbsStart = markupAbsStart + keyStartInMarkup;
            if (position < keyAbsStart || position > keyAbsStart + key.Length)
            {
                return false;
            }

            target = new XamlNavigationTarget
            {
                TargetKind = XamlNavigationTarget.Kind.ResourceKey,
                MemberName = key,
                PreferredFilePath = currentFilePath,
                SpanStart = keyAbsStart,
                SpanLength = key.Length,
            };
            return true;
        }

        private static XamlNavigationTarget StyleClassTarget(
            string className,
            string preferredFilePath,
            int spanStart,
            int spanLength)
            => new XamlNavigationTarget
            {
                TargetKind = XamlNavigationTarget.Kind.StyleClass,
                MemberName = className,
                PreferredFilePath = preferredFilePath,
                SpanStart = spanStart,
                SpanLength = spanLength,
            };

        /// <summary>
        /// 从 Classes 属性值中取光标所在的类名 token（按空白分隔）。
        /// </summary>
        private static bool TryGetStyleClassTokenAt(
            string text,
            int valueStart,
            int valueLength,
            int position,
            out int tokenStart,
            out int tokenLength)
        {
            tokenStart = 0;
            tokenLength = 0;
            var valueEnd = valueStart + valueLength;
            if (position < valueStart || position > valueEnd)
            {
                return false;
            }

            // 允许光标紧贴 token 末尾
            var pos = position;
            if (pos == valueEnd && pos > valueStart)
            {
                pos--;
            }

            if (pos < valueStart || pos >= valueEnd || !IsStyleClassChar(text[pos]))
            {
                if (pos > valueStart && IsStyleClassChar(text[pos - 1]))
                {
                    pos--;
                }
                else
                {
                    return false;
                }
            }

            tokenStart = pos;
            while (tokenStart > valueStart && IsStyleClassChar(text[tokenStart - 1]))
            {
                tokenStart--;
            }

            var tokenEnd = pos + 1;
            while (tokenEnd < valueEnd && IsStyleClassChar(text[tokenEnd]))
            {
                tokenEnd++;
            }

            tokenLength = tokenEnd - tokenStart;
            return tokenLength > 0;
        }

        private static bool IsStyleClassChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '-';

        private static string TryGetBufferFilePath(ITextBuffer buffer)
        {
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                && !string.IsNullOrEmpty(document?.FilePath))
            {
                return document.FilePath;
            }

            return null;
        }

        /// <summary>
        /// {Binding Greeting} / {Binding Path=Foo.Bar} → 跳到 DataType 上的属性。
        /// </summary>
        private bool TryResolveBindingPath(
            int markupAbsStart,
            string markupText,
            int position,
            XmlParser parser,
            out XamlNavigationTarget target)
        {
            target = null;

            if (!TryExtractBindingPath(markupText, out var path, out var pathStartInMarkup))
            {
                return false;
            }

            // 忽略 $parent / #name 等特殊路径前缀（补全支持，导航暂不处理）
            if (path.StartsWith("$", StringComparison.Ordinal) || path.StartsWith("#", StringComparison.Ordinal))
            {
                return false;
            }

            var pathAbsStart = markupAbsStart + pathStartInMarkup;
            if (position < pathAbsStart || position > pathAbsStart + path.Length)
            {
                return false;
            }

            var dataTypeName = parser.FindParentAttributeValue("(x\\:)?DataType");
            if (string.IsNullOrEmpty(dataTypeName))
            {
                return false;
            }

            var currentType = _engine.Helper.LookupType(dataTypeName);
            if (currentType == null)
            {
                return false;
            }

            var segments = path.Split('.');
            var offset = 0;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                {
                    return false;
                }

                var segStart = pathAbsStart + offset;
                var onSegment = position >= segStart && position <= segStart + segment.Length;

                var prop = currentType.Properties?.FirstOrDefault(p => p.Name == segment);
                if (prop == null)
                {
                    return false;
                }

                if (onSegment)
                {
                    var declaring = prop.DeclaringType?.FullName ?? currentType.FullName;
                    if (string.IsNullOrEmpty(declaring))
                    {
                        return false;
                    }

                    target = MemberTarget(declaring, segment, segStart, segment.Length);
                    return true;
                }

                // 沿路径继续深入下一段的声明类型
                currentType = prop.Type;
                if (currentType == null)
                {
                    return false;
                }

                offset += segment.Length + 1; // 含 '.'
            }

            return false;
        }

        /// <summary>
        /// {x:Static model:TestStaticClass.TestStr1} → 类型或静态成员。
        /// </summary>
        private bool TryResolveStaticMember(
            string text,
            int markupAbsStart,
            string markupText,
            int position,
            out XamlNavigationTarget target)
        {
            target = null;

            if (!TryExtractStaticExpression(markupText, out var expr, out var exprStartInMarkup))
            {
                return false;
            }

            var exprAbsStart = markupAbsStart + exprStartInMarkup;
            if (position < exprAbsStart || position > exprAbsStart + expr.Length)
            {
                return false;
            }

            var lastDot = expr.LastIndexOf('.');
            if (lastDot <= 0 || lastDot >= expr.Length - 1)
            {
                // 仅类型名：跳到类型
                var onlyType = _engine.Helper.LookupType(expr);
                if (onlyType?.FullName is not { Length: > 0 } onlyFullName)
                {
                    return false;
                }

                target = new XamlNavigationTarget
                {
                    TargetKind = XamlNavigationTarget.Kind.Type,
                    TypeFullName = onlyFullName,
                    SpanStart = exprAbsStart,
                    SpanLength = expr.Length,
                };
                return true;
            }

            var typeName = expr.Substring(0, lastDot);
            var memberName = expr.Substring(lastDot + 1);
            var memberAbsStart = exprAbsStart + lastDot + 1;

            var mdType = _engine.Helper.LookupType(typeName);
            if (mdType?.FullName is not { Length: > 0 } typeFullName)
            {
                return false;
            }

            // 光标在成员名上 → 跳到静态属性/字段
            if (position >= memberAbsStart && position <= memberAbsStart + memberName.Length)
            {
                var prop = mdType.Properties?.FirstOrDefault(p => p.Name == memberName);
                var declaring = prop?.DeclaringType?.FullName ?? typeFullName;
                target = MemberTarget(declaring, memberName, memberAbsStart, memberName.Length);
                return true;
            }

            // 光标在类型部分（含 xmlns 前缀）→ 跳到类型
            if (position >= exprAbsStart && position < exprAbsStart + lastDot)
            {
                // 细分前缀与类型名高亮：model:TestStaticClass
                GetIdentifierSpan(text, Math.Min(position, text.Length - 1), out var idStart, out var idLength);
                if (idLength <= 0 || idStart < exprAbsStart || idStart + idLength > exprAbsStart + lastDot)
                {
                    idStart = exprAbsStart;
                    idLength = lastDot;
                }

                target = new XamlNavigationTarget
                {
                    TargetKind = XamlNavigationTarget.Kind.Type,
                    TypeFullName = typeFullName,
                    SpanStart = idStart,
                    SpanLength = idLength,
                };
                return true;
            }

            return false;
        }

        private static bool TryGetMarkupExtensionName(string markupText, out string name)
        {
            name = null;
            var match = Regex.Match(markupText, @"^\{\s*([^\s,}]+)");
            if (!match.Success)
            {
                return false;
            }

            name = match.Groups[1].Value;
            return name.Length > 0;
        }

        private static bool IsBindingExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            // Binding / local:Binding / BindingExtension
            var simple = name.Contains(':') ? name.Substring(name.IndexOf(':') + 1) : name;
            return simple.Equals("Binding", StringComparison.OrdinalIgnoreCase)
                   || simple.Equals("BindingExtension", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStaticExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            // x:Static / Static / StaticExtension
            var simple = name.Contains(':') ? name.Substring(name.IndexOf(':') + 1) : name;
            return simple.Equals("Static", StringComparison.OrdinalIgnoreCase)
                   || simple.Equals("StaticExtension", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsResourceExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            // StaticResource / DynamicResource（可带命名空间前缀或 Extension 后缀）
            var simple = name.Contains(':') ? name.Substring(name.IndexOf(':') + 1) : name;
            return simple.Equals("StaticResource", StringComparison.OrdinalIgnoreCase)
                   || simple.Equals("StaticResourceExtension", StringComparison.OrdinalIgnoreCase)
                   || simple.Equals("DynamicResource", StringComparison.OrdinalIgnoreCase)
                   || simple.Equals("DynamicResourceExtension", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从标记扩展文本提取 Binding 路径及在 markup 内的起始偏移。
        /// </summary>
        private static bool TryExtractBindingPath(string markupText, out string path, out int pathStartInMarkup)
        {
            path = null;
            pathStartInMarkup = -1;

            // 优先 Path= 命名参数（可出现在任意位置）
            var named = Regex.Match(markupText, @"\bPath\s*=\s*([^\s,}]+)", RegexOptions.CultureInvariant);
            if (named.Success)
            {
                path = named.Groups[1].Value;
                pathStartInMarkup = named.Groups[1].Index;
                return path.Length > 0;
            }

            // 位置参数：{Binding Greeting} / {Binding Foo.Bar, Mode=OneWay}
            var positional = Regex.Match(
                markupText,
                @"\{\s*(?:[\w]+\s*:\s*)?Binding(?:Extension)?\s+([^\s,=}]+)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (positional.Success)
            {
                path = positional.Groups[1].Value;
                pathStartInMarkup = positional.Groups[1].Index;
                return path.Length > 0;
            }

            return false;
        }

        /// <summary>
        /// 从 {x:Static Type.Member} 提取 Type.Member 表达式。
        /// </summary>
        private static bool TryExtractStaticExpression(string markupText, out string expr, out int exprStartInMarkup)
        {
            expr = null;
            exprStartInMarkup = -1;

            var match = Regex.Match(
                markupText,
                @"\{\s*(?:[\w]+\s*:\s*)?Static(?:Extension)?\s+([^\s,}]+)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            expr = match.Groups[1].Value;
            exprStartInMarkup = match.Groups[1].Index;
            return expr.Length > 0;
        }

        /// <summary>
        /// 从 {StaticResource Key} / {DynamicResource ResourceKey=Key} 提取资源键。
        /// </summary>
        private static bool TryExtractResourceKey(string markupText, out string key, out int keyStartInMarkup)
        {
            key = null;
            keyStartInMarkup = -1;

            var named = Regex.Match(
                markupText,
                @"\bResourceKey\s*=\s*([^\s,}]+)",
                RegexOptions.CultureInvariant);
            if (named.Success)
            {
                key = named.Groups[1].Value;
                keyStartInMarkup = named.Groups[1].Index;
                return key.Length > 0;
            }

            var positional = Regex.Match(
                markupText,
                @"\{\s*(?:[\w]+\s*:\s*)?(?:Static|Dynamic)Resource(?:Extension)?\s+([^\s,=}]+)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (positional.Success)
            {
                key = positional.Groups[1].Value;
                keyStartInMarkup = positional.Groups[1].Index;
                return key.Length > 0;
            }

            return false;
        }

        private bool TryResolveAttributeName(string text, int position, XmlParser parser, out XamlNavigationTarget target)
        {
            target = null;

            // 属性值内不按属性名跳转（事件 / 标记扩展已单独处理）
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

        /// <summary>
        /// 搜索解决方案中 Selector 里的样式类定义并打开。
        /// </summary>
        private async Task NavigateToStyleClassAsync(string className, string preferredFilePath)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (XamlSourceDefinitionLocator.TryFindStyleClass(
                        className,
                        preferredFilePath,
                        out var filePath,
                        out var offset)
                    && OpenFileAtOffset(filePath, offset))
                {
                    return;
                }

                ShowCannotNavigateToDefinition();
            }
            catch
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowCannotNavigateToDefinition();
            }
        }

        /// <summary>
        /// 搜索解决方案中 x:Key 资源定义并打开（类似 WPF StaticResource）。
        /// </summary>
        private async Task NavigateToResourceKeyAsync(string resourceKey, string preferredFilePath)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (XamlSourceDefinitionLocator.TryFindResourceKey(
                        resourceKey,
                        preferredFilePath,
                        out var filePath,
                        out var offset)
                    && OpenFileAtOffset(filePath, offset))
                {
                    return;
                }

                ShowCannotNavigateToDefinition();
            }
            catch
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowCannotNavigateToDefinition();
            }
        }

        private async Task NavigateToEventHandlerAsync(string xamlClassName, string handlerMethodName)
        {
            try
            {
                var workspace = GetWorkspace();
                if (workspace == null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowCannotNavigateToDefinition();
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
                    ShowCannotNavigateToDefinition();
                    return;
                }

                // 走 VS/Roslyn 导航（源码或反编译，与 WPF 一致）
                await NavigateWithVisualStudioAsync(workspace, method, project).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowCannotNavigateToDefinition();
            }
        }

        private async Task NavigateToSymbolAsync(string typeFullName, string memberName)
        {
            try
            {
                var workspace = GetWorkspace();
                if (workspace == null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowCannotNavigateToDefinition();
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
                    ShowCannotNavigateToDefinition();
                    return;
                }

                // 走 VS/Roslyn 导航：有源码则开源文件，否则开反编译视图（勿用对象浏览器）
                await NavigateWithVisualStudioAsync(workspace, symbol, project).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowCannotNavigateToDefinition();
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

            ShowCannotNavigateToDefinition();
        }

        private static ISymbol FindMember(INamedTypeSymbol typeSymbol, string memberName)
            => typeSymbol.GetMembers(memberName).FirstOrDefault(m => m is IPropertySymbol or IEventSymbol)
               ?? typeSymbol.GetMembers(memberName + "Property").OfType<IFieldSymbol>().FirstOrDefault()
               ?? typeSymbol.GetMembers(memberName + "Event").OfType<IFieldSymbol>().FirstOrDefault()
               ?? typeSymbol.GetMembers(memberName).FirstOrDefault();

        /// <summary>
        /// 与 WPF XAML 一致：无法转到定义时弹出「无法导航到定义。」对话框。
        /// </summary>
        public static void ShowCannotNavigateToDefinition()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                VsShellUtilities.ShowMessageBox(
                    ServiceProvider.GlobalProvider,
                    "无法导航到定义。",
                    string.Empty,
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            catch
            {
                // 忽略对话框失败，避免打断编辑器
            }
        }

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

        /// <summary>
        /// 按文档字符偏移打开文件并定位（用于 AXAML 源内跳转）。
        /// </summary>
        private static bool OpenFileAtOffset(string filePath, int offset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (string.IsNullOrEmpty(filePath) || offset < 0 || !System.IO.File.Exists(filePath))
                {
                    return false;
                }

                var content = System.IO.File.ReadAllText(filePath);
                GetLineColumn(content, Math.Min(offset, content.Length), out var line, out var column);
                return OpenFileAt(filePath, line, column);
            }
            catch
            {
                return false;
            }
        }

        private static void GetLineColumn(string text, int offset, out int line, out int column)
        {
            line = 0;
            column = 0;
            var limit = Math.Min(offset, text.Length);
            for (var i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 0;
                }
                else if (text[i] != '\r')
                {
                    column++;
                }
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
            /// <summary>Classes 样式类 → 搜索 Selector</summary>
            StyleClass,
            /// <summary>StaticResource / DynamicResource 键 → 搜索 x:Key</summary>
            ResourceKey,
        }

        public Kind TargetKind { get; set; }
        public string TypeFullName { get; set; }
        public string MemberName { get; set; }
        public string XamlClassName { get; set; }
        public string HandlerMethodName { get; set; }
        /// <summary>优先搜索的当前 AXAML 路径（样式类 / 资源键）。</summary>
        public string PreferredFilePath { get; set; }
        public int SpanStart { get; set; }
        public int SpanLength { get; set; }
    }
}
