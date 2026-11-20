using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace HashCalculator;

/// <summary>
/// 把二维表数据渲染成剪贴板用的两种表达：制表符分隔纯文本与 CF_HTML 表格。<br/>
/// 数据源必须是结构化单元格，不能把模板生成的整行文本再按制表符拆开，
/// 否则文件名里的制表符/换行会串列。
/// </summary>
internal static class WordTableClipboard
{
    private const string HtmlHeaderFormat =
        "Version:0.9\r\n" +
        "StartHTML:{0:D10}\r\n" +
        "EndHTML:{1:D10}\r\n" +
        "StartFragment:{2:D10}\r\n" +
        "EndFragment:{3:D10}\r\n" +
        "StartSelection:{2:D10}\r\n" +
        "EndSelection:{3:D10}\r\n";

    private const string HtmlPrefix =
        "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
    private const string HtmlSuffix = "<!--EndFragment--></body></html>";

    /// <summary>
    /// 生成 TSV 与 CF_HTML 字符串。表头固定一行，Word 粘贴后即为带表头的表格。
    /// </summary>
    public static void Build(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
        out string plainText, out string html)
    {
        StringBuilder plain = new StringBuilder();
        plain.AppendLine(string.Join("\t", headers));
        foreach (string[] row in rows)
        {
            string[] cells = new string[row.Length];
            for (int i = 0; i < row.Length; ++i)
            {
                // 单元格内的制表符与换行替换为空格，避免破坏 TSV 的列/行结构
                cells[i] = FlattenCell(row[i]);
            }
            plain.AppendLine(string.Join("\t", cells));
        }
        plainText = plain.ToString().TrimEnd('\r', '\n');

        StringBuilder fragment = new StringBuilder();
        fragment.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" ");
        fragment.Append("style=\"border-collapse:collapse;width:100%;");
        fragment.Append("font-family:'Microsoft YaHei';font-size:10.5pt\">");
        fragment.Append("<thead><tr>");
        foreach (string header in headers)
        {
            fragment.Append("<th style=\"background-color:#F2F2F2;text-align:left\">");
            fragment.Append(WebUtility.HtmlEncode(header));
            fragment.Append("</th>");
        }
        fragment.Append("</tr></thead><tbody>");
        foreach (string[] row in rows)
        {
            fragment.Append("<tr>");
            foreach (string cell in row)
            {
                // 长哈希值允许在单元格内折行，避免把 Word 表格撑出页面
                fragment.Append("<td style=\"vertical-align:top;word-break:break-all\">");
                fragment.Append(HtmlCell(cell));
                fragment.Append("</td>");
            }
            fragment.Append("</tr>");
        }
        fragment.Append("</tbody></table>");
        html = WrapHtmlFragment(fragment.ToString());
    }

    private static string FlattenCell(string value)
    {
        return value?.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ") ?? string.Empty;
    }

    private static string HtmlCell(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return WebUtility.HtmlEncode(value)
            .Replace("\r\n", "<br>")
            .Replace("\r", "<br>")
            .Replace("\n", "<br>")
            .Replace("\t", "&nbsp;&nbsp;&nbsp;&nbsp;");
    }

    /// <summary>
    /// CF_HTML 的偏移一律按 UTF-8 字节数计算（表头是 ASCII，正文可能含中文），
    /// 片段边界标记必须放在 StartFragment / EndFragment 之间。
    /// </summary>
    private static string WrapHtmlFragment(string fragment)
    {
        string dummy = string.Format(CultureInfo.InvariantCulture, HtmlHeaderFormat, 0, 0, 0, 0);
        int startHtml = Encoding.UTF8.GetByteCount(dummy);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(HtmlPrefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(HtmlSuffix);
        string header = string.Format(CultureInfo.InvariantCulture, HtmlHeaderFormat,
            startHtml, endHtml, startFragment, endFragment);
        return header + HtmlPrefix + fragment + HtmlSuffix;
    }
}
