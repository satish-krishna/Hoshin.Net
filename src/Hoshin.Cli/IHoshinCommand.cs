namespace Hoshin.Cli;

/// <summary>Single-responsibility CLI command contract.</summary>
public interface IHoshinCommand
{
    string Name { get; }
    string Usage { get; }
    Task<int> RunAsync(IReadOnlyList<string> args);
}
