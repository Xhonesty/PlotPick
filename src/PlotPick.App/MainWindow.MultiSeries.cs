using System.Windows;
using System.Windows.Controls;
using PlotPick.Core;
using SkiaSharp;
using Series=PlotPick.Core.Series;

namespace PlotPick.App;
public partial class MainWindow
{
    sealed record Pending(ExtractionResult Result,string Signature,ExtractionOptions Options);
    readonly Dictionary<Guid,Pending> previews=[];bool reuseBlankOnPick;
    string Signature(Series s)=>Json.Serialize(new{plot!.ImageAsset,plot.Axes,s.Kind,s.RightAxis,Options=SeriesOptions.Get(plot,s),s.Points});
    ExtractionOptions RegionOptions=>series!.UseSharedRegion?plot!.Options:series.Extraction!;
    bool PersistSettings()
    {
        if(refreshing||plot is null||series is null)return true;
        try
        {
            var options=ReadOptions();if(RightAxis.IsChecked==true&&plot.Axes.RightY is null)throw new ArgumentException("请先标定右 Y 轴。");
            var before=Json.Serialize(series);var copy=Json.Clone(series);
            ReadSeriesSettings();series.Extraction=options;
            var after=Json.Serialize(series);
            if(before!=after){var updated=series;plot.Series[plot.Series.IndexOf(series)]=copy;Change();plot.Series[plot.Series.IndexOf(copy)]=updated;if(copy.RightAxis!=updated.RightAxis)MarkDerivedStale();}
            return true;
        }
        catch(Exception ex){Fail(ex);return false;}
    }
    void UpdateNavigation()
    {
        if(SelectionSummary is null)return;UpdateSelectionInfo();UpdateCalibrationPrompt();InsertControls.Visibility=Canvas.Tool=="insert"?Visibility.Visible:Visibility.Collapsed;
        SeriesList.IsEnabled=!state.Busy;PlotList.IsEnabled=!state.Busy;TableSeriesPicker.IsEnabled=!state.Busy;
        int selected=plot?.Series.Count(s=>s.ExtractSelected)??0;
        int pending=plot?.Series.Count(s=>s.ExtractSelected&&previews.ContainsKey(s.Id))??0;
        SelectionSummary.Text=plot is null?"先导入图表并设置坐标":$"已勾选 {selected} 条 · {pending} 条有预览 · 保存后可撤销";
        PreviewSelectedButton.IsEnabled=!state.Busy&&plot?.Axes.Calibrated==true&&selected>0;
        SaveSelectedButton.IsEnabled=!state.Busy&&pending>0;
        ImportStepText.Text=plot is null?"① 导入图表":"✓ 导入图表";
        AxisStepText.Text=plot?.Axes.Calibrated==true?"✓ 设置坐标":"② 设置坐标";
        ExtractStepText.Text=plot?.Series.Any(s=>s.Points.Count>0)==true?"✓ 提取数据":"③ 提取数据";
        FinishQuickPick.Visibility=Canvas.Tool=="multi-color"?Visibility.Visible:Visibility.Collapsed;
        foreach(var b in new[]{SelectTool,BoxSelectTool,ManualTool,InsertTool,RangeTool,PickColorTool})b.Style=(Style)FindResource((string)b.Tag==Canvas.Tool?"AccentOutline":"Quiet");
    }
    void SyncPreviews()
    {
        Canvas.Preview=[];Canvas.PreviewLayers=plot?.Series.Where(s=>s.Visible&&previews.ContainsKey(s.Id)).Select(s=>(s.Color,previews[s.Id].Result.Points)).ToList()??[];
        if(series is not null&&previews.TryGetValue(series.Id,out var p))
        {
            Canvas.SetMask(p.Result.Mask);ExtractionInfo.Text=$"预览 {p.Result.Points.Count:N0} 个点 · 尚未保存\n"+string.Join("\n",p.Result.Warnings);
        }
        else{Canvas.SetMask(null);ExtractionInfo.Text="先预览，检查后保存结果。";}
        Canvas.InvalidateVisual();UpdateNavigation();
    }
    async Task PreviewMany(IEnumerable<Series> requested)
    {
        if(!Ready(true)||!PersistSettings())return;
        var targets=requested.Where(s=>!s.Locked&&s.Kind!=SeriesKind.Derived).ToList();
        if(targets.Count==0){state.Status="请选择可编辑的原始系列。";return;}
        var bytes=project.Assets[plot!.ImageAsset];
        await Work(async token=>
        {
            for(int i=0;i<targets.Count;i++)
            {
                var s=targets[i];var options=SeriesOptions.Get(plot,s);var signature=Signature(s);
                state.Status=$"正在预览 {i+1}/{targets.Count}：{s.Name}";
                var offset=i;var report=new Progress<double>(v=>state.Progress=100*(offset+v)/targets.Count);
                var result=await Task.Run(()=>Extraction.Run(bytes,s.Kind,options,token,report),token);token.ThrowIfCancellationRequested();
                if(options.DataStep is double step&&s.Kind==SeriesKind.Curve)
                {
                    var kept=new List<DataPoint>();double? last=null;
                    foreach(var q in result.Points){var x=plot.Axes.ToData(q.Pixel,s.RightAxis).X;if(last is null||Math.Abs(x-last.Value)>=step){kept.Add(q);last=x;}}
                    result=result with{Points=kept};
                }
                previews[s.Id]=new(result,signature,options);SyncPreviews();
            }
            state.Status=$"{targets.Count} 条曲线预览完成。检查叠加结果后保存。";
        });
        SyncPreviews();
    }
    bool SavePreviews(IEnumerable<Series> requested,bool asNew=false)
    {
        if(!Ready()||!PersistSettings())return false;
        var targets=requested.ToList();if(targets.Count==0)return false;
        foreach(var s in targets)
        {
            if(!previews.TryGetValue(s.Id,out var p))throw new ArgumentException($"“{s.Name}”没有预览，请先预览全部选中曲线。");
            if(s.Locked||p.Signature!=Signature(s))throw new ArgumentException($"“{s.Name}”的数据或参数已改变，请重新预览。");
            if(p.Result.Points.Count==0)throw new ArgumentException($"“{s.Name}”未识别到点，请调整颜色或范围。");
        }
        if(!asNew&&targets.Any(s=>s.Points.Count>0)&&!Dialogs.Confirm(this,"选中的部分系列已有数据，保存将替换它们的数据点。此操作可撤销；也可用“另存新系列”保留两份。继续？"))return false;
        Change();Guid? addedId=null;
        foreach(var s in targets)
        {
            var p=previews[s.Id];var dest=s;
            if(asNew){dest=Json.Clone(s);dest.Id=Guid.NewGuid();dest.Name+=" · 新结果";plot!.Series.Add(dest);addedId=dest.Id;}
            else{var previous=series;series=s;MarkDerivedStale();series=previous;}
            dest.Points=Json.Clone(p.Result.Points);dest.Processing="提取参数："+Json.Serialize(p.Options);previews.Remove(s.Id);
        }
        Refresh(seriesId:addedId,image:false);SyncPreviews();state.Status=$"已保存 {targets.Count} 条曲线，请检查后标记已检查。";return true;
    }
    async void PreviewSelectedClick(object s,RoutedEventArgs e){if(plot is not null)await PreviewMany(plot.Series.Where(x=>x.ExtractSelected));}
    void SaveSelectedClick(object s,RoutedEventArgs e)=>Run(()=>{if(plot is not null)SavePreviews(plot.Series.Where(x=>x.ExtractSelected));});
    void ApplyAsNewClick(object s,RoutedEventArgs e)=>Run(()=>{if(series is not null)SavePreviews([series],true);});
    void SaveAndNextClick(object s,RoutedEventArgs e)=>Run(()=>{if(series is not null&&SavePreviews([series]))AddSeriesClick(s,e);});
    void QuickPickClick(object s,RoutedEventArgs e)
    {
        if(!Ready(true)||!PersistSettings())return;CloseExport();Inspector.SelectedIndex=1;reuseBlankOnPick=plot!.Series.Count==1&&series!.Points.Count==0&&!previews.ContainsKey(series.Id);Canvas.Tool="multi-color";
        ToolHint.Text="依次点击不同曲线的颜色 · 每次创建独立系列 · 点“结束选色”后预览";UpdateNavigation();
    }
    void FinishQuickPickClick(object s,RoutedEventArgs e){Canvas.Tool="select";ToolHint.Text="勾选要提取的曲线，然后点击“预览选中曲线”。";UpdateNavigation();}
    void AddPickedSeries(Vec pixel)
    {
        var c=Canvas.ColorAt(pixel);var color=$"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";Change();
        var n=plot!.Series.Count;var next=new Series{Name=$"曲线 {n+1}",Color=color,Marker=n%3,Extraction=Json.Clone(plot.Options)};
        next.Extraction.Color=color;next.Extraction.Seed=null;if(reuseBlankOnPick){next.Id=plot.Series[0].Id;next.Name="曲线 1";plot.Series[0]=next;reuseBlankOnPick=false;}else plot.Series.Add(next);Refresh(seriesId:next.Id,image:false);
        Canvas.Tool="multi-color";UpdateNavigation();state.Status=$"已添加 {next.Name} · {color}。继续点击下一条曲线，或结束选色。";
    }
    void ExtractionSelectionClick(object sender,RoutedEventArgs e)
    {
        if(state.Busy)return;if(sender is CheckBox{DataContext:Series s} c){Change();s.ExtractSelected=c.IsChecked==true;UpdateNavigation();}
    }
    void SharedRegionClick(object s,RoutedEventArgs e)
    {
        if(!Ready())return;var options=SeriesOptions.Get(plot!,series!);Change();series!.UseSharedRegion=SharedRegion.IsChecked==true;series.Extraction=options;Canvas.InvalidateVisual();
    }
    void TableSeriesChanged(object s,SelectionChangedEventArgs e){if(!refreshing&&!state.Busy&&TableSeriesPicker.SelectedItem is Series item)SeriesList.SelectedItem=item;}
    void InspectorChanged(object s,SelectionChangedEventArgs e){if(!IsInitialized||refreshing)return;UpdateNavigation();}
    void SeriesMoreClick(object sender,RoutedEventArgs e)
    {
        if(sender is not Button{DataContext:Series item} b||state.Busy)return;SeriesList.SelectedItem=item;
        var menu=new ContextMenu();var visible=new MenuItem{Header="显示在画布",IsCheckable=true,IsChecked=item.Visible};visible.Click+=(_,_)=>{Change();item.Visible=visible.IsChecked;Refresh(image:false);};
        var locked=new MenuItem{Header="锁定数据",IsCheckable=true,IsChecked=item.Locked};locked.Click+=(_,_)=>{Change();item.Locked=locked.IsChecked;Refresh(image:false);};
        var remove=new MenuItem{Header="删除系列…"};remove.Click+=RemoveSeriesClick;menu.Items.Add(visible);menu.Items.Add(locked);menu.Items.Add(new Separator());menu.Items.Add(remove);b.ContextMenu=menu;menu.IsOpen=true;
    }
    void CloseExport(){ExportPanel.Visibility=Visibility.Collapsed;Inspector.Visibility=Visibility.Visible;}
    void OpenExport(bool copy=false)
    {
        if(!Ready()||!PersistSettings())return;RightColumn.Width=new GridLength(340);Inspector.Visibility=Visibility.Collapsed;ExportPanel.Visibility=Visibility.Visible;
        ExportPanel.Closed=CloseExport;ExportPanel.Status=t=>state.Status=t;
        ExportPanel.ProfileSaved=p=>{if(Json.Serialize(project.ExportProfile)!=Json.Serialize(p)){Change();project.ExportProfile=Json.Clone(p);}};
        ExportPanel.Initialize(project,plot!.Id,series!.Id,copy);
    }
}
