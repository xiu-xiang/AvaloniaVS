using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaVS.Models;
using AvaloniaVS.Shared.IntelliSense;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// Ctrl+点击（与 WPF XAML 一致）的可导航符号源提供者。
    /// </summary>
    [Export(typeof(INavigableSymbolSourceProvider))]
    [Name("Avalonia XAML navigable symbol source")]
    [ContentType("xml")]
    internal sealed class XamlNavigableSymbolSourceProvider : INavigableSymbolSourceProvider
    {
        private readonly CompletionEngineSource _completionEngineSource;

        [ImportingConstructor]
        public XamlNavigableSymbolSourceProvider(CompletionEngineSource completionEngineSource)
        {
            _completionEngineSource = completionEngineSource;
        }

        public INavigableSymbolSource TryCreateNavigableSymbolSource(ITextView textView, ITextBuffer buffer)
        {
            if (!buffer.Properties.ContainsProperty(typeof(XamlBufferMetadata)))
            {
                return null;
            }

            return buffer.Properties.GetOrCreateSingletonProperty(
                typeof(XamlNavigableSymbolSource),
                () => new XamlNavigableSymbolSource(
                    new XamlGoToDefinitionService(_completionEngineSource.CompletionEngine)));
        }
    }

    /// <summary>
    /// 为 AXAML 提供 Ctrl+悬停下划线与点击跳转。
    /// </summary>
    internal sealed class XamlNavigableSymbolSource : INavigableSymbolSource
    {
        private readonly XamlGoToDefinitionService _service;

        public XamlNavigableSymbolSource(XamlGoToDefinitionService service)
        {
            _service = service;
        }

        public void Dispose()
        {
        }

        public Task<INavigableSymbol> GetNavigableSymbolAsync(SnapshotSpan triggerSpan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var buffer = triggerSpan.Snapshot.TextBuffer;
            var position = triggerSpan.Start.Position;

            if (!_service.TryResolve(buffer, position, out var target) || target.SpanLength <= 0)
            {
                return Task.FromResult<INavigableSymbol>(null);
            }

            // 确保 span 落在当前快照范围内
            var snapshot = triggerSpan.Snapshot;
            if (target.SpanStart < 0 || target.SpanStart + target.SpanLength > snapshot.Length)
            {
                return Task.FromResult<INavigableSymbol>(null);
            }

            var symbolSpan = new SnapshotSpan(snapshot, target.SpanStart, target.SpanLength);
            return Task.FromResult<INavigableSymbol>(new XamlNavigableSymbol(_service, target, symbolSpan));
        }
    }

    /// <summary>
    /// Ctrl+点击时的导航符号。
    /// </summary>
    internal sealed class XamlNavigableSymbol : INavigableSymbol
    {
        private readonly XamlGoToDefinitionService _service;
        private readonly XamlNavigationTarget _target;

        public XamlNavigableSymbol(
            XamlGoToDefinitionService service,
            XamlNavigationTarget target,
            SnapshotSpan symbolSpan)
        {
            _service = service;
            _target = target;
            SymbolSpan = symbolSpan;
        }

        public SnapshotSpan SymbolSpan { get; }

        public IEnumerable<INavigableRelationship> Relationships { get; } =
            new[] { PredefinedNavigableRelationships.Definition };

        public void Navigate(INavigableRelationship relationship)
        {
            _service.Navigate(_target);
        }
    }
}
