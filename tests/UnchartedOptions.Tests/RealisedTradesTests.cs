using UnchartedOptions.Core;

namespace UnchartedOptions.Tests;

public class RealisedTradesTests
{
    private static DateTimeOffset At(int d, int h) => new(2026, 8, d, h, 0, 0, TimeSpan.Zero);

    private static Fill F(string symbol, bool buy, decimal qty, decimal price, int d, int h) => new()
    {
        Symbol = symbol,
        IsBuy = buy,
        Quantity = qty,
        Price = price,
        At = At(d, h),
    };

    private const string Long = "SPY260903C00772000";
    private const string Short = "SPY260903C00777000";

    /// <summary>
    /// A round trip on a 10-lot spread. Opened for a 1.62 debit, closed for a 2.40 credit,
    /// so 0.78 per share on 10 contracts is 780 dollars realised.
    /// </summary>
    [Fact]
    public void A_completed_round_trip_realises_the_difference_between_debit_and_credit()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 10, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 10, price: 15.38m, d: 31, h: 14),
            F(Long,  buy: false, qty: 10, price: 18.10m, d: 2, h: 18),
            F(Short, buy: true,  qty: 10, price: 15.70m, d: 2, h: 18),
        ];

        RealisedTrade t = Assert.Single(RealisedTrades.FromFills(fills));

        Assert.Equal(780m, t.RealisedPnl);
        Assert.True(t.IsWin);
        Assert.Equal("SPY", t.Underlying);
        Assert.Equal("772C/777C", t.Structure);
        Assert.Equal(4, t.Fills);
    }

    [Fact]
    public void A_losing_round_trip_realises_a_negative_figure()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 5, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 5, price: 15.38m, d: 31, h: 14),
            F(Long,  buy: false, qty: 5, price: 16.00m, d: 2, h: 18),
            F(Short, buy: true,  qty: 5, price: 15.20m, d: 2, h: 18),
        ];

        RealisedTrade t = Assert.Single(RealisedTrades.FromFills(fills));

        Assert.Equal(-410m, t.RealisedPnl);
        Assert.False(t.IsWin);
    }

    /// <summary>
    /// The reason grouping is by underlying and expiry rather than by symbol. Netting one leg
    /// alone would report the long leg's loss and the short leg's gain as two separate trades.
    /// </summary>
    [Fact]
    public void The_two_legs_of_a_spread_net_into_one_trade_not_two()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 10, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 10, price: 15.38m, d: 31, h: 14),
            F(Long,  buy: false, qty: 10, price: 18.10m, d: 2, h: 18),
            F(Short, buy: true,  qty: 10, price: 15.70m, d: 2, h: 18),
        ];

        Assert.Single(RealisedTrades.FromFills(fills));
    }

    /// <summary>
    /// An open position has no realised figure. Reporting the cash paid so far as a loss would
    /// count every open spread as a losing closed trade.
    /// </summary>
    [Fact]
    public void A_position_that_is_still_open_is_not_reported_as_closed()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 10, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 10, price: 15.38m, d: 31, h: 14),
        ];

        Assert.Empty(RealisedTrades.FromFills(fills));
    }

    [Fact]
    public void A_partially_unwound_spread_is_not_reported_as_closed()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 10, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 10, price: 15.38m, d: 31, h: 14),
            F(Long,  buy: false, qty: 4, price: 18.10m, d: 2, h: 18),
        ];

        Assert.Empty(RealisedTrades.FromFills(fills));
    }

    [Fact]
    public void Separate_expiries_are_separate_trades()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 5, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 5, price: 15.38m, d: 31, h: 14),
            F(Long,  buy: false, qty: 5, price: 18.00m, d: 2, h: 18),
            F(Short, buy: true,  qty: 5, price: 15.50m, d: 2, h: 18),

            F("SPY260918C00776000", buy: true,  qty: 3, price: 20.00m, d: 31, h: 15),
            F("SPY260918C00786000", buy: false, qty: 3, price: 16.64m, d: 31, h: 15),
            F("SPY260918C00776000", buy: false, qty: 3, price: 21.00m, d: 2, h: 19),
            F("SPY260918C00786000", buy: true,  qty: 3, price: 17.00m, d: 2, h: 19),
        ];

        IReadOnlyList<RealisedTrade> trades = RealisedTrades.FromFills(fills);

        Assert.Equal(2, trades.Count);
        Assert.Contains(trades, t => t.Expiration == new DateOnly(2026, 9, 3));
        Assert.Contains(trades, t => t.Expiration == new DateOnly(2026, 9, 18));
    }

    [Fact]
    public void Wins_and_losses_are_counted_from_the_realised_figures()
    {
        List<Fill> fills =
        [
            F(Long,  buy: true,  qty: 5, price: 17.00m, d: 31, h: 14),
            F(Short, buy: false, qty: 5, price: 15.38m, d: 31, h: 14),
            F(Long,  buy: false, qty: 5, price: 18.00m, d: 2, h: 18),
            F(Short, buy: true,  qty: 5, price: 15.50m, d: 2, h: 18),

            F("SPY260918C00776000", buy: true,  qty: 3, price: 20.00m, d: 31, h: 15),
            F("SPY260918C00786000", buy: false, qty: 3, price: 16.64m, d: 31, h: 15),
            F("SPY260918C00776000", buy: false, qty: 3, price: 19.00m, d: 2, h: 19),
            F("SPY260918C00786000", buy: true,  qty: 3, price: 16.50m, d: 2, h: 19),
        ];

        IReadOnlyList<RealisedTrade> trades = RealisedTrades.FromFills(fills);

        Assert.Equal(1, RealisedTrades.Wins(trades));
        Assert.Equal(1, RealisedTrades.Losses(trades));
    }

    [Fact]
    public void Non_option_fills_are_ignored_rather_than_mis_parsed()
    {
        List<Fill> fills =
        [
            F("SPY", buy: true, qty: 100, price: 770m, d: 31, h: 14),
            F("SPY", buy: false, qty: 100, price: 775m, d: 2, h: 18),
        ];

        Assert.Empty(RealisedTrades.FromFills(fills));
    }

    [Fact]
    public void No_fills_yields_no_trades_and_no_wins_or_losses()
    {
        IReadOnlyList<RealisedTrade> trades = RealisedTrades.FromFills([]);

        Assert.Empty(trades);
        Assert.Equal(0, RealisedTrades.Wins(trades));
        Assert.Equal(0, RealisedTrades.Losses(trades));
    }

    [Fact]
    public void Cash_flow_direction_follows_the_side()
    {
        Assert.Equal(-17_000m, F(Long, buy: true, qty: 10, price: 17.00m, d: 31, h: 14).CashFlow);
        Assert.Equal(15_380m, F(Short, buy: false, qty: 10, price: 15.38m, d: 31, h: 14).CashFlow);
    }
}
