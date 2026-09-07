using System;
using CompletionMetadata = Avalonia.Ide.CompletionEngine.Metadata;

namespace AvaloniaVS.Models
{
    internal class XamlBufferMetadata
    {
        private CompletionMetadata _completionMetadata;

        public CompletionMetadata CompletionMetadata
        {
            get => _completionMetadata;
            set
            {
                if (ReferenceEquals(_completionMetadata, value))
                    return;

                _completionMetadata = value;
                // 元数据异步加载完成后通知标签器等刷新
                MetadataChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>CompletionMetadata 赋值或替换时触发。</summary>
        public event EventHandler MetadataChanged;

        public bool NeedInvalidation { get; set; } = true;
    }
}
