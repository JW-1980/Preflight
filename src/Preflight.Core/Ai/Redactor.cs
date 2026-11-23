using System.Text.RegularExpressions;

namespace Preflight.Core.Ai;

/// <summary>
/// Blanks secrets before text is sent to the AI model. Key names stay visible so the model can still
/// reason about the configuration; only values are replaced.
/// </summary>
public static partial class Redactor
{
    public const string Mask = "«redacted»";

    // KEY=value / KEY: value where the key name looks secret (env files, yaml, ini).
    [GeneratedRegex(@"(?im)^(\s*(?:export\s+)?[A-Z0-9_.\-]*(?:PASS(?:WORD)?|PWD|SECRET|TOKEN|API_?KEY|PRIVATE_?KEY|ACCESS_?KEY|AUTH|SALT|CREDENTIAL|DSN|APP_KEY)[A-Z0-9_.\-]*\s*[:=]\s*)(.+)$")]
    private static partial Regex SecretAssignment();

    // URLs with credentials: scheme://user:pass@host
    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://[^\s:/@]+:)([^\s@/]+)(@)")]
    private static partial Regex UrlCredentials();

    // PHP config: 'password' => '...', define('DB_PASSWORD', '...')
    [GeneratedRegex(@"(?i)(['""][a-z0-9_]*(?:pass(?:word)?|secret|token|api_?key|salt)[a-z0-9_]*['""]\s*(?:=>|,)\s*)(['""])(.*?)\2")]
    private static partial Regex PhpSecret();

    // Private key blocks
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyBlock();

    // Python settings: 'PASSWORD': '...'
    [GeneratedRegex(@"(?i)(SECRET_KEY\s*=\s*)(['""]).*?\2")]
    private static partial Regex PythonSecretKey();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = PrivateKeyBlock().Replace(text, Mask);
        text = UrlCredentials().Replace(text, m => m.Groups[1].Value + Mask + m.Groups[3].Value);
        text = PhpSecret().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask + m.Groups[2].Value);
        text = PythonSecretKey().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask + m.Groups[2].Value);
        text = SecretAssignment().Replace(text, m => m.Groups[1].Value + Mask);
        return text;
    }
}
