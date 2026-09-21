using System.Reflection;
using System.Xml.Linq;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Keep architectural decisions executable without loading WPF, camera drivers or the web host.
/// Deliberate project additions should update this map and the architecture documentation together.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["GoldenTicket.Domain"] = [],
            ["GoldenTicket.Application"] = ["GoldenTicket.Domain"],
            ["GoldenTicket.AI"] = ["GoldenTicket.Domain"],
            ["GoldenTicket.Persistence"] = ["GoldenTicket.Domain", "GoldenTicket.Application"],
            ["GoldenTicket.CompanionHost"] = ["GoldenTicket.Application"],
            ["GoldenTicket.Vision"] = [],
            ["GoldenTicket.Desktop"] =
            [
                "GoldenTicket.Domain", "GoldenTicket.Application", "GoldenTicket.AI",
                "GoldenTicket.Persistence", "GoldenTicket.CompanionHost", "GoldenTicket.Vision"
            ]
        };

    private static readonly string[] PortableProjects =
        ["GoldenTicket.Domain", "GoldenTicket.Application", "GoldenTicket.AI", "GoldenTicket.Persistence"];

    [Fact]
    public void ProductionProjectsOnlyReferenceTheirAllowedLayers()
    {
        var projects = ReadProductionProjects();
        Assert.Equal(AllowedReferences.Keys.Order(), projects.Keys.Order());
        foreach (var (name, project) in projects)
        {
            foreach (var target in ProjectReferences(project))
            {
                var targetName = Path.GetFileNameWithoutExtension(target);
                Assert.True(projects.TryGetValue(targetName, out var referenced) && referenced.Path == target,
                    $"{name} references {target}. Production projects must not depend on tools, tests or external projects.");
                Assert.Contains(targetName, AllowedReferences[name]);
            }

            // A linked source file can bypass the assembly dependency graph just as easily as a
            // ProjectReference. Production code must stay under src even when explicitly included.
            foreach (var source in Elements(project.Xml, "Compile").Select(x => (string?)x.Attribute("Include")).OfType<string>())
            {
                var path = Resolve(project, source);
                Assert.True(IsWithin(path, Path.Combine(TestManifest.RepositoryRoot, "src")),
                    $"{name} imports source outside src: {source}.");
            }
        }
    }

    [Fact]
    public void ProductionProjectGraphHasNoCycles()
    {
        var projects = ReadProductionProjects();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var trail = new List<string>();
        foreach (var name in projects.Keys) Visit(name);

        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            Assert.True(visiting.Add(name), $"Project reference cycle: {string.Join(" -> ", trail.Append(name))}.");
            trail.Add(name);
            foreach (var reference in ProjectReferences(projects[name]))
            {
                var target = Path.GetFileNameWithoutExtension(reference);
                Assert.True(projects.ContainsKey(target), $"Unknown production dependency: {name} -> {reference}.");
                Visit(target);
            }
            trail.RemoveAt(trail.Count - 1);
            visiting.Remove(name);
            visited.Add(name);
        }
    }

    [Fact]
    public void CoreProjectsAndTheirTestsRemainPlatformNeutral()
    {
        var projects = ReadProductionProjects();
        var coreTests = ReadProject(Path.Combine(TestManifest.RepositoryRoot, "tests",
            "GoldenTicket.Core.Tests", "GoldenTicket.Core.Tests.csproj"));
        foreach (var project in PortableProjects.Select(name => projects[name]).Append(coreTests))
        {
            Assert.Equal("Microsoft.NET.Sdk", (string?)project.Xml.Root?.Attribute("Sdk"));
            Assert.Equal("net10.0", Assert.Single(Elements(project.Xml, "TargetFramework")).Value);
            Assert.Empty(Elements(project.Xml, "TargetFrameworks"));
            Assert.Empty(Elements(project.Xml, "FrameworkReference"));
            Assert.Empty(Elements(project.Xml, "Reference"));
            foreach (var property in new[] { "UseWPF", "UseWindowsForms", "RuntimeIdentifier", "RuntimeIdentifiers", "SupportedOSPlatformVersion" })
                Assert.Empty(Elements(project.Xml, property));
            foreach (var reference in ProjectReferences(project))
                Assert.Contains(Path.GetFileNameWithoutExtension(reference), PortableProjects);
        }
    }

    [Fact]
    public void DomainApplicationAndAiAssembliesHaveNoInfrastructureDependencies()
    {
        var assemblies = new[]
        {
            typeof(Domain.Engine.GameRules).Assembly,
            typeof(Application.GameCoordinator).Assembly,
            typeof(AI.HeuristicAiPolicy).Assembly
        };
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var dependency = reference.Name!;
                if (dependency.StartsWith("GoldenTicket.", StringComparison.Ordinal))
                    Assert.Contains(dependency, AllowedReferences[name]);
                else
                    Assert.True(dependency.StartsWith("System.", StringComparison.Ordinal) ||
                        dependency is "System" or "netstandard" or "mscorlib" or "Microsoft.CSharp",
                        $"{name} depends on non-core assembly {dependency}.");
                Assert.DoesNotContain(dependency, new[] { "System.Windows", "System.Windows.Forms", "System.Drawing.Common" });
            }
            Assert.DoesNotContain(assembly.GetTypes().SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)),
                method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0);
        }
    }

    private sealed record Project(string Path, XDocument Xml);

    private static Dictionary<string, Project> ReadProductionProjects() =>
        Directory.EnumerateFiles(Path.Combine(TestManifest.RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), ReadProject, StringComparer.Ordinal);

    private static Project ReadProject(string path) => new(Path.GetFullPath(path), XDocument.Load(path));

    private static IEnumerable<XElement> Elements(XDocument document, string name) =>
        document.Descendants().Where(element => element.Name.LocalName == name);

    private static IEnumerable<string> ProjectReferences(Project project) =>
        Elements(project.Xml, "ProjectReference").Select(element => Resolve(project,
            (string?)element.Attribute("Include") ?? throw new InvalidOperationException("ProjectReference is missing Include.")));

    private static string Resolve(Project project, string relative)
    {
        Assert.DoesNotContain("$(", relative, StringComparison.Ordinal);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.Path)!,
            relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool IsWithin(string path, string directory) =>
        path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
