using InfoPanel.Mcp.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>The preview checks' built-in thresholds (<see cref="McpText"/>).</summary>
public sealed class McpTextTests
{
    [Fact]
    public void TheBuiltInThresholds_Read()
    {
        Assert.Equal(new CheckThresholds(1, 0.1, 4.5), new McpText(NullLogger.Instance).Checks());
    }
}
