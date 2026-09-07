using System.ComponentModel.Composition;
using AvaloniaVS.Models;
using AvaloniaVS.Shared.IntelliSense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace AvaloniaVS.IntelliSense
{
    [Export(typeof(ITaggerProvider))]
    [ContentType("xml")]
    [TagType(typeof(IErrorTag))]
    internal sealed class XamlObsoleteTaggerProvider : ITaggerProvider
    {
        private readonly CompletionEngineSource _completionEngineSource;

        [ImportingConstructor]
        public XamlObsoleteTaggerProvider(CompletionEngineSource completionEngineSource)
        {
            _completionEngineSource = completionEngineSource;
        }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            // 仅对带 Avalonia 元数据的 AXAML 缓冲区启用
            if (!buffer.Properties.ContainsProperty(typeof(XamlBufferMetadata)))
                return null;

            return buffer.Properties.GetOrCreateSingletonProperty(
                typeof(XamlObsoleteTagger),
                () => new XamlObsoleteTagger(buffer, _completionEngineSource.CompletionEngine)) as ITagger<T>;
        }
    }
}
