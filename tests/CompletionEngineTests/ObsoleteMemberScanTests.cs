using System.Linq;
using Avalonia.Ide.CompletionEngine;
using Xunit;

namespace CompletionEngineTests
{
    /// <summary>
    /// 验证已声明 AXAML 属性上的 [Obsolete] 扫描。
    /// </summary>
    public class ObsoleteMemberScanTests
    {
        [Fact]
        public void FindObsoleteMembers_Tags_Obsolete_Attribute_Name_Span()
        {
            var textBox = new MetadataType("TextBox")
            {
                FullName = "Avalonia.Controls.TextBox",
            };
            textBox.Properties.Add(new MetadataProperty(
                "Watermark",
                Type: null,
                DeclaringType: textBox,
                IsAttached: false,
                IsStatic: false,
                HasGetter: true,
                HasSetter: true,
                IsObsolete: true,
                ObsoleteMessage: "Use PlaceholderText instead.",
                ObsoleteIsError: false));
            textBox.Properties.Add(new MetadataProperty(
                "Text",
                Type: null,
                DeclaringType: textBox,
                IsAttached: false,
                IsStatic: false,
                HasGetter: true,
                HasSetter: true));

            var metadata = new Metadata();
            metadata.AddType("", textBox);

            const string xaml = "<TextBox Watermark=\"搜索...\" Text=\"ok\" />";
            var engine = new CompletionEngine();
            var hits = engine.FindObsoleteMembers(metadata, xaml);

            Assert.Single(hits);
            var hit = hits[0];
            Assert.Equal("Watermark", xaml.Substring(hit.Start, hit.Length));
            Assert.Equal("TextBox.Watermark", hit.MemberDisplayName);
            Assert.Equal("Use PlaceholderText instead.", hit.ObsoleteMessage);
            Assert.Contains("已过时", hit.GetTooltipText());
            Assert.Contains("Use PlaceholderText instead.", hit.GetTooltipText());
        }

        [Fact]
        public void FindObsoleteMembers_Ignores_NonObsolete_Attributes()
        {
            var button = new MetadataType("Button")
            {
                FullName = "Avalonia.Controls.Button",
            };
            button.Properties.Add(new MetadataProperty(
                "Content",
                Type: null,
                DeclaringType: button,
                IsAttached: false,
                IsStatic: false,
                HasGetter: true,
                HasSetter: true));

            var metadata = new Metadata();
            metadata.AddType("", button);

            var engine = new CompletionEngine();
            var hits = engine.FindObsoleteMembers(metadata, "<Button Content=\"Hi\" />");
            Assert.Empty(hits);
        }
    }
}
