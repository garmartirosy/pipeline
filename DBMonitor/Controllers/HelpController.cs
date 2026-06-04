using Markdig;
using Microsoft.AspNetCore.Mvc;

namespace DBMonitor.Controllers;

public class HelpController : Controller
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public IActionResult Quickstart()
    {
        var md = @"# Quick Start

## 1. Add a connection

Go to **Connections → New Connection** and enter:
- A friendly **name**
- The **provider** (SQL Server or PostgreSQL)
- Your **connection string**

Click **Test** to verify, then **Create**.

## 2. Browse your schema

From the connections list click **Browse** to explore tables, views, stored procedures, and functions.
Click any table to page through its data with filters and sorting.

## 3. Run SQL

Click **SQL** on a connection to open the editor.
Write your query and press **Ctrl+Enter** (or click **Run**).
Results appear in tabs below the editor — click a column header to sort client-side.

## 4. Save queries

In the SQL editor toolbar click **Save query** to name the current SQL and attach it to a connection (or keep it global).
Use the **Saved** tab in the right panel to reload any saved query.

## 5. Import CSV

Click **Import CSV** on any connection or table to bulk-load a file using a four-step wizard:
upload → configure (delimiter, encoding, null handling) → map columns → run.

## 6. Audit log

Every query you run is logged. Click **Activity** in the top nav to search, filter, and export the log.

## Tips

- **Pin connections** you use often using the ★ icon on the connections page — they sort to the top.
- **Drag** rows on the connections page to reorder them.
- **Theme**: switch between Light, Dark, and System in **Settings**.
- Use `SELECT TOP 100 *` / `LIMIT 100` initially so you don't pull millions of rows.
";
        ViewData["Title"] = "Quick Start";
        ViewBag.HtmlContent = Markdown.ToHtml(md, Pipeline);
        return View();
    }
}
