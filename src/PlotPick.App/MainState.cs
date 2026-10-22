using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlotPick.Core;
namespace PlotPick.App;
public partial class MainState:ObservableObject
{
    [ObservableProperty] string projectName="未命名项目";
    [ObservableProperty] string status="准备就绪 · 所有数据仅保存在本机";
    [ObservableProperty] string saveStatus="本地项目";
    [ObservableProperty] string pointCount="0 个点";
    [ObservableProperty] string coordinates="";
    [ObservableProperty] string zoom="100%";
    [ObservableProperty] bool busy;
    [ObservableProperty] double progress;
    public ObservableCollection<PlotItem> Plots {get;}=[];
    public ObservableCollection<Series> Series {get;}=[];
    public ObservableCollection<PointRow> Rows {get;}=[];
}
public record PlotItem(Guid Id,string Name,string Source)
{
    public override string ToString()=>Name;
}
public class PointRow
{
    public Guid Id {get;set;} public int Index {get;set;}
    public string X {get;set;}=""; public string Y {get;set;}="";
    public string Category {get;set;}="";public string Origin {get;set;}="";public string Review {get;set;}="";
}
