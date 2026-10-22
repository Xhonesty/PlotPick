using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PlotPick.Core;
namespace PlotPick.App;

public partial class ExportDrawer:UserControl
{
    Project? project;Guid plotId,seriesId;bool loading;ExportBundle? bundle;ExportProfile profile=new();
    readonly ObservableCollection<ExportColumn> columns=[];readonly List<Choice> choices=[];
    public Action? Closed;public Action<ExportProfile>? ProfileSaved;public Action<string>? Status;
    string PersonalPath=>Path.Combine(App.StateDirectory,"export-preset.json");
    public ExportDrawer(){InitializeComponent();ColumnsList.ItemsSource=columns;}
    public void Initialize(Project source,Guid currentPlot,Guid currentSeries,bool copy=false)
    {
        loading=true;project=source;plotId=currentPlot;seriesId=currentSeries;
        profile=Json.Clone(source.ExportProfile??ReadPersonal()??new ExportProfile());columns.Clear();foreach(var col in profile.Columns)columns.Add(col);
        foreach(var col in ConfiguredExport.DefaultColumns().Where(c=>columns.All(x=>x.Key!=c.Key)))columns.Add(col);
        ((ComboBoxItem)LayoutBox.Items[0]).IsEnabled=true;Scope.SelectedIndex=copy?0:2;FormatBox.SelectedIndex=0;LayoutBox.SelectedIndex=(int)profile.Layout;Preset.SelectedIndex=3;
        PadZeros.IsChecked=profile.PadSignificantZeros;AcceptedOnly.IsChecked=profile.AcceptedOnly;Digits.Text=profile.SignificantDigits.ToString();XTolerance.Text=profile.XTolerance.ToString("G17",CultureInfo.InvariantCulture);Resample.IsChecked=profile.Resample;ResampleStep.Text=profile.ResampleStep.ToString(CultureInfo.InvariantCulture);
        PopulateChoices();loading=false;Rebuild();
    }
    ExportProfile? ReadPersonal(){try{return File.Exists(PersonalPath)?Json.Deserialize<ExportProfile>(File.ReadAllText(PersonalPath)):null;}catch{return null;}}
    void PopulateChoices()
    {
        if(project is null)return;choices.Clear();foreach(var item in ConfiguredExport.Enumerate(project))
        {
            bool selected=Scope.SelectedIndex switch{0=>item.Series.Id==seriesId,3=>true,_=>item.Plot.Id==plotId};
            choices.Add(new(item){Selected=selected});
        }
        SeriesChoices.ItemsSource=null;SeriesChoices.ItemsSource=choices;
    }
    void ScopeChanged(object s,SelectionChangedEventArgs e){if(loading)return;PopulateChoices();Rebuild();}
    void SeriesCheck(object s,RoutedEventArgs e){if(loading)return;loading=true;Scope.SelectedIndex=1;loading=false;Rebuild();}
    void ColumnCheck(object s,RoutedEventArgs e){if(loading)return;loading=true;Preset.SelectedIndex=3;loading=false;Rebuild();}
    void ColumnRenamed(object s,RoutedEventArgs e){if(!loading)Rebuild();}
    void FormatChanged(object s,SelectionChangedEventArgs e)
    {
        if(loading)return;bool text=FormatBox.SelectedIndex is 1 or 2 or 3;((ComboBoxItem)LayoutBox.Items[0]).IsEnabled=!text;
        if(text&&LayoutBox.SelectedIndex==0)LayoutBox.SelectedIndex=1;Rebuild();
    }
    void OptionChanged(object s,RoutedEventArgs e){if(!loading)Rebuild();}
    void PresetChanged(object s,SelectionChangedEventArgs e)
    {
        if(loading||Preset.SelectedIndex==3)return;foreach(var c in columns)c.Enabled=Preset.SelectedIndex switch{0=>c.Key is "x" or "y",1=>c.Key is "series" or "x" or "y",_=>!c.Key.EndsWith("_id")};ColumnsList.Items.Refresh();Rebuild();
    }
    int ReadDigits()
    {
        if(!int.TryParse(Digits.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int value)||value<1||value>17)throw new ArgumentException("有效数字请输入 1–17 之间的整数。");return value;
    }
    void DigitsChanged(object s,TextChangedEventArgs e){if(!loading){Rebuild();if(project is not null)CommitProfile();}}
    void PrecisionChanged(object s,RoutedEventArgs e){if(!loading){Rebuild();if(project is not null)CommitProfile();}}
    void PrecisionPresetClick(object s,RoutedEventArgs e){Digits.Text=(string)((Button)s).Tag;}
    public bool CommitProfile()
    {
        try{var value=ReadProfile();ProfileSaved?.Invoke(Json.Clone(value));return true;}
        catch(Exception ex){Error.Text=ex.Message;return false;}
    }
    ExportProfile ReadProfile()=>new(){Layout=(ExportLayout)Math.Max(0,LayoutBox.SelectedIndex),Columns=columns.ToList(),AcceptedOnly=AcceptedOnly.IsChecked==true,SignificantDigits=ReadDigits(),PadSignificantZeros=PadZeros.IsChecked==true,XTolerance=double.Parse(XTolerance.Text,CultureInfo.InvariantCulture),Resample=Resample.IsChecked==true,ResampleStep=double.Parse(ResampleStep.Text,CultureInfo.InvariantCulture)};
    void Rebuild()
    {
        if(loading||project is null)return;
        try
        {
            profile=ReadProfile();bundle=ConfiguredExport.Build(choices.Where(c=>c.Selected).Select(c=>c.Item),profile);
            var selected=PreviewSheet.SelectedIndex;loading=true;PreviewSheet.ItemsSource=bundle.Tables.Select(t=>t.Name).ToList();PreviewSheet.SelectedIndex=Math.Clamp(selected,0,bundle.Tables.Count-1);loading=false;ShowPreview();
            Summary.Text=$"{bundle.SeriesCount} 个系列 · {bundle.Tables.Count} 张表 · {bundle.PointCount:N0} 个原始数据点";Notice.Text=bundle.Notice+" 预览显示前 30 行。";Error.Text="";ExportButton.IsEnabled=true;
            ExportButton.Content="导出 "+new[]{"Excel","CSV","TSV","TXT","JSON"}[Math.Max(0,FormatBox.SelectedIndex)];
        }
        catch(Exception ex){loading=false;bundle=null;PreviewGrid.ItemsSource=null;Error.Text=ex.Message;Summary.Text="请调整导出设置";ExportButton.IsEnabled=false;}
    }
    void ShowPreview()
    {
        if(bundle is null||PreviewSheet.SelectedIndex<0)return;var t=bundle.Tables[PreviewSheet.SelectedIndex];var view=new DataTable();foreach(var h in t.Headers)view.Columns.Add(h);foreach(var row in t.Rows.Take(30))view.Rows.Add(row.Cast<object>().ToArray());PreviewGrid.ItemsSource=view.DefaultView;
    }
    void PreviewSheetChanged(object s,SelectionChangedEventArgs e){if(!loading)ShowPreview();}
    void CloseClick(object s,RoutedEventArgs e){if(CommitProfile())Closed?.Invoke();}
    void SavePresetClick(object s,RoutedEventArgs e)
    {
        Rebuild();if(bundle is null)return;try{Directory.CreateDirectory(App.StateDirectory);File.WriteAllText(PersonalPath,Json.Serialize(profile));ProfileSaved?.Invoke(Json.Clone(profile));Notice.Text="个人预设已保存，后续新项目会沿用这些列与布局。";}catch(Exception ex){Error.Text=ex.Message;}
    }
    void WriteClick(object s,RoutedEventArgs e)
    {
        Rebuild();if(bundle is null||project is null)return;string ext=new[]{"xlsx","csv","tsv","txt","json"}[Math.Max(0,FormatBox.SelectedIndex)];var dialog=new SaveFileDialog{Filter=$"{ext.ToUpperInvariant()} 文件|*.{ext}",FileName=project.Name+"."+ext};if(dialog.ShowDialog()!=true)return;
        try{ConfiguredExport.Write(bundle,dialog.FileName);ProfileSaved?.Invoke(Json.Clone(profile));Status?.Invoke("已按预览配置导出："+dialog.FileName);Notice.Text="导出完成。";}catch(Exception ex){Error.Text=ex.Message;}
    }
    void CopyClick(object s,RoutedEventArgs e)
    {
        Rebuild();if(bundle is null)return;if(bundle.Tables.Count!=1){Error.Text="当前有多个工作表，请切换独立列对或长表后复制。";return;}
        try{Clipboard.SetText(ConfiguredExport.Delimited(bundle.Tables[0],'\t'));ProfileSaved?.Invoke(Json.Clone(profile));Status?.Invoke($"已复制 {bundle.SeriesCount} 个系列的所选列。");Notice.Text="所选列已复制，可粘贴到 Excel。";}catch(Exception ex){Error.Text=ex.Message;}
    }
    void MoveColumn(int delta){int index=ColumnsList.SelectedIndex;if(index<0||index+delta<0||index+delta>=columns.Count)return;columns.Move(index,index+delta);ColumnsList.SelectedIndex=index+delta;Rebuild();}
    void ColumnUp(object s,RoutedEventArgs e)=>MoveColumn(-1);void ColumnDown(object s,RoutedEventArgs e)=>MoveColumn(1);
    ExportColumn? dragged;Point dragStart;
    void ColumnMouseDown(object s,MouseButtonEventArgs e){dragged=(e.OriginalSource as TextBlock)?.Tag as string=="drag"?(e.OriginalSource as FrameworkElement)?.DataContext as ExportColumn:null;dragStart=e.GetPosition(this);}
    void ColumnMouseMove(object s,MouseEventArgs e){if(e.LeftButton==MouseButtonState.Pressed&&dragged is not null&&(e.GetPosition(this)-dragStart).Length>6){var column=dragged;dragged=null;DragDrop.DoDragDrop(ColumnsList,new DataObject(typeof(ExportColumn),column),DragDropEffects.Move);}}
    void ColumnDrop(object s,DragEventArgs e){if(e.Data.GetData(typeof(ExportColumn)) is not ExportColumn column)return;var target=(e.OriginalSource as FrameworkElement)?.DataContext as ExportColumn;int to=target is null?columns.Count-1:columns.IndexOf(target);int from=columns.IndexOf(column);if(from>=0&&to>=0)columns.Move(from,to);Rebuild();e.Handled=true;}
    public class Choice(ExportSeries item)
    {
        public ExportSeries Item {get;}=item;public bool Selected{get;set;}public string Name=>Item.Series.Name;public string Symbol=>Item.Series.Symbol;public string Color=>Item.Series.Color;public string Count=>$"{Item.Series.Points.Count:N0} 点";public string Source=>$"{Item.Document.Name} / {Item.Plot.Name}";
    }
}
