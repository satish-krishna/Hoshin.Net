using Hoshin.Cli;
using Hoshin.Workflow.Git;

namespace Hoshin.Tests.Architecture.Samples;

public interface IFatSampleInterface
{
    void M1();
    void M2();
    void M3();
    void M4();
    void M5();
    void M6();
    void M7();
    void M8();
    void M9();
    void M10();
    void M11();
}

public sealed class SampleCommandTakingServices(HoshinServices services)
{
    public HoshinServices Services => services;
}

public sealed class SampleOptionalDependency(IGitClient? git = null)
{
    public IGitClient? Git => git;
}

public sealed class SampleStaticDefault
{
    public static SampleStaticDefault Default { get; } = new();
}

public sealed class SampleMutableStatic
{
#pragma warning disable CA2211
    public static int Counter;
#pragma warning restore CA2211
}
