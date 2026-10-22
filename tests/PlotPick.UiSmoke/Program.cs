using System.IO;
using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

// Dedicated engineering test. Targets only the child application launched here.
var root=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var exe=Path.Combine(root,"artifacts/publish-0.3.0/PlotPick.exe");
var sample=Path.Combine(root,"samples/三曲线待提取.plotpick");
var checks=new List<string>();
using var automation=new UIA3Automation();
using var app=Application.Launch(new ProcessStartInfo(exe){ArgumentList={sample},WorkingDirectory=Path.GetDirectoryName(exe)!});
Window? window=null;
T Wait<T>(Func<T?> read,string label) where T:class
{
    var timer=Stopwatch.StartNew();while(timer.Elapsed<TimeSpan.FromSeconds(15)){try{var value=read();if(value is not null)return value;}catch{}Thread.Sleep(100);}throw new Exception("Timeout: "+label);
}
void Click(string name)
{
    var button=Wait(()=>window!.FindFirstDescendant(cf=>cf.ByControlType(ControlType.Button).And(cf.ByName(name)))?.AsButton(),name);button.Invoke();Thread.Sleep(150);
}
try
{
    window=Wait(()=>app.GetMainWindow(automation),"main window");
    Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("0 个点")),"empty fixture");checks.Add("三系列项目打开，原始表格为空");
    Click("▶ 预览选中曲线");Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("3 条曲线预览完成。检查叠加结果后保存。")),"multi preview");
    Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("0 个点")),"preview does not commit");checks.Add("三曲线分别预览，应用前表格保持为空");
    Click("保存选中结果");Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("273 个点")),"save all");checks.Add("一次保存三条曲线");
    Click("↶");Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("0 个点")),"undo all");checks.Add("一次撤销批量保存");
    Click("↷");Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("273 个点")),"redo all");checks.Add("一次重做恢复批量保存");
    window.FindFirstDescendant(cf=>cf.ByControlType(ControlType.TabItem).And(cf.ByName("提取")))!.AsTabItem().Select();
    var list=window.FindFirstDescendant(cf=>cf.ByAutomationId("SeriesList"))!.AsListBox();
    list.Items[1].Select();Wait(()=>window.FindFirstDescendant(cf=>cf.ByAutomationId("SeriesName"))?.AsTextBox().Text=="实验组 A"?window:null,"switch series");checks.Add("切换系列同步右侧属性和数据表");
    window.FindFirstDescendant(cf=>cf.ByAutomationId("BoxSelectTool"))!.AsButton().Invoke();
    Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("拖动框选当前系列的点 · Ctrl 追加 · Delete 删除 · Esc 清除")),"box tool");checks.Add("独立框选工具显示当前系列与快捷键提示");
    window.FindFirstDescendant(cf=>cf.ByAutomationId("InsertTool"))!.AsButton().Invoke();
    var position=Wait(()=>window.FindFirstDescendant(cf=>cf.ByAutomationId("InsertMode"))?.AsComboBox(),"insertion mode");position.Select(2);
    Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("选择参考点后切回补点；开头/结尾无需参考点")),"manual insert hint");checks.Add("补点工具可切换手动插入模式");
    Click("导出数据");Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("3 个系列 · 3 张表 · 819 个原始数据点")),"export all");checks.Add("导出抽屉显示三工作表和 819 点");
    window.FindFirstDescendant(cf=>cf.ByAutomationId("ExportScope"))!.AsComboBox().Select(0);
    Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("1 个系列 · 1 张表 · 273 个原始数据点")),"current export");checks.Add("导出范围切换当前系列");
    var cols=window.FindFirstDescendant(cf=>cf.ByAutomationId("ExportColumns"))!.AsListBox();
    var x=cols.Items[0].FindFirstDescendant(cf=>cf.ByControlType(ControlType.CheckBox))!.AsCheckBox();x.IsChecked=false;
    Click("复制");Wait(()=>window.FindFirstDescendant(cf=>cf.ByName("已复制 1 个系列的所选列。")),"copy");checks.Add("关闭 X 列后按配置复制当前系列");
    string clip="";var thread=new Thread(()=>clip=System.Windows.Clipboard.GetText());thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(!clip.StartsWith("Y\r\n")||clip.Split('\n')[1].Contains('\t'))throw new Exception("Clipboard column projection differs from selection");checks.Add("剪贴板回读确认仅有 Y 列");
    var precision=window.FindFirstDescendant(cf=>cf.ByAutomationId("SignificantDigits"))!.AsTextBox();
    precision.Text="18";Wait(()=>!window.FindFirstDescendant(cf=>cf.ByAutomationId("ConfirmExport"))!.IsEnabled?window:null,"invalid digits");checks.Add("非法有效数字阻止导出");
    precision.Text="3";Click("复制");string rounded="";var copyThread=new Thread(()=>rounded=System.Windows.Clipboard.GetText());copyThread.SetApartmentState(ApartmentState.STA);copyThread.Start();copyThread.Join();
    var originalValue=double.Parse(clip.Split('\n')[1].Trim(),System.Globalization.CultureInfo.InvariantCulture);var expected=originalValue.ToString("G3",System.Globalization.CultureInfo.InvariantCulture);
    if(rounded.Split('\n')[1].Trim()!=expected)throw new Exception("Precision was not applied to clipboard");checks.Add("3 位有效数字即时应用到复制数据");
    Click("×");Click("导出数据");Wait(()=>window.FindFirstDescendant(cf=>cf.ByAutomationId("SignificantDigits"))?.AsTextBox().Text=="3"?window:null,"remember precision");checks.Add("重新打开导出抽屉保留精度设置");
    // Modal dialogs are separately verified through desktop interaction. This harness uses UIA patterns only.
    File.WriteAllText(Path.Combine(root,"artifacts/test-results/v030-ui-smoke.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks,Timestamp=DateTimeOffset.Now},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("UI smoke passed: "+checks.Count);
}
catch(Exception ex){if(window is not null)File.WriteAllLines(Path.Combine(root,"artifacts/test-results/v030-ui-state.txt"),window.FindAllDescendants(cf=>cf.ByControlType(ControlType.Text)).Select(x=>x.Name));File.WriteAllText(Path.Combine(root,"artifacts/test-results/v030-ui-smoke.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=ex.ToString()}));Console.Error.WriteLine(ex);Environment.ExitCode=1;}
finally
{
    // The child only contains generated fixture state; never terminate a pre-existing/user process.
    if(!app.HasExited)app.Kill();
}

