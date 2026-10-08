namespace AutoContext.Worker.DotNet.Tests.Tasks.CSharp;

using System.Xml.Linq;

using AutoContext.Worker.DotNet.Tasks.CSharp;
using AutoContext.Worker.DotNet.Tests.Support.Tasks.CSharp;

public sealed class CSharpProjectKindResolverTests
{
    public sealed class Classify
    {
        [Theory]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk" />""", nameof(CSharpProjectKind.Library))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Razor" />""", nameof(CSharpProjectKind.Library))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""", nameof(CSharpProjectKind.Application))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>""", nameof(CSharpProjectKind.Application))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web" />""", nameof(CSharpProjectKind.Application))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Worker" />""", nameof(CSharpProjectKind.Application))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web/9.0.0" />""", nameof(CSharpProjectKind.Application))]
        [InlineData("""<Project><Sdk Name="Microsoft.NET.Sdk.BlazorWebAssembly" /></Project>""", nameof(CSharpProjectKind.Application))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""", nameof(CSharpProjectKind.Test))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="NUnit" Version="4.0.0" /></ItemGroup></Project>""", nameof(CSharpProjectKind.Test))]
        [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="xunit.v3" Version="3.2.2" /></ItemGroup></Project>""", nameof(CSharpProjectKind.Test))]
        public void Should_classify_the_project_by_what_it_declares(string projectXml, string expected)
        {
            // Act
            var kind = CSharpProjectKindResolver.Classify(XDocument.Parse(projectXml));

            // Assert
            Assert.Equal(expected, kind.ToString());
        }
    }

    public sealed class Resolve
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Sample.cs")]
        public void Should_be_unknown_without_an_absolute_path(string? filePath)
        {
            // Act + Assert
            Assert.Equal(CSharpProjectKind.Unknown, CSharpProjectKindResolver.Resolve(filePath));
        }

        [Fact]
        public void Should_use_the_nearest_project_file_above_the_source()
        {
            // Arrange
            using var project = CSharpProjectTestDirectory.Create(CSharpProjectTestDirectory.Web);

            // Act
            var kind = CSharpProjectKindResolver.Resolve(project.SourcePath(Path.Combine("Controllers", "Nested", "HomeController.cs")));

            // Assert
            Assert.Equal(CSharpProjectKind.Application, kind);
        }

        [Fact]
        public void Should_be_unknown_when_the_project_file_is_not_xml()
        {
            // Arrange
            using var project = CSharpProjectTestDirectory.Create("not xml at all");

            // Act + Assert
            Assert.Equal(CSharpProjectKind.Unknown, CSharpProjectKindResolver.Resolve(project.SourcePath()));
        }
    }
}
