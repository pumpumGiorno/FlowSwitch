using System.Text;

namespace FlowSwitch.Diagnostics;

internal enum CheckResult
{
    Pass,
    Warn,
    Fail,
    Skip,
    Info,
}

/// <summary>A self-test report: one line per check ("Keyboard hook ....... PASS") plus indented details.</summary>
internal sealed class SelfTestReport
{
    private const int NameWidth = 28;
    private readonly List<(string Name, CheckResult Result, string? Detail)> _checks = new();
    private readonly object _gate = new();

    public void Add(string name, CheckResult result, string? detail = null)
    {
        lock (_gate) _checks.Add((name, result, detail));
    }

    public void Add(string name, bool pass, string? detail = null) => Add(name, pass ? CheckResult.Pass : CheckResult.Fail, detail);

    public int Count(CheckResult result)
    {
        lock (_gate) return _checks.Count(c => c.Result == result);
    }

    public string Render(string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        lock (_gate)
        {
            foreach (var (name, result, detail) in _checks)
            {
                string label = result switch
                {
                    CheckResult.Pass => "PASS",
                    CheckResult.Warn => "WARN",
                    CheckResult.Fail => "FAIL",
                    CheckResult.Skip => "SKIP",
                    _ => "INFO",
                };
                string dots = name.Length + 1 >= NameWidth ? " " : " " + new string('.', NameWidth - name.Length - 1) + " ";
                sb.Append(name).Append(dots).AppendLine(label);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    foreach (string line in detail.Split('\n'))
                        sb.Append("    ").AppendLine(line.TrimEnd('\r'));
                }
            }
            int fail = _checks.Count(c => c.Result == CheckResult.Fail);
            int warn = _checks.Count(c => c.Result == CheckResult.Warn);
            sb.AppendLine();
            sb.AppendLine(fail == 0 && warn == 0 ? "Result: everything works."
                : $"Result: {fail} failed, {warn} warning(s). Details above; full log: flowswitch.log in the same folder.");
        }
        return sb.ToString();
    }
}
