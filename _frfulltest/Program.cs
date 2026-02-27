using System;
using 외부 리포팅 도구;

var path = @"D:\새 폴더\클로드 레거시 판매관리\양식\새 폴더\P_거래명세21_2.fr3";
Console.WriteLine($"Path Exists: {System.IO.File.Exists(path)}");
using var report = new Report();
try
{
    report.Load(path);
    Console.WriteLine($"Loaded OK");
    Console.WriteLine($"AllObjects: {report.AllObjects.Count}");
    Console.WriteLine($"Pages: {report.Pages.Count}");
    var prepared = report.Prepare();
    Console.WriteLine($"Prepare: {prepared}");
    Console.WriteLine($"PreparedPages: {report.PreparedPages?.Count}");
}
catch (Exception ex)
{
    Console.WriteLine("EX: " + ex.GetType().FullName);
    Console.WriteLine(ex.Message);
    Console.WriteLine(ex.StackTrace);
}
