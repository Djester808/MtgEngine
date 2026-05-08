using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MtgEngine.Api.Controllers;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class ForumControllerTests
{
    private readonly Mock<IForumService> _forumMock;
    private readonly ForumController _controller;

    private const string TestUserId = "11111111-1111-1111-1111-111111111111";
    private const string TestUsername = "player1";

    public ForumControllerTests()
    {
        _forumMock = new Mock<IForumService>();
        _controller = new ForumController(_forumMock.Object);
        SetUser(TestUserId, TestUsername);
    }

    private void SetUser(string userId, string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, username),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    private static ForumPostSummaryDto MakePostSummary(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DeckId = Guid.NewGuid(),
        AuthorUsername = TestUsername,
        DeckName = "Test Deck",
        DeckFormat = "commander",
        CardCount = 100,
        CommentCount = 0,
        PublishedAt = DateTime.UtcNow,
    };

    private static ForumPostDetailDto MakePostDetail(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DeckId = Guid.NewGuid(),
        AuthorId = TestUserId,
        AuthorUsername = TestUsername,
        DeckName = "Test Deck",
        DeckFormat = "commander",
        PublishedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Cards = [],
        Comments = [],
    };

    private static ForumCommentDto MakeComment(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        AuthorId = TestUserId,
        AuthorUsername = TestUsername,
        Content = "Great deck!",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── GetPosts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPosts_ReturnsOkWithAllPosts()
    {
        var posts = new[] { MakePostSummary(), MakePostSummary() };
        _forumMock.Setup(s => s.GetAllPostsAsync()).ReturnsAsync(posts);

        var result = await _controller.GetPosts();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(posts);
    }

    [Fact]
    public async Task GetPosts_ReturnsEmptyArrayWhenNoPosts()
    {
        _forumMock.Setup(s => s.GetAllPostsAsync()).ReturnsAsync([]);

        var result = await _controller.GetPosts();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        (ok.Value as ForumPostSummaryDto[]).Should().BeEmpty();
    }

    // ── GetPost ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPost_ReturnsOkWhenFound()
    {
        var postId = Guid.NewGuid();
        var detail = MakePostDetail(postId);
        _forumMock.Setup(s => s.GetPostAsync(postId)).ReturnsAsync(detail);

        var result = await _controller.GetPost(postId);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(detail);
    }

    [Fact]
    public async Task GetPost_ReturnsNotFoundWhenPostDoesNotExist()
    {
        var postId = Guid.NewGuid();
        _forumMock.Setup(s => s.GetPostAsync(postId)).ReturnsAsync((ForumPostDetailDto?)null);

        var result = await _controller.GetPost(postId);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ── PublishDeck ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishDeck_ReturnsOkWithCreatedPost()
    {
        var request = new PublishDeckRequest(Guid.NewGuid(), "A fun deck");
        var summary = MakePostSummary();
        _forumMock.Setup(s => s.PublishDeckAsync(TestUserId, TestUsername, request)).ReturnsAsync(summary);

        var result = await _controller.PublishDeck(request);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(summary);
    }

    [Fact]
    public async Task PublishDeck_ReturnsNotFoundWhenDeckDoesNotExist()
    {
        var request = new PublishDeckRequest(Guid.NewGuid());
        _forumMock.Setup(s => s.PublishDeckAsync(TestUserId, TestUsername, request))
                  .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.PublishDeck(request);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── DeletePost ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePost_ReturnsNoContentOnSuccess()
    {
        var postId = Guid.NewGuid();
        _forumMock.Setup(s => s.DeletePostAsync(postId, TestUserId)).ReturnsAsync(true);

        var result = await _controller.DeletePost(postId);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeletePost_ReturnsNotFoundWhenPostDoesNotExistOrUserNotOwner()
    {
        var postId = Guid.NewGuid();
        _forumMock.Setup(s => s.DeletePostAsync(postId, TestUserId)).ReturnsAsync(false);

        var result = await _controller.DeletePost(postId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeletePost_PassesUserIdFromClaimsToService()
    {
        var postId = Guid.NewGuid();
        _forumMock.Setup(s => s.DeletePostAsync(postId, TestUserId)).ReturnsAsync(true);

        await _controller.DeletePost(postId);

        _forumMock.Verify(s => s.DeletePostAsync(postId, TestUserId), Times.Once);
    }

    // ── AddComment ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddComment_ReturnsOkWithComment()
    {
        var postId = Guid.NewGuid();
        var request = new CreateCommentRequest("Nice deck!");
        var comment = MakeComment();
        _forumMock.Setup(s => s.AddCommentAsync(postId, TestUserId, TestUsername, request)).ReturnsAsync(comment);

        var result = await _controller.AddComment(postId, request);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(comment);
    }

    [Fact]
    public async Task AddComment_ReturnsBadRequestWhenContentIsEmpty()
    {
        var postId = Guid.NewGuid();
        var request = new CreateCommentRequest("");

        var result = await _controller.AddComment(postId, request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddComment_ReturnsBadRequestWhenContentIsWhitespace()
    {
        var postId = Guid.NewGuid();
        var request = new CreateCommentRequest("   ");

        var result = await _controller.AddComment(postId, request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddComment_ReturnsNotFoundWhenPostDoesNotExist()
    {
        var postId = Guid.NewGuid();
        var request = new CreateCommentRequest("Nice deck!");
        _forumMock.Setup(s => s.AddCommentAsync(postId, TestUserId, TestUsername, request))
                  .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.AddComment(postId, request);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── UpdateComment ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateComment_ReturnsOkWithUpdatedComment()
    {
        var postId = Guid.NewGuid();
        var commentId = Guid.NewGuid();
        var request = new UpdateCommentRequest("Updated content");
        var comment = MakeComment(commentId);
        _forumMock.Setup(s => s.UpdateCommentAsync(postId, commentId, TestUserId, request)).ReturnsAsync(comment);

        var result = await _controller.UpdateComment(postId, commentId, request);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(comment);
    }

    [Fact]
    public async Task UpdateComment_ReturnsBadRequestWhenContentIsEmpty()
    {
        var request = new UpdateCommentRequest("");

        var result = await _controller.UpdateComment(Guid.NewGuid(), Guid.NewGuid(), request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateComment_ReturnsNotFoundWhenCommentNotFound()
    {
        var postId = Guid.NewGuid();
        var commentId = Guid.NewGuid();
        var request = new UpdateCommentRequest("Updated");
        _forumMock.Setup(s => s.UpdateCommentAsync(postId, commentId, TestUserId, request))
                  .ReturnsAsync((ForumCommentDto?)null);

        var result = await _controller.UpdateComment(postId, commentId, request);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ── DeleteComment ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteComment_ReturnsNoContentOnSuccess()
    {
        var postId = Guid.NewGuid();
        var commentId = Guid.NewGuid();
        _forumMock.Setup(s => s.DeleteCommentAsync(postId, commentId, TestUserId)).ReturnsAsync(true);

        var result = await _controller.DeleteComment(postId, commentId);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteComment_ReturnsNotFoundWhenCommentDoesNotExist()
    {
        var postId = Guid.NewGuid();
        var commentId = Guid.NewGuid();
        _forumMock.Setup(s => s.DeleteCommentAsync(postId, commentId, TestUserId)).ReturnsAsync(false);

        var result = await _controller.DeleteComment(postId, commentId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteComment_PassesUserIdFromClaimsToService()
    {
        var postId = Guid.NewGuid();
        var commentId = Guid.NewGuid();
        _forumMock.Setup(s => s.DeleteCommentAsync(postId, commentId, TestUserId)).ReturnsAsync(true);

        await _controller.DeleteComment(postId, commentId);

        _forumMock.Verify(s => s.DeleteCommentAsync(postId, commentId, TestUserId), Times.Once);
    }
}
