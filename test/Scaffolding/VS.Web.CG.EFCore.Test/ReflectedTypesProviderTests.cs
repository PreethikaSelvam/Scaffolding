// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Resources;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.DotNet.Scaffolding.Shared;
using Microsoft.DotNet.Scaffolding.Shared.ProjectModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.ProjectModel;
using Microsoft.VisualStudio.Web.CodeGeneration.DotNet;
using Microsoft.VisualStudio.Web.CodeGeneration.Utils;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.VisualStudio.Web.CodeGeneration.EntityFrameworkCore.Test
{
    public class ReflectedTypesProviderTests
    {
        private TestAssemblyLoadContext _loader;
        private ILogger _logger;
        private ITestOutputHelper _output;
        private IProjectContext _projectContext;
        private MsBuildProjectSetupHelper _projectSetupHelper;
        private RoslynWorkspace _workspace;

        public ReflectedTypesProviderTests(ITestOutputHelper output)
        {
            _output = output;
            _projectSetupHelper = new MsBuildProjectSetupHelper();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void GetReflectedType_PreservesResources(bool modifyCompilation, bool isPublic)
        {
            var assemblyName = "ResourceProject" + Guid.NewGuid().ToString("N");
            var assemblyPath = Path.Combine(Path.GetTempPath(), assemblyName + ".dll");
            var source = @"
                public class Startup
                {
                    public static void Main() { }
                    public static string ReadResource() =>
                        new System.Resources.ResourceManager(""SampleProject.WebApi.Properties.Resources"",
                            typeof(Startup).Assembly).GetString(""TextFile"");
                    public static string Version => ""original"";
                }
                public class ResourceContext : Microsoft.EntityFrameworkCore.DbContext
                {
                    public ResourceContext() { Text = Startup.ReadResource(); }
                    public string Text { get; }
                    protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder options)
                        => Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions.UseSqlServer(
                            options, new Microsoft.Data.SqlClient.SqlConnection());
                }";
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator).Append(typeof(DbContext).Assembly.Location).Distinct()
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            using var resourceStream = new MemoryStream();
            using (var writer = new ResourceWriter(resourceStream))
            {
                writer.AddResource("TextFile", "text file contents");
                writer.Generate();
                var bytes = resourceStream.ToArray();
                var resource = new ResourceDescription("SampleProject.WebApi.Properties.Resources.resources",
                    () => new MemoryStream(bytes), isPublic);
                try
                {
                    var binaryBytes = new byte[] { 0, 1, 127, 128, 255 };
                    var binaryResource = new ResourceDescription("Custom.Logical.Name",
                        () => new MemoryStream(binaryBytes), isPublic: false);
                    var emitted = compilation.Emit(assemblyPath, manifestResources: new[] { resource, binaryResource });
                    Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
                    var context = new Mock<IProjectContext>();
                    context.SetupGet(c => c.AssemblyFullPath).Returns(assemblyPath);
                    var provider = new ReflectedTypesProvider(compilation,
                        modifyCompilation ? c => c.ReplaceSyntaxTree(c.SyntaxTrees.Single(),
                            CSharpSyntaxTree.ParseText(source.Replace("original", "modified"))) : null,
                        context.Object, new DefaultAssemblyLoadContext(), new ConsoleLogger());

                    var type = provider.GetReflectedType("Startup");
                    Assert.NotNull(type);
                    Assert.Equal(modifyCompilation ? "modified" : "original", type.GetProperty("Version").GetValue(null));
                    var contextType = provider.GetReflectedType("ResourceContext");
                    using var dbContext = DbContextActivator.CreateInstance(contextType, type.Assembly, null);
                    Assert.Equal("text file contents", contextType.GetProperty("Text").GetValue(dbContext));
                    using var binaryStream = type.Assembly.GetManifestResourceStream("Custom.Logical.Name");
                    using var binaryCopy = new MemoryStream();
                    binaryStream.CopyTo(binaryCopy);
                    Assert.Equal(binaryBytes, binaryCopy.ToArray());
                }
                finally
                {
                    File.Delete(assemblyPath);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GetReflectedType_WorksWithoutResources(bool buildOutputExists)
        {
            var name = "NoResources" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(Path.GetTempPath(), name + ".dll");
            var compilation = CSharpCompilation.Create(name,
                new[] { CSharpSyntaxTree.ParseText("public class Startup { public static void Main() { } }") },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            try
            {
                if (buildOutputExists)
                {
                    Assert.True(compilation.Emit(path).Success);
                }

                var context = new Mock<IProjectContext>();
                context.SetupGet(c => c.AssemblyFullPath).Returns(path);
                var provider = new ReflectedTypesProvider(compilation, null, context.Object,
                    new DefaultAssemblyLoadContext(), new ConsoleLogger());

                Assert.NotNull(provider.GetReflectedType("Startup"));
                Assert.Empty(provider.GetReflectedType("Startup").Assembly.GetManifestResourceNames());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact(Skip = "Disable tests on CI that need to load assemblies")]
        public async Task TestGetReflectedType()
        {
            using (var fileProvider = new TemporaryFileProvider())
            {
                SetupProjectInformation(fileProvider);

                var compilation = await _workspace.CurrentSolution.Projects
                    .First()
                    .GetCompilationAsync();

                Func<CodeAnalysis.Compilation, CodeAnalysis.Compilation> func = (c) =>
                {
                    return c;
                };

                var reflectedTypesProvider = new ReflectedTypesProvider(
                    compilation,
                    func,
                    _projectContext,
                    _loader,
                    _logger);

                var carType = reflectedTypesProvider.GetReflectedType("Library1.Models.Car", false);
                Assert.Null(carType);

                carType = reflectedTypesProvider.GetReflectedType("Library1.Models.Car", true);
                Assert.NotNull(carType);
            }
        }

        [Fact(Skip = "test fail")]
        public async Task TestGetReflectedType_FailedCompilation()
        {
            using (var fileProvider = new TemporaryFileProvider())
            {
                SetupProjectInformation(fileProvider);

                var compilation = await _workspace.CurrentSolution.Projects
                    .First()
                    .GetCompilationAsync();

                Func<CodeAnalysis.Compilation, CodeAnalysis.Compilation> func = (c) =>
                {
                    return c.WithReferences(null);
                };

                var reflectedTypesProvider = new ReflectedTypesProvider(
                    compilation,
                    func,
                    _projectContext,
                    _loader,
                    _logger);

                var carType = reflectedTypesProvider.GetReflectedType("Library1.Models.Car", true);
                Assert.Null(carType);

                var compilationErrors = reflectedTypesProvider.GetCompilationErrors();
                Assert.NotNull(compilationErrors);
            }
        }

        private void SetupProjectInformation(TemporaryFileProvider fileProvider)
        {
            _projectSetupHelper.SetupProjects(fileProvider, _output);

            var path = Path.Combine(fileProvider.Root, "Root", "Test.csproj");
            _projectContext = GetProjectInformation(path);
            _workspace = new RoslynWorkspace(_projectContext);
            _loader = new TestAssemblyLoadContext(_projectContext);
            _logger = new ConsoleLogger();
        }

        private IProjectContext GetProjectInformation(string path)
        {
            var rootContext = new MsBuildProjectContextBuilder(path, "Dummy")
                .Build();
            return rootContext;
        }
    }
}
