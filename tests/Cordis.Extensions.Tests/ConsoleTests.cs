using Xunit;
namespace Cordis.Extensions.Tests;

public sealed class ConsoleTests
{
    [Theory]
    [InlineData(0, "[I] test hello")]
    [InlineData(1, "[I] \u001b[33mtest\u001b[0m hello")]
    [InlineData(2, "[I] \u001b[38;5;40;1mtest\u001b[0m hello")]
    public async Task SourcePaletteAndDecoration(int colors, string expected)
    {
        await using var context = new Context();
        await context.RunAsync(ctx =>
        {
            var exporter = new ConsoleExporter(ctx, new() { Colors = colors, ShowTime = "" });
            var message = new LogMessage(1, DateTimeOffset.UnixEpoch, "test", LogLevel.Info, ["hello"], new(ctx.Fiber));
            Assert.Equal(expected, exporter.Render(message));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(999, "999ms")]
    [InlineData(1500, "2s")]
    [InlineData(59500, "1m")]
    [InlineData(3570000, "1h")]
    [InlineData(84600000, "1d")]
    [InlineData(-1500, "-1s")]
    public async Task DurationUsesSourceThresholdsAndRounding(int milliseconds, string expected)
    {
        await using var context = new Context();
        await context.RunAsync(ctx =>
        {
            var exporter = new ConsoleExporter(ctx, new() { ShowTime = "", ShowDiff = true });
            var message = new LogMessage(1, DateTimeOffset.UnixEpoch, "test", LogLevel.Info, ["hello"], new(ctx.Fiber));
            exporter.Render(message);
            Assert.EndsWith(" +" + expected, exporter.Render(message with { Timestamp = message.Timestamp.AddMilliseconds(milliseconds) }));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ExporterFormatsLabelsMultilineAndDisposesOnlyItsRegistration()
    {
        await using var context = new Context();
        await context.RunAsync(async ctx =>
        {
            using var output = new StringWriter();
            var first = new ConsoleExporter(ctx, new() { ShowTime = "", LabelWidth = 5, LabelRightAligned = true, MaxLength = 8 }, output);
            var second = ctx.Logger.Exporter(new DelegateLogExporter(_ => output.WriteLine("second")));
            ctx.Logger.Create("test").Info("%s\nabcdefghij", "hello");
            Assert.Contains(" test [I] hello\n          abcdefgh...", output.ToString());
            await first.DisposeAsync(); output.GetStringBuilder().Clear();
            ctx.Logger.Info("remaining");
            Assert.Equal("second" + Environment.NewLine, output.ToString());
            await second.DisposeAsync();
            output.Write("writer remains owned by caller");
        });
    }
}
