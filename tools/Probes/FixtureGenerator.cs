using System.IO.Compression;
using System.Text;

internal static class FixtureGenerator
{
    public static void Generate(string directory)
    {
        directory = Path.GetFullPath(directory);
        foreach (string folder in new[] { "A", "B", "한글 공백 & 괄호 (검증)" }) Directory.CreateDirectory(Path.Combine(directory, folder));
        File.WriteAllText(Path.Combine(directory, "한글 공백 & 괄호 (검증)", "합성 메모.txt"), "합성 시험 데이터입니다.", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "한글 공백 & 괄호 (검증)", "실행 금지.cmd"), "@echo This fixture must only be revealed, never executed.\r\n", Encoding.ASCII);
        Workbook(Path.Combine(directory, "A", "같은이름.xlsx"), "D127");
        Workbook(Path.Combine(directory, "B", "같은이름.xlsx"), "F42");
    }
    private static void Workbook(string file, string activeCell)
    {
        if (File.Exists(file)) return; // Never replace a fixture someone has edited.
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        void Entry(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
        Entry("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
        Entry("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Entry("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><bookViews><workbookView activeTab="0"/></bookViews><sheets><sheet name="확정자" sheetId="1" r:id="rId1"/><sheet name="대기자" sheetId="2" r:id="rId2"/></sheets></workbook>""");
        Entry("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/></Relationships>""");
        Entry("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0" tabSelected="1"><selection activeCell="{activeCell}" sqref="{activeCell}"/></sheetView></sheetViews><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>합성 시험용</t></is></c></row></sheetData></worksheet>""");
        Entry("xl/worksheets/sheet2.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"/></sheetViews><sheetData/></worksheet>""");
    }
}
