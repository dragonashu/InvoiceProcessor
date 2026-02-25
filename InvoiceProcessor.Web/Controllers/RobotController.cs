using InvoiceProcessor.Web.Contracts;
using InvoiceProcessor.Web.Services.Robot;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceProcessor.Web.Controllers;

[ApiController]
[Route("robot/jobs")]
public class RobotController(IPostingJobService postingJobService) : ControllerBase
{
    [HttpGet("next")]
    public async Task<IActionResult> Next(CancellationToken cancellationToken)
    {
        var payload = await postingJobService.ClaimNextJobAsync(cancellationToken);
        return payload is null ? NoContent() : Ok(payload);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] RobotCompleteRequest request, CancellationToken cancellationToken)
    {
        await postingJobService.CompleteJobAsync(id, request, cancellationToken);
        return Ok(new { id, status = request.Result });
    }
}
