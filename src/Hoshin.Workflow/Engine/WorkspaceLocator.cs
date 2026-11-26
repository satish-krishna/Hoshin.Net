using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>Single owner locating a Hoshin workspace and its master repository via <see cref="IWorkspaceResolver"/> regardless of folder name.</summary>
public interface IWorkspaceLocator
{
    Resolution? TryResolve(string directory);

    bool HasWorkspaceMarker(string directory);
}

/// <summary>Default <see cref="IWorkspaceLocator"/> backed by <see cref="IWorkspaceResolver"/>.</summary>
public sealed class WorkspaceLocator(IWorkspaceResolver resolver) : IWorkspaceLocator
{
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Resolution? TryResolve(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var resolution = _resolver.Resolve(directory, new ResolveOptions(null, null));
        return resolution.Master is not null ? resolution : null;
    }

    public bool HasWorkspaceMarker(string directory) => TryResolve(directory) is not null;
}
