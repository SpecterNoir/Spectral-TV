using Jellyfin.Plugin.SpectralTV;
using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// REST endpoints for SpectralTV channel management.
/// </summary>
[ApiController]
[Route("SpectralTV/api/channels")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ChannelsController : ControllerBase
{
    private readonly ChannelService _channels;
    private readonly StreamService _stream;
    private readonly AiChannelAutoApplyService _aiAutoApply;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelsController"/> class.
    /// </summary>
    /// <param name="channels">Channel service.</param>
    /// <param name="stream">Stream service.</param>
    /// <param name="aiAutoApply">AI auto-apply service.</param>
    public ChannelsController(
        ChannelService channels,
        StreamService stream,
        AiChannelAutoApplyService aiAutoApply)
    {
        _channels = channels;
        _stream = stream;
        _aiAutoApply = aiAutoApply;
    }

    /// <summary>
    /// Gets all configured channels.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<Channel>>> GetAll(CancellationToken cancellationToken)
    {
        return await _channels.GetAllAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a channel by identifier.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Channel>> Get(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(id, cancellationToken);
        return channel is null ? NotFound() : channel;
    }

    /// <summary>
    /// Gets channels that currently have one or more active IPTV viewers.
    /// </summary>
    [HttpGet("on-air")]
    public ActionResult<object> GetOnAir()
    {
        return Ok(new { channels = _stream.GetActiveStreams() });
    }

    /// <summary>
    /// Gets the playout item currently airing on a channel, if any.
    /// </summary>
    [HttpGet("{id:guid}/now-playing")]
    public async Task<ActionResult<object>> GetNowPlaying(Guid id, CancellationToken cancellationToken)
    {
        if (await _channels.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        var current = await _stream.GetCurrentItemAsync(id, cancellationToken);
        return Ok(new { item = current });
    }

    /// <summary>
    /// Creates a new TV or movie channel.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Channel>> Create([FromBody] ChannelUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _channels.CreateAsync(request.ToChannel(), cancellationToken);
            _aiAutoApply.QueueAutoApplyForChannel(created.Id);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing TV or movie channel.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Channel>> Update(Guid id, [FromBody] ChannelUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _channels.UpdateAsync(id, request.ToChannel(), cancellationToken);
            return updated is null ? NotFound() : updated;
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a channel.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await _channels.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }
}
