using System.Text.Json;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;

namespace Cabinet.AWS.Parsers;

/// <summary>
/// Parser for AWS CloudWatch logs
/// </summary>
public class CloudWatchParser : BaseLogParser
{
    public override string Name => "CloudWatch";
    public override string SourceType => SourceTypes.CloudWatch;

    public override bool CanParse(string line)
    {
        // CloudWatch logs often come with timestamp prefix or are JSON
        return line.StartsWith("[") && line.Contains("]") ||
               (line.TrimStart().StartsWith("{") && line.TrimEnd().EndsWith("}"));
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        // Set source type
        logEvent.SourceType = SourceTypes.CloudWatch;
        logEvent.SetField("LogSource", "CloudWatch");

        // Try to parse as JSON first
        if (TryParseJson(line, logEvent))
        {
            return;
        }

        // Parse timestamp prefix format: [2024-01-01 12:34:56.789] message
        var timestampMatch = Regex.Match(line, @"^\[([^\]]+)\]\s*(.*)");
        if (timestampMatch.Success)
        {
            if (DateTime.TryParse(timestampMatch.Groups[1].Value, out var timestamp))
            {
                logEvent.Timestamp = timestamp.ToUniversalTime();
            }

            var message = timestampMatch.Groups[2].Value;
            ParseLogMessage(message, logEvent);
        }
        else
        {
            ParseLogMessage(line, logEvent);
        }

        // Detect AWS service
        DetectAWSService(line, logEvent);

        // Extract request IDs
        ExtractRequestIds(line, logEvent);

        // Detect errors and exceptions
        DetectErrorPatterns(line, logEvent);
    }

    private bool TryParseJson(string line, LogEvent logEvent)
    {
        try
        {
            // Remove timestamp prefix if present
            var jsonStart = line.IndexOf('{');
            if (jsonStart > 0)
            {
                line = line.Substring(jsonStart);
            }

            var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Extract common JSON log fields
            if (root.TryGetProperty("timestamp", out var timestamp))
            {
                if (DateTime.TryParse(timestamp.GetString(), out var dt))
                {
                    logEvent.Timestamp = dt.ToUniversalTime();
                }
            }

            if (root.TryGetProperty("level", out var level))
            {
                logEvent.Severity = ParseLogLevel(level.GetString());
            }

            if (root.TryGetProperty("message", out var message))
            {
                logEvent.SetField("Message", message.GetString());
            }

            if (root.TryGetProperty("logger", out var logger))
            {
                logEvent.SetField("Logger", logger.GetString());
            }

            if (root.TryGetProperty("requestId", out var requestId))
            {
                logEvent.SetField("RequestId", requestId.GetString());
                logEvent.Tags.Add("has_request_id");
            }

            if (root.TryGetProperty("traceId", out var traceId))
            {
                logEvent.SetField("TraceId", traceId.GetString());
                logEvent.Tags.Add("has_trace_id");
            }

            // Lambda specific fields
            if (root.TryGetProperty("type", out var type))
            {
                var typeStr = type.GetString();
                if (typeStr == "START" || typeStr == "END" || typeStr == "REPORT")
                {
                    logEvent.Tags.Add($"lambda_{typeStr.ToLower()}");
                    logEvent.SetField("LambdaEventType", typeStr);
                }
            }

            if (root.TryGetProperty("duration", out var duration))
            {
                logEvent.SetField("Duration", duration.GetDouble());
            }

            if (root.TryGetProperty("billedDuration", out var billedDuration))
            {
                logEvent.SetField("BilledDuration", billedDuration.GetDouble());
            }

            if (root.TryGetProperty("memorySize", out var memorySize))
            {
                logEvent.SetField("MemorySize", memorySize.GetInt32());
            }

            if (root.TryGetProperty("maxMemoryUsed", out var maxMemoryUsed))
            {
                logEvent.SetField("MaxMemoryUsed", maxMemoryUsed.GetInt32());
            }

            // Extract any error information
            if (root.TryGetProperty("error", out var error) || root.TryGetProperty("errorMessage", out error))
            {
                logEvent.SetField("Error", error.GetString());
                logEvent.Severity = LogSeverity.Error;
                logEvent.Tags.Add("error");
            }

            if (root.TryGetProperty("errorType", out var errorType))
            {
                logEvent.SetField("ErrorType", errorType.GetString());
            }

            if (root.TryGetProperty("stackTrace", out var stackTrace))
            {
                logEvent.SetField("StackTrace", stackTrace.GetString());
                logEvent.Tags.Add("has_stacktrace");
            }

            logEvent.Tags.Add("json_log");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ParseLogMessage(string message, LogEvent logEvent)
    {
        // Lambda START/END/REPORT messages
        if (message.StartsWith("START RequestId:"))
        {
            ExtractLambdaStart(message, logEvent);
        }
        else if (message.StartsWith("END RequestId:"))
        {
            ExtractLambdaEnd(message, logEvent);
        }
        else if (message.StartsWith("REPORT RequestId:"))
        {
            ExtractLambdaReport(message, logEvent);
        }
        // API Gateway logs
        else if (message.Contains("Method=") && message.Contains("Resource="))
        {
            ExtractAPIGatewayInfo(message, logEvent);
        }
        // ECS/Fargate logs
        else if (message.Contains("task-definition:") || message.Contains("container:"))
        {
            ExtractECSInfo(message, logEvent);
        }
    }

    private void ExtractLambdaStart(string message, LogEvent logEvent)
    {
        logEvent.Tags.Add("lambda_start");
        logEvent.SetField("LambdaEventType", "START");

        var requestIdMatch = Regex.Match(message, @"RequestId:\s*([a-f0-9\-]+)");
        if (requestIdMatch.Success)
        {
            logEvent.SetField("RequestId", requestIdMatch.Groups[1].Value);
        }

        var versionMatch = Regex.Match(message, @"Version:\s*(\S+)");
        if (versionMatch.Success)
        {
            logEvent.SetField("Version", versionMatch.Groups[1].Value);
        }
    }

    private void ExtractLambdaEnd(string message, LogEvent logEvent)
    {
        logEvent.Tags.Add("lambda_end");
        logEvent.SetField("LambdaEventType", "END");

        var requestIdMatch = Regex.Match(message, @"RequestId:\s*([a-f0-9\-]+)");
        if (requestIdMatch.Success)
        {
            logEvent.SetField("RequestId", requestIdMatch.Groups[1].Value);
        }
    }

    private void ExtractLambdaReport(string message, LogEvent logEvent)
    {
        logEvent.Tags.Add("lambda_report");
        logEvent.SetField("LambdaEventType", "REPORT");

        var requestIdMatch = Regex.Match(message, @"RequestId:\s*([a-f0-9\-]+)");
        if (requestIdMatch.Success)
        {
            logEvent.SetField("RequestId", requestIdMatch.Groups[1].Value);
        }

        var durationMatch = Regex.Match(message, @"Duration:\s*([\d.]+)\s*ms");
        if (durationMatch.Success)
        {
            logEvent.SetField("Duration", double.Parse(durationMatch.Groups[1].Value));
        }

        var billedDurationMatch = Regex.Match(message, @"Billed Duration:\s*([\d.]+)\s*ms");
        if (billedDurationMatch.Success)
        {
            logEvent.SetField("BilledDuration", double.Parse(billedDurationMatch.Groups[1].Value));
        }

        var memoryMatch = Regex.Match(message, @"Memory Size:\s*(\d+)\s*MB");
        if (memoryMatch.Success)
        {
            logEvent.SetField("MemorySize", int.Parse(memoryMatch.Groups[1].Value));
        }

        var maxMemoryMatch = Regex.Match(message, @"Max Memory Used:\s*(\d+)\s*MB");
        if (maxMemoryMatch.Success)
        {
            logEvent.SetField("MaxMemoryUsed", int.Parse(maxMemoryMatch.Groups[1].Value));
        }

        var initDurationMatch = Regex.Match(message, @"Init Duration:\s*([\d.]+)\s*ms");
        if (initDurationMatch.Success)
        {
            logEvent.SetField("InitDuration", double.Parse(initDurationMatch.Groups[1].Value));
            logEvent.Tags.Add("cold_start");
        }
    }

    private void ExtractAPIGatewayInfo(string message, LogEvent logEvent)
    {
        logEvent.Tags.Add("api_gateway");

        var methodMatch = Regex.Match(message, @"Method=(\w+)");
        if (methodMatch.Success)
        {
            logEvent.SetField("HttpMethod", methodMatch.Groups[1].Value);
        }

        var resourceMatch = Regex.Match(message, @"Resource=([^\s]+)");
        if (resourceMatch.Success)
        {
            logEvent.SetField("Resource", resourceMatch.Groups[1].Value);
        }

        var statusMatch = Regex.Match(message, @"Status=(\d+)");
        if (statusMatch.Success)
        {
            var status = int.Parse(statusMatch.Groups[1].Value);
            logEvent.SetField("HttpStatus", status);

            if (status >= 500)
            {
                logEvent.Severity = LogSeverity.Error;
                logEvent.Tags.Add("server_error");
            }
            else if (status >= 400)
            {
                logEvent.Severity = LogSeverity.Warning;
                logEvent.Tags.Add("client_error");
            }
        }

        var latencyMatch = Regex.Match(message, @"Latency=(\d+)");
        if (latencyMatch.Success)
        {
            logEvent.SetField("Latency", int.Parse(latencyMatch.Groups[1].Value));
        }
    }

    private void ExtractECSInfo(string message, LogEvent logEvent)
    {
        logEvent.Tags.Add("ecs");

        var taskMatch = Regex.Match(message, @"task-definition:([^\s]+)");
        if (taskMatch.Success)
        {
            logEvent.SetField("TaskDefinition", taskMatch.Groups[1].Value);
        }

        var containerMatch = Regex.Match(message, @"container:([^\s]+)");
        if (containerMatch.Success)
        {
            logEvent.SetField("Container", containerMatch.Groups[1].Value);
        }

        var taskArnMatch = Regex.Match(message, @"arn:aws:ecs:[^:]+:[^:]+:task/([a-f0-9\-]+)");
        if (taskArnMatch.Success)
        {
            logEvent.SetField("TaskId", taskArnMatch.Groups[1].Value);
        }
    }

    private void DetectAWSService(string line, LogEvent logEvent)
    {
        var services = new Dictionary<string, string[]>
        {
            ["lambda"] = new[] { "RequestId:", "START RequestId", "END RequestId", "REPORT RequestId", "Lambda" },
            ["api_gateway"] = new[] { "Method=", "Resource=", "API Gateway", "RestApi" },
            ["ecs"] = new[] { "task-definition:", "container:", "ECS", "Fargate" },
            ["rds"] = new[] { "RDS", "database", "mysql", "postgres", "aurora" },
            ["s3"] = new[] { "S3", "bucket", "s3://" },
            ["dynamodb"] = new[] { "DynamoDB", "table:", "TableName" },
            ["sqs"] = new[] { "SQS", "queue", "MessageId" },
            ["sns"] = new[] { "SNS", "topic", "PublishBatch" },
            ["cloudfront"] = new[] { "CloudFront", "distribution", "cf-" },
            ["ec2"] = new[] { "EC2", "instance", "i-", "vpc-" }
        };

        foreach (var service in services)
        {
            if (service.Value.Any(pattern => line.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                logEvent.Tags.Add($"aws_{service.Key}");
                logEvent.SetField("AWSService", service.Key);
                break;
            }
        }
    }

    private void ExtractRequestIds(string line, LogEvent logEvent)
    {
        // AWS Request ID
        var requestIdMatch = Regex.Match(line, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})");
        if (requestIdMatch.Success)
        {
            logEvent.SetField("RequestId", requestIdMatch.Groups[1].Value);
            logEvent.Tags.Add("has_request_id");
        }

        // X-Ray Trace ID
        var traceIdMatch = Regex.Match(line, @"X-Amzn-Trace-Id:\s*([^\s]+)");
        if (traceIdMatch.Success)
        {
            logEvent.SetField("TraceId", traceIdMatch.Groups[1].Value);
            logEvent.Tags.Add("has_trace_id");
        }

        // Correlation ID
        var correlationIdMatch = Regex.Match(line, @"correlation[_-]?id[:\s]+([a-f0-9\-]+)", RegexOptions.IgnoreCase);
        if (correlationIdMatch.Success)
        {
            logEvent.SetField("CorrelationId", correlationIdMatch.Groups[1].Value);
        }
    }

    private void DetectErrorPatterns(string line, LogEvent logEvent)
    {
        var lower = line.ToLowerInvariant();

        // AWS specific errors
        if (lower.Contains("throttl"))
        {
            logEvent.Tags.Add("throttled");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (lower.Contains("timeout") || lower.Contains("timed out"))
        {
            logEvent.Tags.Add("timeout");
            logEvent.Severity = LogSeverity.Error;
        }

        if (lower.Contains("permission denied") || lower.Contains("access denied") || lower.Contains("unauthorized"))
        {
            logEvent.Tags.Add("permission_error");
            logEvent.Severity = LogSeverity.Error;
        }

        if (lower.Contains("out of memory") || lower.Contains("oom"))
        {
            logEvent.Tags.Add("memory_error");
            logEvent.Severity = LogSeverity.Critical;
        }

        if (lower.Contains("cold start"))
        {
            logEvent.Tags.Add("cold_start");
        }

        // Performance issues
        if (Regex.IsMatch(line, @"duration[:\s]+(\d{4,})", RegexOptions.IgnoreCase))
        {
            logEvent.Tags.Add("slow_execution");
            logEvent.Severity = LogSeverity.Warning;
        }
    }

    private LogSeverity ParseLogLevel(string? level)
    {
        if (string.IsNullOrEmpty(level)) return LogSeverity.Info;

        return level.ToUpperInvariant() switch
        {
            "FATAL" or "CRITICAL" => LogSeverity.Fatal,
            "ERROR" or "ERR" => LogSeverity.Error,
            "WARN" or "WARNING" => LogSeverity.Warning,
            "INFO" or "INF" => LogSeverity.Info,
            "DEBUG" or "DBG" => LogSeverity.Debug,
            "TRACE" or "TRC" => LogSeverity.Trace,
            _ => LogSeverity.Info
        };
    }
}