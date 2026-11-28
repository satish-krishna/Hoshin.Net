using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit.Driver;

public sealed class DriverContractTests
{
    [Fact]
    public void EachDecision_ReportsItsKind()
    {
        Decision[] decisions = [new Accept(), new Approve(""), new Decline("why"), new Reject(), new Reopen(WorkflowDocument.Spec)];

        Assert.Equal(Enum.GetValues<DecisionKind>(), decisions.Select(d => d.Kind));
    }

    [Theory]
    [InlineData(DecisionKind.Accept, "accept")]
    [InlineData(DecisionKind.Approve, "accept")]
    [InlineData(DecisionKind.Decline, "decline")]
    [InlineData(DecisionKind.Reject, "reject")]
    [InlineData(DecisionKind.Reopen, "reopen")]
    public void GateSkill_NamesTheSkillWhoseGateDecides(DecisionKind kind, string skill) =>
        Assert.Equal(skill, DecisionApplier.GateSkill(kind));

    [Fact]
    public void GateSkill_RejectsAnUnknownDecision() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DecisionApplier.GateSkill((DecisionKind)99));

    [Fact]
    public void DriverRefusedException_KeepsItsMessageAndCause()
    {
        var cause = new InvalidOperationException("cause");

        Assert.NotNull(new DriverRefusedException().Message);
        Assert.Equal("no", new DriverRefusedException("no").Message);
        Assert.Same(cause, new DriverRefusedException("no", cause).InnerException);
    }

    [Fact]
    public void Prompt_JoinsInstructionsBriefAndFeedback()
    {
        var scope = new WorkScope("/r", ["a.md"], [], []);
        var clean = new AgentWork(new WorkId(StepKind.WriteDocument, "intent", 1, "n"), Role.Intent, "do it", "the brief", scope, 1, [], []);
        var retried = clean with { Feedback = ["line 1: bad"] };

        Assert.DoesNotContain("not accepted", clean.Prompt, StringComparison.Ordinal);
        Assert.Contains("line 1: bad", retried.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("do it", retried.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkflowDocument.Intent, Role.Intent)]
    [InlineData(WorkflowDocument.Spec, Role.Spec)]
    [InlineData(WorkflowDocument.Plan, Role.Plan)]
    public void RoleOf_MapsEachDocumentToItsRole(WorkflowDocument document, Role role) =>
        Assert.Equal(role, TurnMapper.RoleOf(document));

    [Fact]
    public void RoleOf_RejectsAnUnknownDocument() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnMapper.RoleOf((WorkflowDocument)99));
}
