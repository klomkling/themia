namespace Themia.Exceptional.AspNetCore;

/// <summary>The dashboard stylesheet, served from a route and linked (CSP-friendly: no inline style).</summary>
internal static class DashboardCss
{
    internal const string Content = """
        :root{--fg:#24292f;--muted:#57606a;--line:#d0d7de;--bg:#fff;--accent:#0969da;--err:#cf222e}
        body{font:14px -apple-system,system-ui,sans-serif;color:var(--fg);margin:1.5rem;background:var(--bg)}
        h1{font-size:1.4rem;margin:0 0 .25rem}
        .summary{color:var(--muted);margin:0 0 1rem}
        table{border-collapse:collapse;width:100%}
        th,td{border-bottom:1px solid var(--line);padding:6px 10px;text-align:left;vertical-align:top}
        th{background:#f6f8fa;font-weight:600}
        tr:hover td{background:#f6f8fa}
        a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
        pre{background:#f6f8fa;border:1px solid var(--line);border-radius:6px;padding:10px;overflow:auto;white-space:pre-wrap;font:12px ui-monospace,Menlo,Consolas,monospace}
        .type{font-weight:600}.type-err{color:var(--err)}
        .meta th{width:160px;white-space:nowrap}
        form.filter{margin:0 0 1rem;display:flex;gap:.5rem;flex-wrap:wrap}
        input,button{font:14px inherit;padding:4px 8px;border:1px solid var(--line);border-radius:6px}
        button{background:#f6f8fa;cursor:pointer}
        .actions{display:inline}.actions button{color:var(--err)}
        .pager{margin:1rem 0;color:var(--muted)}
        time{cursor:help}
        h2{font-size:1.05rem;margin:1.25rem 0 .25rem;border-bottom:1px solid var(--line);padding-bottom:.2rem}
        """;
}
