namespace Slh.Tms.Api.Models;

public sealed record ApplyPlanProposalRequest(bool AcknowledgeUnverified = false);

public sealed record ApplyPlanProposalResult(
    Guid ProposalId,
    string Status,
    int CreatedRunCount,
    IReadOnlyList<Guid> CreatedLoadIds,
    IReadOnlyList<string> Warnings);

public sealed class PlanProposalApplyException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
