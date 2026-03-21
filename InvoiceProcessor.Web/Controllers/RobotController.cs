using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Enums;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceProcessor.Web.Controllers;

[ApiController]
[Route("robot/jobs")]
public class RobotController(IPostingJobService postingJobService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        PostingJobStatus? parsed = null;
        if (status is not null && Enum.TryParse<PostingJobStatus>(status, true, out var s))
            parsed = s;

        var jobs = await postingJobService.ListJobsAsync(parsed, Math.Clamp(limit, 1, 200), cancellationToken);

        return Ok(jobs.Select(j => new
        {
            j.Id,
            j.DocumentId,
            j.BatchId,
            Status = j.Status.ToString(),
            j.CreatedAt,
            j.ClaimedAt,
            j.CompletedAt,
            j.ErpDocNo,
            j.ErrorCategory,
            j.ErrorMessage,
            DocumentCorrelationId = j.Document?.CorrelationId,
            DocumentSupplier = j.Document?.Supplier
        }));
    }

    [HttpGet("next")]
    public async Task<IActionResult> Next(CancellationToken cancellationToken)
    {
        var payload = await postingJobService.ClaimNextJobAsync(cancellationToken);
        return payload is null ? NoContent() : Ok(payload);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await postingJobService.GetJobAsync(id, cancellationToken);
        if (job is null) return NotFound();

        return Ok(new
        {
            job.Id,
            job.DocumentId,
            job.BatchId,
            Status = job.Status.ToString(),
            job.CreatedAt,
            job.ClaimedAt,
            job.CompletedAt,
            job.ErpDocNo,
            job.ErrorCategory,
            job.ErrorMessage,
            job.RequestJson,
            job.ResultJson
        });
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] RobotUpdateRequest request, CancellationToken cancellationToken)
    {
        var job = await postingJobService.UpdateJobAsync(id, request, cancellationToken);
        return Ok(new { job.Id, Status = job.Status.ToString() });
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] RobotCompleteRequest request, CancellationToken cancellationToken)
    {
        await postingJobService.CompleteJobAsync(id, request, cancellationToken);
        return Ok(new { id, status = request.Result });
    }
}
