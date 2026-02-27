using 외부 리포팅 도구;
using 외부 리포팅 도구.Utils;

var output = @"d:\새 폴더\클로드 레거시 판매관리\_frapi\sample.frx";
using var report = new Report();
var page = new ReportPage { Name = "Page1" };
report.Pages.Add(page);

var titleBand = new ReportTitleBand
{
    Name = "ReportTitle1",
    Top = 0,
    Width = page.Width,
    Height = Units.Millimeters * 20
};
page.ReportTitle = titleBand;

var text = new TextObject
{
    Name = "Text1",
    Left = 0,
    Top = 0,
    Width = Units.Millimeters * 100,
    Height = Units.Millimeters * 10,
    Text = "거래명세서"
};
titleBand.Objects.Add(text);

report.Save(output);
Console.WriteLine(output);
