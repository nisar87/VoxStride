using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Json;

namespace VoxStride.Core.Tools;

/// <summary>Opens a document VoxStride created (or one inside the allowed folders) with its default app.</summary>
public interface IFileOpener
{
    Task<bool> OpenAsync(string path, CancellationToken ct);
}

/// <summary>
/// Creates a new document — Excel workbook (.xlsx), CSV, text, Markdown or HTML — in an allowed folder, and optionally
/// opens it. Never overwrites: if the name is taken, " (2)", " (3)"… is added. The written file is read back to verify.
/// </summary>
public sealed class CreateDocumentTool(FileScope scope, IFileOpener? opener = null) : FileOpsToolBase(scope)
{
    public static readonly string[] Formats = ["xlsx", "csv", "txt", "md", "html"];

    public override ToolDefinition Definition { get; } = new(
        "files_create_document",
        "Create a NEW document and (by default) open it: an Excel spreadsheet (xlsx) or CSV from rows, or a text, Markdown or HTML file from text. " +
        "Use this for requests like 'put the weather for France in an Excel sheet'. First row of 'rows' is the header. Never overwrites existing files.",
        Schema.Object(
            ("folder", Schema.Str("Where to save it, e.g. 'Desktop' or 'Documents\\\\Reports' (default Documents)", null, 400), false),
            ("name", Schema.Str("File name without extension, e.g. 'France weather'", @"^[^\\/:*?""<>|]{1,100}$", 100), true),
            ("format", Schema.Str("File type", null, 8, Formats), true),
            ("rows", Schema.Array("Table rows for xlsx/csv; the first row is the header. Numbers can be given as text like '21.5'.", Schema.Array("Cells", Schema.Str("Cell value", null, 2000), 50), 2000), false),
            ("sheetName", Schema.Str("Worksheet name for xlsx", @"^[^\\/:*?\[\]]{1,31}$", 31), false),
            ("text", Schema.Str("Content for txt/md/html", null, 200_000), false),
            ("open", Schema.Bool("Open it after creating (default true)"), false)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var folderText = args["folder"]?.GetValue<string>() is { Length: > 0 } f ? f : "Documents";
        if (!Scope.TryResolve(folderText, out var folder, out var err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        if (!Directory.Exists(folder)) return ValueTask.FromResult(ToolValidation.Invalid("not_found", "That folder doesn't exist."));
        var name = Str(args, "name").Trim().TrimEnd('.');
        if (name.Length == 0 || CreateFolderTool.IsReservedName(name)) return ValueTask.FromResult(ToolValidation.Invalid("invalid_name", "That isn't a valid file name."));
        var format = Str(args, "format");
        var rows = Rows(args);
        if (format is "xlsx" or "csv" && rows.Count == 0) return ValueTask.FromResult(ToolValidation.Invalid("no_rows", "A spreadsheet needs at least one row."));
        if (format is "txt" or "md" or "html" && string.IsNullOrEmpty(args["text"]?.GetValue<string>())) return ValueTask.FromResult(ToolValidation.Invalid("no_text", "No text was given for the document."));
        var target = FreeName(folder, name, format);
        if (!Scope.TryResolve(target, out target, out err)) return ValueTask.FromResult(ToolValidation.Invalid("path_not_allowed", err));
        ctx.Items["target"] = target;
        ctx.Items["rows"] = rows;
        return ValueTask.FromResult(ToolValidation.Valid());
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var target = (string)ctx.Items["target"];
        var rows = (List<List<string>>)ctx.Items["rows"];
        var format = Str(args, "format");
        ctx.EnsureCanWrite(ct);
        if (File.Exists(target)) return ToolResult.Fail("exists", "A file with that name appeared just now; nothing was overwritten.");
        byte[] bytes = format switch
        {
            "xlsx" => Xlsx.Build(rows, args["sheetName"]?.GetValue<string>() ?? "Sheet1"),
            "csv" => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv(rows))).ToArray(),
            _ => Encoding.UTF8.GetBytes(args["text"]!.GetValue<string>()),
        };
        await using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await fs.WriteAsync(bytes, ct);
        ctx.Items["bytes"] = bytes.Length;
        var opened = false;
        if (args["open"]?.GetValue<bool>() != false && opener is not null)
            opened = await opener.OpenAsync(target, ct);
        var what = format == "xlsx" ? "Excel sheet" : format.ToUpperInvariant() + " file";
        return ToolResult.Ok(new JsonObject { ["path"] = target, ["rows"] = rows.Count, ["opened"] = opened },
            $"I created the {what} {Path.GetFileName(target)} in {Path.GetFileName(Path.GetDirectoryName(target))}" + (opened ? " and opened it." : "."), false, [target]);
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var target = (string)ctx.Items["target"];
        var ok = File.Exists(target) && new FileInfo(target).Length == (int)ctx.Items["bytes"];
        if (ok && Str(args, "format") == "xlsx")
        {
            try { using var z = ZipFile.OpenRead(target); ok = z.GetEntry("xl/worksheets/sheet1.xml") is not null; } catch (InvalidDataException) { ok = false; }
        }
        return ValueTask.FromResult(new ToolVerification(ok, ok ? ToolOutcome.Success : ToolOutcome.Unknown, ok ? null : "The file could not be read back."));
    }

    private static List<List<string>> Rows(JsonObject a) =>
        a["rows"] is JsonArray arr ? arr.Select(r => r is JsonArray cells ? cells.Select(c => c?.GetValue<string>() ?? "").ToList() : []).Where(r => r.Count > 0).ToList() : [];

    internal static string FreeName(string folder, string name, string ext)
    {
        var p = Path.Combine(folder, $"{name}.{ext}");
        for (var i = 2; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(folder, $"{name} ({i}).{ext}");
        return p;
    }

    internal static string Csv(IEnumerable<IReadOnlyList<string>> rows) =>
        string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)))) + "\r\n";
}

/// <summary>Minimal, dependency-free .xlsx (Office Open XML) writer: one sheet, inline strings and numbers, bold header.</summary>
public static class Xlsx
{
    public static byte[] Build(IReadOnlyList<IReadOnlyList<string>> rows, string sheetName = "Sheet1")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Esc(sheetName)}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>""");
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(rows));
        }
        return ms.ToArray();
    }

    private static string Sheet(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        var widths = new int[rows.Max(r => r.Count)];
        foreach (var r in rows) for (var c = 0; c < r.Count; c++) widths[c] = Math.Max(widths[c], Math.Min(60, r[c].Length));
        sb.Append("<cols>");
        for (var c = 0; c < widths.Length; c++) sb.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{Math.Max(8, widths[c] + 2)}" customWidth="1"/>""");
        sb.Append("</cols><sheetData>");
        for (var i = 0; i < rows.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<row r="{i + 1}">""");
            for (var c = 0; c < rows[i].Count; c++)
            {
                var v = rows[i][c];
                var refName = Column(c) + (i + 1).ToString(CultureInfo.InvariantCulture);
                var style = i == 0 ? " s=\"1\"" : "";
                if (i > 0 && IsNumber(v, out var d))
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{refName}"{style}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                else
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{refName}" t="inlineStr"{style}><is><t xml:space="preserve">{Esc(v)}</t></is></c>""");
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    /// <summary>Plain numbers become numeric cells; codes with leading zeros (phone numbers, IDs) stay text.</summary>
    internal static bool IsNumber(string v, out double d)
    {
        d = 0;
        v = v.Trim();
        if (v.Length == 0 || v.Length > 20) return false;
        if (v.Length > 1 && v[0] == '0' && v[1] != '.') return false;
        if (v.Length > 2 && v[0] == '-' && v[1] == '0' && v[2] != '.') return false;
        return double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);
    }

    internal static string Column(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    private static string Esc(string s) => SecurityElement.Escape(new string(s.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray()));

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
