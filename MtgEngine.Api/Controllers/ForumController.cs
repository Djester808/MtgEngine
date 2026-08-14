using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ForumController : ControllerBase
{
    private readonly IForumService _forum;

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token");

    private string Username =>
        User.FindFirstValue(ClaimTypes.Name)
        ?? throw new InvalidOperationException("Username claim missing from token");

    public ForumController(IForumService forum)
    {
        _forum = forum;
    }

    // ---- Public read endpoints ----

    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<ForumPostSummaryDto[]>> GetPosts()
    {
        var posts = await _forum.GetAllPostsAsync();
        return Ok(posts);
    }

    [AllowAnonymous]
    [HttpGet("{postId:guid}")]
    public async Task<ActionResult<ForumPostDetailDto>> GetPost(Guid postId)
    {
        var post = await _forum.GetPostAsync(postId);
        if (post == null)
            return NotFound();
        return Ok(post);
    }

    // ---- Auth-required write endpoints ----

    // SQLite does not enforce the model's HasMaxLength, so the caps live here.
    private const int MaxDescriptionLength = 2000;
    private const int MaxCommentLength = 4000;

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<ForumPostSummaryDto>> PublishDeck([FromBody] PublishDeckRequest request)
    {
        if (request.Description is { Length: > MaxDescriptionLength })
            return BadRequest($"Description must be at most {MaxDescriptionLength} characters");

        try
        {
            var post = await _forum.PublishDeckAsync(UserId, Username, request);
            return Ok(post);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Deck not found");
        }
    }

    [Authorize]
    [HttpDelete("{postId:guid}")]
    public async Task<ActionResult> DeletePost(Guid postId)
    {
        var success = await _forum.DeletePostAsync(postId, UserId);
        if (!success)
            return NotFound();
        return NoContent();
    }

    [Authorize]
    [HttpPost("{postId:guid}/comments")]
    public async Task<ActionResult<ForumCommentDto>> AddComment(Guid postId, [FromBody] CreateCommentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Comment content is required");
        if (request.Content.Length > MaxCommentLength)
            return BadRequest($"Comment must be at most {MaxCommentLength} characters");

        try
        {
            var comment = await _forum.AddCommentAsync(postId, UserId, Username, request);
            return Ok(comment);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Forum post not found");
        }
    }

    [Authorize]
    [HttpPut("{postId:guid}/comments/{commentId:guid}")]
    public async Task<ActionResult<ForumCommentDto>> UpdateComment(Guid postId, Guid commentId, [FromBody] UpdateCommentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Comment content is required");
        if (request.Content.Length > MaxCommentLength)
            return BadRequest($"Comment must be at most {MaxCommentLength} characters");

        var comment = await _forum.UpdateCommentAsync(postId, commentId, UserId, request);
        if (comment == null)
            return NotFound();
        return Ok(comment);
    }

    [Authorize]
    [HttpDelete("{postId:guid}/comments/{commentId:guid}")]
    public async Task<ActionResult> DeleteComment(Guid postId, Guid commentId)
    {
        var success = await _forum.DeleteCommentAsync(postId, commentId, UserId);
        if (!success)
            return NotFound();
        return NoContent();
    }
}
