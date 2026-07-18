using FluentAssertions;
using Seeing.Agent.Memory.Core.Index;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Index;

public class RrfFusionTests
{
    [Fact]
    public void Fuse_MergesResults_Correctly()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 0.95),
            ("doc2.md", 0.85),
            ("doc3.md", 0.75)
        };

        var keywordResults = new List<(string Path, double Score)>
        {
            ("doc2.md", 5.2),
            ("doc4.md", 4.8),
            ("doc1.md", 3.5)
        };

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        results.Should().NotBeEmpty();
        results.Count.Should().Be(4); // doc1, doc2, doc3, doc4

        // doc1 and doc2 appear in both lists, should have higher scores
        var doc1 = results.First(r => r.Path == "doc1.md");
        var doc2 = results.First(r => r.Path == "doc2.md");
        var doc3 = results.First(r => r.Path == "doc3.md");
        var doc4 = results.First(r => r.Path == "doc4.md");

        // Verify both scores are present for doc1 and doc2
        doc1.VectorRank.Should().Be(1);
        doc1.KeywordRank.Should().Be(3);
        doc1.VectorScore.Should().Be(0.95);
        doc1.KeywordScore.Should().Be(3.5);

        doc2.VectorRank.Should().Be(2);
        doc2.KeywordRank.Should().Be(1);
        doc2.VectorScore.Should().Be(0.85);
        doc2.KeywordScore.Should().Be(5.2);

        // doc3 only in vector results
        doc3.VectorRank.Should().Be(3);
        doc3.KeywordRank.Should().BeNull();
        doc3.VectorScore.Should().Be(0.75);
        doc3.KeywordScore.Should().Be(0);

        // doc4 only in keyword results
        doc4.VectorRank.Should().BeNull();
        doc4.KeywordRank.Should().Be(2);
        doc4.VectorScore.Should().Be(0);
        doc4.KeywordScore.Should().Be(4.8);

        // doc2 should rank highest (rank 1 in keyword, rank 2 in vector)
        // RRF score for doc2: 0.5/60+2 + 0.5/60+1 = 0.5/62 + 0.5/61
        var doc2RrfScore = 0.5 / (60 + 2) + 0.5 / (60 + 1);
        doc2.Score.Should().BeApproximately(doc2RrfScore, 0.0001);

        // Results should be sorted by score descending
        results.Should().BeInDescendingOrder(r => r.Score);
    }

    [Fact]
    public void Fuse_EmptyLists_ReturnsEmpty()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>();
        var keywordResults = new List<(string Path, double Score)>();

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public void Fuse_VectorOnly_ReturnsVectorResults()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 0.9),
            ("doc2.md", 0.8),
            ("doc3.md", 0.7)
        };

        var keywordResults = new List<(string Path, double Score)>();

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        results.Should().HaveCount(3);
        results.All(r => r.VectorRank.HasValue).Should().BeTrue();
        results.All(r => r.KeywordRank.HasValue).Should().BeFalse();

        // Verify ranking
        results[0].VectorRank.Should().Be(1);
        results[1].VectorRank.Should().Be(2);
        results[2].VectorRank.Should().Be(3);

        // Verify scores
        results[0].VectorScore.Should().Be(0.9);
        results[1].VectorScore.Should().Be(0.8);
        results[2].VectorScore.Should().Be(0.7);

        // RRF score: 0.5/(60+rank)
        results[0].Score.Should().BeApproximately(0.5 / 61, 0.0001);
        results[1].Score.Should().BeApproximately(0.5 / 62, 0.0001);
        results[2].Score.Should().BeApproximately(0.5 / 63, 0.0001);
    }

    [Fact]
    public void Fuse_KeywordOnly_ReturnsKeywordResults()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>();
        var keywordResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 5.0),
            ("doc2.md", 4.5),
            ("doc3.md", 4.0)
        };

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        results.Should().HaveCount(3);
        results.All(r => r.KeywordRank.HasValue).Should().BeTrue();
        results.All(r => r.VectorRank.HasValue).Should().BeFalse();

        // Verify ranking
        results[0].KeywordRank.Should().Be(1);
        results[1].KeywordRank.Should().Be(2);
        results[2].KeywordRank.Should().Be(3);

        // Verify scores
        results[0].KeywordScore.Should().Be(5.0);
        results[1].KeywordScore.Should().Be(4.5);
        results[2].KeywordScore.Should().Be(4.0);

        // RRF score with keyword weight = 0.5: 0.5/(60+rank)
        results[0].Score.Should().BeApproximately(0.5 / 61, 0.0001);
        results[1].Score.Should().BeApproximately(0.5 / 62, 0.0001);
        results[2].Score.Should().BeApproximately(0.5 / 63, 0.0001);
    }

    [Fact]
    public void Fuse_CustomWeight_WorksCorrectly()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 0.9)
        };

        var keywordResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 5.0)
        };

        // Act - Use vector weight 0.7 (keyword weight 0.3)
        var results = RrfFusion.Fuse(vectorResults, keywordResults, vectorWeight: 0.7);

        // Assert
        results.Should().HaveCount(1);
        var doc1 = results[0];

        doc1.VectorRank.Should().Be(1);
        doc1.KeywordRank.Should().Be(1);
        doc1.VectorScore.Should().Be(0.9);
        doc1.KeywordScore.Should().Be(5.0);

        // RRF score: 0.7/(60+1) + 0.3/(60+1) = 1/(61) = 0.01639...
        var expectedScore = 0.7 / 61 + 0.3 / 61;
        doc1.Score.Should().BeApproximately(expectedScore, 0.0001);
    }

    [Fact]
    public void Fuse_BothListsHaveSamePath_MergesScores()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>
        {
            ("shared.md", 0.95),
            ("vector-only.md", 0.85)
        };

        var keywordResults = new List<(string Path, double Score)>
        {
            ("shared.md", 6.5),
            ("keyword-only.md", 5.0)
        };

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        results.Should().HaveCount(3);

        var shared = results.First(r => r.Path == "shared.md");
        var vectorOnly = results.First(r => r.Path == "vector-only.md");
        var keywordOnly = results.First(r => r.Path == "keyword-only.md");

        // shared.md should have both ranks
        shared.VectorRank.Should().Be(1);
        shared.KeywordRank.Should().Be(1);
        shared.VectorScore.Should().Be(0.95);
        shared.KeywordScore.Should().Be(6.5);

        // shared should have highest score (rank 1 in both)
        shared.Score.Should().BeGreaterThan(vectorOnly.Score);
        shared.Score.Should().BeGreaterThan(keywordOnly.Score);

        // vector-only should have vector rank but no keyword rank
        vectorOnly.VectorRank.Should().Be(2);
        vectorOnly.KeywordRank.Should().BeNull();

        // keyword-only should have keyword rank but no vector rank
        keywordOnly.VectorRank.Should().BeNull();
        keywordOnly.KeywordRank.Should().Be(2);
    }

    [Fact]
    public void Fuse_InvalidWeight_ThrowsException()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)> { ("doc.md", 0.5) };
        var keywordResults = new List<(string Path, double Score)> { ("doc.md", 1.0) };

        // Act & Assert
        var act1 = () => RrfFusion.Fuse(vectorResults, keywordResults, vectorWeight: -0.1);
        act1.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("vectorWeight");

        var act2 = () => RrfFusion.Fuse(vectorResults, keywordResults, vectorWeight: 1.1);
        act2.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("vectorWeight");
    }

    [Fact]
    public void Fuse_InvalidK_ThrowsException()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)> { ("doc.md", 0.5) };
        var keywordResults = new List<(string Path, double Score)> { ("doc.md", 1.0) };

        // Act & Assert
        var act = () => RrfFusion.Fuse(vectorResults, keywordResults, k: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("k");

        var actNegative = () => RrfFusion.Fuse(vectorResults, keywordResults, k: -1);
        actNegative.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("k");
    }

    [Fact]
    public void Fuse_CustomK_WorksCorrectly()
    {
        // Arrange
        var vectorResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 0.9)
        };

        var keywordResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 5.0)
        };

        // Act - Use K=10 (smaller smoothing factor)
        var results = RrfFusion.Fuse(vectorResults, keywordResults, k: 10);

        // Assert
        results.Should().HaveCount(1);

        // RRF score with K=10: 0.5/(10+1) + 0.5/(10+1) = 1/11
        var expectedScore = 0.5 / 11 + 0.5 / 11;
        results[0].Score.Should().BeApproximately(expectedScore, 0.0001);
    }

    [Fact]
    public void Fuse_LargeLists_HandlesCorrectly()
    {
        // Arrange
        var vectorResults = Enumerable.Range(1, 100)
            .Select(i => ($"doc{i}.md", 1.0 - i * 0.001))
            .ToList();

        var keywordResults = Enumerable.Range(50, 100)
            .Select(i => ($"doc{i}.md", 10.0 - i * 0.01))
            .ToList();

        // Act
        var results = RrfFusion.Fuse(
            vectorResults.Cast<(string, double)>().ToList(),
            keywordResults.Cast<(string, double)>().ToList()
        );

        // Assert
        results.Should().HaveCount(149); // docs 1-149 (1-49 only vector, 50-149 both, 100-149 only keyword)
        
        // Results should be sorted by score
        results.Should().BeInDescendingOrder(r => r.Score);

        // Docs in both lists (50-149) should generally rank higher
        var top10 = results.Take(10).ToList();
        top10.All(r => r.VectorRank.HasValue && r.KeywordRank.HasValue).Should().BeTrue();
    }

    [Fact]
    public void Fuse_PrioritizesHigherRanks_Correctly()
    {
        // Arrange - doc1 ranked 1 in both, doc2 ranked 1 in vector only, doc3 ranked 1 in keyword only
        var vectorResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 0.95),  // rank 1
            ("doc2.md", 0.90)   // rank 2
        };

        var keywordResults = new List<(string Path, double Score)>
        {
            ("doc1.md", 5.0),   // rank 1
            ("doc3.md", 4.5)    // rank 2
        };

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        var doc1 = results.First(r => r.Path == "doc1.md");
        var doc2 = results.First(r => r.Path == "doc2.md");
        var doc3 = results.First(r => r.Path == "doc3.md");

        // doc1 has highest score (rank 1 in both)
        doc1.Score.Should().BeGreaterThan(doc2.Score);
        doc1.Score.Should().BeGreaterThan(doc3.Score);

        // doc2 and doc3 both have rank 1 in one index, should have similar scores
        Math.Abs(doc2.Score - doc3.Score).Should().BeLessThan(0.001);
    }
}
