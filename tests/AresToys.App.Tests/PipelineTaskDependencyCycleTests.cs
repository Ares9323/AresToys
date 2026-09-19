using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AresToys.Core.Pipeline;
using AresToys.Pipeline;
using Xunit;

namespace AresToys.App.Tests;

/// <summary>Guards the one shape of dependency cycle this app can produce, which takes the whole
/// process down at startup rather than failing quietly.
///
/// <see cref="PipelineExecutor"/> is built from the task registry, which is built from every
/// <see cref="IPipelineTask"/>. So anything a task depends on, however deep, is constructed
/// *while* PipelineExecutor is being constructed — and if one of those things asks for
/// PipelineExecutor (directly, or through a service that runs workflows) the container refuses to
/// build the graph at all: "A circular dependency was detected".
///
/// This actually happened: a launcher cell gained the ability to run a workflow, its runner took
/// PipelineExecutor in its constructor, and LauncherTriggerKeyTask reaches that runner through
/// LauncherActionService. The app stopped starting. The fix is to resolve the executor (or a
/// service that holds it) from IServiceProvider at the moment of use, which this test allows —
/// only constructor parameters count as edges here, which is exactly what the container walks.</summary>
public sealed class PipelineTaskDependencyCycleTests
{
    /// <summary>Types that must not be reachable through task constructors.</summary>
    private static readonly HashSet<Type> Forbidden =
    [
        typeof(PipelineExecutor),
        typeof(AresToys.App.Services.WorkflowRunner),
    ];

    [Fact]
    public void NoPipelineTaskCanReachThePipelineExecutorThroughItsConstructor()
    {
        var appAssembly = typeof(AresToys.App.Services.WorkflowRunner).Assembly;
        var tasks = appAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPipelineTask).IsAssignableFrom(t))
            .ToList();

        // Sanity: if this ever finds nothing, the test is passing for the wrong reason.
        Assert.NotEmpty(tasks);

        var offenders = tasks
            .Select(t => (Task: t, Chain: FindForbiddenChain(t, appAssembly)))
            .Where(x => x.Chain is not null)
            .Select(x => $"{x.Task.Name}: {string.Join(" -> ", x.Chain!)}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "These pipeline tasks reach the PipelineExecutor through their constructors, which is a "
            + "startup-breaking dependency cycle. Resolve it from IServiceProvider at the point of "
            + "use instead:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheScanActuallyDetectsSuchAChain()
    {
        // Keeps the test above honest: a green result there has to mean "no cycles", not "the walk
        // never looks at anything". Bait lives in this assembly, so the walk is pointed here.
        var here = typeof(PipelineTaskDependencyCycleTests).Assembly;

        var direct = FindForbiddenChain(typeof(DirectBait), here);
        Assert.NotNull(direct);
        Assert.Contains(nameof(PipelineExecutor), direct!);

        // The shape that actually bit us: the offender is two hops away, exactly like
        // LauncherTriggerKeyTask -> LauncherActionService -> (runner) -> PipelineExecutor.
        var transitive = FindForbiddenChain(typeof(TaskLikeBait), here);
        Assert.NotNull(transitive);
        Assert.Equal(
            [nameof(TaskLikeBait), nameof(MiddleBait), nameof(PipelineExecutor)],
            transitive!);

        // A graph with no forbidden type anywhere comes back clean.
        Assert.Null(FindForbiddenChain(typeof(HarmlessBait), here));
    }

    /// <summary>Walk constructor parameters breadth-first from <paramref name="root"/>, following
    /// concrete types declared in <paramref name="follow"/>, and return the path to the first
    /// forbidden type found (or null). Interfaces are not followed: mapping them to implementations
    /// needs the container, and the cycles we can catch here run through concrete services.</summary>
    private static List<string>? FindForbiddenChain(Type root, Assembly follow)
    {
        var seen = new HashSet<Type> { root };
        var queue = new Queue<(Type Type, List<string> Path)>();
        queue.Enqueue((root, [root.Name]));

        while (queue.Count > 0)
        {
            var (type, path) = queue.Dequeue();
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var p in ctor.GetParameters())
                {
                    var pt = p.ParameterType;
                    if (Forbidden.Contains(pt)) return [.. path, pt.Name];
                    if (pt.IsInterface || pt.IsAbstract || pt.IsPrimitive) continue;
                    // Only walk into our own services; framework types (loggers, options) can't
                    // close a loop back onto our graph.
                    if (pt.Assembly != follow) continue;
                    if (!seen.Add(pt)) continue;
                    queue.Enqueue((pt, [.. path, pt.Name]));
                }
            }
        }
        return null;
    }

    // Bait types for the self-check above. Never registered anywhere; they exist only so the walk
    // has a known-bad graph (and a known-good one) to be measured against.
    internal sealed class DirectBait
    {
        public DirectBait(PipelineExecutor executor) => _ = executor;
    }

    internal sealed class TaskLikeBait
    {
        public TaskLikeBait(MiddleBait middle) => _ = middle;
    }

    internal sealed class MiddleBait
    {
        public MiddleBait(PipelineExecutor executor) => _ = executor;
    }

    internal sealed class HarmlessBait
    {
        public HarmlessBait(MiddleBaitFree free) => _ = free;
    }

    internal sealed class MiddleBaitFree
    {
        public MiddleBaitFree(string whatever) => _ = whatever;
    }
}
