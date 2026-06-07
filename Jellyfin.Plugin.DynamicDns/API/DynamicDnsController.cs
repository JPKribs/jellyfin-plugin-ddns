using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DynamicDns.Api;

/// <summary>
/// Administrative endpoints backing the Dynamic DNS dashboard page.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("DynamicDns")]
[Produces("application/json")]
public class DynamicDnsController : ControllerBase
{
    private readonly DnsUpdateService _updateService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicDnsController"/> class.
    /// </summary>
    /// <param name="updateService">The update service.</param>
    public DynamicDnsController(DnsUpdateService updateService)
    {
        _updateService = updateService;
    }

    /// <summary>
    /// Runs an immediate update pass over every enabled record and returns the per-record outcome.
    /// Behaves like the scheduled run: a record whose IP is unchanged since its last successful update
    /// is skipped rather than re-pushed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregate update outcome.</returns>
    [HttpPost("RunNow")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DnsUpdateOutcome>> RunNow(CancellationToken cancellationToken)
    {
        var outcome = await _updateService.RunAsync(cancellationToken).ConfigureAwait(false);
        return Ok(outcome);
    }
}
