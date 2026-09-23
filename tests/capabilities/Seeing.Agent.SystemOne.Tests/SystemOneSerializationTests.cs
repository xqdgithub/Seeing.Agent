using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneSerializationTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Serialize_StateAsString_WritesRawString()
    {
        var request = new SystemOneRequest { State = "hello", Model = "jev-latest" };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("state").GetString().Should().Be("hello");
        doc.RootElement.GetProperty("model").GetString().Should().Be("jev-latest");
    }

    [Fact]
    public void Serialize_StateAsObject_WritesObject()
    {
        var request = new SystemOneRequest { State = new { text = "hi", level = 3 } };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        var state = doc.RootElement.GetProperty("state");
        state.GetProperty("text").GetString().Should().Be("hi");
        state.GetProperty("level").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Serialize_StateAsArray_WritesArray()
    {
        var request = new SystemOneRequest { State = new object[] { "a", 1 } };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        var state = doc.RootElement.GetProperty("state");
        state.ValueKind.Should().Be(JsonValueKind.Array);
        state.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Serialize_ThreeQuestionTypes_PreserveTypeAndCriteria()
    {
        var request = new SystemOneRequest
        {
            Model = "jev-latest",
            State = "x",
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                ["isHappy"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Noul,
                    Instructions = "Is the user happy?"
                },
                ["mood"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Choice,
                    Criteria = new Dictionary<string, string> { ["a"] = "Happy", ["b"] = "Sad" }
                },
                ["rating"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Score,
                    Criteria = new object[] { "low", "high" }
                }
            }
        };

        var json = JsonSerializer.Serialize(request, Opts);

        using var doc = JsonDocument.Parse(json);
        var questions = doc.RootElement.GetProperty("questions");
        questions.GetProperty("isHappy").GetProperty("type").GetString().Should().Be("noul");
        questions.GetProperty("isHappy").GetProperty("instructions").GetString().Should().Be("Is the user happy?");
        questions.GetProperty("mood").GetProperty("type").GetString().Should().Be("choice");
        questions.GetProperty("mood").GetProperty("criteria").GetProperty("a").GetString().Should().Be("Happy");
        questions.GetProperty("rating").GetProperty("type").GetString().Should().Be("score");
        questions.GetProperty("rating").GetProperty("criteria").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Deserialize_NoulAnswer_ReadsProbabilityConfidenceAndUsage()
    {
        const string json = """
        {
          "model": "jev-latest",
          "answers": {
            "isHappy": { "type": "noul", "noul": 0.83, "confidence": 0.91 }
          },
          "usage": { "input_tokens": 12, "output_tokens": 3 }
        }
        """;

        var response = JsonSerializer.Deserialize<SystemOneResponse>(json, Opts);

        response.Should().NotBeNull();
        response!.Model.Should().Be("jev-latest");
        var answer = response.Answers["isHappy"];
        answer.Type.Should().Be("noul");
        answer.Noul.Should().BeApproximately(0.83, 1e-9);
        answer.Confidence.Should().BeApproximately(0.91, 1e-9);
        response.Usage!.InputTokens.Should().Be(12);
        response.Usage.OutputTokens.Should().Be(3);
    }

    [Fact]
    public void Deserialize_ChoiceAnswer_ReadsChoiceLegendAndProbabilities()
    {
        const string json = """
        {
          "answers": {
            "mood": {
              "type": "choice",
              "choice": "a",
              "legend": { "a": "Happy", "b": "Sad" },
              "probabilities": { "a": 0.7, "b": 0.3 }
            }
          }
        }
        """;

        var response = JsonSerializer.Deserialize<SystemOneResponse>(json, Opts);

        var answer = response!.Answers["mood"];
        answer.Type.Should().Be("choice");
        answer.Choice.Should().Be("a");
        answer.Legend!["a"].Should().Be("Happy");
        answer.Probabilities!["a"].Should().BeApproximately(0.7, 1e-9);
        answer.Probabilities["b"].Should().BeApproximately(0.3, 1e-9);
    }

    [Fact]
    public void Deserialize_ScoreAnswer_ReadsScore()
    {
        const string json = """
        { "answers": { "rating": { "type": "score", "score": 4.2 } } }
        """;

        var response = JsonSerializer.Deserialize<SystemOneResponse>(json, Opts);

        var answer = response!.Answers["rating"];
        answer.Type.Should().Be("score");
        answer.Score.Should().BeApproximately(4.2, 1e-9);
    }

    [Fact]
    public void Deserialize_ProviderConfig_MapsJsonPropertyNames()
    {
        const string json = """
        {
          "id": "typesafe",
          "type": "typesafe",
          "name": "TypeSafe Jev",
          "baseURL": "https://example.test",
          "apiKey": "ts-123",
          "model": "jev-latest",
          "timeout": 15000,
          "max_retries": 3,
          "retry_base_delay": 250,
          "retry_max_delay": 4000
        }
        """;

        var config = JsonSerializer.Deserialize<SystemOneProviderConfig>(json, Opts);

        config.Should().NotBeNull();
        config!.Id.Should().Be("typesafe");
        config.Type.Should().Be("typesafe");
        config.Name.Should().Be("TypeSafe Jev");
        config.BaseUrl.Should().Be("https://example.test");
        config.ApiKey.Should().Be("ts-123");
        config.Model.Should().Be("jev-latest");
        config.Timeout.Should().Be(15000);
        config.MaxRetries.Should().Be(3);
        config.RetryBaseDelayMs.Should().Be(250);
        config.RetryMaxDelayMs.Should().Be(4000);
    }

    [Fact]
    public void ProviderConfig_Defaults_MatchContract()
    {
        var config = new SystemOneProviderConfig();

        config.Id.Should().Be("typesafe");
        config.Type.Should().Be(SystemOneProviderTypes.TypeSafe);
        config.Model.Should().Be("jev-latest");
        config.Timeout.Should().Be(10000);
        config.MaxRetries.Should().Be(2);
        config.RetryBaseDelayMs.Should().Be(500);
        config.RetryMaxDelayMs.Should().Be(5000);
    }

    [Theory]
    [InlineData("TypeSafe", "typesafe")]
    [InlineData("  TYPESAFE ", "typesafe")]
    [InlineData("other", "other")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_ReturnsCanonicalLowercase(string? input, string expected)
    {
        SystemOneProviderTypes.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Defaults_MatchContract()
    {
        SystemOneDefaults.DefaultBaseUrl.Should().Be("https://api.typesafe.ai");
        SystemOneDefaults.DefaultModel.Should().Be("jev-latest");
        SystemOneDefaults.EvaluatePath.Should().Be("/v1/systemone");
        SystemOneDefaults.ModelsPath.Should().Be("/v1/models");
        SystemOneDefaults.DefaultTimeoutMs.Should().Be(10000);
    }

    [Fact]
    public void Deserialize_ModelsResponse_ReadsModels()
    {
        const string json = """
        { "models": [ { "name": "jev-latest", "description": "default", "release_date": "2026-01-01" } ] }
        """;

        var response = JsonSerializer.Deserialize<SystemOneModelsResponse>(json, Opts);

        response!.Models.Should().HaveCount(1);
        response.Models[0].Name.Should().Be("jev-latest");
        response.Models[0].Description.Should().Be("default");
        response.Models[0].ReleaseDate.Should().Be("2026-01-01");
    }

    [Fact]
    public void Exception_PreservesStatusCodeBodyMessageAndInner()
    {
        var inner = new IOException("boom");

        var exception = new SystemOneException(429, "{\"error\":\"rate\"}", "限流", inner);

        exception.StatusCode.Should().Be(429);
        exception.ResponseBody.Should().Be("{\"error\":\"rate\"}");
        exception.Message.Should().Be("限流");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
