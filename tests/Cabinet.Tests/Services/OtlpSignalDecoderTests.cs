using System.Text;
using Cabinet.API.Services;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class OtlpSignalDecoderTests
{
    [Fact]
    public void TracesJson_EachSpanBecomesARecord()
    {
        const string json = """
            {"resourceSpans":[{
              "resource":{"attributes":[{"key":"service.name","value":{"stringValue":"checkout"}}]},
              "scopeSpans":[{"spans":[{
                "traceId":"5b8efff798038103d269b633813fc60c","spanId":"eee19b7ec3c1b174","parentSpanId":"aaa19b7ec3c1b174",
                "name":"charge-card","kind":3,
                "startTimeUnixNano":"1772600767000000000","endTimeUnixNano":"1772600767250000000",
                "status":{"code":2,"message":"declined"},
                "attributes":[{"key":"payment.provider","value":{"stringValue":"acme"}}]
              }]}]
            }]}
            """;

        var record = OtlpSignalDecoder.DecodeTracesJson(Encoding.UTF8.GetBytes(json)).Should().ContainSingle().Subject;

        record.DefaultIndex.Should().Be("traces");
        record.Body.Should().Be("span charge-card 250ms error: declined");
        record.TraceId.Should().Be("5b8efff798038103d269b633813fc60c");
        record.Attribute("span.kind").Should().Be("client");
        record.Attribute("parent_span_id").Should().Be("aaa19b7ec3c1b174");
        record.Attribute("payment.provider").Should().Be("acme");
        record.Attribute("service.name").Should().Be("checkout");
        Convert.ToDouble(record.Attributes["duration_ms"]).Should().Be(250);
    }

    [Fact]
    public void MetricsJson_EachPointBecomesARecord()
    {
        const string json = """
            {"resourceMetrics":[{
              "resource":{"attributes":[{"key":"service.name","value":{"stringValue":"shop"}}]},
              "scopeMetrics":[{"metrics":[
                {"name":"queue_depth","unit":"1","gauge":{"dataPoints":[
                  {"timeUnixNano":"1772600767000000000","asInt":"12","attributes":[{"key":"queue","value":{"stringValue":"a"}}]},
                  {"timeUnixNano":"1772600767000000000","asDouble":3.5,"attributes":[{"key":"queue","value":{"stringValue":"b"}}]}
                ]}},
                {"name":"latency","unit":"ms","histogram":{"dataPoints":[
                  {"timeUnixNano":"1772600767000000000","count":"4","sum":100}
                ]}}
              ]}]
            }]}
            """;

        var records = OtlpSignalDecoder.DecodeMetricsJson(Encoding.UTF8.GetBytes(json));

        records.Should().HaveCount(3);
        records.Should().OnlyContain(record => record.DefaultIndex == "metrics");
        records[0].Attribute("metric.name").Should().Be("queue_depth");
        records[0].Attribute("metric.type").Should().Be("gauge");
        Convert.ToDouble(records[0].Attributes["value"]).Should().Be(12);
        records[1].Attribute("queue").Should().Be("b");
        Convert.ToDouble(records[1].Attributes["value"]).Should().Be(3.5);
        records[2].Attribute("metric.type").Should().Be("histogram");
        Convert.ToDouble(records[2].Attributes["count"]).Should().Be(4);
        Convert.ToDouble(records[2].Attributes["sum"]).Should().Be(100);
    }

    [Theory]
    [InlineData(new byte[] { 0x0A, 0xFF })]
    [InlineData(new byte[] { 0x0A, 0x05, 0x12, 0x03 })]
    public void MalformedProtobuf_IsAFormatError(byte[] data)
    {
        ((Action)(() => OtlpSignalDecoder.DecodeTracesProtobuf(data))).Should().Throw<FormatException>();
        ((Action)(() => OtlpSignalDecoder.DecodeMetricsProtobuf(data))).Should().Throw<FormatException>();
    }
}
