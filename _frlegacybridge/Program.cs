using 외부 리포팅 도구;
var path=@"d:\새 폴더\클로드 레거시 판매관리\양식\새 폴더\박사_거래21_2.fr3";
using var report=new Report();
report.Load(path);
Console.WriteLine($"AllObjects={report.AllObjects.Count}, Pages={report.Pages.Count}");
Console.WriteLine($"Prepare={report.Prepare()}, Prepared={report.PreparedPages?.Count ?? -1}");
