namespace NetBoost.Models;

/// <summary>DNS 对比表格里的一行（属性均为绑定用，文本已格式化）。</summary>
public sealed class DnsEntry
{
    public string Name { get; set; } = "";
    public string Primary { get; set; } = "";
    public string Secondary { get; set; } = "";

    public bool Responded { get; set; }
    public double? LatencyMs { get; set; }
    public double? SecondaryLatencyMs { get; set; }
    public int AnsweredDomains { get; set; }
    public int DomainCount { get; set; }
    public int RCode { get; set; } = -1;
    public int AnswerCount { get; set; }
    public string AnswerText { get; set; } = "";

    public bool IsCurrent { get; set; }
    public bool IsBest { get; set; }
    public string Note { get; set; } = "";

    public string SecondaryText => Secondary.Length == 0 ? "—" : Secondary;

    public string LatencyText => Responded && LatencyMs.HasValue ? $"{LatencyMs.Value:F0} ms" : "无响应";

    public string SecondaryLatencyText => Secondary.Length == 0
        ? "—"
        : SecondaryLatencyMs.HasValue ? $"{SecondaryLatencyMs.Value:F0} ms" : "无响应";

    public string StatusText
    {
        get
        {
            if (!Responded) return "超时";
            if (RCode == 3) return "域名不存在";
            if (RCode == 2) return "服务器错误";
            if (RCode != 0) return $"错误码 {RCode}";
            return AnswerCount > 0 ? "解析正常" : "无记录返回";
        }
    }

    public string AnswerTextOrDash => AnswerText.Length == 0 ? "—" : AnswerText;

    public string NoteText
    {
        get
        {
            var tags = new List<string>(4);
            if (IsCurrent) tags.Add("当前使用");
            if (IsBest) tags.Add("本次最快");
            if (DomainCount > 0) tags.Add($"{AnsweredDomains}/{DomainCount} 域名");
            if (Note.Length > 0) tags.Add(Note);
            return string.Join(" · ", tags);
        }
    }
}
