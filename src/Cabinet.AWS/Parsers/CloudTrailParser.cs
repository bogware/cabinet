using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;

namespace Cabinet.AWS.Parsers;

/// <summary>
/// Parser for AWS CloudTrail audit logs
/// </summary>
public class CloudTrailParser : BaseLogParser
{
    public override string Name => "CloudTrail";
    public override string SourceType => SourceTypes.CloudTrail;

    public override bool CanParse(string line)
    {
        // CloudTrail logs are JSON with specific structure
        if (line.TrimStart().StartsWith("{") && line.TrimEnd().EndsWith("}"))
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                return root.TryGetProperty("eventTime", out _) &&
                       root.TryGetProperty("eventName", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // Also handle formatted CloudTrail output
        return line.Contains("eventName") && line.Contains("eventSource");
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        logEvent.SourceType = SourceTypes.CloudTrail;
        logEvent.SetField("LogSource", "CloudTrail");
        logEvent.Tags.Add("audit_log");

        // Try to parse as JSON
        if (TryParseCloudTrailJson(line, logEvent))
        {
            return;
        }

        // Fall back to formatted text parsing
        ParseFormattedCloudTrail(line, logEvent);
    }

    private bool TryParseCloudTrailJson(string line, LogEvent logEvent)
    {
        try
        {
            var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Core event fields
            if (root.TryGetProperty("eventTime", out var eventTime))
            {
                if (DateTime.TryParse(eventTime.GetString(), out var dt))
                {
                    logEvent.Timestamp = dt.ToUniversalTime();
                }
            }

            if (root.TryGetProperty("eventName", out var eventName))
            {
                var name = eventName.GetString();
                logEvent.SetField("EventName", name);
                CategorizeEvent(name, logEvent);
            }

            if (root.TryGetProperty("eventSource", out var eventSource))
            {
                logEvent.SetField("EventSource", eventSource.GetString());
            }

            if (root.TryGetProperty("eventID", out var eventId))
            {
                logEvent.SetField("EventId", eventId.GetString());
            }

            if (root.TryGetProperty("eventType", out var eventType))
            {
                logEvent.SetField("EventType", eventType.GetString());
            }

            if (root.TryGetProperty("eventVersion", out var eventVersion))
            {
                logEvent.SetField("EventVersion", eventVersion.GetString());
            }

            if (root.TryGetProperty("awsRegion", out var awsRegion))
            {
                logEvent.SetField("Region", awsRegion.GetString());
            }

            // User identity
            if (root.TryGetProperty("userIdentity", out var userIdentity))
            {
                ExtractUserIdentity(userIdentity, logEvent);
            }

            // Source IP
            if (root.TryGetProperty("sourceIPAddress", out var sourceIP))
            {
                logEvent.SetField("SourceIP", sourceIP.GetString());
                logEvent.Tags.Add("has_ip");
            }

            // User agent
            if (root.TryGetProperty("userAgent", out var userAgent))
            {
                logEvent.SetField("UserAgent", userAgent.GetString());
            }

            // Request parameters
            if (root.TryGetProperty("requestParameters", out var requestParams))
            {
                ExtractRequestParameters(requestParams, logEvent);
            }

            // Response elements
            if (root.TryGetProperty("responseElements", out var responseElements))
            {
                ExtractResponseElements(responseElements, logEvent);
            }

            // Error information
            if (root.TryGetProperty("errorCode", out var errorCode))
            {
                logEvent.SetField("ErrorCode", errorCode.GetString());
                logEvent.Severity = LogSeverity.Error;
                logEvent.Tags.Add("error");
            }

            if (root.TryGetProperty("errorMessage", out var errorMessage))
            {
                logEvent.SetField("ErrorMessage", errorMessage.GetString());
            }

            // Resources
            if (root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                ExtractResources(resources, logEvent);
            }

            // Request ID
            if (root.TryGetProperty("requestID", out var requestId))
            {
                logEvent.SetField("RequestId", requestId.GetString());
            }

            // Read only
            if (root.TryGetProperty("readOnly", out var readOnly))
            {
                var isReadOnly = readOnly.GetBoolean();
                if (!isReadOnly)
                {
                    logEvent.Tags.Add("write_operation");
                }
            }

            // Management event
            if (root.TryGetProperty("managementEvent", out var managementEvent))
            {
                if (managementEvent.GetBoolean())
                {
                    logEvent.Tags.Add("management_event");
                }
            }

            // Detect important security events
            DetectSecurityEvents(root, logEvent);

            logEvent.Tags.Add("json_log");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ExtractUserIdentity(JsonElement userIdentity, LogEvent logEvent)
    {
        if (userIdentity.TryGetProperty("type", out var type))
        {
            var userType = type.GetString();
            logEvent.SetField("UserType", userType);

            if (userType == "Root")
            {
                logEvent.Tags.Add("root_user");
                logEvent.Severity = LogSeverity.Warning;
            }
        }

        if (userIdentity.TryGetProperty("principalId", out var principalId))
        {
            logEvent.SetField("PrincipalId", principalId.GetString());
        }

        if (userIdentity.TryGetProperty("arn", out var arn))
        {
            logEvent.SetField("UserArn", arn.GetString());
        }

        if (userIdentity.TryGetProperty("accountId", out var accountId))
        {
            logEvent.SetField("AccountId", accountId.GetString());
        }

        if (userIdentity.TryGetProperty("userName", out var userName))
        {
            logEvent.SetField("UserName", userName.GetString());
        }

        // Session context for assumed roles
        if (userIdentity.TryGetProperty("sessionContext", out var sessionContext))
        {
            if (sessionContext.TryGetProperty("sessionIssuer", out var sessionIssuer))
            {
                if (sessionIssuer.TryGetProperty("userName", out var issuerName))
                {
                    logEvent.SetField("SessionIssuer", issuerName.GetString());
                }
            }

            // mfaAuthenticated can be at sessionContext.attributes.mfaAuthenticated
            var mfaSource = sessionContext;
            if (sessionContext.TryGetProperty("attributes", out var attrs))
                mfaSource = attrs;
            if (mfaSource.TryGetProperty("mfaAuthenticated", out var mfa))
            {
                // AWS CloudTrail may encode this as string "true"/"false" or as a JSON boolean
                var mfaUsed = mfa.ValueKind == System.Text.Json.JsonValueKind.True ||
                              (mfa.ValueKind == System.Text.Json.JsonValueKind.String &&
                               string.Equals(mfa.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                logEvent.SetField("MFAUsed", mfaUsed);
                if (!mfaUsed)
                {
                    logEvent.Tags.Add("no_mfa");
                }
            }
        }
    }

    private void ExtractRequestParameters(JsonElement requestParams, LogEvent logEvent)
    {
        // Extract key request parameters based on common patterns
        if (requestParams.TryGetProperty("bucketName", out var bucketName))
        {
            logEvent.SetField("BucketName", bucketName.GetString());
            logEvent.Tags.Add("s3_operation");
        }

        if (requestParams.TryGetProperty("instanceId", out var instanceId))
        {
            logEvent.SetField("InstanceId", instanceId.GetString());
            logEvent.Tags.Add("ec2_operation");
        }

        if (requestParams.TryGetProperty("groupName", out var groupName))
        {
            logEvent.SetField("GroupName", groupName.GetString());
        }

        if (requestParams.TryGetProperty("userName", out var userName))
        {
            logEvent.SetField("TargetUser", userName.GetString());
            logEvent.Tags.Add("user_management");
        }

        if (requestParams.TryGetProperty("policyDocument", out _))
        {
            logEvent.Tags.Add("policy_change");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (requestParams.TryGetProperty("roleArn", out var roleArn))
        {
            logEvent.SetField("RoleArn", roleArn.GetString());
            logEvent.Tags.Add("role_operation");
        }
    }

    private void ExtractResponseElements(JsonElement responseElements, LogEvent logEvent)
    {
        if (responseElements.TryGetProperty("credentials", out _))
        {
            logEvent.Tags.Add("credential_access");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (responseElements.TryGetProperty("assumedRoleUser", out var assumedRole))
        {
            if (assumedRole.TryGetProperty("arn", out var arn))
            {
                logEvent.SetField("AssumedRoleArn", arn.GetString());
                logEvent.Tags.Add("role_assumption");
            }
        }
    }

    private void ExtractResources(JsonElement resources, LogEvent logEvent)
    {
        var resourceArns = new List<string>();
        var resourceTypes = new HashSet<string>();

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.TryGetProperty("ARN", out var arn))
            {
                var arnStr = arn.GetString();
                if (!string.IsNullOrEmpty(arnStr))
                {
                    resourceArns.Add(arnStr);

                    // Extract resource type from ARN
                    var arnParts = arnStr.Split(':');
                    if (arnParts.Length > 5)
                    {
                        resourceTypes.Add(arnParts[5].Split('/')[0]);
                    }
                }
            }

            if (resource.TryGetProperty("type", out var type))
            {
                resourceTypes.Add(type.GetString() ?? "");
            }
        }

        if (resourceArns.Any())
        {
            logEvent.SetField("ResourceArns", string.Join(", ", resourceArns));
        }

        foreach (var resourceType in resourceTypes)
        {
            if (!string.IsNullOrEmpty(resourceType))
            {
                logEvent.Tags.Add($"resource_{resourceType.ToLower()}");
            }
        }
    }

    private void CategorizeEvent(string? eventName, LogEvent logEvent)
    {
        if (string.IsNullOrEmpty(eventName)) return;

        // Categorize based on event name patterns
        if (eventName.StartsWith("Create") || eventName.StartsWith("Put"))
        {
            logEvent.Tags.Add("create_operation");
        }
        else if (eventName.StartsWith("Delete") || eventName.StartsWith("Remove"))
        {
            logEvent.Tags.Add("delete_operation");
            logEvent.Severity = LogSeverity.Warning;
        }
        else if (eventName.StartsWith("Update") || eventName.StartsWith("Modify"))
        {
            logEvent.Tags.Add("update_operation");
        }
        else if (eventName.StartsWith("Get") || eventName.StartsWith("Describe") || eventName.StartsWith("List"))
        {
            logEvent.Tags.Add("read_operation");
        }
        else if (eventName.Contains("Login") || eventName.Contains("Signin"))
        {
            logEvent.Tags.Add("authentication");
        }
        else if (eventName.Contains("Assume") || eventName.Contains("Role"))
        {
            logEvent.Tags.Add("role_operation");
        }

        // High-risk operations
        var highRiskOperations = new[]
        {
            "DeleteBucket", "DeleteDBInstance", "DeleteUser", "DeleteAccessKey",
            "PutBucketPolicy", "PutUserPolicy", "AttachUserPolicy", "CreateAccessKey",
            "CreateUser", "ModifyDBInstance", "AuthorizeSecurityGroupIngress",
            "RevokeSecurityGroupIngress", "RunInstances", "TerminateInstances",
            "StopLogging", "DeleteTrail", "UpdateTrail", "PutEventSelectors"
        };

        if (highRiskOperations.Contains(eventName))
        {
            logEvent.Tags.Add("high_risk_operation");
            logEvent.Severity = LogSeverity.Warning;
        }
    }

    private void DetectSecurityEvents(JsonElement root, LogEvent logEvent)
    {
        var eventName = root.TryGetProperty("eventName", out var name) ? name.GetString() : "";

        // Console login without MFA
        if (eventName == "ConsoleLogin")
        {
            logEvent.Tags.Add("console_login");

            if (root.TryGetProperty("additionalEventData", out var additionalData))
            {
                if (additionalData.TryGetProperty("MFAUsed", out var mfaUsed))
                {
                    if (mfaUsed.GetString() == "No")
                    {
                        logEvent.Tags.Add("no_mfa_console_login");
                        logEvent.Severity = LogSeverity.Warning;
                    }
                }
            }
        }

        // Failed authentication
        if (root.TryGetProperty("errorCode", out var errorCode))
        {
            var error = errorCode.GetString();
            if (error == "UnauthorizedAccess" || error == "Forbidden" || error == "TokenRefreshRequired")
            {
                logEvent.Tags.Add("auth_failure");
                logEvent.Severity = LogSeverity.Warning;
            }
        }

        // Privilege escalation indicators
        if (eventName?.Contains("Attach") == true || eventName?.Contains("PutPolicy") == true)
        {
            logEvent.Tags.Add("potential_privilege_escalation");
        }

        // Data exfiltration indicators
        if (eventName == "GetObject" || eventName == "SelectObjectContent" || eventName == "GetSecretValue")
        {
            logEvent.Tags.Add("data_access");
        }

        // Logging tampering
        if (eventName == "StopLogging" || eventName == "DeleteTrail" || eventName == "UpdateTrail")
        {
            logEvent.Tags.Add("logging_change");
            logEvent.Severity = LogSeverity.Critical;
        }
    }

    private void ParseFormattedCloudTrail(string line, LogEvent logEvent)
    {
        // Parse formatted CloudTrail output (from CLI tools)
        // Format: [timestamp] source: eventName by userName from IP

        if (line.Contains(": ") && line.Contains(" by "))
        {
            var parts = line.Split(new[] { ": ", " by ", " from " }, StringSplitOptions.None);

            if (parts.Length >= 2)
            {
                logEvent.SetField("EventDescription", parts[1].Trim());
            }

            if (parts.Length >= 3)
            {
                logEvent.SetField("UserName", parts[2].Trim());
            }

            if (parts.Length >= 4)
            {
                logEvent.SetField("SourceIP", parts[3].Trim());
            }
        }
    }
}