using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Tweaker.App.Services;

namespace Tweaker.App.Tests;

public sealed class OptimizationElevationTestsStrictJson
{
    [Theory]
    [InlineData(",\"rawCommand\":\"cmd.exe\"")]
    [InlineData(",\"schemaVersion\":1")]
    public async Task PipeParser_RejectsUnknownAndDuplicateTopLevelProperties(string injected)
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var json = $"{{\"schemaVersion\":1,\"requestId\":\"{id:D}\",\"action\":0,\"targetTransactionId\":null,\"operations\":[{{\"operationId\":\"power.known\",\"requestedValueId\":\"default\"}}],\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"{injected}}}";
        await FluentActions.Invoking(() => ParseAsync(json)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task PipeParser_RejectsRawNestedOperationProperty()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var json = $"{{\"schemaVersion\":1,\"requestId\":\"{id:D}\",\"action\":0,\"targetTransactionId\":null,\"operations\":[{{\"operationId\":\"power.known\",\"requestedValueId\":\"default\",\"registryPath\":\"HKLM\\\\Injected\"}}],\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}}";
        await FluentActions.Invoking(() => ParseAsync(json)).Should().ThrowAsync<InvalidDataException>();
    }

    private static async Task ParseAsync(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
        body.CopyTo(framed, 4);
        await using var stream = new MemoryStream(framed);
        _ = await PipeProtocol.ReadAsync<PrivilegedWorkerRequest>(stream, CancellationToken.None);
    }
}
