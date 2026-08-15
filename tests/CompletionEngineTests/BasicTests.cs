using System.Linq;
using Xunit;

namespace CompletionEngineTests
{
    public class BasicTests : XamlCompletionTestBase
    {
        [Fact]
        public void ClosingTagShouldBeProperlyCompleted()
        {
            AssertSingleCompletion("<UserControl><Button><Button.Styles><Style/></Button.Styles><", "/", "/Button>");
        }

        [Fact]
        public void Property_Should_Be_Renamed()
        {
            AssertSingleCompletionInMiddleOfText("<UserControl ", "=\"Top\"","HorizontalAlign", "HorizontalAlignment");
        }

        [Fact]
        public void Property_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl ", "HorizontalAlign", "HorizontalAlignment=\"\"");
        }

        [Fact]
        public void Classes_Property_Should_Be_Completed()
        {
            var compl = GetCompletionsFor("<Button C");

            Assert.Contains(compl.Completions, c => c.DisplayText == "Classes");
        }

        [Fact]
        public void Classes_Attribute_Should_Complete_Style_Classes()
        {
            var compl = GetCompletionsFor("<Button Classes=\"");

            Assert.Contains(compl.Completions, c => c.InsertText == "myClass");
            Assert.Contains(compl.Completions, c => c.InsertText == "other");
            Assert.Contains(compl.Completions, c => c.InsertText == "ctAccent");
            Assert.Contains(compl.Completions, c => c.InsertText == "globalClass");
        }

        [Fact]
        public void Classes_Attribute_Should_Complete_Base_Type_Classes()
        {
            // Control.baseClass is defined in TestCompiledTheme.xaml and must be offered on Button.
            var compl = GetCompletionsFor("<Button Classes=\"");

            Assert.Contains(compl.Completions, c => c.InsertText == "baseClass");
        }

        [Fact]
        public void Classes_Attribute_Should_Filter_By_Prefix()
        {
            var compl = GetCompletionsFor("<Button Classes=\"my");

            Assert.Contains(compl.Completions, c => c.InsertText == "myClass");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "other");
        }

        [Fact]
        public void Classes_Attribute_Should_Not_Include_Classes_Of_Other_Types()
        {
            var compl = GetCompletionsFor("<TextBlock Classes=\"");

            Assert.Contains(compl.Completions, c => c.InsertText == "myClass");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "other");
        }

        [Fact]
        public void Classes_Attribute_Should_Only_Include_Globals_When_No_Type_Classes()
        {
            var compl = GetCompletionsFor("<Border Classes=\"");

            Assert.Contains(compl.Completions, c => c.InsertText == "globalClass");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "myClass");
        }

        [Fact]
        public void Classes_Attribute_Should_Complete_After_Existing_Class()
        {
            // Start position must be at the cursor (after the space), not at the first class.
            AssertSingleCompletion("<Button Classes=\"myClass ", "", "other");
        }

        [Fact]
        public void Classes_Binding_Attribute_Should_Complete_Style_Classes()
        {
            // Classes.className="{Binding}" - completion must fire after the dot.
            var compl = GetCompletionsFor("<Button Classes.");

            Assert.Contains(compl.Completions, c => c.InsertText == "myClass=\"\"");
            Assert.Contains(compl.Completions, c => c.InsertText == "other=\"\"");
            Assert.Contains(compl.Completions, c => c.InsertText == "ctAccent=\"\"");
            Assert.Contains(compl.Completions, c => c.InsertText == "globalClass=\"\"");
        }

        [Fact]
        public void Classes_Binding_Attribute_Should_Filter_By_Prefix()
        {
            var compl = GetCompletionsFor("<Button Classes.my");

            Assert.Contains(compl.Completions, c => c.InsertText == "myClass=\"\"");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "other=\"\"");
        }

        [Fact]
        public void Classes_Binding_Attribute_Should_Not_Include_Classes_Of_Other_Types()
        {
            var compl = GetCompletionsFor("<TextBlock Classes.");

            Assert.Contains(compl.Completions, c => c.InsertText == "myClass=\"\"");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "other=\"\"");
        }

        [Fact]
        public void Classes_Binding_Attribute_Should_Start_After_The_Dot()
        {
            var compl = GetCompletionsFor("<Button Classes.");

            Assert.Equal("<Button Classes.".Length, compl.StartPosition);
        }

        [Fact]
        public void Classes_Property_Should_Be_Completed_In_Setter()
        {
            var compl = GetCompletionsFor("<Style Selector=\"Button\"><Setter Property=\"Cla");

            Assert.Contains(compl.Completions, c => c.DisplayText == "Classes");
        }

        [Fact]
        public void Property_Completions_Should_Be_Unique()
        {
            var compl = GetCompletionsFor("<UserControl P");
            Assert.All(compl.Completions.GroupBy(v => v.DisplayText), v => Assert.Single(v));
        }

        [Fact]
        public void Get_Only_Property_Should_Not_Be_Completed()
        {
            var compl = GetCompletionsFor("<UserControl P");

            Assert.All(compl.Completions, c => Assert.NotEqual("Parent", c.DisplayText));
        }

        [Fact]
        public void XmlContent_Property_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl><UserControl.", "HorizontalAlign", "HorizontalAlignment");
        }

        [Fact]
        public void AttachedProperty_Class_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl ", "Gri", "Grid.");
        }

        [Fact]
        public void XmlContent_AttachedProperty_Class_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl><", "Gri", "Grid");
        }

        [Fact]
        public void AttachedProperty_Full_Name_Should_Be_Completed()
        {
            // Typing a type prefix in attribute position should directly offer
            // "Grid.Row" instead of requiring the intermediate "Grid." step.
            var compl = GetCompletionsFor("<UserControl Gri");

            Assert.Contains(compl.Completions, c => c.InsertText == "Grid.Row=\"\"");
            Assert.Contains(compl.Completions, c => c.InsertText == "Grid.Column=\"\"");
        }

        [Fact]
        public void AttachedProperty_Full_Name_Should_Be_Completed_For_Single_Type()
        {
            AssertSingleCompletion("<UserControl ", "Gri", "Grid.Row=\"\"");
        }

        [Fact]
        public void AttachedProperty_Full_Name_Should_Be_Available_On_Fresh_Attribute()
        {
            // Visual Studio only filters the completion list built when the session opened
            // (with an empty attribute name), so the direct "Type.Property" completions must
            // be present even there.
            var compl = GetCompletionsFor("<Border ");

            Assert.Contains(compl.Completions, c => c.InsertText == "Grid.Row=\"\"");
            Assert.Contains(compl.Completions, c => c.InsertText == "FlyoutBase.AttachedFlyout=\"\"");
        }

        [Fact]
        public void AttachedProperty_Full_Name_Should_Be_Completed_For_Abstract_Type()
        {
            var compl = GetCompletionsFor("<Border Fly");

            Assert.Contains(compl.Completions, c => c.InsertText == "FlyoutBase.AttachedFlyout=\"\"");
        }

        [Fact]
        public void AttachedProperty_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl Grid.", "Ro", "Row=\"\"");
        }

        [Fact]
        public void AttachedProperty_Should_Be_Renamed()
        {
            AssertSingleCompletionInMiddleOfText("<UserControl Grid.", "=\"2\"", "Ro", "Row");
        }

        [Fact]
        public void XmlContent_AttachedProperty_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl><Grid.", "Ro", "Row");
        }

        [Fact]
        public void Abstract_Type_With_Attached_Properties_Should_Be_Completed()
        {
            // FlyoutBase is abstract but declares the AttachedFlyout attached property,
            // so it must be offered as a completion to allow <FlyoutBase.AttachedFlyout>.
            var compl = GetCompletionsFor("<UserControl><Flyout");

            Assert.Contains(compl.Completions, c => c.InsertText == "FlyoutBase");
        }

        [Fact]
        public void Abstract_Type_Attached_Property_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl><FlyoutBase.", "A", "AttachedFlyout");
        }

        [Fact]
        public void Property_Element_Child_Should_Be_Filtered_By_Property_Type()
        {
            // Inside <ContentControl.ContentTemplate> only types assignable to IDataTemplate are valid,
            // so unrelated controls must not be offered.
            var compl = GetCompletionsFor("<ContentControl.ContentTemplate><");

            Assert.Contains(compl.Completions, c => c.InsertText == "DataTemplate");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Grid");
        }

        [Fact]
        public void Property_Element_Child_Should_Be_Filtered_For_Styles()
        {
            // Inside <UserControl.Styles> only types assignable to IStyle (Style, ControlTheme, ...) are valid.
            var compl = GetCompletionsFor("<UserControl.Styles><");

            Assert.Contains(compl.Completions, c => c.InsertText == "Style");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "DataTemplate");
        }

        [Fact]
        public void Property_Element_Child_Should_Be_Filtered_For_DataTemplates()
        {
            var compl = GetCompletionsFor("<Window.DataTemplates><");

            Assert.Contains(compl.Completions, c => c.InsertText == "DataTemplate");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
        }

        [Fact]
        public void Property_Element_Child_Should_Be_Filtered_For_Flyout()
        {
            // Inside <Button.Flyout> only FlyoutBase-derived types are valid.
            var compl = GetCompletionsFor("<Button.Flyout><");

            Assert.Contains(compl.Completions, c => c.InsertText == "Flyout");
            Assert.Contains(compl.Completions, c => c.InsertText == "MenuFlyout");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "DataTemplate");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
        }

        [Fact]
        public void Property_Element_Child_Should_Not_Be_Filtered_For_Resources()
        {
            // Resources may hold arbitrary objects, so all element types remain available.
            var compl = GetCompletionsFor("<UserControl.Resources><");

            Assert.Contains(compl.Completions, c => c.InsertText == "Button");
            Assert.Contains(compl.Completions, c => c.InsertText == "SolidColorBrush");
        }

        [Fact]
        public void Property_Element_Child_Single_Completion_Should_Work()
        {
            // Once the prefix is narrowed, the assignable type should complete in one step.
            AssertSingleCompletion("<ContentControl.ContentTemplate><", "DataTemp", "DataTemplate");
        }

        [Fact]
        public void Style_Element_Children_Should_Be_IStyle_Or_SetterBase()
        {
            // A <Style> element may only contain nested styles/themes (IStyle) and setters (SetterBase).
            var compl = GetCompletionsFor("<Style><");

            Assert.Contains(compl.Completions, c => c.InsertText == "Style");
            Assert.Contains(compl.Completions, c => c.InsertText == "ControlTheme");
            Assert.Contains(compl.Completions, c => c.InsertText == "FluentTheme");
            Assert.Contains(compl.Completions, c => c.InsertText == "Setter");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "DataTemplate");
        }

        [Fact]
        public void ControlTheme_Element_Children_Should_Be_IStyle_Or_SetterBase()
        {
            var compl = GetCompletionsFor("<ControlTheme><");

            Assert.Contains(compl.Completions, c => c.InsertText == "Style");
            Assert.Contains(compl.Completions, c => c.InsertText == "ControlTheme");
            Assert.Contains(compl.Completions, c => c.InsertText == "Setter");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
        }

        [Fact]
        public void Style_Element_Children_Single_Completion_Should_Work()
        {
            AssertSingleCompletion("<Style><", "Sett", "Setter");
        }

        [Fact]
        public void Template_Property_Element_Child_Should_Be_Filtered()
        {
            // Inside <TemplatedControl.Template> only types assignable to ITemplate<Control>
            // (i.e. ControlTemplate) are valid.
            var compl = GetCompletionsFor("<TemplatedControl.Template><");

            Assert.Contains(compl.Completions, c => c.InsertText == "ControlTemplate");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Style");
        }

        [Fact]
        public void Theme_Property_Element_Child_Should_Be_Filtered()
        {
            // Inside <Control.Theme> only ControlTheme is valid.
            var compl = GetCompletionsFor("<Control.Theme><");

            Assert.Contains(compl.Completions, c => c.InsertText == "ControlTheme");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Button");
            Assert.DoesNotContain(compl.Completions, c => c.InsertText == "Setter");
        }

        [Fact]
        public void EnumValue_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl HorizontalAlignment=\"", "Le", "Left");
        }

        [Fact]
        public void WellKnown_UrlNameSpaces_Should_Be_Completed()
        {
            var compl = GetCompletionsFor("<UserControl xmlns:t=\"http");

            Assert.NotEmpty(compl.Completions);
            Assert.Contains(compl.Completions, v => v.InsertText == "https://github.com/avaloniaui");
            Assert.Contains(compl.Completions, v => v.InsertText == "http://schemas.microsoft.com/winfx/2006/xaml");
        }

        [Fact]
        public void Clr_NameSpaces_Should_Be_Completed()
        {
            var compl = GetCompletionsFor("<UserControl xmlns:t=\"clr-namespace:Ava");

            Assert.NotEmpty(compl.Completions);
            Assert.Contains(compl.Completions, v => v.InsertText == "clr-namespace:Avalonia.Data;assembly=Avalonia.Base");
            Assert.Contains(compl.Completions, v => v.InsertText == "clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls");
        }

        [Fact]
        public void Using_NameSpaces_Should_Be_Completed()
        {
            var compl = GetCompletionsFor("<UserControl xmlns:t=\"using:Ava");

            Assert.NotEmpty(compl.Completions);
            Assert.Contains(compl.Completions, v => v.InsertText == "using:Avalonia.Data");
            Assert.Contains(compl.Completions, v => v.InsertText == "using:Avalonia.Controls");
        }

        [Fact]
        public void Extension_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl Content=\"{", "Bind", "Binding");
        }

        [Fact]
        public void Extension_Property_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl Content=\"{Binding ", "Pa", "Path=");
        }

        [Fact]
        public void Extension_Property_Enum_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl Content=\"{Binding Mode=", "One", "OneWay");
        }
        
        [Fact]
        public void Extension_DataType_Should_Be_Completed()
        {
            AssertSingleCompletion("<UserControl ", "x:Data", "x:DataType=\"\"");
        }

        [Fact]
        public void Completions_Should_Be_Sorted()
        {
            var compl = GetCompletionsFor("<DataTemplate");

            Assert.Equal(2, compl.Completions.Count);
            Assert.Equal("DataTemplate", compl.Completions[0].DisplayText);
            Assert.Equal("DataTemplates", compl.Completions[1].DisplayText);
        }

        [Fact] 
        public void Completation_Event_Handler_Without_xmlsn()
        {
            var comp = GetCompletionsFor("<local:MyButton Click=\"");

            Assert.NotNull(comp);
            Assert.Equal(1, comp.Completions?.Count);
            Assert.Equal("MyButton_Click", comp.Completions[0].InsertText);
        }

        [Fact]
        public void Completions_With_Multiple_Kinds_Should_Be_Sorted()
        {
            var compl = GetCompletionsFor("<Style Se");

            Assert.Equal(3, compl.Completions.Count);
            Assert.Equal("Selector", compl.Completions[0].DisplayText);
            Assert.Equal("SelectableTextBlock", compl.Completions[1].DisplayText);
            Assert.Equal("SelectingItemsControl", compl.Completions[2].DisplayText);
        }

        [Fact]
        public void GenericType_Should_Transform_TypeArguments()
        {
            var compl = GetCompletionsFor("<FuncDataTemplate");

            Assert.Equal(2, compl.Completions.Count);
            Assert.Equal("FuncDataTemplate", compl.Completions[0].DisplayText);
            Assert.Equal("FuncDataTemplate", compl.Completions[0].InsertText);
            Assert.Equal("FuncDataTemplate<T>", compl.Completions[1].DisplayText);
            Assert.Equal("FuncDataTemplate x:TypeArguments=\"\"", compl.Completions[1].InsertText);
        }
    }
}
