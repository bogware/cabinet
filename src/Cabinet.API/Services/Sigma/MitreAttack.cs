namespace Cabinet.API.Services.Sigma;

/// <summary>
/// MITRE ATT&amp;CK Enterprise tactics, and the techniques detection rules most often cover, for
/// showing which a deployment's rules cover and which they don't. Sub-techniques (T1110.001)
/// count toward their technique. Not the whole matrix: a technique outside this list still
/// counts, under "Other".
/// </summary>
public static class MitreAttack
{
    /// <summary>The tactics, in the order of an attack.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Tactics =
    [
        ("TA0043", "Reconnaissance"),
        ("TA0042", "Resource Development"),
        ("TA0001", "Initial Access"),
        ("TA0002", "Execution"),
        ("TA0003", "Persistence"),
        ("TA0004", "Privilege Escalation"),
        ("TA0005", "Defense Evasion"),
        ("TA0006", "Credential Access"),
        ("TA0007", "Discovery"),
        ("TA0008", "Lateral Movement"),
        ("TA0009", "Collection"),
        ("TA0011", "Command and Control"),
        ("TA0010", "Exfiltration"),
        ("TA0040", "Impact"),
    ];

    /// <summary>Techniques: ID, name, and the tactics they belong to.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string[] Tactics)> Techniques =
    [
        ("T1595", "Active Scanning", ["TA0043"]),
        ("T1592", "Gather Victim Host Information", ["TA0043"]),
        ("T1589", "Gather Victim Identity Information", ["TA0043"]),
        ("T1583", "Acquire Infrastructure", ["TA0042"]),
        ("T1588", "Obtain Capabilities", ["TA0042"]),
        ("T1190", "Exploit Public-Facing Application", ["TA0001"]),
        ("T1133", "External Remote Services", ["TA0001", "TA0003"]),
        ("T1566", "Phishing", ["TA0001"]),
        ("T1078", "Valid Accounts", ["TA0001", "TA0003", "TA0004", "TA0005"]),
        ("T1195", "Supply Chain Compromise", ["TA0001"]),
        ("T1199", "Trusted Relationship", ["TA0001"]),
        ("T1059", "Command and Scripting Interpreter", ["TA0002"]),
        ("T1203", "Exploitation for Client Execution", ["TA0002"]),
        ("T1053", "Scheduled Task/Job", ["TA0002", "TA0003", "TA0004"]),
        ("T1047", "Windows Management Instrumentation", ["TA0002"]),
        ("T1204", "User Execution", ["TA0002"]),
        ("T1569", "System Services", ["TA0002"]),
        ("T1610", "Deploy Container", ["TA0002", "TA0005"]),
        ("T1098", "Account Manipulation", ["TA0003", "TA0004"]),
        ("T1136", "Create Account", ["TA0003"]),
        ("T1543", "Create or Modify System Process", ["TA0003", "TA0004"]),
        ("T1547", "Boot or Logon Autostart Execution", ["TA0003", "TA0004"]),
        ("T1505", "Server Software Component", ["TA0003"]),
        ("T1546", "Event Triggered Execution", ["TA0003", "TA0004"]),
        ("T1556", "Modify Authentication Process", ["TA0003", "TA0005", "TA0006"]),
        ("T1548", "Abuse Elevation Control Mechanism", ["TA0004", "TA0005"]),
        ("T1068", "Exploitation for Privilege Escalation", ["TA0004"]),
        ("T1055", "Process Injection", ["TA0004", "TA0005"]),
        ("T1134", "Access Token Manipulation", ["TA0004", "TA0005"]),
        ("T1611", "Escape to Host", ["TA0004"]),
        ("T1562", "Impair Defenses", ["TA0005"]),
        ("T1070", "Indicator Removal", ["TA0005"]),
        ("T1027", "Obfuscated Files or Information", ["TA0005"]),
        ("T1036", "Masquerading", ["TA0005"]),
        ("T1112", "Modify Registry", ["TA0005"]),
        ("T1218", "System Binary Proxy Execution", ["TA0005"]),
        ("T1140", "Deobfuscate/Decode Files or Information", ["TA0005"]),
        ("T1222", "File and Directory Permissions Modification", ["TA0005"]),
        ("T1110", "Brute Force", ["TA0006"]),
        ("T1003", "OS Credential Dumping", ["TA0006"]),
        ("T1558", "Steal or Forge Kerberos Tickets", ["TA0006"]),
        ("T1552", "Unsecured Credentials", ["TA0006"]),
        ("T1555", "Credentials from Password Stores", ["TA0006"]),
        ("T1557", "Adversary-in-the-Middle", ["TA0006", "TA0009"]),
        ("T1621", "Multi-Factor Authentication Request Generation", ["TA0006"]),
        ("T1528", "Steal Application Access Token", ["TA0006"]),
        ("T1087", "Account Discovery", ["TA0007"]),
        ("T1083", "File and Directory Discovery", ["TA0007"]),
        ("T1046", "Network Service Discovery", ["TA0007"]),
        ("T1057", "Process Discovery", ["TA0007"]),
        ("T1082", "System Information Discovery", ["TA0007"]),
        ("T1018", "Remote System Discovery", ["TA0007"]),
        ("T1069", "Permission Groups Discovery", ["TA0007"]),
        ("T1580", "Cloud Infrastructure Discovery", ["TA0007"]),
        ("T1021", "Remote Services", ["TA0008"]),
        ("T1570", "Lateral Tool Transfer", ["TA0008"]),
        ("T1550", "Use Alternate Authentication Material", ["TA0005", "TA0008"]),
        ("T1210", "Exploitation of Remote Services", ["TA0008"]),
        ("T1005", "Data from Local System", ["TA0009"]),
        ("T1114", "Email Collection", ["TA0009"]),
        ("T1530", "Data from Cloud Storage", ["TA0009"]),
        ("T1560", "Archive Collected Data", ["TA0009"]),
        ("T1071", "Application Layer Protocol", ["TA0011"]),
        ("T1105", "Ingress Tool Transfer", ["TA0011"]),
        ("T1572", "Protocol Tunneling", ["TA0011"]),
        ("T1090", "Proxy", ["TA0011"]),
        ("T1219", "Remote Access Software", ["TA0011"]),
        ("T1568", "Dynamic Resolution", ["TA0011"]),
        ("T1041", "Exfiltration Over C2 Channel", ["TA0010"]),
        ("T1048", "Exfiltration Over Alternative Protocol", ["TA0010"]),
        ("T1567", "Exfiltration Over Web Service", ["TA0010"]),
        ("T1537", "Transfer Data to Cloud Account", ["TA0010"]),
        ("T1486", "Data Encrypted for Impact", ["TA0040"]),
        ("T1485", "Data Destruction", ["TA0040"]),
        ("T1489", "Service Stop", ["TA0040"]),
        ("T1490", "Inhibit System Recovery", ["TA0040"]),
        ("T1498", "Network Denial of Service", ["TA0040"]),
        ("T1499", "Endpoint Denial of Service", ["TA0040"]),
        ("T1496", "Resource Hijacking", ["TA0040"]),
        ("T1531", "Account Access Removal", ["TA0040"]),
    ];

    /// <summary>The technique a rule's technique ID counts toward: T1110.001 counts as T1110.</summary>
    public static string Parent(string technique) => technique.Split('.')[0].ToUpperInvariant();
}
