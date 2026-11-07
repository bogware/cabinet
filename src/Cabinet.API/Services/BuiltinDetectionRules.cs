using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>
/// Security detections that come with Cabinet, off until turned on. Each is a starting point
/// written for common log formats, and a test runs each against lines it should and shouldn't match.
/// </summary>
public static class BuiltinDetectionRules
{
    private const string SshSource = @"from (?<src_ip>[0-9a-fA-F.:]+) port";
    private const string WebClient = @"^(?<src_ip>[0-9a-fA-F.:]+) ";

    /// <summary>All of them, as shipped.</summary>
    public static IReadOnlyList<DetectionRule> All { get; } =
    [
        Rule("ssh-brute-force", "SSH brute force", "Error", "T1110",
            @"sshd.*Failed password", SshSource, "src_ip", 5, 300,
            "Repeated failed SSH passwords from one address. Check whether a login from it then succeeded."),
        Rule("ssh-invalid-users", "SSH invalid user enumeration", "Warning", "T1110.001",
            @"sshd.*Invalid user", SshSource, "src_ip", 10, 300,
            "One address trying many usernames that don't exist."),
        Rule("ssh-root-login", "SSH login as root", "Warning", "T1078.003",
            @"sshd.*Accepted \w+ for root from", SshSource, "src_ip", 1, 300,
            "A successful root login over SSH. Most systems should have this disabled."),
        Rule("sudo-failures", "Repeated sudo failures", "Warning", "T1548.003",
            @"sudo.*(authentication failure|incorrect password attempt)", @"(?:user|ruser)=(?<user>\w+)|^\S+ \S+ \S+ \S+ sudo: +(?<user>\w+)", "user", 3, 300,
            "Someone is failing to authenticate to sudo."),
        Rule("sudo-not-allowed", "sudo refused: not in sudoers", "Error", "T1548.003",
            @"sudo.*user NOT in sudoers", null, null, 1, 300,
            "An account tried to use sudo without being allowed to."),
        Rule("account-created", "Local account created", "Warning", "T1136.001",
            @"(useradd|adduser)\b.*new user", null, null, 1, 60,
            "A new local account. Confirm it was expected."),
        Rule("admin-group-change", "Account added to an admin group", "Error", "T1098",
            @"(usermod|gpasswd)\b.*\b(add|added)\b.*\b(sudo|wheel|admin|adm)\b", null, null, 1, 60,
            "An account gained administrative rights. Confirm it was expected."),
        Rule("audit-stopped", "Logging or audit service stopped", "Error", "T1562.001",
            @"(Stopped|Stopping|stopped|killed).*\b(auditd|rsyslog|syslog-ng|journald)\b|\b(auditd|rsyslog)\b.*\b(stopped|killed|exiting)\b", null, null, 1, 300,
            "Stopping logging is a common way to hide activity."),
        Rule("reverse-shell", "Reverse shell command", "Critical", "T1059.004",
            @"/dev/tcp/\S+|bash -i >&|\bnc\b.*\s-e\s|mkfifo.*\bnc\b", null, null, 1, 60,
            "A command line typical of a reverse shell."),
        Rule("windows-logon-failures", "Windows logon failures", "Error", "T1110",
            @"(EventID|Event ID|EventCode)[=: ""]+4625\b", null, "Host", 10, 300,
            "Repeated failed Windows logons on one host."),
        Rule("windows-log-cleared", "Windows event log cleared", "Critical", "T1070.001",
            @"(EventID|Event ID|EventCode)[=: ""]+(1102|104)\b.*(clear|log)|wevtutil\S* +cl\b", null, null, 1, 300,
            "Clearing an event log is a common way to hide activity."),
        Rule("windows-service-installed", "Windows service installed", "Warning", "T1543.003",
            @"(EventID|Event ID|EventCode)[=: ""]+7045\b", null, null, 1, 60,
            "A new service, which attackers often use to persist."),
        Rule("powershell-encoded", "PowerShell encoded command", "Error", "T1059.001",
            @"powershell(\.exe)?\b.*\s-(e|ec|enc|encodedcommand)\s+[A-Za-z0-9+/=]{16,}", null, null, 1, 300,
            "Encoded commands are common in malware, since they hide what's being run."),
        Rule("web-sql-injection", "SQL injection attempt", "Error", "T1190",
            @"(union(%20|\+|\s)+(all(%20|\+|\s)+)?select|(%27|')(%20|\+|\s)*or(%20|\+|\s)+(%27|')?1(%27|')?(%20|\+|\s)*=|information_schema|\bsleep(%28|\()\d)", WebClient, "src_ip", 1, 300,
            "A request containing SQL. Check whether its response was unusual."),
        Rule("web-path-traversal", "Path traversal attempt", "Warning", "T1083",
            @"(\.\./\.\./|%2e%2e(%2f|/)|\.\.%2f|/etc/passwd|/etc/shadow|win\.ini)", WebClient, "src_ip", 1, 300,
            "A request trying to read outside the web root."),
        Rule("web-probing", "Probing for sensitive files", "Warning", "T1595.003",
            @"""(GET|POST|HEAD) [^""]*(/\.env|/\.git/|/wp-login\.php|/phpmyadmin|/\.aws/|/actuator)", WebClient, "src_ip", 5, 60,
            "One client requesting many well-known sensitive paths."),
        Rule("web-scanning", "Web scanning: many 404s", "Warning", "T1595",
            @""" 404 ", WebClient, "src_ip", 50, 60,
            "One client getting many Not Found responses in a short time."),
        Rule("log4shell", "Log4Shell exploit attempt", "Critical", "T1190",
            @"\$\{(jndi|\$\{|lower:|upper:)", null, null, 1, 60,
            "A JNDI lookup string, used to exploit CVE-2021-44228."),
        Rule("aws-console-failures", "AWS console login failures", "Warning", "T1110",
            @"ConsoleLogin.*(Failure|Failed authentication)", @"sourceIPAddress""?[=: ""]+(?<src_ip>[0-9a-fA-F.:]+)", "src_ip", 5, 300,
            "Repeated failed AWS console logins from one address."),
        Rule("aws-logging-disabled", "AWS CloudTrail logging disabled", "Critical", "T1562.008",
            @"(StopLogging|DeleteTrail|UpdateTrail)", null, null, 1, 300,
            "CloudTrail being stopped or changed. Confirm it was expected."),
    ];

    private static DetectionRule Rule(
        string id,
        string name,
        string severity,
        string technique,
        string pattern,
        string? extract,
        string? groupBy,
        int threshold,
        int windowSeconds,
        string description) => new()
    {
        Id = id,
        Name = name,
        Severity = severity,
        Technique = technique,
        Pattern = pattern,
        Extract = extract,
        GroupBy = groupBy,
        Threshold = threshold,
        WindowSeconds = windowSeconds,
        CooldownSeconds = Math.Max(windowSeconds, 300),
        Description = description,
        Builtin = true,
        Enabled = false,
    };
}
