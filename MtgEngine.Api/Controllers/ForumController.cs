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

    [HttpGet]
    public async Task<ActionResult<ForumPostSummaryDto[]>> GetPosts()
    {
        var posts = await _forum.GetAllPostsAsync();
        return Ok(posts);
    }

    [HttpGet("{postId:guid}")]
    public async Task<ActionResult<ForumPostDetailDto>> GetPost(Guid postId)
    {
        var post = await _forum.GetPostAsync(postId);
        if (post == null) return NotFound();
        return Ok(post);
    }

    // ---- Auth-required write endpoints ----

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<ForumPostSummaryDto>> PublishDeck([FromBody] PublishDeckRequest request)
    {
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
        if (!success) return NotFound();
        return NoContent();
    }

    [Authorize]
    [HttpPost("{postId:guid}/comments")]
    public async Task<ActionResult<ForumCommentDto>> AddComment(Guid postId, [FromBody] CreateCommentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Comment content is required");

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

        var comment = await _forum.UpdateCommentAsync(postId, commentId, UserId, request);
        if (comment == null) return NotFound();
        return Ok(comment);
    }

    [Authorize]
    [HttpDelete("{postId:guid}/comments/{commentId:guid}")]
    public async Task<ActionResult> DeleteComment(Guid postId, Guid commentId)
    {
        var success = await _forum.DeleteCommentAsync(postId, commentId, UserId);
        if (!success) return NotFound();
        return NoContent();
    }
}
