using thuvu.Models;

namespace thuvu.Tests.Models;

public class TokenTrackerTests
{
    [Fact]
    public void EstimateTokens_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, TokenTracker.EstimateTokens(""));
    }

    [Fact]
    public void EstimateTokens_NullString_ReturnsZero()
    {
        Assert.Equal(0, TokenTracker.EstimateTokens(null!));
    }

    [Fact]
    public void EstimateTokens_ShortString_ReturnsOne()
    {
        Assert.Equal(1, TokenTracker.EstimateTokens("hi"));
    }

    [Fact]
    public void EstimateTokens_4Chars_ReturnsOne()
    {
        Assert.Equal(1, TokenTracker.EstimateTokens("abcd"));
    }

    [Fact]
    public void EstimateTokens_5Chars_ReturnsTwo()
    {
        Assert.Equal(2, TokenTracker.EstimateTokens("abcde"));
    }

    [Fact]
    public void EstimateTokens_100Chars_Returns25()
    {
        Assert.Equal(25, TokenTracker.EstimateTokens(new string('x', 100)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    public void EstimateTokens_PredictableRounding(int charCount, int expectedTokens)
    {
        Assert.Equal(expectedTokens, TokenTracker.EstimateTokens(new string('a', charCount)));
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.System, 100);
        tracker.AddTokens(TokenCategory.User, 200);
        tracker.AddTokens(TokenCategory.Assistant, 300);
        tracker.AddTokens(TokenCategory.Tool, 400);
        Assert.Equal(1000, tracker.TotalTokens);

        tracker.Reset();

        Assert.Equal(0, tracker.TotalTokens);
        Assert.Equal(0, tracker.SystemTokens);
        Assert.Equal(0, tracker.UserTokens);
        Assert.Equal(0, tracker.AssistantTokens);
        Assert.Equal(0, tracker.ToolTokens);
    }

    [Fact]
    public void AddTokens_SystemCategory_IncrementsCorrectly()
    {
        var tracker = new TokenTracker { MaxContextLength = 10000 };
        tracker.AddTokens(TokenCategory.System, 500);
        Assert.Equal(500, tracker.SystemTokens);
        Assert.Equal(500, tracker.TotalTokens);
    }

    [Fact]
    public void AddTokens_UserCategory_IncrementsCorrectly()
    {
        var tracker = new TokenTracker { MaxContextLength = 10000 };
        tracker.AddTokens(TokenCategory.User, 300);
        Assert.Equal(300, tracker.UserTokens);
        Assert.Equal(300, tracker.TotalTokens);
    }

    [Fact]
    public void AddTokens_AssistantCategory_IncrementsCorrectly()
    {
        var tracker = new TokenTracker { MaxContextLength = 10000 };
        tracker.AddTokens(TokenCategory.Assistant, 200);
        Assert.Equal(200, tracker.AssistantTokens);
        Assert.Equal(200, tracker.TotalTokens);
    }

    [Fact]
    public void AddTokens_ToolCategory_IncrementsCorrectly()
    {
        var tracker = new TokenTracker { MaxContextLength = 10000 };
        tracker.AddTokens(TokenCategory.Tool, 400);
        Assert.Equal(400, tracker.ToolTokens);
        Assert.Equal(400, tracker.TotalTokens);
    }

    [Fact]
    public void AddTokens_MultipleCategories_Accumulates()
    {
        var tracker = new TokenTracker { MaxContextLength = 10000 };
        tracker.AddTokens(TokenCategory.System, 100);
        tracker.AddTokens(TokenCategory.User, 100);
        tracker.AddTokens(TokenCategory.Assistant, 100);
        tracker.AddTokens(TokenCategory.Tool, 100);

        Assert.Equal(400, tracker.TotalTokens);
    }

    [Fact]
    public void UpdateFromUsage_SetsCorrectCounts()
    {
        var tracker = new TokenTracker { MaxContextLength = 32768 };
        tracker.UpdateFromUsage(1000, 200, 1500);

        Assert.Equal(1200, tracker.TotalTokens);
        Assert.Equal(200, tracker.AssistantTokens);
        Assert.Equal(1000, tracker.LastPromptTokens);
    }

    [Fact]
    public void UpdateFromUsage_MultipleCalls_AccumulatesAssistant()
    {
        var tracker = new TokenTracker { MaxContextLength = 32768 };
        tracker.UpdateFromUsage(500, 100, 600);
        tracker.UpdateFromUsage(600, 150, 800);

        Assert.Equal(750, tracker.TotalTokens);
        Assert.Equal(250, tracker.AssistantTokens);
    }

    [Fact]
    public void UpdateFromUsage_WithUsageObject_UpdatesMaxContext()
    {
        var tracker = new TokenTracker { MaxContextLength = 32768 };
        var usage = new Usage
        {
            PromptTokens = 1000,
            CompletionTokens = 200,
            TotalTokens = 1200,
            MaxContextLength = 65536
        };

        tracker.UpdateFromUsage(usage);

        Assert.Equal(65536, tracker.MaxContextLength);
        Assert.Equal(1200, tracker.TotalTokens);
    }

    [Fact]
    public void UsagePercent_EmptyTracker_ReturnsZero()
    {
        var tracker = new TokenTracker { MaxContextLength = 32768 };
        Assert.Equal(0, tracker.UsagePercent);
    }

    [Fact]
    public void UsagePercent_HalfCapacity_ReturnsCorrect()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 500);
        Assert.Equal(0.5, tracker.UsagePercent);
    }

    [Fact]
    public void UsagePercent_ZeroMaxContext_ReturnsZero()
    {
        var tracker = new TokenTracker { MaxContextLength = 0 };
        tracker.AddTokens(TokenCategory.User, 500);
        Assert.Equal(0, tracker.UsagePercent);
    }

    [Fact]
    public void RemainingTokens_CalculatesCorrectly()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 700);
        Assert.Equal(300, tracker.RemainingTokens);
    }

    [Fact]
    public void RemainingTokens_OverCapacity_ReturnsZero()
    {
        var tracker = new TokenTracker { MaxContextLength = 100 };
        tracker.AddTokens(TokenCategory.User, 150);
        Assert.Equal(0, tracker.RemainingTokens);
    }

    [Fact]
    public void IsWarning_Below70Percent_ReturnsFalse()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 699);
        Assert.False(tracker.IsWarning);
    }

    [Fact]
    public void IsWarning_At70Percent_ReturnsTrue()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 700);
        Assert.True(tracker.IsWarning);
    }

    [Fact]
    public void IsCritical_Below85Percent_ReturnsFalse()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 840);
        Assert.False(tracker.IsCritical);
    }

    [Fact]
    public void IsCritical_At85Percent_ReturnsTrue()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 850);
        Assert.True(tracker.IsCritical);
    }

    [Fact]
    public void NeedsSummarization_At90Percent_ReturnsTrue()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 900);
        Assert.True(tracker.NeedsSummarization);
    }

    [Fact]
    public void NeedsSummarization_Below90Percent_ReturnsFalse()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 899);
        Assert.False(tracker.NeedsSummarization);
    }

    [Fact]
    public void NeedsTruncation_At95Percent_ReturnsTrue()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 950);
        Assert.True(tracker.NeedsTruncation);
    }

    [Fact]
    public void NeedsTruncation_Below95Percent_ReturnsFalse()
    {
        var tracker = new TokenTracker { MaxContextLength = 1000 };
        tracker.AddTokens(TokenCategory.User, 940);
        Assert.False(tracker.NeedsTruncation);
    }

    [Fact]
    public void GetCompactStatus_ReturnsFormattedString()
    {
        var tracker = new TokenTracker { MaxContextLength = 32768 };
        tracker.AddTokens(TokenCategory.User, 16384);

        var status = tracker.GetCompactStatus();
        Assert.Contains("50%", status);
        Assert.Contains("16384".Replace(",", ""), status.Replace(".", "").Replace(",", ""));
    }

    [Fact]
    public void Singleton_ReturnsSameInstance()
    {
        var instance1 = TokenTracker.Instance;
        var instance2 = TokenTracker.Instance;
        Assert.Same(instance1, instance2);
    }
}
