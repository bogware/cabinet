using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Query.NaturalLanguage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public class QueryControllerTests
{
    private readonly Mock<IQueryEngine> _queryEngine;
    private readonly Mock<IQueryParser> _queryParser;
    private readonly Mock<ISavedQueryStorage> _savedQueryStorage;
    private readonly Mock<IAuditLogger> _auditLogger;
    private readonly QueryController _controller;

    public QueryControllerTests()
    {
        _queryEngine = new Mock<IQueryEngine>();
        _queryParser = new Mock<IQueryParser>();
        _savedQueryStorage = new Mock<ISavedQueryStorage>();
        _auditLogger = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<QueryController>>();

        _controller = new QueryController(
            _queryEngine.Object,
            _queryParser.Object,
            _savedQueryStorage.Object,
            _auditLogger.Object,
            logger.Object);

        SetupHttpContext(_controller);
    }

    [Fact]
    public async Task ExecuteQuery_EmptyQuery_ReturnsBadRequest()
    {
        var request = new QueryRequest { Query = "" };

        var result = await _controller.ExecuteQuery(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ExecuteQuery_TooLongQuery_ReturnsBadRequest()
    {
        var request = new QueryRequest { Query = new string('x', 10_001) };

        var result = await _controller.ExecuteQuery(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ExecuteQuery_ValidQuery_ReturnsResults()
    {
        var request = new QueryRequest { Query = "find errors" };
        _queryEngine.Setup(e => e.ExecuteAsync(It.IsAny<QueryExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult { Events = new List<dynamic> { new LogEvent { RawLine = "test" } } });

        var result = await _controller.ExecuteQuery(request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Query.Should().Be("find errors");
        response.Count.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteQuery_ParseError_ReturnsBadRequest()
    {
        var request = new QueryRequest { Query = "invalid ||| query" };
        _queryEngine.Setup(e => e.ExecuteAsync(It.IsAny<QueryExecutionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueryParseException("Unexpected token"));

        var result = await _controller.ExecuteQuery(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void ValidateQuery_EmptyQuery_ReturnsBadRequest()
    {
        var request = new QueryRequest { Query = "" };

        var result = _controller.ValidateQuery(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void ValidateQuery_TooLong_ReturnsBadRequest()
    {
        var request = new QueryRequest { Query = new string('x', 10_001) };

        var result = _controller.ValidateQuery(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void ValidateQuery_ValidQuery_ReturnsIsValid()
    {
        var request = new QueryRequest { Query = "find errors" };
        _queryParser.Setup(p => p.Parse("find errors"))
            .Returns(new QueryPlan { Operators = new List<QueryOperator>() });

        var result = _controller.ValidateQuery(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = okResult.Value.Should().BeOfType<ValidationResponse>().Subject;
        validation.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateQuery_InvalidSyntax_ReturnsIsValidFalse()
    {
        var request = new QueryRequest { Query = "bad query syntax" };
        _queryParser.Setup(p => p.Parse("bad query syntax"))
            .Throws(new QueryParseException("Parse error"));

        var result = _controller.ValidateQuery(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = okResult.Value.Should().BeOfType<ValidationResponse>().Subject;
        validation.IsValid.Should().BeFalse();
        validation.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExplainQuery_EmptyQuery_ReturnsBadRequest()
    {
        var request = new QueryRequest { Query = "" };

        var result = _controller.ExplainQuery(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void ExplainQuery_ValidQuery_ReturnsExplanation()
    {
        var request = new QueryRequest { Query = "find errors | stats count" };
        _queryParser.Setup(p => p.Parse("find errors | stats count"))
            .Returns(new QueryPlan
            {
                Operators = new List<QueryOperator>
                {
                    new StubOperator("search"),
                    new StubOperator("stats")
                }
            });

        var result = _controller.ExplainQuery(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var explanation = okResult.Value.Should().BeOfType<ExplainResponse>().Subject;
        explanation.StageCount.Should().Be(2);
        explanation.HasAggregation.Should().BeTrue();
        explanation.HasSearch.Should().BeTrue();
    }

    [Fact]
    public void GetSuggestions_NoFilter_ReturnsAllSuggestions()
    {
        var result = _controller.GetSuggestions();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var suggestions = okResult.Value.Should().BeAssignableTo<List<QuerySuggestion>>().Subject;
        suggestions.Should().NotBeEmpty();
        suggestions.Should().Contain(s => s.Text == "search");
        suggestions.Should().Contain(s => s.Text == "find");
    }

    [Fact]
    public void GetSuggestions_WithFilter_ReturnsMatching()
    {
        var result = _controller.GetSuggestions("st");

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var suggestions = okResult.Value.Should().BeAssignableTo<List<QuerySuggestion>>().Subject;
        suggestions.Should().Contain(s => s.Text == "stats");
        suggestions.Should().Contain(s => s.Text == "streamstats");
        suggestions.Should().NotContain(s => s.Text == "search");
    }

    [Fact]
    public async Task SaveQuery_EmptyName_ReturnsBadRequest()
    {
        var request = new SaveQueryRequest { Name = "", Query = "find *" };

        var result = await _controller.SaveQuery(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SaveQuery_EmptyQuery_ReturnsBadRequest()
    {
        var request = new SaveQueryRequest { Name = "test", Query = "" };

        var result = await _controller.SaveQuery(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SaveQuery_ValidRequest_CreatesAndReturns201()
    {
        var request = new SaveQueryRequest { Name = "My Query", Query = "find errors" };
        _queryParser.Setup(p => p.Parse("find errors")).Returns(new QueryPlan());

        var result = await _controller.SaveQuery(request);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var savedQuery = createdResult.Value.Should().BeOfType<SavedQuery>().Subject;
        savedQuery.Name.Should().Be("My Query");
        savedQuery.Query.Should().Be("find errors");
    }

    [Fact]
    public async Task SaveQuery_InvalidSyntax_ReturnsBadRequest()
    {
        var request = new SaveQueryRequest { Name = "test", Query = "bad syntax" };
        _queryParser.Setup(p => p.Parse("bad syntax")).Throws(new QueryParseException("Parse error"));

        var result = await _controller.SaveQuery(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetSavedQuery_NotFound_Returns404()
    {
        _savedQueryStorage.Setup(s => s.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedQuery?)null);

        var result = await _controller.GetSavedQuery("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DeleteSavedQuery_Existing_ReturnsNoContent()
    {
        _savedQueryStorage.Setup(s => s.DeleteAsync("test-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.DeleteSavedQuery("test-id");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteSavedQuery_NotFound_Returns404()
    {
        _savedQueryStorage.Setup(s => s.DeleteAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DeleteSavedQuery("missing");

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ExportQuery_EmptyQuery_ReturnsBadRequest()
    {
        var request = new ExportRequest { Query = "" };

        var result = await _controller.ExportQuery(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ExportQuery_TooLongQuery_ReturnsBadRequest()
    {
        var request = new ExportRequest { Query = new string('x', 10_001) };

        var result = await _controller.ExportQuery(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    private static void SetupHttpContext(ControllerBase controller)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("ClientId", "test-hash"),
            new Claim(ClaimTypes.NameIdentifier, "test-client"),
            new Claim(ClaimTypes.Name, "test-user")
        }, "ApiKey"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    /// <summary>Stub operator for explain tests</summary>
    private sealed class StubOperator : QueryOperator
    {
        public override string Name { get; }
        public StubOperator(string name) { Name = name; }

        public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }
}
