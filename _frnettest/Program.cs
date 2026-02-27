using 외부 리포팅 도구;

var path = args.Length > 0
    ? args[0]
    : @"d:\새 폴더\클로드 레거시 판매관리\양식\P_거래명세21_2.fr3";

using var report = new Report();
report.Load(path);
Console.WriteLine($"Loaded: {path}");
Console.WriteLine($"AllObjects: {report.AllObjects.Count}");
Console.WriteLine($"Pages: {report.Pages.Count}");
var ok = report.Prepare();
Console.WriteLine($"Prepare: {ok}, PreparedPages: {report.PreparedPages?.Count ?? -1}");
