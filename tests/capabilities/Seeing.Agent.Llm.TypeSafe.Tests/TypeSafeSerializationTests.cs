using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.TypeSafe.Clients;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Llm.TypeSafe.Tests;

/// <summary>
/// Decision DTO ↔ TypeSafe wire 的序列化契约。
/// <para>wire 键名必须保持小写（<c>state/model/questions/type/instructions/criteria</c>
/// 与 <c>noul/choice/score/legend/probabilities/confidence/usage.input_tokens/release_date</c>）：
/// 能力端口 DTO 不带 wire 注解，协议字段名是本包的私有事实。</para>
/// </summary>
public class TypeSafeSerializationTests
{
    private static JsonSerializerOptions Opts => TypeSafeWireMap.JsonOptions;

    [Fact]
    public void Serialize_StateAsString_WritesRawString()
    {
        var wire = TypeSafeWireMap.FromRequest(
            new DecisionRequest { State = "hello", Model = "jev-latest" }, "jev-latest");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(wire, Opts));
        doc.RootElement.GetProperty("state").GetString().Should().Be("hello");
        doc.RootElement.GetProperty("model").GetString().Should().Be("jev-latest");
    }

    [Fact]
    public void Serialize_StateAsObject_WritesObject()
    {
        var request = new DecisionRequest
        {
            State = new Dictionary<string, object?> { ["text"] = "hi", ["level"] = 3 },
            Model = "jev-latest"
        };

        var wire = TypeSafeWireMap.FromRequest(request, "jev-latest");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(wire, Opts));

        var state = doc.RootElement.GetProperty("state");
        state.GetProperty("text").GetString().Should().Be("hi");
        state.GetProperty("level").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Serialize_StateAsJsonElement_WritesVerbatim()
    {
        using var state = JsonDocument.Parse("""{"ticket":"x","tags":["a","b"]}""");
        var wire = TypeSafeWireMap.FromRequest(
            new DecisionRequest { State = state.RootElement.Clone(), Model = "m" }, "m");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(wire, Opts));
        var element = doc.RootElement.GetProperty("state");
        element.GetProperty("ticket").GetString().Should().Be("x");
        element.GetProperty("tags").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Serialize_ThreeQuestionTypes_PreserveTypeAndCriteria()
    {
        var request = new DecisionRequest
        {
            Model = "jev-latest",
            State = "x",
            Questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
            {
                ["isHappy"] = new DecisionQuestion
                {
                    Type = DecisionQuestionTypes.Noul,
                    Instructions = "Is the user happy?"
                },
                ["mood"] = new DecisionQuestion
                {
                    Type = DecisionQuestionTypes.Choice,
                    Criteria = new Dictionary<string, string> { ["a"] = "Happy", ["b"] = "Sad" }
                },
                ["rating"] = new DecisionQuestion
                {
                    Type = DecisionQuestionTypes.Score,
                    Criteria = new object[] { "low", "high" }
                }
            }
        };

        using var doc = JsonDocument.Parse(
            JsonSerializer.Serialize(TypeSafeWireMap.FromRequest(request, "jev-latest"), Opts));

        var questions = doc.RootElement.GetProperty("questions");
        questions.GetProperty("isHappy").GetProperty("type").GetString().Should().Be("noul");
        questions.GetProperty("isHappy").GetProperty("instructions").GetString().Should().Be("Is the user happy?");
        questions.GetProperty("mood").GetProperty("type").GetString().Should().Be("choice");
        questions.GetProperty("mood").GetProperty("criteria").GetProperty("a").GetString().Should().Be("Happy");
        questions.GetProperty("rating").GetProperty("type").GetString().Should().Be("score");
        questions.GetProperty("rating").GetProperty("criteria").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Serialize_CriterionKeys_AreNotRenamed()
    {
        // choice 的候选 label 是数据键，不得被任何命名策略改写
        var request = new DecisionRequest
        {
            Model = "m",
            State = "s",
            Questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
            {
                ["Q1"] = new DecisionQuestion
                {
                    Type = DecisionQuestionTypes.Choice,
                    Criteria = new Dictionary<string, string> { ["MyLabel"] = "v" }
                }
            }
        };

        using var doc = JsonDocument.Parse(
            JsonSerializer.Serialize(TypeSafeWireMap.FromRequest(request, "m"), Opts));

        doc.RootElement.GetProperty("questions").GetProperty("Q1")
            .GetProperty("criteria").GetProperty("MyLabel").GetString().Should().Be("v");
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

        var response = TypeSafeWireMap.ToResponse(
            JsonSerializer.Deserialize<DecisionResponseWire>(json, Opts)!);

        response.Model.Should().Be("jev-latest");
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

        var response = TypeSafeWireMap.ToResponse(
            JsonSerializer.Deserialize<DecisionResponseWire>(json, Opts)!);

        var answer = response.Answers["mood"];
        answer.Type.Should().Be("choice");
        answer.Choice.Should().Be("a");
        answer.Legend!["a"].Should().Be("Happy");
        answer.Probabilities!["a"].Should().BeApproximately(0.7, 1e-9);
        answer.Probabilities["b"].Should().BeApproximately(0.3, 1e-9);
    }

    [Fact]
    public void Deserialize_ScoreAnswer_ReadsScore()
    {
        const string json = """{ "answers": { "rating": { "type": "score", "score": 4.2 } } }""";

        var response = TypeSafeWireMap.ToResponse(
            JsonSerializer.Deserialize<DecisionResponseWire>(json, Opts)!);

        var answer = response.Answers["rating"];
        answer.Type.Should().Be("score");
        answer.Score.Should().BeApproximately(4.2, 1e-9);
    }

    [Fact]
    public void Deserialize_ModelsResponse_MapsToDecisionModels()
    {
        const string json = """
        { "models": [ { "name": "jev-latest", "description": "default", "release_date": "2026-01-01" } ] }
        """;

        var models = TypeSafeWireMap.ToDecisionModels(
            JsonSerializer.Deserialize<ModelsResponseWire>(json, Opts)!);

        models.Should().ContainSingle();
        models[0].Name.Should().Be("jev-latest");
        models[0].Description.Should().Be("default");
        models[0].ReleaseDate.Should().Be("2026-01-01");
    }

    [Fact]
    public void ToModelConfig_MarksDecisionTypeAndOwnsProvider()
    {
        var config = TypeSafeWireMap.ToModelConfig(
            new DecisionModel { Name = "jev-latest", Description = "default", ReleaseDate = "2026-01-01" },
            "typesafe");

        config.Id.Should().Be("jev-latest");
        config.Provider.Should().Be("typesafe");
        config.Types.Should().Equal(ModelType.Decision);
        config.Metadata!["description"].Should().Be("default");
        config.Metadata["releaseDate"].Should().Be("2026-01-01");
    }

    [Fact]
    public void ToModelConfig_WithoutOptionalFields_LeavesMetadataNull()
    {
        var config = TypeSafeWireMap.ToModelConfig(new DecisionModel { Name = "jev" }, "typesafe");

        config.Metadata.Should().BeNull();
    }

    [Theory]
    [InlineData(DecisionQuestionTypes.Noul, "noul")]
    [InlineData(DecisionQuestionTypes.Choice, "choice")]
    [InlineData(DecisionQuestionTypes.Score, "score")]
    public void QuestionTypes_AreWireNames(string constant, string expected)
    {
        constant.Should().Be(expected);
    }

    [Fact]
    public void DecisionConfigKey_IsLowerCamelWireName()
    {
        // 配置端能力键由 LlmCapabilityMap 投影：typesafe 的 provider 配置写 "decision"
        LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.Decision).Should().Be("decision");
    }

    [Fact]
    public void Exception_PreservesStatusCodeBodyMessageAndInner()
    {
        var inner = new IOException("boom");

        var exception = new DecisionProtocolException(429, "{\"error\":\"rate\"}", "限流", inner);

        exception.StatusCode.Should().Be(429);
        exception.ResponseBody.Should().Be("{\"error\":\"rate\"}");
        exception.Message.Should().Be("限流");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
