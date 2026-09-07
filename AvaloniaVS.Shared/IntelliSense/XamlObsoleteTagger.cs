using System;
using System.Collections.Generic;
using Avalonia.Ide.CompletionEngine;
using AvaloniaVS.Models;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// 在 AXAML 编辑器中为已废弃属性/事件名显示警告波浪线与悬停提示（类似 CS0618）。
    /// </summary>
    internal sealed class XamlObsoleteTagger : ITagger<IErrorTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly CompletionEngine _engine;
        private readonly XamlBufferMetadata _metadata;
        private ITextSnapshot _cachedSnapshot;
        private List<ITagSpan<IErrorTag>> _cachedTags = new List<ITagSpan<IErrorTag>>();
        private bool _disposed;

        public XamlObsoleteTagger(ITextBuffer buffer, CompletionEngine engine)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _buffer.Changed += OnBufferChanged;

            if (_buffer.Properties.TryGetProperty(typeof(XamlBufferMetadata), out XamlBufferMetadata metadata))
            {
                _metadata = metadata;
                _metadata.MetadataChanged += OnMetadataChanged;
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_disposed || spans == null || spans.Count == 0)
                yield break;

            EnsureTags(spans[0].Snapshot);

            foreach (var tag in _cachedTags)
            {
                var tagSpan = tag.Span.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
                foreach (var span in spans)
                {
                    if (span.IntersectsWith(tagSpan))
                    {
                        yield return new TagSpan<IErrorTag>(tagSpan, tag.Tag);
                        break;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _buffer.Changed -= OnBufferChanged;
            if (_metadata != null)
                _metadata.MetadataChanged -= OnMetadataChanged;
            _cachedTags.Clear();
            _cachedSnapshot = null;
        }

        private void OnMetadataChanged(object sender, EventArgs e) => InvalidateAndRaise();

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e) => InvalidateAndRaise();

        private void InvalidateAndRaise()
        {
            _cachedSnapshot = null;
            _cachedTags.Clear();

            var snapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private void EnsureTags(ITextSnapshot snapshot)
        {
            if (_cachedSnapshot == snapshot)
                return;

            _cachedSnapshot = snapshot;
            _cachedTags = BuildTags(snapshot);
        }

        private List<ITagSpan<IErrorTag>> BuildTags(ITextSnapshot snapshot)
        {
            var tags = new List<ITagSpan<IErrorTag>>();

            if (!_buffer.Properties.TryGetProperty(typeof(XamlBufferMetadata), out XamlBufferMetadata metadata)
                || metadata?.CompletionMetadata == null)
            {
                return tags;
            }

            var text = snapshot.GetText();
            var assemblyName = _buffer.Properties.TryGetProperty("AssemblyName", out string asm) ? asm : null;
            var obsoleteMembers = _engine.FindObsoleteMembers(metadata.CompletionMetadata, text, assemblyName);

            foreach (var member in obsoleteMembers)
            {
                if (member.Start < 0 || member.Length <= 0 || member.Start + member.Length > snapshot.Length)
                    continue;

                var span = new SnapshotSpan(snapshot, member.Start, member.Length);
                // Warning：绿色波浪线，与 C# CS0618 类似；IsError 时用编译错误样式
                var errorType = member.IsError
                    ? PredefinedErrorTypeNames.CompilerError
                    : PredefinedErrorTypeNames.Warning;
                var tag = new ErrorTag(errorType, member.GetTooltipText());
                tags.Add(new TagSpan<IErrorTag>(span, tag));
            }

            return tags;
        }
    }
}
