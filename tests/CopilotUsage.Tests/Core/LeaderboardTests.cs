using CopilotUsage.Core.Calculations;

namespace CopilotUsage.Tests.Core;

public class LeaderboardTests
{
    [Fact]
    public void Ranks_highest_first_with_shares_and_fair_share_flags()
    {
        var board = Leaderboard.Rank(
            [new("low", 100m), new("high", 3800m), new("mid", 1900m)], fairShare: 1900m, poolLimit: 38_000m);

        Assert.Equal(["high", "mid", "low"], board.Select(e => e.Login));
        Assert.Equal([1, 2, 3], board.Select(e => e.Rank));
        Assert.Equal(10m, board[0].ShareOfPoolPercent);
        Assert.Equal(200m, board[0].FairSharePercent);
        Assert.True(board[0].OverFairShare);
        Assert.False(board[1].OverFairShare); // exactly 100% is not "over"
        Assert.Equal(100m, board[1].FairSharePercent);
    }

    [Fact]
    public void Ties_share_a_rank_and_the_next_rank_skips()
    {
        var board = Leaderboard.Rank([new("b", 50m), new("a", 50m), new("c", 10m), new("top", 99m)], 1900m, 1900m);
        Assert.Equal([1, 2, 2, 4], board.Select(e => e.Rank));
        Assert.Equal(["top", "a", "b", "c"], board.Select(e => e.Login));
    }

    [Fact]
    public void Zero_credit_users_are_ranked_last_together()
    {
        var board = Leaderboard.Rank([new("idle1", 0m), new("busy", 5m), new("idle2", 0m)], 1900m, 1900m);
        Assert.Equal(["busy", "idle1", "idle2"], board.Select(e => e.Login));
        Assert.Equal([1, 2, 2], board.Select(e => e.Rank));
    }

    [Fact]
    public void Zero_pool_or_zero_fair_share_gives_undefined_percentages_not_exceptions()
    {
        var board = Leaderboard.Rank([new("a", 10m)], fairShare: 0m, poolLimit: 0m);
        Assert.Null(board[0].ShareOfPoolPercent);
        Assert.Null(board[0].FairSharePercent);
        Assert.False(board[0].OverFairShare);
    }

    [Fact]
    public void Empty_input_gives_empty_board() =>
        Assert.Empty(Leaderboard.Rank([], 1900m, 1900m));

    [Fact]
    public void Negative_credits_are_clamped_to_zero() =>
        Assert.Equal(0m, Leaderboard.Rank([new("a", -5m)], 1900m, 1900m)[0].Credits);

    [Fact]
    public void Fair_share_percent_helper()
    {
        Assert.Equal(50m, Leaderboard.FairSharePercent(950m, 1900m));
        Assert.Null(Leaderboard.FairSharePercent(1m, 0m));
    }
}
