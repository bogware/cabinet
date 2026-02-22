using Cabinet.API.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Middleware;

public class ErrorHandlingMiddlewareTests
{
    private readonly Mock<ILogger<ErrorHandlingMiddleware>> _logger = new();

    [Fact]
    public async Task InvokeAsync_NoException_PassesThrough()
    {
        var middleware = new ErrorHandlingMiddleware(_ => Task.CompletedTask, _logger.Object);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_ArgumentException_Returns400()
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new ArgumentException("Invalid input"),
            _logger.Object);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(400);
        // WriteAsJsonAsync overrides the content type to application/json
        context.Response.ContentType.Should().Be("application/json; charset=utf-8");
    }

    [Fact]
    public async Task InvokeAsync_UnauthorizedAccessException_Returns401()
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new UnauthorizedAccessException("No access"),
            _logger.Object);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task InvokeAsync_KeyNotFoundException_Returns404()
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new KeyNotFoundException("Not found"),
            _logger.Object);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task InvokeAsync_GenericException_Returns500()
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new InvalidOperationException("Something went wrong"),
            _logger.Object);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task InvokeAsync_GenericException_DoesNotLeakDetails()
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new InvalidOperationException("Sensitive internal details"),
            _logger.Object);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().NotContain("Sensitive internal details");
        body.Should().Contain("An unexpected error occurred");
    }

    [Fact]
    public async Task InvokeAsync_ClientException_IncludesExceptionMessage()
    {
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new ArgumentException("Missing required field: name"),
            _logger.Object);

        var context = CreateContext();
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        // Client error messages are safe to expose — helps API consumers debug issues
        body.Should().Contain("Invalid request parameters:");
        body.Should().Contain("Missing required field: name");
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Items["RequestId"] = "test-request-123";
        return context;
    }
}
