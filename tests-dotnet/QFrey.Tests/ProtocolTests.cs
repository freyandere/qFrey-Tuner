using System.Text.Json;
using QFrey.Core.Contracts;
using Xunit;

namespace QFrey.Tests;
public class ProtocolTests
{
    private const string Valid = """{"protocolVersion":1,"requestId":"00000000-0000-0000-0000-000000000001","command":"Initialize","targetSessionId":null,"expectedRevision":null,"payload":{"protocolVersion":1}}""";
    [Fact]
    public void InitializeRoundTrips() => Assert.Equal("Initialize", Protocol.Parse(JsonSerializer.Serialize(Protocol.Parse(Valid), Protocol.Json)).Command);
    [Fact]
    public void SharedInitializeFixtureRoundTrips()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/initialize-success.json"));
        var reply = JsonSerializer.Deserialize<Reply>(source, Protocol.Json)!;
        Assert.True(reply.Ok);
        Assert.Equal(Protocol.Version, reply.Data!.ProtocolVersion);
        Assert.Equal(Protocol.SchemaVersion, reply.Data.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(reply, Protocol.Json), JsonSerializer.Serialize(JsonSerializer.Deserialize<Reply>(JsonSerializer.Serialize(reply, Protocol.Json), Protocol.Json), Protocol.Json));
    }
    [Theory]
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":true")]
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":2")]
    [InlineData("Initialize", "ApplyPlan")]
    [InlineData("\"targetSessionId\":null,", "")]
    [InlineData("\"expectedRevision\":null", "\"expectedRevision\":NaN")]
    [InlineData("\"expectedRevision\":null", "\"expectedRevision\":-1")]
    [InlineData("\"command\":", "\"extra\":true,\"command\":")]
    [InlineData("\"command\":", "\"command\":\"ApplyPlan\",\"command\":")]
    public void RejectsInvalidBoundary(string original, string replacement) => Assert.ThrowsAny<JsonException>(() => Protocol.Parse(Valid.Replace(original, replacement)));
    [Fact]
    public void RejectsOversize() => Assert.Throws<JsonException>(() => Protocol.Parse(Valid + new string(' ', Protocol.MaxCommandBytes)));
    [Fact]
    public void UnknownIsNotZero()
    {
        MetricReading reading = new UnavailableReading("REMOTE_PROCESS");
        var json = JsonSerializer.Serialize(reading, Protocol.Json);
        Assert.DoesNotContain("value", json);
        Assert.IsType<UnavailableReading>(JsonSerializer.Deserialize<MetricReading>(json, Protocol.Json));
    }
}
