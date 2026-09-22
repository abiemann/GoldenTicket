using System.Reflection;
using System.Runtime.Loader;
using GoldenTicket.Domain.Ai;

namespace GoldenTicket.Simulator;

/// <summary>Loads a preserved policy without replacing the current AI assembly or sharing its statics.</summary>
internal sealed class PolicyAssembly : AssemblyLoadContext
{
    private readonly Type _policyType;

    internal PolicyAssembly(string path) : base("benchmark-baseline-" + Guid.NewGuid().ToString("N"), isCollectible: true)
    {
        var assembly = LoadFromAssemblyPath(Path.GetFullPath(path));
        _policyType = assembly.GetType("GoldenTicket.AI.HeuristicAiPolicy", throwOnError: true)!;
        if (!typeof(IAiPolicy).IsAssignableFrom(_policyType))
            throw new InvalidDataException("The baseline policy does not implement this build's IAiPolicy contract.");
    }

    internal IAiPolicy Create() => (IAiPolicy)Activator.CreateInstance(_policyType)!;

    protected override Assembly? Load(AssemblyName name) =>
        name.Name == typeof(IAiPolicy).Assembly.GetName().Name ? typeof(IAiPolicy).Assembly : null;
}
